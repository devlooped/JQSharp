// The abstract syntax tree: immutable plain data, no behaviour, no back-pointers. Every node and
// pattern carries the position of the token that introduces it (its keyword, operator, bracket or
// name). Names stay as written (without '$', '.' or '@'; a qualified name keeps its "::"): the
// resolver binds them. Forms the spec defines by rewriting (§3.3) are already rewritten by the
// parser (see the list at the top of parser.ts), so the node set below is all an interpreter sees.

import type { JqValue } from "../values/jqValue.js";

/** A 1-based source position; columns count code points. */
export class Position {
  constructor(
    readonly line: number,
    readonly column: number,
  ) {}
}

export const enum NodeKind {
  Identity,
  Literal,
  Template,
  Format,
  Index,
  Slice,
  Iterate,
  Variable,
  Call,
  Pipe,
  Comma,
  Binary,
  And,
  Or,
  Alternative,
  Negate,
  ArrayExpr,
  ObjectExpr,
  If,
  Try,
  Reduce,
  Foreach,
  Bind,
  FuncDef,
  Label,
  Break,
}

export type Node =
  | Identity
  | Literal
  | Template
  | Format
  | Index
  | Slice
  | Iterate
  | Variable
  | Call
  | Pipe
  | Comma
  | Binary
  | And
  | Or
  | Alternative
  | Negate
  | ArrayExpr
  | ObjectExpr
  | If
  | Try
  | Reduce
  | Foreach
  | Bind
  | FuncDef
  | Label
  | Break;

/** `.` */
export class Identity {
  readonly kind = NodeKind.Identity;
  constructor(readonly position: Position) {}
}

/** A constant: a number, a string without interpolation, `null`, `true`, `false`. */
export class Literal {
  readonly kind = NodeKind.Literal;
  constructor(
    readonly value: JqValue,
    readonly position: Position,
  ) {}
}

/**
 * A string with interpolations: `texts[0] parts[0] texts[1] … parts[n-1] texts[n]`, so
 * `texts.length == parts.length + 1`. Each part's outputs are converted with `format` (null:
 * `tostring`); the texts are taken as they are.
 */
export class Template {
  readonly kind = NodeKind.Template;
  constructor(
    readonly format: string | null,
    readonly texts: readonly string[],
    readonly parts: readonly Node[],
    readonly position: Position,
  ) {}
}

/** `@name` alone: the format applied to the input. */
export class Format {
  readonly kind = NodeKind.Format;
  constructor(
    readonly name: string,
    readonly position: Position,
  ) {}
}

/** `Target[Key]`; also `.name` and `."s"`. The key runs on the input of the target. */
export class Index {
  readonly kind = NodeKind.Index;
  constructor(
    readonly target: Node,
    readonly key: Node,
    readonly position: Position,
  ) {}
}

/** `Target[From:To]`; a missing bound is null. */
export class Slice {
  readonly kind = NodeKind.Slice;
  constructor(
    readonly target: Node,
    readonly from: Node | null,
    readonly to: Node | null,
    readonly position: Position,
  ) {}
}

/** `Target[]` */
export class Iterate {
  readonly kind = NodeKind.Iterate;
  constructor(
    readonly target: Node,
    readonly position: Position,
  ) {}
}

/** `$name` or `$alias::name`. */
export class Variable {
  readonly kind = NodeKind.Variable;
  constructor(
    readonly name: string,
    readonly position: Position,
  ) {}
}

/** `name` or `name(a; b)`: a function, closure parameter or builtin of arity `arguments.length`. */
export class Call {
  readonly kind = NodeKind.Call;
  readonly arguments: readonly Node[];
  constructor(
    readonly name: string,
    args: readonly Node[],
    readonly position: Position,
  ) {
    this.arguments = args;
  }
}

/** `Left | Right` */
export class Pipe {
  readonly kind = NodeKind.Pipe;
  constructor(
    readonly left: Node,
    readonly right: Node,
    readonly position: Position,
  ) {}
}

/** `Left, Right` */
export class Comma {
  readonly kind = NodeKind.Comma;
  constructor(
    readonly left: Node,
    readonly right: Node,
    readonly position: Position,
  ) {}
}

export const enum BinaryOp {
  Add,
  Subtract,
  Multiply,
  Divide,
  Modulo,
  Equal,
  NotEqual,
  Less,
  LessEqual,
  Greater,
  GreaterEqual,
}

/** An arithmetic operator or a comparison: one output per pair of outputs of the operands. */
export class Binary {
  readonly kind = NodeKind.Binary;
  constructor(
    readonly op: BinaryOp,
    readonly left: Node,
    readonly right: Node,
    readonly position: Position,
  ) {}
}

/** `Left and Right` */
export class And {
  readonly kind = NodeKind.And;
  constructor(
    readonly left: Node,
    readonly right: Node,
    readonly position: Position,
  ) {}
}

/** `Left or Right` */
export class Or {
  readonly kind = NodeKind.Or;
  constructor(
    readonly left: Node,
    readonly right: Node,
    readonly position: Position,
  ) {}
}

/** `Left // Right` */
export class Alternative {
  readonly kind = NodeKind.Alternative;
  constructor(
    readonly left: Node,
    readonly right: Node,
    readonly position: Position,
  ) {}
}

/** Unary `-Operand`. */
export class Negate {
  readonly kind = NodeKind.Negate;
  constructor(
    readonly operand: Node,
    readonly position: Position,
  ) {}
}

/** `[Body]`; `[]` has no body. */
export class ArrayExpr {
  readonly kind = NodeKind.ArrayExpr;
  constructor(
    readonly body: Node | null,
    readonly position: Position,
  ) {}
}

