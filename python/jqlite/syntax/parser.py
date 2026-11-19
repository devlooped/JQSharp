"""Recursive descent parser of jqlite (Parser.cs; spec §3) over the tokens of the lexer.

It never raises for bad input: it returns the Program or one SyntaxError.

Rewritten by the parser (spec §3.3), so the AST has one form for each:

- ``..`` is the call ``recurse``;
- ``E?`` and ``try E`` are Try without a handler;
- ``.name``, ``."s"``, ``T.name``, ``T."s"`` are Index with a literal or template key;
- ``elif`` nests If; a missing ``else`` is ``.``; a ``foreach`` without extract has ``.``;
- ``{$x}`` is ``{"x": $x}``; the string shorthands keep a None value;
- ``L = R`` is ``_assign(L; R)``, ``L |= R`` is ``_modify(L; R)``, ``L op= R`` is
  ``R as $v | _modify(L; . op $v)`` (``//=`` likewise with ``//``), ``$v`` being UPDATE_VARIABLE;
- a string or ``@f "s"`` without interpolation is a Literal; parentheses leave no node.

Where jq 1.8.2 parses differently from the EBNF of spec §3.2, this parser follows jq (see
Parser.cs). Nesting is limited to MAX_NESTING; sequences do not recurse per element: pipes,
commas, bindings and leading definitions are folded from a list, binary operators are parsed with
an operator stack, and runs of unary minus and of ``elif`` are loops.
"""

from __future__ import annotations

from typing import Any

from ..values.jq_array import JqArray
from ..values.jq_object import JqObject
from ..values.jq_object_builder import JqObjectBuilder
from . import recursion_limit
from .ast import (
    Alternative,
    And,
    ArrayExpr,
    ArrayPattern,
    Binary,
    BinaryOp,
    Bind,
    Break,
    Call,
    Comma,
    Definition,
    Foreach,
    Format,
    FuncDef,
    Identity,
    If,
    Import,
    Index,
    Iterate,
    Label,
    Literal,
    Negate,
    Node,
    NodeKind,
    ObjectEntry,
    ObjectExpr,
    ObjectPattern,
    Or,
    Param,
    Pattern,
    PatternEntry,
    Pipe,
    Position,
    Program,
    Reduce,
    Slice,
    Template,
    Try,
    Variable,
    VariablePattern,
)
from .lexer import Lexer
from .syntax_error import SyntaxError, SyntaxMessages
from .token import Token, TokenKind, is_keyword

# The nesting limit of spec §11.
MAX_NESTING = 1000

# The Python frames that parsing or binding a program nested MAX_NESTING deep may use (Python
# only): about 4 per level of nesting are measured, the rest is margin. The parser and the
# resolver raise the recursion limit by this much while they run (recursion_limit.py).
PARSE_FRAMES = 8 * MAX_NESTING

# The variable that the rewriting of `L op= R` binds; '*' cannot appear in a source name.
UPDATE_VARIABLE = "*update"

# `$__loc__`, the location object of §7.5: the resolver replaces it; no pattern, parameter or import binds it.
LOCATION_VARIABLE = "__loc__"

_LEFT = 0
_RIGHT = 1
_NONE = 2

# The binary operators (spec §3.1, levels 4 to 10) with their level, higher binding tighter, and
# associativity; level 0 for any other token.
_BINARY_LEVELS: dict[int, tuple[int, int]] = {
    TokenKind.ALT: (4, _RIGHT),
    TokenKind.ASSIGN: (5, _NONE), TokenKind.PIPE_ASSIGN: (5, _NONE), TokenKind.PLUS_ASSIGN: (5, _NONE),
    TokenKind.MINUS_ASSIGN: (5, _NONE), TokenKind.STAR_ASSIGN: (5, _NONE), TokenKind.SLASH_ASSIGN: (5, _NONE),
    TokenKind.PERCENT_ASSIGN: (5, _NONE), TokenKind.ALT_ASSIGN: (5, _NONE),
    TokenKind.OR: (6, _LEFT),
    TokenKind.AND: (7, _LEFT),
    TokenKind.EQUAL: (8, _NONE), TokenKind.NOT_EQUAL: (8, _NONE), TokenKind.LESS: (8, _NONE),
    TokenKind.LESS_EQUAL: (8, _NONE), TokenKind.GREATER: (8, _NONE), TokenKind.GREATER_EQUAL: (8, _NONE),
    TokenKind.PLUS: (9, _LEFT), TokenKind.MINUS: (9, _LEFT),
    TokenKind.STAR: (10, _LEFT), TokenKind.SLASH: (10, _LEFT), TokenKind.PERCENT: (10, _LEFT),
}
_NOT_BINARY = (0, _LEFT)

_BINARY_OPS = {
    TokenKind.PLUS: BinaryOp.ADD, TokenKind.MINUS: BinaryOp.SUBTRACT, TokenKind.STAR: BinaryOp.MULTIPLY,
    TokenKind.SLASH: BinaryOp.DIVIDE, TokenKind.PERCENT: BinaryOp.MODULO, TokenKind.EQUAL: BinaryOp.EQUAL,
    TokenKind.NOT_EQUAL: BinaryOp.NOT_EQUAL, TokenKind.LESS: BinaryOp.LESS, TokenKind.LESS_EQUAL: BinaryOp.LESS_EQUAL,
    TokenKind.GREATER: BinaryOp.GREATER, TokenKind.GREATER_EQUAL: BinaryOp.GREATER_EQUAL,
}

