import { JqArray } from "../values/jqArray.js";
import { JqObjectBuilder } from "../values/jqObjectBuilder.js";
import { JqKind, type JqValue, kindOf } from "../values/jqValue.js";
import {
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
  type Node,
  NodeKind,
  ObjectEntry,
  ObjectExpr,
  ObjectPattern,
  Or,
  Param,
  type Pattern,
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
} from "./ast.js";
import { Lexer } from "./lexer.js";
import { SyntaxError } from "./syntaxError.js";
import * as SyntaxMessages from "./syntaxError.js";
import { type Token, TokenKind, isKeyword } from "./token.js";

/** The nesting limit of spec §11. */
export const MaxNesting = 1000;

/** The variable that the rewriting of `L op= R` binds; '*' cannot appear in a source name. */
export const UpdateVariable = "*update";

/** `$__loc__`, the location object of §7.5: the resolver replaces it; no pattern, parameter or import binds it. */
export const LocationVariable = "__loc__";

const enum Assoc {
  Left,
  Right,
  None,
}

/**
 * The level of a binary operator (spec §3.1, levels 4 to 10), higher binding tighter; 0 for any
 * other token. Levels 1 to 3 (`|`, `,`, `as`) are parsed by `parsePipe`, levels 11 to 13 (unary
 * minus, suffixes, terms) by `parseExpression`.
 */
function binaryLevel(kind: TokenKind): number {
  switch (kind) {
    case TokenKind.Alt:
      return 4;
    case TokenKind.Assign:
    case TokenKind.PipeAssign:
    case TokenKind.PlusAssign:
    case TokenKind.MinusAssign:
    case TokenKind.StarAssign:
    case TokenKind.SlashAssign:
    case TokenKind.PercentAssign:
    case TokenKind.AltAssign:
      return 5;
    case TokenKind.Or:
      return 6;
    case TokenKind.And:
      return 7;
    case TokenKind.Equal:
    case TokenKind.NotEqual:
    case TokenKind.Less:
    case TokenKind.LessEqual:
    case TokenKind.Greater:
    case TokenKind.GreaterEqual:
      return 8;
    case TokenKind.Plus:
    case TokenKind.Minus:
      return 9;
    case TokenKind.Star:
    case TokenKind.Slash:
    case TokenKind.Percent:
      return 10;
    default:
      return 0;
  }
}

// The associativity of a level of binaryLevel.
function binaryAssoc(level: number): Assoc {
  return level === 4 ? Assoc.Right : level === 5 || level === 8 ? Assoc.None : Assoc.Left;
}

function pos(token: Token): Position {
  return new Position(token.line, token.column);
}

function isLocation(token: Token): boolean {
  return token.kind === TokenKind.Variable && token.text === LocationVariable;
}

function isLiteralName(name: string): boolean {
  return name === "null" || name === "true" || name === "false";
}

// The placeholder for "the rest of the pipe" in a partial node of parsePipe.
const Hole: Node = new Identity(new Position(0, 0));

/**
 * Recursive descent parser of jqlite (spec §3) over the tokens of `Lexer`. It never throws for
 * bad input: it returns the `Program` or one `SyntaxError`.
 *
 * Rewritten by the parser (spec §3.3), so the AST has one form for each:
 * - `..` is the call `recurse` (resolved like any call: a user `recurse/0` shadows it, as in jq).
 * - `E?` and `try E` are `Try` without a handler (which discards the error).
 * - `.name`, `."s"`, `T.name`, `T."s"` are `Index` with a literal or template key.
 * - `elif` nests `If`; a missing `else` is `.`; a `foreach` without extract has `.`.
 * - `{$x}` is `{"x": $x}`. The string shorthands (`{a}`, `{"s"}`, `{@f "s"}`) keep a null value,
 *   see `ObjectEntry`.
 * - `L = R` is `_assign(L; R)`, `L |= R` is `_modify(L; R)`, `L op= R` is
 *   `R as $v | _modify(L; . op $v)` (`//=` likewise with `//`), where `$v` is `UpdateVariable`,
 *   a name no program can write.
 * - A string or `@f "s"` without interpolation is a `Literal` (a format applies to
 *   interpolations only); parentheses leave no node.
 *
 * Where jq 1.8.2 parses differently from the EBNF of spec §3.2, this parser follows jq: `def`
 * and `label` start a pipe or comma item only, never an operand (`1 + def f: 1; f` fails);
 * object values are `Alt ('|' Alt)*` without `as`; object constructions accept a trailing comma;
 * `.[:]` is an error; a definition name may be qualified (`def a::b:`); module metadata
 * constants have no negative numbers.
 *
 * Nesting (brackets, interpolations, `if`, `reduce`, `foreach`, `try`, `def`) is limited to
 * `MaxNesting`; within the limit the host stack is not exhausted. Sequences do not recurse per
 * element: pipes, commas, bindings and leading definitions are folded from a list
 * (`parsePipe`), binary operators are parsed with an operator stack (`parseExpression`), and
 * runs of unary minus and of `elif` are loops.
 */
