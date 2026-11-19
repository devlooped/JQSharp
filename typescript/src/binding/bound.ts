// The bound program: what the interpreter evaluates. A tree of its own node types rather than
// the AST plus a side table, because every port can build it bottom-up as plain immutable data:
// no node is mutated after construction, no map is keyed by node identity, and nothing is cyclic.
//
// Names are gone. The environment at run time is a chain of frames, innermost first, and every
// reference counts frames outward from where it is written:
// - a frame is pushed by `as` and by the binder of `reduce`/`foreach` (one value slot per
//   variable of all its patterns), by `label` (the label instance), and by a call of a function
//   with parameters (one closure slot per parameter);
// - a variable is (hops, slot), a closure parameter (hops, slot), a `break` hops;
// - a function call names its definition by index in BoundProgram.functions (recursion is an
//   index, not a cycle), and hops is the number of frames between the call and the definition:
//   the callee runs in the caller's environment with hops frames removed, plus its parameters.
// Host variables are the one late-bound name (spec §7.5): each has an index in
// BoundProgram.hostVariables, so the host's values can be looked up once per evaluation.
//
// Every node also says whether it yields at most one output (atMostOne: the single expressions
// of spec §7.7), computed from its children when it is built. A call is single when its function
// is; the resolver knows that of a function still being defined by assuming it (Resolver.call).
//
// Not in the reference: `height` and `direct`. JavaScript cannot grow its stack, so this port's
// interpreter runs on an explicit stack (Interpreter) and recurses on the host stack only for
// *direct* nodes: those that yield at most one output without calling a function, a closure
// parameter, a native that takes closures or a generator, and whose nesting is at most
// MaxDirectHeight. `height` is the number of nested Interpreter.evalOne calls the node takes
// (Infinity when it cannot be evaluated directly). The reference's EvalOne takes every atMostOne
// node; this port's takes the direct ones, and the others go on the explicit stack.

import type { Native } from "../builtins/natives.js";
import type { BinaryOp } from "../syntax/ast.js";
import type { JqValue } from "../values/jqValue.js";
import type { ModuleTable } from "./modules.js";

export const enum NodeKind {
  Identity,
  Literal,
  Template,
  Format,
  Index,
  Slice,
  Iterate,
  Variable,
  HostVariable,
  EnvVariable,
  CallFunction,
  CallParameter,
  CallNative,
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
  Label,
  Break,
}

/**
 * The deepest nesting Interpreter.evalOne recurses into on the host stack; a node nested deeper
 * is evaluated on the interpreter's explicit stack, like a generator. A level costs two to four
 * host frames.
 */
export const MaxDirectHeight = 64;

const NotDirect = Infinity;

export type Node =
  | Identity
  | Literal
  | Template
  | Format
  | Index
  | Slice
  | Iterate
  | Variable
  | HostVariable
  | EnvVariable
  | CallFunction
  | CallParameter
  | CallNative
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
  | Label
  | Break;

/** What every bound node has; `atMostOne`: it yields zero or one output whatever its input and environment. */
abstract class NodeBase {
  /** Whether Interpreter.evalOne evaluates the node (see the top of this file). */
  readonly direct: boolean;

  protected constructor(
    readonly atMostOne: boolean,
    /** The levels of host recursion of a direct evaluation; Infinity when there is none. */
    readonly height: number,
  ) {
    this.direct = height <= MaxDirectHeight;
  }
}

function all(nodes: readonly Node[]): boolean {
  for (const node of nodes) {
    if (!node.atMostOne) return false;
  }

  return true;
}

function one(node: Node | null): boolean {
  return node === null || node.atMostOne;
}

/** Whether the arguments in `slots` are all single: what makes a call single (spec §7.7). */
export function allSingle(slots: readonly number[], args: readonly Node[]): boolean {
  for (const slot of slots) {
    if (!args[slot]!.atMostOne) return false;
  }

  return true;
}

// The height of a child evaluated by a nested evalOne; 0 for a missing child.
function under(node: Node | null): number {
  return node === null ? 0 : node.height + 1;
}

function underAll(nodes: readonly Node[]): number {
  let height = 1;
  for (const node of nodes) height = Math.max(height, node.height + 1);
  return height;
}

// The height of the operand that continues a chain the interpreter walks in a loop (a left-deep
// chain of operators, of indexes, of minus signs): the same level when it is of the chain's kind.
function chained(node: Node, inChain: boolean): number {
  return inChain ? node.height : node.height + 1;
}