/** `{…}` */
export class ObjectExpr {
  readonly kind = NodeKind.ObjectExpr;
  constructor(
    readonly entries: readonly ObjectEntry[],
    readonly position: Position,
  ) {}
}

/**
 * One entry of an object construction. `key` is the key expression (a literal for `name:` and
 * `"s":`, a template for interpolated keys, a variable for `$x:`, any expression for `(e):`). A
 * null `value` is the shorthand `{"s"}`, `{name}`, `{@f "s"}`: the value is the input indexed by
 * each key, evaluated once per key. (`{$x}` is rewritten to the key `"x"` and the value `$x`.)
 */
export class ObjectEntry {
  constructor(
    readonly key: Node,
    readonly value: Node | null,
    readonly position: Position,
  ) {}
}

/** `if Condition then Then else Else end`; `elif` nests, a missing else is `Identity`. */
export class If {
  readonly kind = NodeKind.If;
  readonly else: Node;
  constructor(
    readonly condition: Node,
    readonly then: Node,
    otherwise: Node,
    readonly position: Position,
  ) {
    this.else = otherwise;
  }
}

/** `try Body catch Handler`; also `Body?`. A null handler discards the error. */
export class Try {
  readonly kind = NodeKind.Try;
  constructor(
    readonly body: Node,
    readonly handler: Node | null,
    readonly position: Position,
  ) {}
}

/** `reduce Source as Patterns (Init; Update)` */
export class Reduce {
  readonly kind = NodeKind.Reduce;
  constructor(
    readonly source: Node,
    readonly patterns: readonly Pattern[],
    readonly init: Node,
    readonly update: Node,
    readonly position: Position,
  ) {}
}

/** `foreach Source as Patterns (Init; Update; Extract)`; a missing extract is `Identity`. */
export class Foreach {
  readonly kind = NodeKind.Foreach;
  constructor(
    readonly source: Node,
    readonly patterns: readonly Pattern[],
    readonly init: Node,
    readonly update: Node,
    readonly extract: Node,
    readonly position: Position,
  ) {}
}

/** `Source as Patterns | Body`. Several patterns are the `?//` alternatives. */
export class Bind {
  readonly kind = NodeKind.Bind;
  constructor(
    readonly source: Node,
    readonly patterns: readonly Pattern[],
    readonly body: Node,
    readonly position: Position,
  ) {}
}

/** `def …; Rest`: the definition is visible in its own body and in `rest`. */
export class FuncDef {
  readonly kind = NodeKind.FuncDef;
  constructor(
    readonly definition: Definition,
    readonly rest: Node,
    readonly position: Position,
  ) {}
}

/** `label $Name | Body` */
export class Label {
  readonly kind = NodeKind.Label;
  constructor(
    readonly name: string,
    readonly body: Node,
    readonly position: Position,
  ) {}
}

/** `break $Name` */
export class Break {
  readonly kind = NodeKind.Break;
  constructor(
    readonly name: string,
    readonly position: Position,
  ) {}
}

/**
 * `def Name(Params): Body;`. Every parameter is a filter parameter; a `$name` parameter
 * (`Param.isVariable`) also binds `$name` to each output of its own argument, first parameter
 * outermost (§3.3). The resolver does that binding by position, since a name may repeat: in
 * `def f($a; a)` the filter `a` is the second parameter and `$a` the first.
 */
export class Definition {
  constructor(
    readonly name: string,
    readonly params: readonly Param[],
    readonly body: Node,
    readonly position: Position,
  ) {}
}

export class Param {
  constructor(
    readonly name: string,
    readonly isVariable: boolean,
    readonly position: Position,
  ) {}
}

export const enum PatternKind {
  Variable,
  Array,
  Object,
}

export type Pattern = VariablePattern | ArrayPattern | ObjectPattern;

/** `$name` */
export class VariablePattern {
  readonly kind = PatternKind.Variable;
  constructor(
    readonly name: string,
    readonly position: Position,
  ) {}
}

/** `[P0, P1, …]`: element i is matched against `.[i]`. */
export class ArrayPattern {
  readonly kind = PatternKind.Array;
  constructor(
    readonly elements: readonly Pattern[],
    readonly position: Position,
  ) {}
}

/** `{…}` */
export class ObjectPattern {
  readonly kind = PatternKind.Object;
  constructor(
    readonly entries: readonly PatternEntry[],
    readonly position: Position,
  ) {}
}

/**
 * One entry of an object pattern: for each output of `key`, the value `.[key]` is bound to
 * `variable` (when not null) and matched against `value` (when not null). `$name` is the key
 * `"name"` with the variable `name`; `$name: P` adds the pattern; `name: P`, `"s": P` and
 * `(e): P` have no variable.
 */
export class PatternEntry {
  constructor(
    readonly key: Node,
    readonly variable: string | null,
    readonly value: Pattern | null,
    readonly position: Position,
  ) {}
}

/** `import "Path" as Alias Metadata;`, `import "Path" as $Alias …;` (data) or `include "Path" …;` (no alias). */
export class Import {
  constructor(
    readonly path: string,
    readonly alias: string | null,
    readonly isData: boolean,
    /** The metadata object, or undefined without one. */
    readonly metadata: JqValue | undefined,
    readonly position: Position,
  ) {}
}

/**
 * A parsed program or module file. `definitions` are the top-level definitions in order (what a
 * module exports); `body` is the main expression, `Identity` for a program of definitions only,
 * and null for a module file. `metadata` is undefined without a `module` directive.
 */
export class Program {
  constructor(
    readonly metadata: JqValue | undefined,
    readonly imports: readonly Import[],
    readonly definitions: readonly Definition[],
    readonly body: Node | null,
  ) {}
}