export class Parser {
  private readonly tokens: Token[];
  // The token at next, kept in a field: reading it is the commonest operation of the parser.
  private current: Token;
  private next = 0;
  private depth = 0;
  private error: SyntaxError | null = null;

  private constructor(source: string) {
    this.tokens = Lexer.tokenize(source);
    this.current = this.tokens[0]!;
  }

  /** Parses a program (spec §3.2, rule `Program`): the program, or the error. */
  static tryParse(source: string): Program | SyntaxError {
    return new Parser(source).run(false);
  }

  /** Parses a module file (spec §3.2, rule `ModuleBody`): directives and definitions only. */
  static tryParseModule(source: string): Program | SyntaxError {
    return new Parser(source).run(true);
  }

  private run(module: boolean): Program | SyntaxError {
    return this.parseProgram(module) ?? this.error!;
  }

  // The kind of the current token. A call, so that the compiler does not narrow it across advance().
  private kind(): TokenKind {
    return this.current.kind;
  }

  private peek(): Token {
    return this.tokens[Math.min(this.next + 1, this.tokens.length - 1)]!;
  }

  private advance(): void {
    if (this.next < this.tokens.length - 1) this.current = this.tokens[++this.next]!;
  }

  // Program = ModuleDecl? Import* FuncDef* Pipe?      ModuleBody = ModuleDecl? Import* FuncDef*
  private parseProgram(module: boolean): Program | null {
    let metadata: JqValue | undefined;
    if (this.kind() === TokenKind.Module) {
      this.advance();
      const value = this.parseMetadata();
      if (value === undefined || !this.expect(TokenKind.Semicolon, "';'")) return null;
      metadata = value;
    }

    const imports: Import[] = [];
    while (this.kind() === TokenKind.Import || this.kind() === TokenKind.Include) {
      const directive = this.parseImport();
      if (directive === null) return null;
      imports.push(directive);
    }

    const definitions: Definition[] = [];
    while (this.kind() === TokenKind.Def) {
      const definition = this.parseDefinition();
      if (definition === null) return null;
      definitions.push(definition);
    }

    let body: Node | null = null;
    if (module) {
      if (this.kind() !== TokenKind.Eof) return this.failAt(SyntaxMessages.ModuleHasBody, this.current);
    } else {
      body = this.kind() === TokenKind.Eof ? new Identity(pos(this.current)) : this.parsePipe();
      if (body === null) return null;
      if (this.kind() !== TokenKind.Eof) return this.failAt(SyntaxMessages.unexpected(this.current), this.current);
    }

    return new Program(metadata, imports, definitions, body);
  }

  // Import = 'import' STRING 'as' (IDENT | VAR) ConstObject? ';' | 'include' STRING ConstObject? ';'
  private parseImport(): Import | null {
    const keyword = this.current;
    this.advance();
    if (this.kind() === TokenKind.StringStart) return this.failAt(SyntaxMessages.ImportPathNotConstant, this.current);
    if (this.kind() !== TokenKind.String) return this.failAt(SyntaxMessages.expected("a module path string", this.current), this.current);
    const path = this.current.text;
    this.advance();

    let alias: string | null = null;
    let isData = false;
    if (keyword.kind === TokenKind.Import) {
      if (!this.expect(TokenKind.As, "'as'")) return null;
      if ((this.kind() !== TokenKind.Ident && this.kind() !== TokenKind.Variable) || isLocation(this.current)) {
        return this.failAt(SyntaxMessages.expected("a module name or a $name", this.current), this.current);
      }

      alias = this.current.text;
      isData = this.kind() === TokenKind.Variable;
      this.advance();
    }

    let metadata: JqValue | undefined;
    if (this.kind() === TokenKind.LeftBrace) {
      metadata = this.parseMetadata();
      if (metadata === undefined) return null;
    }

    return this.expect(TokenKind.Semicolon, "';'") ? new Import(path, alias, isData, metadata, pos(keyword)) : null;
  }

  // The metadata of 'module' and of imports: a constant object. Undefined after an error (null is a constant).
  private parseMetadata(): JqValue | undefined {
    const start = this.current;
    const value = this.parseConstant(start);
    if (value === undefined) return undefined;
    return kindOf(value) === JqKind.Object ? value : this.failValue(SyntaxMessages.MetadataNotObject, start);
  }

  // Const = NUMBER | STRING | null | true | false | '[' (Const (',' Const)*)? ']' | '{' (CEntry (',' CEntry)* ','?)? '}'
  // CEntry = (IDENT | Keyword | STRING) ':' Const. Any other token: "Module metadata must be constant" at the metadata.
  private parseConstant(start: Token): JqValue | undefined {
    switch (this.kind()) {
      case TokenKind.LeftBracket:
        return this.parseConstantArray(start);
      case TokenKind.LeftBrace:
        return this.parseConstantObject(start);
      default:
        return this.parseConstantScalar(start);
    }
  }