_UPDATE_OPS = {
    TokenKind.PLUS_ASSIGN: BinaryOp.ADD, TokenKind.MINUS_ASSIGN: BinaryOp.SUBTRACT,
    TokenKind.STAR_ASSIGN: BinaryOp.MULTIPLY, TokenKind.SLASH_ASSIGN: BinaryOp.DIVIDE,
    TokenKind.PERCENT_ASSIGN: BinaryOp.MODULO,
}

_LITERAL_NAMES = {"null": None, "true": True, "false": False}

_STRING_KINDS = (TokenKind.STRING, TokenKind.STRING_START)

_NAME_KEY_END = frozenset((
    TokenKind.COMMA, TokenKind.RIGHT_BRACE, TokenKind.RIGHT_BRACKET, TokenKind.RIGHT_PAREN, TokenKind.SEMICOLON,
    TokenKind.COLON, TokenKind.PIPE, TokenKind.EOF, TokenKind.ERROR,
))

# The placeholder for "the rest of the pipe" in a partial node of _parse_pipe.
_HOLE = Identity(Position(0, 0))


def _pos(token: Token) -> Position:
    return Position(token.line, token.column)


def _is_location(token: Token) -> bool:
    return token.kind == TokenKind.VARIABLE and token.text == LOCATION_VARIABLE


