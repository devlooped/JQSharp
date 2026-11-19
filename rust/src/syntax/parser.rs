//! Recursive descent parser of jqlite (spec §3) over the tokens of the lexer. It never panics for
//! bad input: it returns the `Program` or one `SyntaxError`.
//!
//! Rewritten by the parser (spec §3.3), so the AST has one form for each:
//!
//! - `..` is the call `recurse` (resolved like any call: a user `recurse/0` shadows it, as in jq).
//! - `E?` and `try E` are `Try` without a handler (which discards the error).
//! - `.name`, `."s"`, `T.name`, `T."s"` are `Index` with a literal or template key.
//! - `elif` nests `If`; a missing `else` is `.`; a `foreach` without extract has `.`.
//! - `{$x}` is `{"x": $x}`. The string shorthands (`{a}`, `{"s"}`, `{@f "s"}`) keep no value, see
//!   `ObjectEntry`.
//! - `L = R` is `_assign(L; R)`, `L |= R` is `_modify(L; R)`, `L op= R` is
//!   `R as $v | _modify(L; . op $v)` (`//=` likewise with `//`), where `$v` is
//!   [`UPDATE_VARIABLE`], a name no program can write.
//! - A string or `@f "s"` without interpolation is a `Literal` (a format applies to interpolations
//!   only); parentheses leave no node.
//!
//! Where jq 1.8.2 parses differently from the EBNF of spec §3.2, this parser follows jq: `def` and
//! `label` start a pipe or comma item only, never an operand (`1 + def f: 1; f` fails); object
//! values are `Alt ('|' Alt)*` without `as`; object constructions accept a trailing comma; `.[:]`
//! is an error; a definition name may be qualified (`def a::b:`); module metadata constants have
//! no negative numbers.
//!
//! Nesting (brackets, interpolations, `if`, `reduce`, `foreach`, `try`, `def`) is limited to
//! [`MAX_NESTING`]; within the limit the host stack is not exhausted. Sequences do not recurse per
//! element: pipes, commas, bindings and leading definitions are folded from a list
//! (`parse_pipe`), binary operators are parsed with an operator stack (`parse_expression`), and
//! runs of unary minus and of `elif` are loops.
//!
//! Rust: what recurses per nesting level (`parse_pipe`, `parse_expression`, `parse_pattern`,
//! `parse_constant`) checks the host stack first and continues on a fresh one when it runs short
//! (`crate::stack_guard`, which belongs to no layer). The reference keeps its frames under 1 KB a
//! level instead, which a Rust build without optimizations cannot promise: there a level takes
//! several KB.

use std::sync::Arc;

use crate::stack_guard;
use crate::values::{JqArray, JqObject, JqValue};

use super::ast::{BinaryOp, Definition, Import, Node, NodeKind, ObjectEntry, Param, Pattern, PatternEntry, PatternKind, Position, Program};
use super::lexer::Lexer;
use super::syntax_error::{SyntaxError, syntax_messages};
use super::token::{Token, TokenKind, is_keyword};

/// The nesting limit of spec §11.
pub const MAX_NESTING: u32 = 1000;

/// The variable that the rewriting of `L op= R` binds; '*' cannot appear in a source name.
pub const UPDATE_VARIABLE: &str = "*update";

/// `$__loc__`, the location object of §7.5: the resolver replaces it; no pattern, parameter or
/// import binds it.
pub const LOCATION_VARIABLE: &str = "__loc__";

#[derive(Clone, Copy, PartialEq, Eq)]
enum Assoc {
    Left,
    Right,
    None,
}

/// The binary operators (spec §3.1, levels 4 to 10) with their level, higher binding tighter, and
/// associativity; level 0 for any other token. Levels 1 to 3 (`|`, `,`, `as`) are parsed by
/// `parse_pipe`, levels 11 to 13 (unary minus, suffixes, terms) by `parse_expression`.
fn binary_level(kind: TokenKind) -> (u32, Assoc) {
    match kind {
        TokenKind::Alt => (4, Assoc::Right),
        TokenKind::Assign
        | TokenKind::PipeAssign
        | TokenKind::PlusAssign
        | TokenKind::MinusAssign
        | TokenKind::StarAssign
        | TokenKind::SlashAssign
        | TokenKind::PercentAssign
        | TokenKind::AltAssign => (5, Assoc::None),
        TokenKind::Or => (6, Assoc::Left),
        TokenKind::And => (7, Assoc::Left),
        TokenKind::Equal | TokenKind::NotEqual | TokenKind::Less | TokenKind::LessEqual | TokenKind::Greater | TokenKind::GreaterEqual => (8, Assoc::None),
        TokenKind::Plus | TokenKind::Minus => (9, Assoc::Left),
        TokenKind::Star | TokenKind::Slash | TokenKind::Percent => (10, Assoc::Left),
        _ => (0, Assoc::Left),
    }
}

pub struct Parser {
    tokens: Vec<Token>,
    // The index of the current token.
    next: usize,
    depth: u32,
    error: Option<SyntaxError>,
}

// The head of a reduce or foreach: what precedes its bodies.
struct Head {
    is_reduce: bool,
    source: Node,
    patterns: Vec<Pattern>,
    position: Position,
}

impl Parser {
    fn new(source: &str) -> Parser {
        Parser { tokens: Lexer::tokenize(source), next: 0, depth: 0, error: None }
    }

    /// Parses a program (spec §3.2, rule `Program`).
    pub fn try_parse(source: &str) -> Result<Program, SyntaxError> {
        Parser::new(source).run(false)
    }

    /// Parses a module file (spec §3.2, rule `ModuleBody`): directives and definitions only.
    pub fn try_parse_module(source: &str) -> Result<Program, SyntaxError> {
        Parser::new(source).run(true)
    }