  private parseConstantScalar(start: Token): JqValue | undefined {
    const token = this.current;
    this.advance();
    switch (token.kind) {
      case TokenKind.Number:
        return token.number;
      case TokenKind.String:
        return token.text;
      case TokenKind.Ident:
        if (token.text === "null") return null;
        if (token.text === "true" || token.text === "false") return token.text === "true";
        break;
    }

    return this.failValue(SyntaxMessages.MetadataNotConstant, start);
  }

  private parseConstantArray(start: Token): JqValue | undefined {
    if (!this.enter(this.current)) return undefined;
    this.advance();
    const items: JqValue[] = [];
    while (this.kind() !== TokenKind.RightBracket) {
      if (items.length > 0 && !this.expectConstant(TokenKind.Comma, start)) return undefined;
      const item = this.parseConstant(start);
      if (item === undefined) return undefined;
      items.push(item);
    }

    this.advance();
    this.depth--;
    return JqArray.fromOwned(items);
  }

  private parseConstantObject(start: Token): JqValue | undefined {
    if (!this.enter(this.current)) return undefined;
    this.advance();
    const builder = new JqObjectBuilder();
    while (this.kind() !== TokenKind.RightBrace) {
      const key = this.current;
      if (key.kind !== TokenKind.Ident && key.kind !== TokenKind.String && !isKeyword(key.kind)) return this.failValue(SyntaxMessages.MetadataNotConstant, start);
      this.advance();
      if (!this.expectConstant(TokenKind.Colon, start)) return undefined;
      const value = this.parseConstant(start);
      if (value === undefined) return undefined;
      builder.set(key.text, value);
      if (this.kind() === TokenKind.Comma) this.advance();
      else if (this.kind() !== TokenKind.RightBrace) return this.failValue(SyntaxMessages.MetadataNotConstant, start);
    }

    this.advance();
    this.depth--;
    return builder.toObject();
  }

  private expectConstant(kind: TokenKind, start: Token): boolean {
    if (this.kind() !== kind) {
      this.fail(SyntaxMessages.MetadataNotConstant, start);
      return false;
    }

    this.advance();
    return true;
  }

  // FuncDef = 'def' IDENT ('(' Param (';' Param)* ')')? ':' Pipe ';'
  private parseDefinition(): Definition | null {
    const keyword = this.current;
    if (!this.enter(keyword)) return null;
    this.advance();
    if (this.kind() !== TokenKind.Ident) return this.failAt(SyntaxMessages.expected("a function name", this.current), this.current);
    const name = this.current.text;
    this.advance();
    const parameters = this.parseParams();
    if (parameters === null || !this.expect(TokenKind.Colon, "':'")) return null;
    const body = this.parsePipe();
    if (body === null || !this.expect(TokenKind.Semicolon, "';'")) return null;
    this.depth--;
    return new Definition(name, parameters, body, pos(keyword));
  }

  // ('(' Param (';' Param)* ')')?      Param = IDENT | VAR
  private parseParams(): Param[] | null {
    const parameters: Param[] = [];
    if (this.kind() !== TokenKind.LeftParen) return parameters;
    do {
      this.advance();
      if ((this.kind() !== TokenKind.Ident && this.kind() !== TokenKind.Variable) || isLocation(this.current)) {
        return this.failAt(SyntaxMessages.expected("a parameter name", this.current), this.current);
      }

      parameters.push(new Param(this.current.text, this.kind() === TokenKind.Variable, pos(this.current)));
      this.advance();
    } while (this.kind() === TokenKind.Semicolon);

    return this.expect(TokenKind.RightParen, "')'") ? parameters : null;
  }

  // Pipe = Item (',' Item)* ('|' Pipe)?, where an Item is
  //   'def' … ';' Pipe  |  'label' VAR '|' Pipe  |  Alt 'as' Patterns '|' Pipe  |  Alt
  // The first three, and '|', take the rest of the pipe. To avoid recursing per element, each of
  // them is kept as a partial node whose last child is Hole, and the partial nodes are completed
  // from the last to the first when the pipe ends.
  // parsePipe and parseExpression are on the stack once per nesting level of the program, with
  // the frame of the construct (parseArray, parseIf, …): three frames a level.
  private parsePipe(): Node | null {
    const pending: Node[] = [];
    let items: Node | null = null;
    let comma = this.current;
    while (true) {
      let partial: Node | null;
      if (this.kind() === TokenKind.Def) {
        partial = this.parseFuncDef();
      } else if (this.kind() === TokenKind.Label) {
        partial = this.parseLabel();
      } else {
        const item = this.parseExpression(false);
        if (item === null) return null;
        if (this.kind() === TokenKind.As) {
          partial = this.parseBinding(item);
        } else {
          items = addItem(items, item, comma);
          if (this.kind() === TokenKind.Comma) {
            comma = this.current;
            this.advance();
            continue;
          }

          if (this.kind() !== TokenKind.Pipe) break;
          pending.push(new Pipe(items, Hole, pos(this.current)));
          this.advance();
          items = null;
          continue;
        }
      }

      if (partial === null) return null;

      // A binding takes the rest of the pipe; the items before it are the left side of a comma.
      pending.push(items === null ? partial : addItem(items, partial, comma));
      items = null;
    }

    return completeAll(pending, items);
  }