class Parser:
    __slots__ = ("tokens", "current", "next", "depth", "error")

    def __init__(self, source: str) -> None:
        self.tokens = Lexer.tokenize(source)
        # The token at next, kept in a field: reading it is the commonest operation of the parser.
        self.current = self.tokens[0]
        self.next = 0
        self.depth = 0
        self.error: SyntaxError | None = None

    @staticmethod
    def try_parse(source: str) -> Program | SyntaxError:
        """Parses a program (spec §3.2, rule ``Program``)."""
        with recursion_limit.raised(PARSE_FRAMES):
            return Parser(source)._run(False)

    @staticmethod
    def try_parse_module(source: str) -> Program | SyntaxError:
        """Parses a module file (spec §3.2, rule ``ModuleBody``): directives and definitions only."""
        with recursion_limit.raised(PARSE_FRAMES):
            return Parser(source)._run(True)

    def _run(self, module: bool) -> Program | SyntaxError:
        program = self._parse_program(module)
        if program is None:
            assert self.error is not None
            return self.error
        return program

    def _peek(self) -> Token:
        tokens = self.tokens
        return tokens[min(self.next + 1, len(tokens) - 1)]

    def _advance(self) -> None:
        if self.next < len(self.tokens) - 1:
            self.next += 1
            self.current = self.tokens[self.next]

    # Program = ModuleDecl? Import* FuncDef* Pipe?      ModuleBody = ModuleDecl? Import* FuncDef*
    def _parse_program(self, module: bool) -> Program | None:
        metadata = None
        if self.current.kind == TokenKind.MODULE:
            self._advance()
            ok, value = self._parse_metadata()
            if not ok or not self._expect(TokenKind.SEMICOLON, "';'"):
                return None
            metadata = value

        imports: list[Import] = []
        while self.current.kind == TokenKind.IMPORT or self.current.kind == TokenKind.INCLUDE:
            imp = self._parse_import()
            if imp is None:
                return None
            imports.append(imp)

        definitions: list[Definition] = []
        while self.current.kind == TokenKind.DEF:
            definition = self._parse_definition()
            if definition is None:
                return None
            definitions.append(definition)

        body: Node | None = None
        if module:
            if self.current.kind != TokenKind.EOF:
                return self._fail(SyntaxMessages.MODULE_HAS_BODY, self.current)
        else:
            body = Identity(_pos(self.current)) if self.current.kind == TokenKind.EOF else self._parse_pipe()
            if body is None:
                return None
            if self.current.kind != TokenKind.EOF:
                return self._fail(SyntaxMessages.unexpected(self.current), self.current)
        return Program(metadata, imports, definitions, body)

    # Import = 'import' STRING 'as' (IDENT | VAR) ConstObject? ';' | 'include' STRING ConstObject? ';'
    def _parse_import(self) -> Import | None:
        keyword = self.current
        self._advance()
        if self.current.kind == TokenKind.STRING_START:
            return self._fail(SyntaxMessages.IMPORT_PATH_NOT_CONSTANT, self.current)
        if self.current.kind != TokenKind.STRING:
            return self._fail(SyntaxMessages.expected("a module path string", self.current), self.current)
        path = self.current.text
        self._advance()

        alias: str | None = None
        is_data = False
        if keyword.kind == TokenKind.IMPORT:
            if not self._expect(TokenKind.AS, "'as'"):
                return None
            if self.current.kind not in (TokenKind.IDENT, TokenKind.VARIABLE) or _is_location(self.current):
                return self._fail(SyntaxMessages.expected("a module name or a $name", self.current), self.current)
            alias = self.current.text
            is_data = self.current.kind == TokenKind.VARIABLE
            self._advance()

        metadata = None
        if self.current.kind == TokenKind.LEFT_BRACE:
            ok, metadata = self._parse_metadata()
            if not ok:
                return None

        if not self._expect(TokenKind.SEMICOLON, "';'"):
            return None
        return Import(path, alias, is_data, metadata, _pos(keyword))

    def _parse_metadata(self) -> tuple[bool, Any]:
        # The metadata of 'module' and of imports: a constant object.
        start = self.current
        ok, value = self._parse_constant(start)
        if not ok:
            return False, None
        if type(value) is JqObject:
            return True, value
        self._fail(SyntaxMessages.METADATA_NOT_OBJECT, start)
        return False, None

    # Const = NUMBER | STRING | null | true | false | '[' (Const (',' Const)*)? ']' | '{' (CEntry (',' CEntry)* ','?)? '}'
    # CEntry = (IDENT | Keyword | STRING) ':' Const. Any other token: "Module metadata must be constant" at the metadata.
    def _parse_constant(self, start: Token) -> tuple[bool, Any]:
        k = self.current.kind
        if k == TokenKind.LEFT_BRACKET:
            return self._parse_constant_array(start)
        if k == TokenKind.LEFT_BRACE:
            return self._parse_constant_object(start)
        return self._parse_constant_scalar(start)

    def _parse_constant_scalar(self, start: Token) -> tuple[bool, Any]:
        token = self.current
        self._advance()
        if token.kind == TokenKind.NUMBER:
            return True, token.number
        if token.kind == TokenKind.STRING:
            return True, token.text
        if token.kind == TokenKind.IDENT and token.text in _LITERAL_NAMES:
            return True, _LITERAL_NAMES[token.text]
        self._fail(SyntaxMessages.METADATA_NOT_CONSTANT, start)
        return False, None

    def _parse_constant_array(self, start: Token) -> tuple[bool, Any]:
        if not self._enter(self.current):
            return False, None
        self._advance()
        items: list = []
        while self.current.kind != TokenKind.RIGHT_BRACKET:
            if items and not self._expect_constant(TokenKind.COMMA, start):
                return False, None
            ok, item = self._parse_constant(start)
            if not ok:
                return False, None
            items.append(item)
        self._advance()
        self.depth -= 1
        return True, JqArray.from_owned(items)

    def _parse_constant_object(self, start: Token) -> tuple[bool, Any]:
        if not self._enter(self.current):
            return False, None
        self._advance()
        builder = JqObjectBuilder()
        while self.current.kind != TokenKind.RIGHT_BRACE:
            key = self.current
            if key.kind not in (TokenKind.IDENT, TokenKind.STRING) and not is_keyword(key.kind):
                self._fail(SyntaxMessages.METADATA_NOT_CONSTANT, start)
                return False, None
            self._advance()
            if not self._expect_constant(TokenKind.COLON, start):
                return False, None
            ok, value = self._parse_constant(start)
            if not ok:
                return False, None
            builder.set(key.text, value)  # type: ignore[arg-type]
            if self.current.kind == TokenKind.COMMA:
                self._advance()
            elif self.current.kind != TokenKind.RIGHT_BRACE:
                self._fail(SyntaxMessages.METADATA_NOT_CONSTANT, start)
                return False, None
        self._advance()
        self.depth -= 1
        return True, builder.to_object()

    def _expect_constant(self, kind: int, start: Token) -> bool:
        if self.current.kind != kind:
            self._fail(SyntaxMessages.METADATA_NOT_CONSTANT, start)
            return False
        self._advance()
        return True

    # FuncDef = 'def' IDENT ('(' Param (';' Param)* ')')? ':' Pipe ';'
    def _parse_definition(self) -> Definition | None:
        keyword = self.current
        if not self._enter(keyword):
            return None
        self._advance()
        if self.current.kind != TokenKind.IDENT:
            return self._fail(SyntaxMessages.expected("a function name", self.current), self.current)
        name = self.current.text
        self._advance()
        parameters = self._parse_params()
        if parameters is None or not self._expect(TokenKind.COLON, "':'"):
            return None
        body = self._parse_pipe()
        if body is None or not self._expect(TokenKind.SEMICOLON, "';'"):
            return None
        self.depth -= 1
        return Definition(name, parameters, body, _pos(keyword))

    # ('(' Param (';' Param)* ')')?      Param = IDENT | VAR
    def _parse_params(self) -> list[Param] | None:
        parameters: list[Param] = []
        if self.current.kind != TokenKind.LEFT_PAREN:
            return parameters
        while True:
            self._advance()
            current = self.current
            if current.kind not in (TokenKind.IDENT, TokenKind.VARIABLE) or _is_location(current):
                return self._fail(SyntaxMessages.expected("a parameter name", current), current)
            parameters.append(Param(current.text, current.kind == TokenKind.VARIABLE, _pos(current)))
            self._advance()
            if self.current.kind != TokenKind.SEMICOLON:
                break
        return parameters if self._expect(TokenKind.RIGHT_PAREN, "')'") else None

    # Pipe = Item (',' Item)* ('|' Pipe)?, where an Item is
    #   'def' ... ';' Pipe  |  'label' VAR '|' Pipe  |  Alt 'as' Patterns '|' Pipe  |  Alt
    # The first three, and '|', take the rest of the pipe. To avoid recursing per element, each of
    # them is kept as a partial node whose last child is the hole, and the partial nodes are
    # completed from the last to the first when the pipe ends.
    def _parse_pipe(self) -> Node | None:
        pending: list[Node] = []
        items: Node | None = None
        comma = self.current
        while True:
            kind = self.current.kind
            if kind == TokenKind.DEF:
                partial = self._parse_func_def()
            elif kind == TokenKind.LABEL:
                partial = self._parse_label()
            else:
                item = self._parse_expression(False)
                if item is None:
                    return None
                if self.current.kind == TokenKind.AS:
                    partial = self._parse_binding(item)
                else:
                    items = item if items is None else Comma(items, item, _pos(comma))
                    if self.current.kind == TokenKind.COMMA:
                        comma = self.current
                        self._advance()
                        continue
                    if self.current.kind != TokenKind.PIPE:
                        break
                    pending.append(Pipe(items, _HOLE, _pos(self.current)))
                    self._advance()
                    items = None
                    continue
            if partial is None:
                return None
            # A binding takes the rest of the pipe; the items before it are the left side of a comma.
            pending.append(partial if items is None else Comma(items, partial, _pos(comma)))
            items = None
        last: Node = items  # type: ignore[assignment]
        for i in range(len(pending) - 1, -1, -1):
            last = _complete(pending[i], last)
        return last

    def _parse_func_def(self) -> Node | None:
        # 'def' ... ';', as a partial node.
        definition = self._parse_definition()
        return None if definition is None else FuncDef(definition, _HOLE, definition.position)

    def _parse_label(self) -> Node | None:
        # 'label' VAR '|', as a partial node.
        start = self.current
        self._advance()
        if self.current.kind != TokenKind.VARIABLE:
            return self._fail(SyntaxMessages.expected("a label name ($name)", self.current), self.current)
        name = self.current.text
        self._advance()
        return Label(name, _HOLE, _pos(start)) if self._expect(TokenKind.PIPE, "'|'") else None

    def _parse_binding(self, source: Node) -> Node | None:
        # Source 'as' Patterns '|', as a partial node.
        keyword = self.current
        self._advance()
        patterns = self._parse_patterns()
        if patterns is None or not self._expect(TokenKind.PIPE, "'|'"):
            return None
        return Bind(source, patterns, _HOLE, _pos(keyword))

    def _parse_object_value(self) -> Node | None:
        # ObjVal = Alt ('|' Alt)*: an object value, which has no top-level ',' and no 'as', 'def' or 'label' (as jq).
        items: list[Node] = []
        bars: list[Token] = []
        while True:
            item = self._parse_expression(False)
            if item is None:
                return None
            items.append(item)
            if self.current.kind != TokenKind.PIPE:
                break
            bars.append(self.current)
            self._advance()
        result = items[-1]
        for i in range(len(items) - 2, -1, -1):
            result = Pipe(items[i], result, _pos(bars[i]))
        return result

    # Levels 4 to 13 of the precedence table: binary operators with an operand stack and an
    # operator stack (shunting-yard), so a chain of any length does not recurse; each operand is
    # Unary = '-'* Postfix, Postfix = Term Suffix*. With operand_only, one operand (the Unary of
    # 'try' and 'catch').
    def _parse_expression(self, operand_only: bool) -> Node | None:
        operands: list[Node] = []
        operators: list[Token] = []
        while True:
            first_minus = self.next
            while self.current.kind == TokenKind.MINUS:
                self._advance()
            term_start = self.next

            kind = self.current.kind
            operand: Node | None
            if kind == TokenKind.LEFT_PAREN:
                operand = self._parse_parenthesized()
            elif kind == TokenKind.LEFT_BRACKET:
                operand = self._parse_array()
            elif kind == TokenKind.LEFT_BRACE:
                operand = self._parse_object()
            elif kind == TokenKind.STRING or kind == TokenKind.STRING_START:
                operand = self._parse_string(None)
            elif kind == TokenKind.REDUCE or kind == TokenKind.FOREACH:
                operand = self._parse_reduce_or_foreach()
            elif kind == TokenKind.IF:
                operand = self._parse_if()
            elif kind == TokenKind.TRY:
                operand = self._parse_try()
            elif kind == TokenKind.IDENT and self.current.text not in _LITERAL_NAMES:
                operand = self._parse_call()
            else:
                operand = self._parse_atom()

            while operand is not None and self._is_suffix():
                operand = self._parse_bracket_suffix(operand) if self._is_bracket_suffix() else self._parse_suffix(operand)
            if operand is None:
                return None
            # The minus tokens apply to the operand, the last one innermost.
            for i in range(term_start - 1, first_minus - 1, -1):
                operand = Negate(operand, _pos(self.tokens[i]))
            if operand_only:
                return operand

            operands.append(operand)
            if _BINARY_LEVELS.get(self.current.kind, _NOT_BINARY)[0] == 0:
                while operators:
                    _reduce(operands, operators)
                return operands[0]
            if not self._push_operator(operands, operators):
                return None

    def _push_operator(self, operands: list[Node], operators: list[Token]) -> bool:
        # Reduces the operators that bind at least as tightly as the current one, then pushes it.
        op = self.current
        level, assoc = _BINARY_LEVELS[op.kind]
        while operators:
            top = _BINARY_LEVELS[operators[-1].kind][0]
            if top < level or (top == level and assoc == _RIGHT):
                break
            if top == level and assoc == _NONE:
                self._fail(SyntaxMessages.not_associative(op), op)
                return False
            _reduce(operands, operators)
        operators.append(op)
        self._advance()
        return True

    def _is_bracket_suffix(self) -> bool:
        k = self.current.kind
        return k == TokenKind.LEFT_BRACKET or (k == TokenKind.DOT and self._peek().kind == TokenKind.LEFT_BRACKET)

    def _is_suffix(self) -> bool:
        k = self.current.kind
        if k in (TokenKind.QUESTION, TokenKind.FIELD, TokenKind.LEFT_BRACKET, TokenKind.QUESTION_ALT):
            return True
        return k == TokenKind.DOT and self._peek().kind in (TokenKind.LEFT_BRACKET, TokenKind.STRING, TokenKind.STRING_START)

    def _parse_suffix(self, target: Node) -> Node | None:
        # Suffix = '?' | FIELD | '.' String; the bracket suffixes are _parse_bracket_suffix. '?//'
        # is an error here: it separates patterns only.
        token = self.current
        if token.kind == TokenKind.QUESTION:
            self._advance()
            return Try(target, None, _pos(token))
        if token.kind == TokenKind.FIELD:
            self._advance()
            return Index(target, Literal(token.text, _pos(token)), _pos(token))
        if token.kind == TokenKind.QUESTION_ALT:
            return self._fail(SyntaxMessages.question_alt(token), token)
        self._advance()
        key = self._parse_string(None)
        return None if key is None else Index(target, key, _pos(token))

    def _parse_bracket_suffix(self, target: Node) -> Node | None:
        # '.'? '[' ']' | '[' Pipe ']' | '[' Pipe ':' Pipe? ']' | '[' ':' Pipe ']'
        if self.current.kind == TokenKind.DOT:
            self._advance()
        open_ = self.current
        if not self._enter(open_):
            return None
        self._advance()
        result: Node
        if self.current.kind == TokenKind.RIGHT_BRACKET:
            result = Iterate(target, _pos(open_))
        else:
            frm: Node | None = None
            if self.current.kind != TokenKind.COLON:
                frm = self._parse_pipe()
                if frm is None:
                    return None
            if self.current.kind == TokenKind.COLON:
                self._advance()
                to: Node | None = None
                if frm is None or self.current.kind != TokenKind.RIGHT_BRACKET:
                    to = self._parse_pipe()
                    if to is None:
                        return None
                result = Slice(target, frm, to, _pos(open_))
            else:
                result = Index(target, frm, _pos(open_))  # type: ignore[arg-type]
        return self._close(TokenKind.RIGHT_BRACKET, "']'", result)

    def _parse_parenthesized(self) -> Node | None:
        # '(' Pipe ')'
        if not self._enter(self.current):
            return None
        self._advance()
        inner = self._parse_pipe()
        return None if inner is None else self._close(TokenKind.RIGHT_PAREN, "')'", inner)

    def _parse_array(self) -> Node | None:
        # '[' Pipe? ']'
        open_ = self.current
        if not self._enter(open_):
            return None
        self._advance()
        if self.current.kind == TokenKind.RIGHT_BRACKET:
            return self._close(TokenKind.RIGHT_BRACKET, "']'", ArrayExpr(None, _pos(open_)))
        body = self._parse_pipe()
        return None if body is None else self._close(TokenKind.RIGHT_BRACKET, "']'", ArrayExpr(body, _pos(open_)))

    def _parse_atom(self) -> Node | None:
        # '.', '..', '.' String, NUMBER, FORMAT String?, FIELD, VAR, null, true, false, 'break' VAR.
        token = self.current
        at = _pos(token)
        k = token.kind
        if k == TokenKind.DOT:
            self._advance()
            if self.current.kind in _STRING_KINDS:
                key = self._parse_string(None)
                return None if key is None else Index(Identity(at), key, at)
            return Identity(at)
        if k == TokenKind.DOT_DOT:
            self._advance()
            return Call("recurse", [], at)
        if k == TokenKind.NUMBER:
            self._advance()
            return Literal(token.number, at)
        if k == TokenKind.FORMAT:
            # Any name parses: an unknown format is a runtime error (spec §9.9).
            self._advance()
            return self._parse_string(token.text) if self.current.kind in _STRING_KINDS else Format(token.text, at)
        if k == TokenKind.FIELD:
            self._advance()
            return Index(Identity(at), Literal(token.text, at), at)
        if k == TokenKind.VARIABLE:
            self._advance()
            return Variable(token.text, at)
        if k == TokenKind.IDENT:
            self._advance()
            return Literal(_LITERAL_NAMES[token.text], at)
        if k == TokenKind.BREAK:
            self._advance()
            if self.current.kind != TokenKind.VARIABLE:
                return self._fail(SyntaxMessages.expected("a label name ($name)", self.current), self.current)
            label = self.current.text
            self._advance()
            return Break(label, at)
        return self._fail(SyntaxMessages.unexpected(token), token)

    def _parse_call(self) -> Node | None:
        # IDENT ('(' Pipe (';' Pipe)* ')')?
        name = self.current
        self._advance()
        arguments: list[Node] = []
        if self.current.kind == TokenKind.LEFT_PAREN:
            if not self._enter(self.current):
                return None
            while True:
                self._advance()
                argument = self._parse_pipe()
                if argument is None:
                    return None
                arguments.append(argument)
                if self.current.kind != TokenKind.SEMICOLON:
                    break
            if not self._expect(TokenKind.RIGHT_PAREN, "')'"):
                return None
            self.depth -= 1
        return Call(name.text, arguments, _pos(name))

    def _parse_string(self, format: str | None) -> Node | None:
        # A STRING, or STRING_START Pipe (STRING_MIDDLE Pipe)* STRING_END.
        start = self.current
        if start.kind == TokenKind.STRING:
            self._advance()
            return Literal(start.text, _pos(start))
        if not self._enter(start):
            return None
        texts: list[str] = []
        parts: list[Node] = []
        while True:
            texts.append(self.current.text)
            self._advance()
            part = self._parse_pipe()
            if part is None:
                return None
            parts.append(part)
            if self.current.kind not in (TokenKind.STRING_MIDDLE, TokenKind.STRING_END):
                return self._fail(SyntaxMessages.expected("')'", self.current), self.current)
            if self.current.kind != TokenKind.STRING_MIDDLE:
                break
        texts.append(self.current.text)
        self._advance()
        self.depth -= 1
        return Template(format, texts, parts, _pos(start))

    def _parse_object(self) -> Node | None:
        # '{' (Entry (',' Entry)* ','?)? '}'
        open_ = self.current
        if not self._enter(open_):
            return None
        self._advance()
        entries: list[ObjectEntry] = []
        while self.current.kind != TokenKind.RIGHT_BRACE:
            entry = self._parse_object_key()
            if entry is not None and entry.value is None and self.current.kind == TokenKind.COLON:
                self._advance()
                value = self._parse_object_value()
                entry = None if value is None else ObjectEntry(entry.key, value, entry.position)
            if entry is None:
                return None
            entries.append(entry)
            if self.current.kind == TokenKind.COMMA:
                self._advance()
            elif self.current.kind != TokenKind.RIGHT_BRACE:
                return self._fail(SyntaxMessages.expected("',' or '}'", self.current), self.current)
        self._advance()
        self.depth -= 1
        return ObjectExpr(entries, _pos(open_))

    def _parse_object_key(self) -> ObjectEntry | None:
        # Entry = (IDENT | Keyword | VAR | String | '(' Pipe ')') ':' ObjVal  |  IDENT | Keyword | VAR | String
        # The key of an entry, with a None value when a ':' and the value follow. A shorthand is
        # complete: {$x} has its value, {name} and {"s"} have none and no ':' after them.
        token = self.current
        at = _pos(token)
        if token.kind == TokenKind.VARIABLE:
            self._advance()
            if self.current.kind == TokenKind.COLON:
                return ObjectEntry(Variable(token.text, at), None, at)
            return ObjectEntry(Literal(token.text, at), Variable(token.text, at), at)
        key = self._parse_parenthesized_key() if token.kind == TokenKind.LEFT_PAREN else self._parse_name_key()
        if key is None:
            return None
        if token.kind == TokenKind.LEFT_PAREN and self.current.kind != TokenKind.COLON:
            return self._fail(SyntaxMessages.expected("':'", self.current), self.current)
        return ObjectEntry(key, None, at)

    def _parse_name_key(self) -> Node | None:
        # A key written as a name or a string (objects and object patterns): IDENT | Keyword | FORMAT? String.
        token = self.current
        if token.kind == TokenKind.IDENT or is_keyword(token.kind):
            self._advance()
            return Literal(token.text, _pos(token))
        if token.kind in _STRING_KINDS:
            return self._parse_string(None)
        if token.kind == TokenKind.FORMAT and self._peek().kind in _STRING_KINDS:
            self._advance()
            return self._parse_string(token.text)
        if token.kind in _NAME_KEY_END:
            return self._fail(SyntaxMessages.unexpected(token), token)
        return self._fail(SyntaxMessages.MAY_NEED_PARENTHESES, token)

    def _parse_parenthesized_key(self) -> Node | None:
        # '(' Pipe ')' as a key; a number, boolean or null literal there is a static error (spec §3.2).
        open_ = self.current
        if not self._enter(open_):
            return None
        self._advance()
        key = self._parse_pipe()
        if key is None or not self._expect(TokenKind.RIGHT_PAREN, "')'"):
            return None
        self.depth -= 1
        if key.kind == NodeKind.LITERAL and type(key.value) is not str:  # type: ignore[attr-defined]
            return self._fail_at(SyntaxMessages.invalid_object_key(key.value), key.position)  # type: ignore[attr-defined]
        return key

    def _parse_reduce_or_foreach(self) -> Node | None:
        # 'reduce' Alt 'as' Patterns '(' Pipe ';' Pipe ')'   |   'foreach' Alt 'as' Patterns '(' Pipe ';' Pipe (';' Pipe)? ')'
        if not self._enter(self.current):
            return None
        head = self._parse_reduce_or_foreach_head()
        if head is None:
            return None
        limit = 2 if head.kind == NodeKind.REDUCE else 3
        bodies: list[Node] = []
        while True:
            if bodies:
                self._advance()
            body = self._parse_pipe()
            if body is None:
                return None
            bodies.append(body)
            if not (self.current.kind == TokenKind.SEMICOLON and len(bodies) < limit):
                break
        if len(bodies) < 2:
            return self._fail(SyntaxMessages.expected("';'", self.current), self.current)
        if head.kind == NodeKind.REDUCE:
            head.init = bodies[0]  # type: ignore[attr-defined]
            head.update = bodies[1]  # type: ignore[attr-defined]
        else:
            head.init = bodies[0]  # type: ignore[attr-defined]
            head.update = bodies[1]  # type: ignore[attr-defined]
            head.extract = bodies[2] if len(bodies) > 2 else Identity(head.position)  # type: ignore[attr-defined]
        return self._close(TokenKind.RIGHT_PAREN, "')'", head)

    def _parse_reduce_or_foreach_head(self) -> Node | None:
        # ('reduce' | 'foreach') Alt 'as' Patterns '(', as a node whose bodies are the hole.
        keyword = self.current
        self._advance()
        source = self._parse_expression(False)
        if source is None or not self._expect(TokenKind.AS, "'as'"):
            return None
        patterns = self._parse_patterns()
        if patterns is None or not self._expect(TokenKind.LEFT_PAREN, "'('"):
            return None
        if keyword.kind == TokenKind.REDUCE:
            return Reduce(source, patterns, _HOLE, _HOLE, _pos(keyword))
        return Foreach(source, patterns, _HOLE, _HOLE, _HOLE, _pos(keyword))

    def _parse_if(self) -> Node | None:
        # 'if' Pipe 'then' Pipe ('elif' Pipe 'then' Pipe)* ('else' Pipe)? 'end'. Each branch is a
        # partial If whose else is the hole, completed from the last to the first.
        if not self._enter(self.current):
            return None
        branches: list[If] = []
        while True:
            keyword = self.current
            self._advance()
            condition = self._parse_pipe()
            if condition is None or not self._expect(TokenKind.THEN, "'then'"):
                return None
            then = self._parse_pipe()
            if then is None:
                return None
            branches.append(If(condition, then, _HOLE, _pos(keyword)))
            if self.current.kind != TokenKind.ELIF:
                break
        otherwise: Node | None = None
        if self.current.kind == TokenKind.ELSE:
            self._advance()
            otherwise = self._parse_pipe()
            if otherwise is None:
                return None
        last: Node = otherwise if otherwise is not None else Identity(_pos(self.current))
        for i in range(len(branches) - 1, -1, -1):
            branches[i].else_ = last
            last = branches[i]
        return self._close(TokenKind.END, "'end'", last)

    def _parse_try(self) -> Node | None:
        # 'try' Unary ('catch' Unary)?
        keyword = self.current
        if not self._enter(keyword):
            return None
        self._advance()
        body = self._parse_expression(True)
        if body is None:
            return None
        handler: Node | None = None
        if self.current.kind == TokenKind.CATCH:
            self._advance()
            handler = self._parse_expression(True)
            if handler is None:
                return None
        self.depth -= 1
        return Try(body, handler, _pos(keyword))

    def _parse_patterns(self) -> list[Pattern] | None:
        # Patterns = Pattern ('?//' Pattern)*
        patterns: list[Pattern] = []
        while True:
            pattern = self._parse_pattern()
            if pattern is None:
                return None
            patterns.append(pattern)
            if self.current.kind != TokenKind.QUESTION_ALT:
                return patterns
            self._advance()

    def _parse_pattern(self) -> Pattern | None:
        # Pattern = VAR | '[' Pattern (',' Pattern)* ']' | '{' PEntry (',' PEntry)* '}'
        token = self.current
        at = _pos(token)
        if token.kind == TokenKind.VARIABLE and not _is_location(token):
            self._advance()
            return VariablePattern(token.text, at)
        if token.kind == TokenKind.LEFT_BRACKET:
            if not self._enter(token):
                return None
            elements: list[Pattern] = []
            while True:
                self._advance()
                element = self._parse_pattern()
                if element is None:
                    return None
                elements.append(element)
                if self.current.kind != TokenKind.COMMA:
                    break
            if not self._expect(TokenKind.RIGHT_BRACKET, "']'"):
                return None
            self.depth -= 1
            return ArrayPattern(elements, at)
        if token.kind == TokenKind.LEFT_BRACE:
            if not self._enter(token):
                return None
            entries: list[PatternEntry] = []
            while True:
                self._advance()
                entry = self._parse_pattern_entry()
                if entry is None:
                    return None
                entries.append(entry)
                if self.current.kind != TokenKind.COMMA:
                    break
            if not self._expect(TokenKind.RIGHT_BRACE, "'}'"):
                return None
            self.depth -= 1
            return ObjectPattern(entries, at)
        return self._fail(SyntaxMessages.expected("a pattern ($name, [...] or {...})", token), token)

    def _parse_pattern_entry(self) -> PatternEntry | None:
        # PEntry = VAR (':' Pattern)? | (IDENT | Keyword | String | '(' Pipe ')') ':' Pattern
        token = self.current
        at = _pos(token)
        variable: str | None = None
        if _is_location(token):
            return self._fail(SyntaxMessages.unexpected(token), token)
        if token.kind == TokenKind.VARIABLE:
            self._advance()
            key: Node | None = Literal(token.text, at)
            variable = token.text
            if self.current.kind != TokenKind.COLON:
                return PatternEntry(key, variable, None, at)  # type: ignore[arg-type]
        else:
            key = self._parse_parenthesized_key() if token.kind == TokenKind.LEFT_PAREN else self._parse_name_key()
            if key is None:
                return None
        if not self._expect(TokenKind.COLON, "':'"):
            return None
        value = self._parse_pattern()
        return None if value is None else PatternEntry(key, variable, value, at)  # type: ignore[arg-type]

    def _enter(self, token: Token) -> bool:
        # Opens one level of nesting (spec §11).
        self.depth += 1
        if self.depth <= MAX_NESTING:
            return True
        self._fail(SyntaxMessages.TOO_DEEPLY_NESTED, token)
        return False

    def _close(self, kind: int, what: str, node: Node) -> Node | None:
        # Expects the token that closes a nesting level, and leaves the level.
        if not self._expect(kind, what):
            return None
        self.depth -= 1
        return node

    def _expect(self, kind: int, what: str) -> bool:
        if self.current.kind != kind:
            self._fail(SyntaxMessages.expected(what, self.current), self.current)
            return False
        self._advance()
        return True

    def _fail_at(self, message: str, position: Position) -> Any:
        if self.error is None:
            self.error = SyntaxError(message, position.line, position.column)
        return None

    def _fail(self, message: str, token: Token) -> Any:
        # Records the error and returns None. At a lexical error token, the lexer's message wins.
        if self.error is None:
            if token.kind == TokenKind.ERROR:
                self.error = SyntaxError(token.text, token.line, token.column)
            else:
                self.error = SyntaxError(message, token.line, token.column)
        return None