export class Identity extends NodeBase {
  readonly kind = NodeKind.Identity;
  constructor() {
    super(true, 1);
  }
}

export class Literal extends NodeBase {
  readonly kind = NodeKind.Literal;
  constructor(readonly value: JqValue) {
    super(true, 1);
  }
}

/** An interpolated string; see the AST's Template. */
export class Template extends NodeBase {
  readonly kind = NodeKind.Template;
  constructor(
    readonly format: string | null,
    readonly texts: readonly string[],
    readonly parts: readonly Node[],
  ) {
    super(all(parts), underAll(parts));
  }
}

export class Format extends NodeBase {
  readonly kind = NodeKind.Format;
  constructor(readonly name: string) {
    super(true, 1);
  }
}

export class Index extends NodeBase {
  readonly kind = NodeKind.Index;
  constructor(
    readonly target: Node,
    readonly key: Node,
  ) {
    super(target.atMostOne && key.atMostOne, Math.max(postfixTarget(target), under(key)));
  }
}

export class Slice extends NodeBase {
  readonly kind = NodeKind.Slice;
  constructor(
    readonly target: Node,
    readonly from: Node | null,
    readonly to: Node | null,
  ) {
    super(target.atMostOne && one(from) && one(to), Math.max(postfixTarget(target), under(from), under(to)));
  }
}

function postfixTarget(target: Node): number {
  return chained(target, target.kind === NodeKind.Index || target.kind === NodeKind.Slice);
}

export class Iterate extends NodeBase {
  readonly kind = NodeKind.Iterate;
  constructor(readonly target: Node) {
    super(false, NotDirect);
  }
}

/** A lexical variable: slot `slot` of the frame `hops` frames out. */
export class Variable extends NodeBase {
  readonly kind = NodeKind.Variable;
  constructor(
    readonly hops: number,
    readonly slot: number,
  ) {
    super(true, 1);
  }
}

/** A `$name` without lexical binding: entry `index` of BoundProgram.hostVariables. */
export class HostVariable extends NodeBase {
  readonly kind = NodeKind.HostVariable;
  constructor(
    readonly name: string,
    readonly index: number,
  ) {
    super(true, 1);
  }
}

/** `$ENV` where no binding shadows it. */
export class EnvVariable extends NodeBase {
  readonly kind = NodeKind.EnvVariable;
  constructor() {
    super(true, 1);
  }
}

/**
 * A call of BoundProgram.functions[function], defined `hops` frames out. `single`: the function
 * is single and the argument of each of its `$` parameters is (spec §7.7).
 */
export class CallFunction extends NodeBase {
  readonly kind = NodeKind.CallFunction;
  readonly function: number;
  readonly arguments: readonly Node[];
  constructor(
    func: number,
    readonly hops: number,
    args: readonly Node[],
    readonly single: boolean,
  ) {
    super(single, NotDirect);
    this.function = func;
    this.arguments = args;
  }
}

/** A call of a closure parameter: slot `slot` of the call frame `hops` frames out. */
export class CallParameter extends NodeBase {
  readonly kind = NodeKind.CallParameter;
  constructor(
    readonly hops: number,
    readonly slot: number,
  ) {
    super(false, NotDirect);
  }
}

/** A call of a native; the arguments are closures over the caller's environment. */
export class CallNative extends NodeBase {
  readonly kind = NodeKind.CallNative;
  readonly arguments: readonly Node[];
  constructor(
    readonly native: Native,
    args: readonly Node[],
  ) {
    super(native.singleWhen !== null && allSingle(native.singleWhen, args), native.takesClosures ? NotDirect : underAll(args));
    this.arguments = args;
  }
}

export class Pipe extends NodeBase {
  readonly kind = NodeKind.Pipe;
  constructor(
    readonly left: Node,
    readonly right: Node,
  ) {
    super(left.atMostOne && right.atMostOne, Math.max(under(left), right.height));
  }
}

/** `a, b, c`: a left-nested chain of commas is one node, so that evaluating it does not recurse per term. */
export class Comma extends NodeBase {
  readonly kind = NodeKind.Comma;
  constructor(readonly items: readonly Node[]) {
    super(false, NotDirect);
  }
}