  // 'def' … ';', as a partial node.
  private parseFuncDef(): Node | null {
    const definition = this.parseDefinition();
    return definition === null ? null : new FuncDef(definition, Hole, definition.position);
  }

  // 'label' VAR '|', as a partial node.
  private parseLabel(): Node | null {
    const start = this.current;
    this.advance();
    if (this.kind() !== TokenKind.Variable) return this.failAt(SyntaxMessages.expected("a label name ($name)", this.current), this.current);
    const name = this.current.text;
    this.advance();
    return this.expect(TokenKind.Pipe, "'|'") ? new Label(name, Hole, pos(start)) : null;
  }

  // Source 'as' Patterns '|', as a partial node.
  private parseBinding(source: Node): Node | null {
    const keyword = this.current;
    this.advance();
    const patterns = this.parsePatterns();
    if (patterns === null || !this.expect(TokenKind.Pipe, "'|'")) return null;
    return new Bind(source, patterns, Hole, pos(keyword));
  }

  // ObjVal = Alt ('|' Alt)*: an object value, which has no top-level ',' and no 'as', 'def' or 'label' (as jq).
  private parseObjectValue(): Node | null {
    const items: Node[] = [];
    const bars: Token[] = [];
    while (true) {
      const item = this.parseExpression(false);
      if (item === null) return null;
      items.push(item);
      if (this.kind() !== TokenKind.Pipe) break;
      bars.push(this.current);
      this.advance();
    }

    let result = items[items.length - 1]!;
    for (let i = items.length - 2; i >= 0; i--) result = new Pipe(items[i]!, result, pos(bars[i]!));
    return result;
  }

  // Levels 4 to 13 of the precedence table: binary operators with an operand stack and an
  // operator stack (shunting-yard), so a chain of any length does not recurse; each operand is
  // Unary = '-'* Postfix, Postfix = Term Suffix*. With operandOnly, one operand (the Unary of
  // 'try' and 'catch'). The term dispatch is here rather than in a function of its own, so that
  // a nesting level costs three frames: parsePipe, parseExpression and the construct.
  private parseExpression(operandOnly: boolean): Node | null {
    const operands: Node[] = [];
    const operators: Token[] = [];
    while (true) {
      const firstMinus = this.next;
      while (this.kind() === TokenKind.Minus) this.advance();
      const termStart = this.next;

      let operand: Node | null;
      switch (this.kind()) {
        case TokenKind.LeftParen:
          operand = this.parseParenthesized();
          break;
        case TokenKind.LeftBracket:
          operand = this.parseArray();
          break;
        case TokenKind.LeftBrace:
          operand = this.parseObject();
          break;
        case TokenKind.String:
        case TokenKind.StringStart:
          operand = this.parseString(null);
          break;
        case TokenKind.Reduce:
        case TokenKind.Foreach:
          operand = this.parseReduceOrForeach();
          break;
        case TokenKind.If:
          operand = this.parseIf();
          break;
        case TokenKind.Try:
          operand = this.parseTry();
          break;
        default:
          operand = this.kind() === TokenKind.Ident && !isLiteralName(this.current.text) ? this.parseCall() : this.parseAtom();
          break;
      }

      while (operand !== null && this.isSuffix()) operand = this.isBracketSuffix() ? this.parseBracketSuffix(operand) : this.parseSuffix(operand);
      if (operand === null) return null;
      operand = this.negate(operand, firstMinus, termStart);
      if (operandOnly) return operand;

      operands.push(operand);
      if (binaryLevel(this.kind()) === 0) return reduceAll(operands, operators);
      if (!this.pushOperator(operands, operators)) return null;
    }
  }

  // Reduces the operators that bind at least as tightly as the current one, then pushes it.
  private pushOperator(operands: Node[], operators: Token[]): boolean {
    const op = this.current;
    const level = binaryLevel(op.kind);
    const assoc = binaryAssoc(level);
    while (operators.length > 0) {
      const top = binaryLevel(operators[operators.length - 1]!.kind);
      if (top < level || (top === level && assoc === Assoc.Right)) break;
      if (top === level && assoc === Assoc.None) {
        this.fail(SyntaxMessages.notAssociative(op), op);
        return false;
      }

      reduce(operands, operators);
    }

    operators.push(op);
    this.advance();
    return true;
  }

  // The minus tokens from first (included) to end (excluded) applied to node, the last one innermost.
  private negate(node: Node, first: number, end: number): Node {
    for (let i = end - 1; i >= first; i--) node = new Negate(node, pos(this.tokens[i]!));
    return node;
  }

  private isBracketSuffix(): boolean {
    return this.kind() === TokenKind.LeftBracket || (this.kind() === TokenKind.Dot && this.peek().kind === TokenKind.LeftBracket);
  }

  private isSuffix(): boolean {
    switch (this.kind()) {
      case TokenKind.Question:
      case TokenKind.Field:
      case TokenKind.LeftBracket:
      case TokenKind.QuestionAlt:
        return true;
      case TokenKind.Dot: {
        const after = this.peek().kind;
        return after === TokenKind.LeftBracket || after === TokenKind.String || after === TokenKind.StringStart;
      }
      default:
        return false;
    }
  }