def _reduce(operands: list[Node], operators: list[Token]) -> None:
    # Replaces the last two operands with the last operator applied to them.
    op = operators.pop()
    right = operands.pop()
    left = operands.pop()
    operands.append(_make_binary(op, left, right))


def _make_binary(op: Token, left: Node, right: Node) -> Node:
    at = _pos(op)
    k = op.kind
    binary = _BINARY_OPS.get(k)
    if binary is not None:
        return Binary(binary, left, right, at)
    if k == TokenKind.AND:
        return And(left, right, at)
    if k == TokenKind.OR:
        return Or(left, right, at)
    if k == TokenKind.ALT:
        return Alternative(left, right, at)
    if k == TokenKind.ASSIGN:
        return Call("_assign", [left, right], at)
    if k == TokenKind.PIPE_ASSIGN:
        return Call("_modify", [left, right], at)
    return _make_update(k, left, right, at)


def _make_update(kind: int, left: Node, right: Node, at: Position) -> Node:
    # L op= R  =>  R as $v | _modify(L; . op $v)
    dot = Identity(at)
    value = Variable(UPDATE_VARIABLE, at)
    op = _UPDATE_OPS.get(kind)
    update: Node = Binary(op, dot, value, at) if op is not None else Alternative(dot, value, at)
    return Bind(right, [VariablePattern(UPDATE_VARIABLE, at)], Call("_modify", [left, update], at), at)


def _complete(partial: Node, rest: Node) -> Node:
    # A partial node of _parse_pipe with its hole filled.
    k = partial.kind
    if k == NodeKind.PIPE:
        partial.right = rest  # type: ignore[attr-defined]
    elif k == NodeKind.COMMA:
        partial.right = _complete(partial.right, rest)  # type: ignore[attr-defined]
    elif k == NodeKind.FUNC_DEF:
        partial.rest = rest  # type: ignore[attr-defined]
    elif k == NodeKind.LABEL:
        partial.body = rest  # type: ignore[attr-defined]
    elif k == NodeKind.BIND:
        partial.body = rest  # type: ignore[attr-defined]
    elif k == NodeKind.IF:
        partial.else_ = rest  # type: ignore[attr-defined]
    else:
        return rest
    return partial