    fn run(mut self, module: bool) -> Result<Program, SyntaxError> {
        match self.parse_program(module) {
            Some(program) => Ok(program),
            None => Err(self.error.take().unwrap_or_else(|| SyntaxError::new("Syntax error", 1, 1))),
        }
    }

    fn kind(&self) -> TokenKind {
        self.tokens[self.next].kind
    }

    fn peek_kind(&self) -> TokenKind {
        self.tokens[(self.next + 1).min(self.tokens.len() - 1)].kind
    }

    fn text(&self) -> &str {
        &self.tokens[self.next].text
    }

    // The text of the current token, which the parser reads once.
    fn take_text(&mut self) -> String {
        self.tokens[self.next].text.to_string()
    }

    fn advance(&mut self) {
        if self.next < self.tokens.len() - 1 {
            self.next += 1;
        }
    }

    // The position of the current token.
    fn pos(&self) -> Position {
        self.pos_of(self.next)
    }

    fn pos_of(&self, index: usize) -> Position {
        Position::new(self.tokens[index].line, self.tokens[index].column)
    }

    fn is_location(&self) -> bool {
        self.kind() == TokenKind::Variable && self.text() == LOCATION_VARIABLE
    }

    fn is_string_start(kind: TokenKind) -> bool {
        matches!(kind, TokenKind::String | TokenKind::StringStart)
    }

    // Program = ModuleDecl? Import* FuncDef* Pipe?      ModuleBody = ModuleDecl? Import* FuncDef*
    fn parse_program(&mut self, module: bool) -> Option<Program> {
        let mut metadata = None;
        if self.kind() == TokenKind::Module {
            self.advance();
            let value = self.parse_metadata()?;
            self.expect(TokenKind::Semicolon, "';'")?;
            metadata = Some(value);
        }

        let mut imports = Vec::new();
        while matches!(self.kind(), TokenKind::Import | TokenKind::Include) {
            imports.push(self.parse_import()?);
        }

        let mut definitions = Vec::new();
        while self.kind() == TokenKind::Def {
            definitions.push(self.parse_definition()?);
        }

        let mut body = None;
        if module {
            if self.kind() != TokenKind::Eof {
                return self.fail(syntax_messages::MODULE_HAS_BODY.to_owned(), self.next);
            }
        } else {
            let node = if self.kind() == TokenKind::Eof { Node::new(NodeKind::Identity, self.pos()) } else { self.parse_pipe()? };
            if self.kind() != TokenKind::Eof {
                let message = syntax_messages::unexpected(&self.tokens[self.next]);
                return self.fail(message, self.next);
            }
            body = Some(node);
        }

        Some(Program { metadata, imports, definitions, body })
    }

    // Import = 'import' STRING 'as' (IDENT | VAR) ConstObject? ';' | 'include' STRING ConstObject? ';'
    fn parse_import(&mut self) -> Option<Import> {
        let keyword = self.next;
        let is_import = self.kind() == TokenKind::Import;
        self.advance();
        if self.kind() == TokenKind::StringStart {
            return self.fail(syntax_messages::IMPORT_PATH_NOT_CONSTANT.to_owned(), self.next);
        }
        if self.kind() != TokenKind::String {
            let message = syntax_messages::expected("a module path string", &self.tokens[self.next]);
            return self.fail(message, self.next);
        }
        let path = self.take_text();
        self.advance();

        let mut alias = None;
        let mut is_data = false;
        if is_import {
            self.expect(TokenKind::As, "'as'")?;
            if !matches!(self.kind(), TokenKind::Ident | TokenKind::Variable) || self.is_location() {
                let message = syntax_messages::expected("a module name or a $name", &self.tokens[self.next]);
                return self.fail(message, self.next);
            }
            alias = Some(self.take_text());
            is_data = self.kind() == TokenKind::Variable;
            self.advance();
        }

        let mut metadata = None;
        if self.kind() == TokenKind::LeftBrace {
            metadata = Some(self.parse_metadata()?);
        }

        self.expect(TokenKind::Semicolon, "';'")?;
        Some(Import { path, alias, is_data, metadata, position: self.pos_of(keyword) })
    }

    // The metadata of 'module' and of imports: a constant object.
    fn parse_metadata(&mut self) -> Option<JqValue> {
        let start = self.next;
        let value = self.parse_constant(start)?;
        if matches!(value, JqValue::Object(_)) { Some(value) } else { self.fail(syntax_messages::METADATA_NOT_OBJECT.to_owned(), start) }
    }

    // Const = NUMBER | STRING | null | true | false | '[' (Const (',' Const)*)? ']' | '{' (CEntry (',' CEntry)* ','?)? '}'
    // CEntry = (IDENT | Keyword | STRING) ':' Const. Any other token: "Module metadata must be constant" at the metadata.
    fn parse_constant(&mut self, start: usize) -> Option<JqValue> {
        if !stack_guard::has_stack() {
            return stack_guard::on_fresh_stack(|| self.parse_constant(start));
        }
        match self.kind() {
            TokenKind::LeftBracket => self.parse_constant_array(start),
            TokenKind::LeftBrace => self.parse_constant_object(start),
            _ => self.parse_constant_scalar(start),
        }
    }

    fn parse_constant_scalar(&mut self, start: usize) -> Option<JqValue> {
        let token = self.next;
        self.advance();
        let token = &self.tokens[token];
        match token.kind {
            TokenKind::Number => Some(JqValue::Number(token.number)),
            TokenKind::String => Some(JqValue::from_text(&token.text)),
            TokenKind::Ident if token.text == "null" => Some(JqValue::Null),
            TokenKind::Ident if token.text == "true" || token.text == "false" => Some(JqValue::from_bool(token.text == "true")),
            _ => self.fail(syntax_messages::METADATA_NOT_CONSTANT.to_owned(), start),
        }
    }