  // Suffix = '?' | FIELD | '.' String; the bracket suffixes are parseBracketSuffix. '?//' is an
  // error here: it separates patterns only.
  private parseSuffix(target: Node): Node | null {
    const token = this.current;
    switch (token.kind) {
      case TokenKind.Question:
        this.advance();
        return new Try(target, null, pos(token));
      case TokenKind.Field:
        this.advance();
        return new Index(target, new Literal(token.text, pos(token)), pos(token));
      case TokenKind.QuestionAlt:
        return this.failAt(SyntaxMessages.questionAlt(token), token);
      default: {
        this.advance();
        const key = this.parseString(null);
        return key === null ? null : new Index(target, key, pos(token));
      }
    }
  }

  // '.'? '[' ']' | '[' Pipe ']' | '[' Pipe ':' Pipe? ']' | '[' ':' Pipe ']'
  private parseBracketSuffix(target: Node): Node | null {
    if (this.kind() === TokenKind.Dot) this.advance();
    const open = this.current;
    if (!this.enter(open)) return null;
    this.advance();
    let result: Node;
    if (this.kind() === TokenKind.RightBracket) {
      result = new Iterate(target, pos(open));
    } else {
      let from: Node | null = null;
      if (this.kind() !== TokenKind.Colon) {
        from = this.parsePipe();
        if (from === null) return null;
      }

      if (this.kind() === TokenKind.Colon) {
        this.advance();
        let to: Node | null = null;
        if (from === null || this.kind() !== TokenKind.RightBracket) {
          to = this.parsePipe();
          if (to === null) return null;
        }

        result = new Slice(target, from, to, pos(open));
      } else {
        result = new Index(target, from!, pos(open));
      }
    }

    return this.close(TokenKind.RightBracket, "']'", result);
  }

  // '(' Pipe ')'
  private parseParenthesized(): Node | null {
    if (!this.enter(this.current)) return null;
    this.advance();
    const inner = this.parsePipe();
    return inner === null ? null : this.close(TokenKind.RightParen, "')'", inner);
  }

  // '[' Pipe? ']'
  private parseArray(): Node | null {
    const open = this.current;
    if (!this.enter(open)) return null;
    this.advance();
    if (this.kind() === TokenKind.RightBracket) return this.close(TokenKind.RightBracket, "']'", new ArrayExpr(null, pos(open)));
    const body = this.parsePipe();
    return body === null ? null : this.close(TokenKind.RightBracket, "']'", new ArrayExpr(body, pos(open)));
  }

  // '.', '..', '.' String, NUMBER, FORMAT String?, FIELD, VAR, null, true, false, 'break' VAR.
  private parseAtom(): Node | null {
    const token = this.current;
    const at = pos(token);
    switch (token.kind) {
      case TokenKind.Dot: {
        this.advance();
        if (this.kind() === TokenKind.String || this.kind() === TokenKind.StringStart) {
          const key = this.parseString(null);
          return key === null ? null : new Index(new Identity(at), key, at);
        }

        return new Identity(at);
      }
      case TokenKind.DotDot:
        this.advance();
        return new Call("recurse", [], at);
      case TokenKind.Number:
        this.advance();
        return new Literal(token.number, at);
      case TokenKind.Format:
        // Any name parses: an unknown format is a runtime error (spec §9.9).
        this.advance();
        return this.kind() === TokenKind.String || this.kind() === TokenKind.StringStart ? this.parseString(token.text) : new Format(token.text, at);
      case TokenKind.Field:
        this.advance();
        return new Index(new Identity(at), new Literal(token.text, at), at);
      case TokenKind.Variable:
        this.advance();
        return new Variable(token.text, at);
      case TokenKind.Ident:
        this.advance();
        return new Literal(token.text === "null" ? null : token.text === "true", at);
      case TokenKind.Break: {
        this.advance();
        if (this.kind() !== TokenKind.Variable) return this.failAt(SyntaxMessages.expected("a label name ($name)", this.current), this.current);
        const label = this.current.text;
        this.advance();
        return new Break(label, at);
      }
      default:
        return this.failAt(SyntaxMessages.unexpected(token), token);
    }
  }

  // IDENT ('(' Pipe (';' Pipe)* ')')?
  private parseCall(): Node | null {
    const name = this.current;
    this.advance();
    const args: Node[] = [];
    if (this.kind() === TokenKind.LeftParen) {
      if (!this.enter(this.current)) return null;
      do {
        this.advance();
        const argument = this.parsePipe();
        if (argument === null) return null;
        args.push(argument);
      } while (this.kind() === TokenKind.Semicolon);

      if (!this.expect(TokenKind.RightParen, "')'")) return null;
      this.depth--;
    }

    return new Call(name.text, args, pos(name));
  }