export class Binary extends NodeBase {
  readonly kind = NodeKind.Binary;
  constructor(
    readonly op: BinaryOp,
    readonly left: Node,
    readonly right: Node,
  ) {
    super(left.atMostOne && right.atMostOne, Math.max(chained(left, left.kind === NodeKind.Binary), under(right)));
  }
}

function logicLeft(left: Node): number {
  return chained(left, left.kind === NodeKind.And || left.kind === NodeKind.Or);
}

export class And extends NodeBase {
  readonly kind = NodeKind.And;
  constructor(
    readonly left: Node,
    readonly right: Node,
  ) {
    super(left.atMostOne && right.atMostOne, Math.max(logicLeft(left), under(right)));
  }
}

export class Or extends NodeBase {
  readonly kind = NodeKind.Or;
  constructor(
    readonly left: Node,
    readonly right: Node,
  ) {
    super(left.atMostOne && right.atMostOne, Math.max(logicLeft(left), under(right)));
  }
}

export class Alternative extends NodeBase {
  readonly kind = NodeKind.Alternative;
  constructor(
    readonly left: Node,
    readonly right: Node,
  ) {
    super(left.atMostOne && right.atMostOne, Math.max(under(left), right.height));
  }
}

export class Negate extends NodeBase {
  readonly kind = NodeKind.Negate;
  constructor(readonly operand: Node) {
    super(operand.atMostOne, chained(operand, operand.kind === NodeKind.Negate));
  }
}

export class ArrayExpr extends NodeBase {
  readonly kind = NodeKind.ArrayExpr;
  constructor(readonly body: Node | null) {
    // Collecting the outputs of a generator is not a direct evaluation.
    super(true, body === null ? 1 : body.atMostOne ? under(body) : NotDirect);
  }
}

export class ObjectExpr extends NodeBase {
  readonly kind = NodeKind.ObjectExpr;
  constructor(readonly entries: readonly ObjectEntry[]) {
    super(allEntries(entries), entriesHeight(entries));
  }
}

function allEntries(entries: readonly ObjectEntry[]): boolean {
  for (const entry of entries) {
    if (!entry.key.atMostOne || !one(entry.value)) return false;
  }

  return true;
}

function entriesHeight(entries: readonly ObjectEntry[]): number {
  let height = 1;
  for (const entry of entries) height = Math.max(height, under(entry.key), under(entry.value));
  return height;
}

/** See the AST's ObjectEntry: a null value is the input indexed by each output of the key. */
export class ObjectEntry {
  constructor(
    readonly key: Node,
    readonly value: Node | null,
  ) {}
}

export class If extends NodeBase {
  readonly kind = NodeKind.If;
  readonly else: Node;
  constructor(
    readonly condition: Node,
    readonly then: Node,
    otherwise: Node,
  ) {
    super(condition.atMostOne && then.atMostOne && otherwise.atMostOne, Math.max(under(condition), then.height, otherwise.height));
    this.else = otherwise;
  }
}

export class Try extends NodeBase {
  readonly kind = NodeKind.Try;
  constructor(
    readonly body: Node,
    readonly handler: Node | null,
  ) {
    super(body.atMostOne && one(handler), Math.max(under(body), handler === null ? 0 : handler.height));
  }
}

/** `update` runs with the binder's frame pushed; `init` without it. */
export class Reduce extends NodeBase {
  readonly kind = NodeKind.Reduce;
  constructor(
    readonly source: Node,
    readonly binder: Binder,
    readonly init: Node,
    readonly update: Node,
  ) {
    super(init.atMostOne, NotDirect);
  }
}

/** `update` and `extract` run with the binder's frame pushed. */
export class Foreach extends NodeBase {
  readonly kind = NodeKind.Foreach;
  constructor(
    readonly source: Node,
    readonly binder: Binder,
    readonly init: Node,
    readonly update: Node,
    readonly extract: Node,
  ) {
    super(false, NotDirect);
  }
}

/** `Source as Patterns | Body`: `body` runs with the binder's frame pushed. */
export class Bind extends NodeBase {
  readonly kind = NodeKind.Bind;
  constructor(
    readonly source: Node,
    readonly binder: Binder,
    readonly body: Node,
  ) {
    // With one pattern the body is a tail position of evalOne; with several it is a nested call.
    super(
      source.atMostOne && binder.keysAtMostOne && body.atMostOne,
      Math.max(under(source), binder.keysHeight + 1, binder.patterns.length === 1 ? body.height : under(body)),
    );
  }
}