    fn parse_constant_array(&mut self, start: usize) -> Option<JqValue> {
        self.enter(self.next)?;
        self.advance();
        let mut items = Vec::new();
        while self.kind() != TokenKind::RightBracket {
            if !items.is_empty() {
                self.expect_constant(TokenKind::Comma, start)?;
            }
            items.push(self.parse_constant(start)?);
        }

        self.advance();
        self.depth -= 1;
        Some(JqValue::Array(JqArray::from_owned(items)))
    }

    fn parse_constant_object(&mut self, start: usize) -> Option<JqValue> {
        self.enter(self.next)?;
        self.advance();
        let mut builder = JqObject::empty();
        while self.kind() != TokenKind::RightBrace {
            if !matches!(self.kind(), TokenKind::Ident | TokenKind::String) && !is_keyword(self.kind()) {
                return self.fail(syntax_messages::METADATA_NOT_CONSTANT.to_owned(), start);
            }
            let key = self.take_text();
            self.advance();
            self.expect_constant(TokenKind::Colon, start)?;
            let value = self.parse_constant(start)?;
            builder.insert(Arc::new(key), value);
            if self.kind() == TokenKind::Comma {
                self.advance();
            } else if self.kind() != TokenKind::RightBrace {
                return self.fail(syntax_messages::METADATA_NOT_CONSTANT.to_owned(), start);
            }
        }

        self.advance();
        self.depth -= 1;
        Some(JqValue::Object(builder))
    }

    fn expect_constant(&mut self, kind: TokenKind, start: usize) -> Option<()> {
        if self.kind() != kind {
            return self.fail(syntax_messages::METADATA_NOT_CONSTANT.to_owned(), start);
        }

        self.advance();
        Some(())
    }

    // FuncDef = 'def' IDENT ('(' Param (';' Param)* ')')? ':' Pipe ';'
    fn parse_definition(&mut self) -> Option<Definition> {
        let keyword = self.next;
        self.enter(keyword)?;
        self.advance();
        if self.kind() != TokenKind::Ident {
            let message = syntax_messages::expected("a function name", &self.tokens[self.next]);
            return self.fail(message, self.next);
        }
        let name = self.take_text();
        self.advance();
        let params = self.parse_params()?;
        self.expect(TokenKind::Colon, "':'")?;
        let body = self.parse_pipe()?;
        self.expect(TokenKind::Semicolon, "';'")?;
        self.depth -= 1;
        Some(Definition { name, params, body, position: self.pos_of(keyword) })
    }

    // ('(' Param (';' Param)* ')')?      Param = IDENT | VAR
    fn parse_params(&mut self) -> Option<Vec<Param>> {
        let mut params = Vec::new();
        if self.kind() != TokenKind::LeftParen {
            return Some(params);
        }
        loop {
            self.advance();
            if !matches!(self.kind(), TokenKind::Ident | TokenKind::Variable) || self.is_location() {
                let message = syntax_messages::expected("a parameter name", &self.tokens[self.next]);
                return self.fail(message, self.next);
            }
            params.push(Param { name: self.take_text(), is_variable: self.kind() == TokenKind::Variable, position: self.pos() });
            self.advance();
            if self.kind() != TokenKind::Semicolon {
                break;
            }
        }

        self.expect(TokenKind::RightParen, "')'")?;
        Some(params)
    }

    // Pipe = Item (',' Item)* ('|' Pipe)?, where an Item is
    //   'def' … ';' Pipe  |  'label' VAR '|' Pipe  |  Alt 'as' Patterns '|' Pipe  |  Alt
    // The first three, and '|', take the rest of the pipe. To avoid recursing per element, each of
    // them is kept as a partial node whose last child is a hole, and the partial nodes are
    // completed from the last to the first when the pipe ends.
    fn parse_pipe(&mut self) -> Option<Node> {
        if !stack_guard::has_stack() {
            return stack_guard::on_fresh_stack(|| self.parse_pipe());
        }
        let mut pending: Vec<Node> = Vec::new();
        let mut items: Option<Node> = None;
        let mut comma = self.pos();
        loop {
            let partial;
            if self.kind() == TokenKind::Def {
                partial = self.parse_func_def()?;
            } else if self.kind() == TokenKind::Label {
                partial = self.parse_label()?;
            } else {
                let item = self.parse_expression(false)?;
                if self.kind() == TokenKind::As {
                    partial = self.parse_binding(item)?;
                } else {
                    let list = add_item(items.take(), item, comma);
                    if self.kind() == TokenKind::Comma {
                        comma = self.pos();
                        self.advance();
                        items = Some(list);
                        continue;
                    }

                    if self.kind() != TokenKind::Pipe {
                        return Some(complete_all(pending, list));
                    }
                    pending.push(Node::new(NodeKind::Pipe { left: Box::new(list), right: Box::new(hole()) }, self.pos()));
                    self.advance();
                    continue;
                }
            }

            // A binding takes the rest of the pipe; the items before it are the left side of a comma.
            pending.push(match items.take() {
                None => partial,
                Some(list) => add_item(Some(list), partial, comma),
            });
        }
    }

    // 'def' … ';', as a partial node.
    fn parse_func_def(&mut self) -> Option<Node> {
        let definition = self.parse_definition()?;
        let position = definition.position;
        Some(Node::new(NodeKind::FuncDef { definition: Box::new(definition), rest: Box::new(hole()) }, position))
    }

    // 'label' VAR '|', as a partial node.
    fn parse_label(&mut self) -> Option<Node> {
        let start = self.pos();
        self.advance();
        if self.kind() != TokenKind::Variable {
            let message = syntax_messages::expected("a label name ($name)", &self.tokens[self.next]);
            return self.fail(message, self.next);
        }
        let name = self.take_text();
        self.advance();
        self.expect(TokenKind::Pipe, "'|'")?;
        Some(Node::new(NodeKind::Label { name, body: Box::new(hole()) }, start))
    }