  // A STRING, or StringStart Pipe (StringMiddle Pipe)* StringEnd.
  private parseString(format: string | null): Node | null {
    const start = this.current;
    if (start.kind === TokenKind.String) {
      this.advance();
      return new Literal(start.text, pos(start));
    }

    if (!this.enter(start)) return null;
    const texts: string[] = [];
    const parts: Node[] = [];
    do {
      texts.push(this.current.text);
      this.advance();
      const part = this.parsePipe();
      if (part === null) return null;
      parts.push(part);
      if (this.kind() !== TokenKind.StringMiddle && this.kind() !== TokenKind.StringEnd) return this.failAt(SyntaxMessages.expected("')'", this.current), this.current);
    } while (this.kind() === TokenKind.StringMiddle);

    texts.push(this.current.text);
    this.advance();
    this.depth--;
    return new Template(format, texts, parts, pos(start));
  }

  // '{' (Entry (',' Entry)* ','?)? '}'
  private parseObject(): Node | null {
    const open = this.current;
    if (!this.enter(open)) return null;
    this.advance();
    const entries: ObjectEntry[] = [];
    while (this.kind() !== TokenKind.RightBrace) {
      let entry = this.parseObjectKey();
      if (entry !== null && entry.value === null && this.kind() === TokenKind.Colon) {
        this.advance();
        const value = this.parseObjectValue();
        entry = value === null ? null : new ObjectEntry(entry.key, value, entry.position);
      }

      if (entry === null) return null;
      entries.push(entry);
      if (this.kind() === TokenKind.Comma) this.advance();
      else if (this.kind() !== TokenKind.RightBrace) return this.failAt(SyntaxMessages.expected("',' or '}'", this.current), this.current);
    }

    this.advance();
    this.depth--;
    return new ObjectExpr(entries, pos(open));
  }

  // Entry = (IDENT | Keyword | VAR | String | '(' Pipe ')') ':' ObjVal  |  IDENT | Keyword | VAR | String
  // The key of an entry, with a null value when a ':' and the value follow. A shorthand is
  // complete: {$x} has its value, {name} and {"s"} have none and no ':' after them.
  private parseObjectKey(): ObjectEntry | null {
    const token = this.current;
    const at = pos(token);
    if (token.kind === TokenKind.Variable) {
      this.advance();
      return this.kind() === TokenKind.Colon
        ? new ObjectEntry(new Variable(token.text, at), null, at)
        : new ObjectEntry(new Literal(token.text, at), new Variable(token.text, at), at);
    }

    const key = token.kind === TokenKind.LeftParen ? this.parseParenthesizedKey() : this.parseNameKey();
    if (key === null) return null;
    if (token.kind === TokenKind.LeftParen && this.kind() !== TokenKind.Colon) return this.failAt(SyntaxMessages.expected("':'", this.current), this.current);
    return new ObjectEntry(key, null, at);
  }

  // A key written as a name or a string (objects and object patterns): IDENT | Keyword | FORMAT? String.
  private parseNameKey(): Node | null {
    const token = this.current;
    if (token.kind === TokenKind.Ident || isKeyword(token.kind)) {
      this.advance();
      return new Literal(token.text, pos(token));
    }

    if (token.kind === TokenKind.String || token.kind === TokenKind.StringStart) return this.parseString(null);

    if (token.kind === TokenKind.Format) {
      const after = this.peek().kind;
      if (after === TokenKind.String || after === TokenKind.StringStart) {
        this.advance();
        return this.parseString(token.text);
      }
    }

    switch (token.kind) {
      case TokenKind.Comma:
      case TokenKind.RightBrace:
      case TokenKind.RightBracket:
      case TokenKind.RightParen:
      case TokenKind.Semicolon:
      case TokenKind.Colon:
      case TokenKind.Pipe:
      case TokenKind.Eof:
      case TokenKind.Error:
        return this.failAt(SyntaxMessages.unexpected(token), token);
      default:
        return this.failAt(SyntaxMessages.MayNeedParentheses, token);
    }
  }

  // '(' Pipe ')' as a key; a number, boolean or null literal there is a static error (spec §3.2).
  private parseParenthesizedKey(): Node | null {
    const open = this.current;
    if (!this.enter(open)) return null;
    this.advance();
    const key = this.parsePipe();
    if (key === null || !this.expect(TokenKind.RightParen, "')'")) return null;
    this.depth--;
    if (key.kind === NodeKind.Literal && typeof key.value !== "string") {
      this.error ??= new SyntaxError(SyntaxMessages.invalidObjectKey(key.value), key.position.line, key.position.column);
      return null;
    }

    return key;
  }

  // 'reduce' Alt 'as' Patterns '(' Pipe ';' Pipe ')'   |   'foreach' Alt 'as' Patterns '(' Pipe ';' Pipe (';' Pipe)? ')'
  private parseReduceOrForeach(): Node | null {
    if (!this.enter(this.current)) return null;
    const keyword = this.current;
    this.advance();
    const source = this.parseExpression(false);
    if (source === null || !this.expect(TokenKind.As, "'as'")) return null;
    const patterns = this.parsePatterns();
    if (patterns === null || !this.expect(TokenKind.LeftParen, "'('")) return null;
    const isReduce = keyword.kind === TokenKind.Reduce;
    const bodies: Node[] = [];
    do {
      if (bodies.length > 0) this.advance();
      const body = this.parsePipe();
      if (body === null) return null;
      bodies.push(body);
    } while (this.kind() === TokenKind.Semicolon && bodies.length < (isReduce ? 2 : 3));

    if (bodies.length < 2) return this.failAt(SyntaxMessages.expected("';'", this.current), this.current);
    const at = pos(keyword);
    return this.close(
      TokenKind.RightParen,
      "')'",
      isReduce ? new Reduce(source, patterns, bodies[0]!, bodies[1]!, at) : new Foreach(source, patterns, bodies[0]!, bodies[1]!, bodies.length > 2 ? bodies[2]! : new Identity(at), at),
    );
  }