/** `body` runs with a frame holding a new label instance. */
export class Label extends NodeBase {
  readonly kind = NodeKind.Label;
  constructor(readonly body: Node) {
    super(body.atMostOne, under(body));
  }
}

/** Breaks to the label instance of the frame `hops` frames out. */
export class Break extends NodeBase {
  readonly kind = NodeKind.Break;
  constructor(readonly hops: number) {
    super(true, 1);
  }
}

/**
 * The patterns of one binding (several are the `?//` alternatives) and the size of the frame
 * they fill: one slot per distinct variable name of all of them, null until a pattern binds it.
 */
export class Binder {
  /** Every key expression of the patterns yields at most one output, so a value binds at most once per pattern. */
  readonly keysAtMostOne: boolean;
  /** The greatest height of the key expressions: Infinity when one cannot be evaluated directly. */
  readonly keysHeight: number;
  /** Every key expression is direct: Interpreter.matchOne destructures a value. */
  readonly keysDirect: boolean;

  constructor(
    readonly patterns: readonly Pattern[],
    readonly slots: number,
  ) {
    this.keysAtMostOne = allKeys(patterns);
    this.keysHeight = keysHeight(patterns);
    this.keysDirect = this.keysHeight <= MaxDirectHeight;
  }
}

function allKeys(patterns: readonly Pattern[]): boolean {
  for (const pattern of patterns) {
    if (!keysOf(pattern)) return false;
  }

  return true;
}

function keysOf(pattern: Pattern): boolean {
  switch (pattern.kind) {
    case PatternKind.Array:
      return allKeys(pattern.elements);
    case PatternKind.Object:
      for (const entry of pattern.entries) {
        if (!entry.key.atMostOne || (entry.value !== null && !keysOf(entry.value))) return false;
      }

      return true;
    default:
      return true;
  }
}

function keysHeight(patterns: readonly Pattern[]): number {
  let height = 0;
  for (const pattern of patterns) {
    switch (pattern.kind) {
      case PatternKind.Array:
        height = Math.max(height, keysHeight(pattern.elements));
        break;
      case PatternKind.Object:
        for (const entry of pattern.entries) {
          height = Math.max(height, entry.key.height);
          if (entry.value !== null) height = Math.max(height, keysHeight([entry.value]));
        }

        break;
    }
  }

  return height;
}

export const enum PatternKind {
  Variable,
  Array,
  Object,
}

export type Pattern = VariablePattern | ArrayPattern | ObjectPattern;

/**
 * Stores the value in `slot`; -1 discards it. Patterns are matched in order (array elements from
 * the last, object entries from the first), so a slot written twice keeps the later value; the
 * resolver gives -1 to the repetitions that must not win (spec §7.5).
 */
export class VariablePattern {
  readonly kind = PatternKind.Variable;
  constructor(readonly slot: number) {}
}

export class ArrayPattern {
  readonly kind = PatternKind.Array;
  constructor(readonly elements: readonly Pattern[]) {}
}

export class ObjectPattern {
  readonly kind = PatternKind.Object;
  constructor(readonly entries: readonly PatternEntry[]) {}
}

/**
 * See the AST's PatternEntry; `slot` is -1 without a variable. The key runs in the scope outside
 * the binding: the pattern's own variables are not visible in it.
 */
export class PatternEntry {
  constructor(
    readonly key: Node,
    readonly slot: number,
    readonly value: Pattern | null,
  ) {}
}

/**
 * A jqlite-defined function (user or prelude). Its body runs with its parameters' frame pushed
 * when `arity` > 0. `single`: the body yields at most one output for each binding of the `$`
 * parameters, whose slots are `valueParameters` (spec §7.7).
 */
export class Function {
  constructor(
    readonly name: string,
    readonly arity: number,
    readonly body: Node,
    readonly single: boolean,
    readonly valueParameters: readonly number[],
  ) {}

  toString(): string {
    return `${this.name}/${this.arity}`;
  }
}

/**
 * A bound program: immutable and shareable. `functions` starts with the prelude's functions
 * (shared by every program), followed by the program's own.
 */
export class BoundProgram {
  constructor(
    readonly functions: readonly Function[],
    readonly hostVariables: readonly string[],
    readonly body: Node,
    /** The modules loaded, for `modulemeta`; null when the host gave no module source (§9.11). */
    readonly modules: ModuleTable | null = null,
  ) {}
}