    // Source 'as' Patterns '|', as a partial node.
    fn parse_binding(&mut self, source: Node) -> Option<Node> {
        let keyword = self.pos();
        self.advance();
        let patterns = self.parse_patterns()?;
        self.expect(TokenKind::Pipe, "'|'")?;
        Some(Node::new(NodeKind::Bind { source: Box::new(source), patterns, body: Box::new(hole()) }, keyword))
    }

    // ObjVal = Alt ('|' Alt)*: an object value, which has no top-level ',' and no 'as', 'def' or 'label' (as jq).
    fn parse_object_value(&mut self) -> Option<Node> {
        let mut items = Vec::new();
        let mut bars = Vec::new();
        loop {
            items.push(self.parse_expression(false)?);
            if self.kind() != TokenKind::Pipe {
                break;
            }
            bars.push(self.pos());
            self.advance();
        }

        let mut result = items.pop()?;
        while let Some(item) = items.pop() {
            let bar = bars.pop()?;
            result = Node::new(NodeKind::Pipe { left: Box::new(item), right: Box::new(result) }, bar);
        }
        Some(result)
    }

    // Levels 4 to 13 of the precedence table: binary operators with an operand stack and an
    // operator stack (shunting-yard), so a chain of any length does not recurse; each operand is
    // Unary = '-'* Postfix, Postfix = Term Suffix*. With operand_only, one operand (the Unary of
    // 'try' and 'catch').
    fn parse_expression(&mut self, operand_only: bool) -> Option<Node> {
        if !stack_guard::has_stack() {
            return stack_guard::on_fresh_stack(|| self.parse_expression(operand_only));
        }
        let mut operands: Vec<Node> = Vec::new();
        // The operators, as the indexes of their tokens.
        let mut operators: Vec<usize> = Vec::new();
        loop {
            let first_minus = self.next;
            while self.kind() == TokenKind::Minus {
                self.advance();
            }
            let term_start = self.next;

            let mut operand = match self.kind() {
                TokenKind::LeftParen => self.parse_parenthesized()?,
                TokenKind::LeftBracket => self.parse_array()?,
                TokenKind::LeftBrace => self.parse_object()?,
                TokenKind::String | TokenKind::StringStart => self.parse_string(None)?,
                TokenKind::Reduce | TokenKind::Foreach => self.parse_reduce_or_foreach()?,
                TokenKind::If => self.parse_if()?,
                TokenKind::Try => self.parse_try()?,
                TokenKind::Ident if !is_literal_name(self.text()) => self.parse_call()?,
                _ => self.parse_atom()?,
            };

            while self.is_suffix() {
                operand = if self.is_bracket_suffix() { self.parse_bracket_suffix(operand)? } else { self.parse_suffix(operand)? };
            }
            // The minus tokens from first_minus (included) to term_start (excluded), the last one innermost.
            for i in (first_minus..term_start).rev() {
                operand = Node::new(NodeKind::Negate { operand: Box::new(operand) }, self.pos_of(i));
            }
            if operand_only {
                return Some(operand);
            }

            operands.push(operand);
            if binary_level(self.kind()).0 == 0 {
                while !operators.is_empty() {
                    self.reduce(&mut operands, &mut operators);
                }
                return operands.pop();
            }
            self.push_operator(&mut operands, &mut operators)?;
        }
    }

    // Reduces the operators that bind at least as tightly as the current one, then pushes it.
    fn push_operator(&mut self, operands: &mut Vec<Node>, operators: &mut Vec<usize>) -> Option<()> {
        let op = self.next;
        let (level, assoc) = binary_level(self.kind());
        while let Some(last) = operators.last() {
            let top = binary_level(self.tokens[*last].kind).0;
            if top < level || (top == level && assoc == Assoc::Right) {
                break;
            }
            if top == level && assoc == Assoc::None {
                let message = syntax_messages::not_associative(&self.tokens[op]);
                return self.fail(message, op);
            }

            self.reduce(operands, operators);
        }

        operators.push(op);
        self.advance();
        Some(())
    }

    // Replaces the last two operands with the last operator applied to them.
    fn reduce(&self, operands: &mut Vec<Node>, operators: &mut Vec<usize>) {
        let (Some(op), Some(right), Some(left)) = (operators.pop(), operands.pop(), operands.pop()) else {
            return;
        };
        operands.push(make_binary(self.tokens[op].kind, left, right, self.pos_of(op)));
    }

    fn is_bracket_suffix(&self) -> bool {
        self.kind() == TokenKind::LeftBracket || (self.kind() == TokenKind::Dot && self.peek_kind() == TokenKind::LeftBracket)
    }

    fn is_suffix(&self) -> bool {
        matches!(self.kind(), TokenKind::Question | TokenKind::Field | TokenKind::LeftBracket | TokenKind::QuestionAlt)
            || (self.kind() == TokenKind::Dot && matches!(self.peek_kind(), TokenKind::LeftBracket | TokenKind::String | TokenKind::StringStart))
    }

    // Suffix = '?' | FIELD | '.' String; the bracket suffixes are parse_bracket_suffix. '?//' is an
    // error here: it separates patterns only.
    fn parse_suffix(&mut self, target: Node) -> Option<Node> {
        let at = self.pos();
        match self.kind() {
            TokenKind::Question => {
                self.advance();
                Some(Node::new(NodeKind::Try { body: Box::new(target), handler: None }, at))
            }
            TokenKind::Field => {
                let name = self.take_text();
                self.advance();
                let key = Node::new(NodeKind::Literal(JqValue::from_string(name)), at);
                Some(Node::new(NodeKind::Index { target: Box::new(target), key: Box::new(key) }, at))
            }
            TokenKind::QuestionAlt => {
                let message = syntax_messages::question_alt(&self.tokens[self.next]);
                self.fail(message, self.next)
            }
            _ => {
                self.advance();
                let key = self.parse_string(None)?;
                Some(Node::new(NodeKind::Index { target: Box::new(target), key: Box::new(key) }, at))
            }
        }
    }