  // 'if' Pipe 'then' Pipe ('elif' Pipe 'then' Pipe)* ('else' Pipe)? 'end'. Each branch is a partial
  // If whose else is Hole, completed from the last to the first.
  private parseIf(): Node | null {
    if (!this.enter(this.current)) return null;
    const branches: Node[] = [];
    do {
      const keyword = this.current;
      this.advance();
      const condition = this.parsePipe();
      if (condition === null || !this.expect(TokenKind.Then, "'then'")) return null;
      const then = this.parsePipe();
      if (then === null) return null;
      branches.push(new If(condition, then, Hole, pos(keyword)));
    } while (this.kind() === TokenKind.Elif);

    let otherwise: Node | null = null;
    if (this.kind() === TokenKind.Else) {
      this.advance();
      otherwise = this.parsePipe();
      if (otherwise === null) return null;
    }

    return this.close(TokenKind.End, "'end'", completeAll(branches, otherwise ?? new Identity(pos(this.current))));
  }

  // 'try' Unary ('catch' Unary)?
  private parseTry(): Node | null {
    const keyword = this.current;
    if (!this.enter(keyword)) return null;
    this.advance();
    const body = this.parseExpression(true);
    if (body === null) return null;
    let handler: Node | null = null;
    if (this.kind() === TokenKind.Catch) {
      this.advance();
      handler = this.parseExpression(true);
      if (handler === null) return null;
    }

    this.depth--;
    return new Try(body, handler, pos(keyword));
  }

  // Patterns = Pattern ('?//' Pattern)*
  private parsePatterns(): Pattern[] | null {
    const patterns: Pattern[] = [];
    while (true) {
      const pattern = this.parsePattern();
      if (pattern === null) return null;
      patterns.push(pattern);
      if (this.kind() !== TokenKind.QuestionAlt) return patterns;
      this.advance();
    }
  }

  // Pattern = VAR | '[' Pattern (',' Pattern)* ']' | '{' PEntry (',' PEntry)* '}'
  private parsePattern(): Pattern | null {
    const token = this.current;
    const at = pos(token);
    if (token.kind === TokenKind.Variable && !isLocation(token)) {
      this.advance();
      return new VariablePattern(token.text, at);
    }

    if (token.kind === TokenKind.LeftBracket) {
      if (!this.enter(token)) return null;
      const elements: Pattern[] = [];
      do {
        this.advance();
        const element = this.parsePattern();
        if (element === null) return null;
        elements.push(element);
      } while (this.kind() === TokenKind.Comma);

      if (!this.expect(TokenKind.RightBracket, "']'")) return null;
      this.depth--;
      return new ArrayPattern(elements, at);
    }

    if (token.kind === TokenKind.LeftBrace) {
      if (!this.enter(token)) return null;
      const entries: PatternEntry[] = [];
      do {
        this.advance();
        const entry = this.parsePatternEntry();
        if (entry === null) return null;
        entries.push(entry);
      } while (this.kind() === TokenKind.Comma);

      if (!this.expect(TokenKind.RightBrace, "'}'")) return null;
      this.depth--;
      return new ObjectPattern(entries, at);
    }

    return this.failAt(SyntaxMessages.expected("a pattern ($name, [...] or {...})", token), token);
  }

  // PEntry = VAR (':' Pattern)? | (IDENT | Keyword | String | '(' Pipe ')') ':' Pattern
  private parsePatternEntry(): PatternEntry | null {
    const token = this.current;
    const at = pos(token);
    let key: Node | null;
    let variable: string | null = null;
    if (isLocation(token)) return this.failAt(SyntaxMessages.unexpected(token), token);
    if (token.kind === TokenKind.Variable) {
      this.advance();
      key = new Literal(token.text, at);
      variable = token.text;
      if (this.kind() !== TokenKind.Colon) return new PatternEntry(key, variable, null, at);
    } else {
      key = token.kind === TokenKind.LeftParen ? this.parseParenthesizedKey() : this.parseNameKey();
      if (key === null) return null;
    }

    if (!this.expect(TokenKind.Colon, "':'")) return null;
    const value = this.parsePattern();
    return value === null ? null : new PatternEntry(key, variable, value, at);
  }

  // Opens one level of nesting (spec §11).
  private enter(token: Token): boolean {
    if (++this.depth <= MaxNesting) return true;
    this.fail(SyntaxMessages.TooDeeplyNested, token);
    return false;
  }