    // '.'? '[' ']' | '[' Pipe ']' | '[' Pipe ':' Pipe? ']' | '[' ':' Pipe ']'
    fn parse_bracket_suffix(&mut self, target: Node) -> Option<Node> {
        if self.kind() == TokenKind::Dot {
            self.advance();
        }
        let open = self.pos();
        self.enter(self.next)?;
        self.advance();
        let result;
        if self.kind() == TokenKind::RightBracket {
            result = Node::new(NodeKind::Iterate { target: Box::new(target) }, open);
        } else {
            let mut from = None;
            if self.kind() != TokenKind::Colon {
                from = Some(self.parse_pipe()?);
            }

            if self.kind() == TokenKind::Colon {
                self.advance();
                let mut to = None;
                if from.is_none() || self.kind() != TokenKind::RightBracket {
                    to = Some(self.parse_pipe()?);
                }

                result = Node::new(NodeKind::Slice { target: Box::new(target), from: from.map(Box::new), to: to.map(Box::new) }, open);
            } else {
                result = Node::new(NodeKind::Index { target: Box::new(target), key: Box::new(from?) }, open);
            }
        }

        self.close(TokenKind::RightBracket, "']'", result)
    }

    // '(' Pipe ')'
    fn parse_parenthesized(&mut self) -> Option<Node> {
        self.enter(self.next)?;
        self.advance();
        let inner = self.parse_pipe()?;
        self.close(TokenKind::RightParen, "')'", inner)
    }

    // '[' Pipe? ']'
    fn parse_array(&mut self) -> Option<Node> {
        let open = self.pos();
        self.enter(self.next)?;
        self.advance();
        if self.kind() == TokenKind::RightBracket {
            return self.close(TokenKind::RightBracket, "']'", Node::new(NodeKind::ArrayExpr { body: None }, open));
        }
        let body = self.parse_pipe()?;
        self.close(TokenKind::RightBracket, "']'", Node::new(NodeKind::ArrayExpr { body: Some(Box::new(body)) }, open))
    }

    // '.', '..', '.' String, NUMBER, FORMAT String?, FIELD, VAR, null, true, false, 'break' VAR.
    fn parse_atom(&mut self) -> Option<Node> {
        let at = self.pos();
        match self.kind() {
            TokenKind::Dot => {
                self.advance();
                if Parser::is_string_start(self.kind()) {
                    let key = self.parse_string(None)?;
                    let target = Node::new(NodeKind::Identity, at);
                    return Some(Node::new(NodeKind::Index { target: Box::new(target), key: Box::new(key) }, at));
                }

                Some(Node::new(NodeKind::Identity, at))
            }
            TokenKind::DotDot => {
                self.advance();
                Some(Node::new(NodeKind::Call { name: "recurse".to_owned(), arguments: Vec::new() }, at))
            }
            TokenKind::Number => {
                let number = self.tokens[self.next].number;
                self.advance();
                Some(Node::new(NodeKind::Literal(JqValue::Number(number)), at))
            }
            TokenKind::Format => {
                // Any name parses: an unknown format is a runtime error (spec §9.9).
                let name = self.take_text();
                self.advance();
                if Parser::is_string_start(self.kind()) { self.parse_string(Some(name)) } else { Some(Node::new(NodeKind::Format(name), at)) }
            }
            TokenKind::Field => {
                let name = self.take_text();
                self.advance();
                let target = Node::new(NodeKind::Identity, at);
                let key = Node::new(NodeKind::Literal(JqValue::from_string(name)), at);
                Some(Node::new(NodeKind::Index { target: Box::new(target), key: Box::new(key) }, at))
            }
            TokenKind::Variable => {
                let name = self.take_text();
                self.advance();
                Some(Node::new(NodeKind::Variable(name), at))
            }
            TokenKind::Ident => {
                let value = if self.text() == "null" { JqValue::Null } else { JqValue::from_bool(self.text() == "true") };
                self.advance();
                Some(Node::new(NodeKind::Literal(value), at))
            }
            TokenKind::Break => {
                self.advance();
                if self.kind() != TokenKind::Variable {
                    let message = syntax_messages::expected("a label name ($name)", &self.tokens[self.next]);
                    return self.fail(message, self.next);
                }
                let name = self.take_text();
                self.advance();
                Some(Node::new(NodeKind::Break { name }, at))
            }
            _ => {
                let message = syntax_messages::unexpected(&self.tokens[self.next]);
                self.fail(message, self.next)
            }
        }
    }

    // IDENT ('(' Pipe (';' Pipe)* ')')?
    fn parse_call(&mut self) -> Option<Node> {
        let at = self.pos();
        let name = self.take_text();
        self.advance();
        let mut arguments = Vec::new();
        if self.kind() == TokenKind::LeftParen {
            self.enter(self.next)?;
            loop {
                self.advance();
                arguments.push(self.parse_pipe()?);
                if self.kind() != TokenKind::Semicolon {
                    break;
                }
            }

            self.expect(TokenKind::RightParen, "')'")?;
            self.depth -= 1;
        }

        Some(Node::new(NodeKind::Call { name, arguments }, at))
    }

    // A STRING, or StringStart Pipe (StringMiddle Pipe)* StringEnd.
    fn parse_string(&mut self, format: Option<String>) -> Option<Node> {
        let start = self.pos();
        if self.kind() == TokenKind::String {
            let text = self.take_text();
            self.advance();
            return Some(Node::new(NodeKind::Literal(JqValue::from_string(text)), start));
        }
        self.enter(self.next)?;
        let mut texts = Vec::new();
        let mut parts = Vec::new();
        loop {
            texts.push(self.take_text());
            self.advance();
            parts.push(self.parse_pipe()?);
            if !matches!(self.kind(), TokenKind::StringMiddle | TokenKind::StringEnd) {
                let message = syntax_messages::expected("')'", &self.tokens[self.next]);
                return self.fail(message, self.next);
            }
            if self.kind() != TokenKind::StringMiddle {
                break;
            }
        }

        texts.push(self.take_text());
        self.advance();
        self.depth -= 1;
        Some(Node::new(NodeKind::Template { format, texts, parts }, start))
    }

    // '{' (Entry (',' Entry)* ','?)? '}'
    fn parse_object(&mut self) -> Option<Node> {
        let open = self.pos();
        self.enter(self.next)?;
        self.advance();
        let mut entries = Vec::new();
        while self.kind() != TokenKind::RightBrace {
            let mut entry = self.parse_object_key()?;
            if entry.value.is_none() && self.kind() == TokenKind::Colon {
                self.advance();
                entry.value = Some(self.parse_object_value()?);
            }

            entries.push(entry);
            if self.kind() == TokenKind::Comma {
                self.advance();
            } else if self.kind() != TokenKind::RightBrace {
                let message = syntax_messages::expected("',' or '}'", &self.tokens[self.next]);
                return self.fail(message, self.next);
            }
        }

        self.advance();
        self.depth -= 1;
        Some(Node::new(NodeKind::ObjectExpr { entries }, open))
    }

    // Entry = (IDENT | Keyword | VAR | String | '(' Pipe ')') ':' ObjVal  |  IDENT | Keyword | VAR | String
    // The key of an entry, with no value when a ':' and the value follow. A shorthand is complete:
    // {$x} has its value, {name} and {"s"} have none and no ':' after them.
    fn parse_object_key(&mut self) -> Option<ObjectEntry> {
        let at = self.pos();
        if self.kind() == TokenKind::Variable {
            let name = self.take_text();
            self.advance();
            return Some(if self.kind() == TokenKind::Colon {
                ObjectEntry { key: Node::new(NodeKind::Variable(name), at), value: None, position: at }
            } else {
                ObjectEntry {
                    key: Node::new(NodeKind::Literal(JqValue::from_text(&name)), at),
                    value: Some(Node::new(NodeKind::Variable(name), at)),
                    position: at,
                }
            });
        }

        let parenthesized = self.kind() == TokenKind::LeftParen;
        let key = if parenthesized { self.parse_parenthesized_key()? } else { self.parse_name_key()? };
        if parenthesized && self.kind() != TokenKind::Colon {
            let message = syntax_messages::expected("':'", &self.tokens[self.next]);
            return self.fail(message, self.next);
        }
        Some(ObjectEntry { key, value: None, position: at })
    }

    // A key written as a name or a string (objects and object patterns): IDENT | Keyword | FORMAT? String.
    fn parse_name_key(&mut self) -> Option<Node> {
        let kind = self.kind();
        if kind == TokenKind::Ident || is_keyword(kind) {
            let at = self.pos();
            let name = self.take_text();
            self.advance();
            return Some(Node::new(NodeKind::Literal(JqValue::from_string(name)), at));
        }

        if Parser::is_string_start(kind) {
            return self.parse_string(None);
        }

        if kind == TokenKind::Format && Parser::is_string_start(self.peek_kind()) {
            let name = self.take_text();
            self.advance();
            return self.parse_string(Some(name));
        }

        if matches!(
            kind,
            TokenKind::Comma
                | TokenKind::RightBrace
                | TokenKind::RightBracket
                | TokenKind::RightParen
                | TokenKind::Semicolon
                | TokenKind::Colon
                | TokenKind::Pipe
                | TokenKind::Eof
                | TokenKind::Error
        ) {
            let message = syntax_messages::unexpected(&self.tokens[self.next]);
            return self.fail(message, self.next);
        }
        self.fail(syntax_messages::MAY_NEED_PARENTHESES.to_owned(), self.next)
    }

    // '(' Pipe ')' as a key; a number, boolean or null literal there is a static error (spec §3.2).
    fn parse_parenthesized_key(&mut self) -> Option<Node> {
        self.enter(self.next)?;
        self.advance();
        let key = self.parse_pipe()?;
        self.expect(TokenKind::RightParen, "')'")?;
        self.depth -= 1;
        if let NodeKind::Literal(value) = &key.kind {
            if !matches!(value, JqValue::String(_)) {
                let message = syntax_messages::invalid_object_key(value);
                return self.fail_at(message, key.position);
            }
        }
        Some(key)
    }

    // 'reduce' Alt 'as' Patterns '(' Pipe ';' Pipe ')'   |   'foreach' Alt 'as' Patterns '(' Pipe ';' Pipe (';' Pipe)? ')'
    fn parse_reduce_or_foreach(&mut self) -> Option<Node> {
        self.enter(self.next)?;
        let head = self.parse_reduce_or_foreach_head()?;
        let mut bodies = Vec::new();
        loop {
            if !bodies.is_empty() {
                self.advance();
            }
            bodies.push(self.parse_pipe()?);
            if self.kind() != TokenKind::Semicolon || bodies.len() >= (if head.is_reduce { 2 } else { 3 }) {
                break;
            }
        }

        if bodies.len() < 2 {
            let message = syntax_messages::expected("';'", &self.tokens[self.next]);
            return self.fail(message, self.next);
        }
        let node = complete_reduce_or_foreach(head, bodies)?;
        self.close(TokenKind::RightParen, "')'", node)
    }