  // Expects the token that closes a nesting level, and leaves the level.
  private close(kind: TokenKind, what: string, node: Node): Node | null {
    if (!this.expect(kind, what)) return null;
    this.depth--;
    return node;
  }

  private expect(kind: TokenKind, what: string): boolean {
    if (this.kind() !== kind) {
      this.fail(SyntaxMessages.expected(what, this.current), this.current);
      return false;
    }

    this.advance();
    return true;
  }

  private failAt(message: string, token: Token): null {
    this.fail(message, token);
    return null;
  }

  private failValue(message: string, token: Token): undefined {
    this.fail(message, token);
    return undefined;
  }

  // Records the error. At a lexical error token, the lexer's message wins.
  private fail(message: string, token: Token): void {
    this.error ??= token.kind === TokenKind.Error ? new SyntaxError(token.text, token.line, token.column) : new SyntaxError(message, token.line, token.column);
  }
}

function addItem(items: Node | null, item: Node, comma: Token): Node {
  return items === null ? item : new Comma(items, item, pos(comma));
}

// The pending partial nodes of parsePipe completed from the last to the first.
function completeAll(pending: Node[], last: Node): Node {
  for (let i = pending.length - 1; i >= 0; i--) last = complete(pending[i]!, last);
  return last;
}

// A partial node of parsePipe with its hole filled.
function complete(partial: Node, rest: Node): Node {
  switch (partial.kind) {
    case NodeKind.Pipe:
      return new Pipe(partial.left, rest, partial.position);
    case NodeKind.Comma:
      return new Comma(partial.left, complete(partial.right, rest), partial.position);
    case NodeKind.FuncDef:
      return new FuncDef(partial.definition, rest, partial.position);
    case NodeKind.Label:
      return new Label(partial.name, rest, partial.position);
    case NodeKind.Bind:
      return new Bind(partial.source, partial.patterns, rest, partial.position);
    case NodeKind.If:
      return new If(partial.condition, partial.then, rest, partial.position);
    default:
      return rest;
  }
}

function reduceAll(operands: Node[], operators: Token[]): Node {
  while (operators.length > 0) reduce(operands, operators);
  return operands[0]!;
}

// Replaces the last two operands with the last operator applied to them.
function reduce(operands: Node[], operators: Token[]): void {
  const op = operators.pop()!;
  const right = operands.pop()!;
  const left = operands.pop()!;
  operands.push(makeBinary(op, left, right));
}

function makeBinary(op: Token, left: Node, right: Node): Node {
  const at = pos(op);
  switch (op.kind) {
    case TokenKind.Plus:
      return new Binary(BinaryOp.Add, left, right, at);
    case TokenKind.Minus:
      return new Binary(BinaryOp.Subtract, left, right, at);
    case TokenKind.Star:
      return new Binary(BinaryOp.Multiply, left, right, at);
    case TokenKind.Slash:
      return new Binary(BinaryOp.Divide, left, right, at);
    case TokenKind.Percent:
      return new Binary(BinaryOp.Modulo, left, right, at);
    case TokenKind.Equal:
      return new Binary(BinaryOp.Equal, left, right, at);
    case TokenKind.NotEqual:
      return new Binary(BinaryOp.NotEqual, left, right, at);
    case TokenKind.Less:
      return new Binary(BinaryOp.Less, left, right, at);
    case TokenKind.LessEqual:
      return new Binary(BinaryOp.LessEqual, left, right, at);
    case TokenKind.Greater:
      return new Binary(BinaryOp.Greater, left, right, at);
    case TokenKind.GreaterEqual:
      return new Binary(BinaryOp.GreaterEqual, left, right, at);
    case TokenKind.And:
      return new And(left, right, at);
    case TokenKind.Or:
      return new Or(left, right, at);
    case TokenKind.Alt:
      return new Alternative(left, right, at);
    case TokenKind.Assign:
      return new Call("_assign", [left, right], at);
    case TokenKind.PipeAssign:
      return new Call("_modify", [left, right], at);
    default:
      return makeUpdate(op.kind, left, right, at);
  }
}

// L op= R  =>  R as $v | _modify(L; . op $v)
function makeUpdate(kind: TokenKind, left: Node, right: Node, at: Position): Node {
  const dot = new Identity(at);
  const value = new Variable(UpdateVariable, at);
  let update: Node;
  switch (kind) {
    case TokenKind.PlusAssign:
      update = new Binary(BinaryOp.Add, dot, value, at);
      break;
    case TokenKind.MinusAssign:
      update = new Binary(BinaryOp.Subtract, dot, value, at);
      break;
    case TokenKind.StarAssign:
      update = new Binary(BinaryOp.Multiply, dot, value, at);
      break;
    case TokenKind.SlashAssign:
      update = new Binary(BinaryOp.Divide, dot, value, at);
      break;
    case TokenKind.PercentAssign:
      update = new Binary(BinaryOp.Modulo, dot, value, at);
      break;
    default:
      update = new Alternative(dot, value, at);
      break;
  }

  return new Bind(right, [new VariablePattern(UpdateVariable, at)], new Call("_modify", [left, update], at), at);
}