    // ('reduce' | 'foreach') Alt 'as' Patterns '('
    fn parse_reduce_or_foreach_head(&mut self) -> Option<Head> {
        let position = self.pos();
        let is_reduce = self.kind() == TokenKind::Reduce;
        self.advance();
        let source = self.parse_expression(false)?;
        self.expect(TokenKind::As, "'as'")?;
        let patterns = self.parse_patterns()?;
        self.expect(TokenKind::LeftParen, "'('")?;
        Some(Head { is_reduce, source, patterns, position })
    }

    // 'if' Pipe 'then' Pipe ('elif' Pipe 'then' Pipe)* ('else' Pipe)? 'end'. Each branch is a partial
    // If whose else is a hole, completed from the last to the first.
    fn parse_if(&mut self) -> Option<Node> {
        self.enter(self.next)?;
        let mut branches = Vec::new();
        loop {
            let keyword = self.pos();
            self.advance();
            let condition = self.parse_pipe()?;
            self.expect(TokenKind::Then, "'then'")?;
            let then = self.parse_pipe()?;
            branches.push(Node::new(NodeKind::If { condition: Box::new(condition), then: Box::new(then), otherwise: Box::new(hole()) }, keyword));
            if self.kind() != TokenKind::Elif {
                break;
            }
        }

        let otherwise = if self.kind() == TokenKind::Else {
            self.advance();
            self.parse_pipe()?
        } else {
            Node::new(NodeKind::Identity, self.pos())
        };

        self.close(TokenKind::End, "'end'", complete_all(branches, otherwise))
    }

    // 'try' Unary ('catch' Unary)?
    fn parse_try(&mut self) -> Option<Node> {
        let keyword = self.pos();
        self.enter(self.next)?;
        self.advance();
        let body = self.parse_expression(true)?;
        let mut handler = None;
        if self.kind() == TokenKind::Catch {
            self.advance();
            handler = Some(Box::new(self.parse_expression(true)?));
        }

        self.depth -= 1;
        Some(Node::new(NodeKind::Try { body: Box::new(body), handler }, keyword))
    }

    // Patterns = Pattern ('?//' Pattern)*
    fn parse_patterns(&mut self) -> Option<Vec<Pattern>> {
        let mut patterns = Vec::new();
        loop {
            patterns.push(self.parse_pattern()?);
            if self.kind() != TokenKind::QuestionAlt {
                return Some(patterns);
            }
            self.advance();
        }
    }

    // Pattern = VAR | '[' Pattern (',' Pattern)* ']' | '{' PEntry (',' PEntry)* '}'
    fn parse_pattern(&mut self) -> Option<Pattern> {
        if !stack_guard::has_stack() {
            return stack_guard::on_fresh_stack(|| self.parse_pattern());
        }
        let at = self.pos();
        match self.kind() {
            TokenKind::Variable if !self.is_location() => {
                let name = self.take_text();
                self.advance();
                Some(Pattern { position: at, kind: PatternKind::Variable(name) })
            }
            TokenKind::LeftBracket => {
                self.enter(self.next)?;
                let mut elements = Vec::new();
                loop {
                    self.advance();
                    elements.push(self.parse_pattern()?);
                    if self.kind() != TokenKind::Comma {
                        break;
                    }
                }

                self.expect(TokenKind::RightBracket, "']'")?;
                self.depth -= 1;
                Some(Pattern { position: at, kind: PatternKind::Array(elements) })
            }
            TokenKind::LeftBrace => {
                self.enter(self.next)?;
                let mut entries = Vec::new();
                loop {
                    self.advance();
                    entries.push(self.parse_pattern_entry()?);
                    if self.kind() != TokenKind::Comma {
                        break;
                    }
                }

                self.expect(TokenKind::RightBrace, "'}'")?;
                self.depth -= 1;
                Some(Pattern { position: at, kind: PatternKind::Object(entries) })
            }
            _ => {
                let message = syntax_messages::expected("a pattern ($name, [...] or {...})", &self.tokens[self.next]);
                self.fail(message, self.next)
            }
        }
    }

    // PEntry = VAR (':' Pattern)? | (IDENT | Keyword | String | '(' Pipe ')') ':' Pattern
    fn parse_pattern_entry(&mut self) -> Option<PatternEntry> {
        let at = self.pos();
        let key;
        let mut variable = None;
        if self.is_location() {
            let message = syntax_messages::unexpected(&self.tokens[self.next]);
            return self.fail(message, self.next);
        }
        if self.kind() == TokenKind::Variable {
            let name = self.take_text();
            self.advance();
            key = Node::new(NodeKind::Literal(JqValue::from_text(&name)), at);
            variable = Some(name);
            if self.kind() != TokenKind::Colon {
                return Some(PatternEntry { key, variable, value: None, position: at });
            }
        } else {
            key = if self.kind() == TokenKind::LeftParen { self.parse_parenthesized_key()? } else { self.parse_name_key()? };
        }

        self.expect(TokenKind::Colon, "':'")?;
        let value = self.parse_pattern()?;
        Some(PatternEntry { key, variable, value: Some(value), position: at })
    }

    // Opens one level of nesting (spec §11).
    fn enter(&mut self, token: usize) -> Option<()> {
        self.depth += 1;
        if self.depth <= MAX_NESTING {
            return Some(());
        }
        self.fail(syntax_messages::TOO_DEEPLY_NESTED.to_owned(), token)
    }

    // Expects the token that closes a nesting level, and leaves the level.
    fn close(&mut self, kind: TokenKind, what: &str, node: Node) -> Option<Node> {
        self.expect(kind, what)?;
        self.depth -= 1;
        Some(node)
    }

    fn expect(&mut self, kind: TokenKind, what: &str) -> Option<()> {
        if self.kind() != kind {
            let message = syntax_messages::expected(what, &self.tokens[self.next]);
            return self.fail(message, self.next);
        }

        self.advance();
        Some(())
    }

    fn fail_at<T>(&mut self, message: String, position: Position) -> Option<T> {
        if self.error.is_none() {
            self.error = Some(SyntaxError::new(message, position.line, position.column));
        }
        None
    }

    // Records the error and returns None. At a lexical error token, the lexer's message wins.
    fn fail<T>(&mut self, message: String, token: usize) -> Option<T> {
        if self.error.is_none() {
            let token = &self.tokens[token];
            let message = if token.kind == TokenKind::Error { token.text.to_string() } else { message };
            self.error = Some(SyntaxError::new(message, token.line, token.column));
        }
        None
    }
}

fn is_literal_name(name: &str) -> bool {
    name == "null" || name == "true" || name == "false"
}

// The placeholder for "the rest of the pipe" in a partial node of parse_pipe.
fn hole() -> Node {
    Node::new(NodeKind::Identity, Position::default())
}

fn add_item(items: Option<Node>, item: Node, comma: Position) -> Node {
    match items {
        None => item,
        Some(items) => Node::new(NodeKind::Comma { left: Box::new(items), right: Box::new(item) }, comma),
    }
}

// The pending partial nodes of parse_pipe completed from the last to the first.
fn complete_all(mut pending: Vec<Node>, mut last: Node) -> Node {
    while let Some(partial) = pending.pop() {
        last = complete(partial, last);
    }
    last
}

// A partial node of parse_pipe with its hole filled.
fn complete(mut partial: Node, rest: Node) -> Node {
    match &mut partial.kind {
        NodeKind::Pipe { right, .. } => **right = rest,
        NodeKind::Comma { right, .. } => {
            let inner = std::mem::replace(&mut **right, hole());
            **right = complete(inner, rest);
        }
        NodeKind::FuncDef { rest: hole, .. } => **hole = rest,
        NodeKind::Label { body, .. } => **body = rest,
        NodeKind::Bind { body, .. } => **body = rest,
        NodeKind::If { otherwise, .. } => **otherwise = rest,
        _ => return rest,
    }
    partial
}

fn complete_reduce_or_foreach(head: Head, mut bodies: Vec<Node>) -> Option<Node> {
    let extract = if bodies.len() > 2 { bodies.pop() } else { None };
    let update = Box::new(bodies.pop()?);
    let init = Box::new(bodies.pop()?);
    let source = Box::new(head.source);
    Some(if head.is_reduce {
        Node::new(NodeKind::Reduce { source, patterns: head.patterns, init, update }, head.position)
    } else {
        let extract = Box::new(extract.unwrap_or_else(|| Node::new(NodeKind::Identity, head.position)));
        Node::new(NodeKind::Foreach { source, patterns: head.patterns, init, update, extract }, head.position)
    })
}

fn make_binary(kind: TokenKind, left: Node, right: Node, at: Position) -> Node {
    let (left, right) = (Box::new(left), Box::new(right));
    let binary = |op: BinaryOp, left: Box<Node>, right: Box<Node>| Node::new(NodeKind::Binary { op, left, right }, at);
    match kind {
        TokenKind::Plus => binary(BinaryOp::Add, left, right),
        TokenKind::Minus => binary(BinaryOp::Subtract, left, right),
        TokenKind::Star => binary(BinaryOp::Multiply, left, right),
        TokenKind::Slash => binary(BinaryOp::Divide, left, right),
        TokenKind::Percent => binary(BinaryOp::Modulo, left, right),
        TokenKind::Equal => binary(BinaryOp::Equal, left, right),
        TokenKind::NotEqual => binary(BinaryOp::NotEqual, left, right),
        TokenKind::Less => binary(BinaryOp::Less, left, right),
        TokenKind::LessEqual => binary(BinaryOp::LessEqual, left, right),
        TokenKind::Greater => binary(BinaryOp::Greater, left, right),
        TokenKind::GreaterEqual => binary(BinaryOp::GreaterEqual, left, right),
        TokenKind::And => Node::new(NodeKind::And { left, right }, at),
        TokenKind::Or => Node::new(NodeKind::Or { left, right }, at),
        TokenKind::Alt => Node::new(NodeKind::Alternative { left, right }, at),
        TokenKind::Assign => Node::new(NodeKind::Call { name: "_assign".to_owned(), arguments: vec![*left, *right] }, at),
        TokenKind::PipeAssign => Node::new(NodeKind::Call { name: "_modify".to_owned(), arguments: vec![*left, *right] }, at),
        _ => make_update(kind, *left, *right, at),
    }
}

// L op= R  =>  R as $v | _modify(L; . op $v)
fn make_update(kind: TokenKind, left: Node, right: Node, at: Position) -> Node {
    let dot = Box::new(Node::new(NodeKind::Identity, at));
    let value = Box::new(Node::new(NodeKind::Variable(UPDATE_VARIABLE.to_owned()), at));
    let binary = |op: BinaryOp, left: Box<Node>, right: Box<Node>| Node::new(NodeKind::Binary { op, left, right }, at);
    let update = match kind {
        TokenKind::PlusAssign => binary(BinaryOp::Add, dot, value),
        TokenKind::MinusAssign => binary(BinaryOp::Subtract, dot, value),
        TokenKind::StarAssign => binary(BinaryOp::Multiply, dot, value),
        TokenKind::SlashAssign => binary(BinaryOp::Divide, dot, value),
        TokenKind::PercentAssign => binary(BinaryOp::Modulo, dot, value),
        _ => Node::new(NodeKind::Alternative { left: dot, right: value }, at),
    };
    let modify = Node::new(NodeKind::Call { name: "_modify".to_owned(), arguments: vec![left, update] }, at);
    let pattern = Pattern { position: at, kind: PatternKind::Variable(UPDATE_VARIABLE.to_owned()) };
    Node::new(NodeKind::Bind { source: Box::new(right), patterns: vec![pattern], body: Box::new(modify) }, at)
}
