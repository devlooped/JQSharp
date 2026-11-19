import {
  type Alternative,
  type And,
  type ArrayExpr,
  type ArrayPattern,
  type Binary,
  type Bind,
  type Binder,
  type BoundProgram,
  type Break,
  type CallNative,
  type Comma,
  type Foreach,
  type Format,
  type HostVariable,
  type If,
  type Index,
  type Iterate,
  type Label,
  type Negate,
  type Node,
  NodeKind,
  type ObjectEntry,
  type ObjectExpr,
  type ObjectPattern,
  type Or,
  type Pattern,
  PatternKind,
  type Pipe,
  type Reduce,
  type Slice,
  type Template,
  type Try,
} from "../binding/bound.js";
import * as Formats from "../builtins/formats.js";
import * as NativeTable from "../builtins/nativeTable.js";
import type { ValueNative } from "../builtins/nativeTable.js";
import { BinaryOp } from "../syntax/ast.js";
import * as Errors from "../values/errors.js";
import { JqArray } from "../values/jqArray.js";
import { JqObject } from "../values/jqObject.js";
import { Fail, JqKind, type JqValue, isTruthy, kindOf } from "../values/jqValue.js";
import * as ValueOps from "../values/valueOps.js";
import * as ValueText from "../values/valueText.js";
import { Closure, Env } from "./env.js";
import { type EvaluationOptions, type IClock, Outcome, OutcomeKind } from "./evaluation.js";
import { Path } from "./path.js";
import { Signal, SignalKind } from "./signal.js";

/**
 * Receives one output of an evaluation and schedules what consumes it (`Interpreter.eval`,
 * `output` or `ret`); it calls nothing itself.
 */
export type Emit = (value: JqValue, path: Path | null) => void;

/**
 * What a construct does when an evaluation it started returns: `signal` is null when the
 * evaluation is done, or the signal that stopped it. Like an `Emit`, it schedules one step.
 */
export type After = (signal: Signal | null) => void;

// What the interpreter does next.
const enum Mode {
  // Nothing scheduled: a step that leaves this is a defect.
  Idle,
  // Evaluate a node.
  Eval,
  // Give an output to its consumer.
  Output,
  // Return to the construct on top of the stack.
  Return,
}

/**
 * The push-based interpreter of a bound program (spec §6 to §8). One instance is one
 * evaluation: it holds every piece of per-evaluation state (host variables, `$ENV`, the call
 * depth, label numbers, the stack), while the bound program and the prelude are immutable and
 * shared.
 *
 * The reference's `Eval(node, env, input, path, emit)` evaluates a node, pushes each output to
 * `emit` and returns null or the signal that stopped it; it recurses on the host stack and
 * continues on a fresh stack when that runs short (StackGuard), which JavaScript cannot do. So
 * this interpreter is a trampoline with the same shape:
 *
 * - `eval`, `forEach`, `output` and `ret` only *schedule* a step (they set the registers of the
 *   interpreter and return), and `run` executes the steps one after the other in a loop. An
 *   `Emit` is called by that loop with an output, and schedules what consumes it.
 * - What the reference does after a call returns (`var signal = Eval(…); …`) is an `After`
 *   pushed on the interpreter's own stack before the call is scheduled (`after`): the loop calls
 *   it with the signal, or null, when the evaluation scheduled above it returns (`ret`). The
 *   reference's `return signal` is `ret(signal)`; a tail call just schedules.
 *
 * Every function that schedules ends by scheduling exactly one step, so nothing of an
 * evaluation recurses on the host stack: not the nesting of the program, not function calls,
 * not generators feeding generators. The call depth of §11 (`MaxDepth`) is therefore the only
 * limit on recursion, whatever its value: the stack is on the heap.
 *
 * `evalOne` is the direct evaluation of the reference's `EvalOne`, kept for the nodes the
 * resolver marks *direct* (bound.ts): those that yield at most one output without calling a
 * function, a closure parameter or a native that takes closures, nested at most
 * MaxDirectHeight deep. It returns the output instead of pushing it and recurses on the host
 * stack, by a bounded amount: arithmetic, indexing, object construction and the like run
 * without callbacks or allocations. Path mode (§8.1) is `path` not null: then every output
 * carries where it came from (see Path).
 */
export class Interpreter {
  /** The program being evaluated. */
  readonly program: BoundProgram;
  // The host variables, by the index of BoundProgram.hostVariables; undefined when the host has none.
  private readonly hostValues: (JqValue | undefined)[];
  private readonly readEnvironment: () => JqValue;
  private environment: JqValue | undefined = undefined;
  private readonly maxDepth: number;
  private depth = 0;
  private labels = 0;

  /** The host's clock, for the date builtins. */
  readonly clock: IClock | null;

  /** The host's handler of `debug`. */
  readonly debug: ((value: JqValue) => void) | null;

  /** The host's handler of `stderr`. */
  readonly stderr: ((value: JqValue) => void) | null;

  // The stack of the constructs waiting for an evaluation to return, and the scheduled step.
  private readonly stack: After[] = [];
  private mode = Mode.Idle;
  private stepNode: Node | null = null;
  private stepEnv: Env | null = null;
  private stepValue: JqValue = null;
  private stepPath: Path | null = null;
  private stepEmit: Emit | null = null;
  private stepSignal: Signal | null = null;

  /** The results of `evalOne` (and of the helpers that say so): whether there is an output, the output and its path. */
  has = false;
  value: JqValue = null;
  at: Path | null = null;

  // Other results of helpers: the frame bindOne made, whether matchOne matched, the key evalEntryOne read.
  private frame: Env | null = null;
  private matched = false;
  private entryKey = "";

  // Scratch stacks of the chain walks of evalOne; each walk uses the part above its mark.
  private readonly spineNodes: Node[] = [];
  private readonly spineValues: JqValue[] = [];

  private constructor(program: BoundProgram, options: EvaluationOptions) {
    this.program = program;
    this.hostValues = [];
    for (const name of program.hostVariables) this.hostValues.push(options.variables === null ? undefined : options.variables.get(name));
    this.readEnvironment = options.environment;
    this.maxDepth = options.maxDepth;
    this.clock = options.clock;
    this.debug = options.debug;
    this.stderr = options.stderr;
  }

  /**
   * Evaluates `program` on `input`, giving each output to `output`, which returns false to stop
   * the stream.
   */
  static run(program: BoundProgram, input: JqValue, options: EvaluationOptions, output: (value: JqValue) => boolean): Outcome {
    const interpreter = new Interpreter(program, options);
    interpreter.eval(program.body, null, input, null, (value) => interpreter.ret(output(value) ? null : Signal.Stop));
    const signal = interpreter.loop();
    if (signal === null) return new Outcome(OutcomeKind.Completed);
    switch (signal.kind) {
      case SignalKind.Error:
        return new Outcome(OutcomeKind.Error, signal.value, true);
      case SignalKind.Halt:
        return new Outcome(OutcomeKind.Halt, signal.value, signal.hasValue, signal.code);
      default:
        // A break always meets its label: the resolver binds every break to an enclosing one.
        return new Outcome(OutcomeKind.Stopped);
    }
  }

  // Runs the scheduled steps until the evaluation returns to an empty stack: its signal, or null.
  private loop(): Signal | null {
    const stack = this.stack;
    while (true) {
      switch (this.mode) {
        case Mode.Eval:
          this.mode = Mode.Idle;
          this.step();
          break;
        case Mode.Output: {
          const emit = this.stepEmit!;
          this.mode = Mode.Idle;
          emit(this.stepValue, this.stepPath);
          break;
        }
        case Mode.Return: {
          if (stack.length === 0) return this.stepSignal;
          const after = stack.pop()!;
          this.mode = Mode.Idle;
          after(this.stepSignal);
          break;
        }
        default:
          throw new Error("A step of the interpreter scheduled nothing.");
      }
    }
  }

  /** Schedules the evaluation of `node`: each output goes to `emit`, and its end to the construct on top of the stack. */
  eval(node: Node, env: Env | null, input: JqValue, path: Path | null, emit: Emit): void {
    this.stepNode = node;
    this.stepEnv = env;
    this.stepValue = input;
    this.stepPath = path;
    this.stepEmit = emit;
    this.mode = Mode.Eval;
  }

  /** Schedules the giving of an output to a consumer. */
  output(emit: Emit, value: JqValue, at: Path | null): void {
    this.stepEmit = emit;
    this.stepValue = value;
    this.stepPath = at;
    this.mode = Mode.Output;
  }

  /** Schedules the return to the construct on top of the stack: `signal` null when the evaluation is done. */
  ret(signal: Signal | null): void {
    this.stepSignal = signal;
    this.mode = Mode.Return;
  }

  /** Schedules the return of an error. */
  fail(error: JqValue): void {
    this.ret(Signal.error(error));
  }

  /** Pushes what to do when the evaluation scheduled next returns. */
  after(then: After): void {
    this.stack.push(then);
  }

  /** Schedules the output of a fallible operation's result, or the return of its error. */
  apply(result: JqValue | Fail, at: Path | null, emit: Emit): void {
    if (result instanceof Fail) this.fail(result.error);
    else this.output(emit, result, at);
  }

  /** The next label instance, unique within this evaluation. */
  newLabel(): number {
    return ++this.labels;
  }

  // Leaves a call: what every call pushes, so that it counts in the depth of §11 until it returns.
  private readonly leave: After = (signal) => {
    this.depth--;
    this.ret(signal);
  };

  // A single call is back in the depth when the consumer of its output returns (leaving).
  private readonly reenter: After = (signal) => {
    this.depth++;
    this.ret(signal);
  };

  // The emit of a single call (spec §7.7): the call has ended when its output is there, so it
  // does not count in the depth of §11 while that output is consumed.
  private leaving(emit: Emit): Emit {
    return (value, at) => {
      this.depth--;
      this.stack.push(this.reenter);
      this.output(emit, value, at);
    };
  }

  // One step of an evaluation: the scheduled node. A loop where the node's last step is another
  // node (a pipe whose left side is direct, a branch of `if`, the body of a binding or of a
  // function, a closure), as the reference's Eval.
  private step(): void {
    let node = this.stepNode!;
    let env = this.stepEnv;
    let input = this.stepValue;
    let path = this.stepPath;
    let emit = this.stepEmit!;
    let signal: Signal | null;
    while (true) {
      if (node.direct) {
        signal = this.evalOne(node, env, input, path);
        if (signal !== null || !this.has) this.ret(signal);
        else emit(this.value, this.at);
        return;
      }

      switch (node.kind) {
        case NodeKind.Comma:
          this.evalComma(node, env, input, path, emit);
          return;
        case NodeKind.Pipe:
          if (!node.left.direct) {
            this.evalPipe(node, env, input, path, emit);
            return;
          }

          signal = this.evalOne(node.left, env, input, path);
          if (signal !== null || !this.has) {
            this.ret(signal);
            return;
          }

          input = this.value;
          path = this.at;
          node = node.right;
          continue;
        case NodeKind.If:
          if (!node.condition.direct) {
            this.evalIf(node, env, input, path, emit);
            return;
          }

          signal = this.evalOne(node.condition, env, input, null);
          if (signal !== null || !this.has) {
            this.ret(signal);
            return;
          }

          node = isTruthy(this.value) ? node.then : node.else;
          continue;
        case NodeKind.Alternative:
          this.evalAlternative(node, env, input, path, emit);
          return;
        case NodeKind.Bind:
          if (!isSimple(node)) {
            this.evalBind(node, env, input, path, emit);
            return;
          }

          signal = this.bindOne(node, env, input);
          if (signal !== null || !this.has) {
            this.ret(signal);
            return;
          }

          env = this.frame;
          node = node.body;
          continue;
        case NodeKind.CallFunction:
          if (this.depth >= this.maxDepth) {
            this.fail(Errors.depthExceeded);
            return;
          }

          // The call stays active, counting in the depth of §11, until it returns: a single call
          // (§7.7) leaves the depth while its one output is consumed.
          this.depth++;
          this.stack.push(this.leave);
          if (node.single) emit = this.leaving(emit);
          env = callEnv(node.hops, node.arguments, env);
          node = this.program.functions[node.function]!.body;
          continue;
        case NodeKind.CallParameter: {
          const closure: Closure = Env.up(env, node.hops)!.closures![node.slot]!;
          env = closure.env;
          node = closure.node;
          continue;
        }
        default:
          this.evalMany(node, env, input, path, emit);
          return;
      }
    }
  }

  // The items of a comma, the last one in tail position.
  private evalComma(node: Comma, env: Env | null, input: JqValue, path: Path | null, emit: Emit): void {
    const items = node.items;
    const last = items.length - 1;
    let i = 0;
    const next: After = (signal) => {
      if (signal !== null) {
        this.ret(signal);
        return;
      }

      if (i < last) this.stack.push(next);
      this.eval(items[i++]!, env, input, path, emit);
    };
    next(null);
  }

  // A binding the step takes in tail position: one pattern, a source and keys that are direct.
  private bindOne(node: Bind, env: Env | null, input: JqValue): Signal | null {
    let signal = this.evalOne(node.source, env, input, null);
    if (signal !== null || !this.has) return signal;
    const slots = newSlots(node.binder.slots);
    signal = this.matchOne(node.binder.patterns[0]!, this.value, slots, env);
    this.has = signal === null && this.matched;
    if (this.has) this.frame = Env.ofValues(env, slots);
    return signal;
  }

  // The constructs of the step that are not in tail position.
  private evalMany(node: Node, env: Env | null, input: JqValue, path: Path | null, emit: Emit): void {
    switch (node.kind) {
      case NodeKind.Iterate:
        this.evalIterate(node, env, input, path, emit);
        break;
      case NodeKind.Index:
        this.evalIndex(node, env, input, path, emit);
        break;
      case NodeKind.Slice:
        this.evalSlice(node, env, input, path, emit);
        break;
      case NodeKind.Binary:
        this.evalBinary(node, env, input, path, emit);
        break;
      case NodeKind.And:
      case NodeKind.Or:
        this.evalLogic(node, env, input, path, emit);
        break;
      case NodeKind.Negate:
        this.evalNegate(node, env, input, path, emit);
        break;
      case NodeKind.ArrayExpr:
        this.evalArray(node, env, input, path, emit);
        break;
      case NodeKind.ObjectExpr:
        this.evalObject(node, 0, JqObject.Empty, env, input, Path.of(path), emit);
        break;
      case NodeKind.Template:
        this.evalTemplate(node, node.parts.length - 1, new Array<string>(node.parts.length), env, input, Path.of(path), emit);
        break;
      case NodeKind.Try:
        this.evalTry(node, env, input, path, emit);
        break;
      case NodeKind.Reduce:
        this.evalReduce(node, env, input, path, emit);
        break;
      case NodeKind.Foreach:
        this.evalForeach(node, env, input, path, emit);
        break;
      case NodeKind.Label:
        this.evalLabel(node, env, input, path, emit);
        break;
      case NodeKind.CallNative:
        this.evalNative(node, env, input, path, emit);
        break;
      default:
        // Every other kind is direct, or a tail position of the step.
        throw new Error(`Node kind ${node.kind} is not evaluated here.`);
    }
  }

  private evalPipe(node: Pipe, env: Env | null, input: JqValue, path: Path | null, emit: Emit): void {
    this.eval(node.left, env, input, path, (value, at) => this.eval(node.right, env, value, at, emit));
  }

  private evalIf(node: If, env: Env | null, input: JqValue, path: Path | null, emit: Emit): void {
    this.eval(node.condition, env, input, null, (condition) => this.eval(isTruthy(condition) ? node.then : node.else, env, input, path, emit));
  }

  private evalNegate(node: Negate, env: Env | null, input: JqValue, path: Path | null, emit: Emit): void {
    const at = Path.of(path);
    this.forEach(node.operand, env, input, null, (value) => this.apply(ValueOps.tryNegate(value), at, emit));
  }

  // [f] for an f that is not direct: its outputs are collected.
  private evalArray(node: ArrayExpr, env: Env | null, input: JqValue, path: Path | null, emit: Emit): void {
    const items: JqValue[] = [];
    this.stack.push((signal) => {
      if (signal !== null) this.ret(signal);
      else this.output(emit, JqArray.fromOwned(items), Path.of(path));
    });
    this.eval(node.body!, env, input, null, (item) => {
      items.push(item);
      this.ret(null);
    });
  }

  /**
   * Evaluates a direct node (Node.direct), on the host stack: `has` tells whether it gave an
   * output, and `value` and `at` are the output and its path. Any signal returned is the node's
   * own: there is no consumer. The three results are fields of the interpreter, which the next
   * call overwrites: read them at once.
   */
  evalOne(node: Node, env: Env | null, input: JqValue, path: Path | null): Signal | null {
    let signal: Signal | null;
    while (true) {
      switch (node.kind) {
        case NodeKind.Identity:
          this.has = true;
          this.value = input;
          this.at = path;
          return null;
        case NodeKind.Literal:
          this.has = true;
          this.value = node.value;
          this.at = path === null ? null : Path.Untracked;
          return null;
        case NodeKind.Variable:
          this.has = true;
          this.value = Env.up(env, node.hops)!.values![node.slot]!;
          this.at = path === null ? null : Path.Untracked;
          return null;
        case NodeKind.Pipe:
          signal = this.evalOne(node.left, env, input, path);
          if (signal !== null || !this.has) return signal;
          input = this.value;
          path = this.at;
          node = node.right;
          continue;
        case NodeKind.If:
          signal = this.evalOne(node.condition, env, input, null);
          if (signal !== null || !this.has) return signal;
          node = isTruthy(this.value) ? node.then : node.else;
          continue;
        case NodeKind.Alternative:
          signal = this.evalOne(node.left, env, input, path);
          if (signal !== null || (this.has && isTruthy(this.value))) return signal;
          node = node.right;
          continue;
        case NodeKind.Try:
          signal = this.evalOne(node.body, env, input, path);
          if (signal === null || signal.kind !== SignalKind.Error) return signal;
          this.has = false;
          if (node.handler === null) return null;
          input = signal.value;
          path = Path.of(path);
          node = node.handler;
          continue;
        case NodeKind.Bind:
          if (node.binder.patterns.length !== 1) return this.evalBindOne(node, env, input, path);
          signal = this.bindOne(node, env, input);
          if (signal !== null || !this.has) return signal;
          env = this.frame;
          node = node.body;
          continue;
        default:
          return this.evalOther(node, env, input, path);
      }
    }
  }

  // The nodes of evalOne that are not in tail position.
  private evalOther(node: Node, env: Env | null, input: JqValue, path: Path | null): Signal | null {
    let signal: Signal | null;
    switch (node.kind) {
      case NodeKind.Index:
      case NodeKind.Slice:
        return this.evalPostfixOne(node, env, input, path);
      case NodeKind.Binary:
        signal = this.evalBinaryOne(node, env, input);
        break;
      case NodeKind.And:
      case NodeKind.Or:
        signal = this.evalLogicOne(node, env, input);
        break;
      case NodeKind.Negate:
        signal = this.evalNegateOne(node, env, input);
        break;
      case NodeKind.ArrayExpr:
        signal = this.evalArrayOne(node, env, input);
        break;
      case NodeKind.ObjectExpr:
        signal = this.evalObjectOne(node, env, input);
        break;
      case NodeKind.Template:
        signal = this.evalTemplateOne(node, env, input);
        break;
      case NodeKind.CallNative:
        signal = this.evalNativeOne(node, env, input);
        break;
      case NodeKind.Label:
        return this.evalLabelOne(node, env, input, path);
      default:
        signal = this.evalLeafOne(node as HostVariable | Format | Break, env, input);
        break;
    }

    // These give a value that has no path.
    this.at = path === null ? null : Path.Untracked;
    return signal;
  }

  // Variables, formats and break: no children.
  private evalLeafOne(node: Node, env: Env | null, input: JqValue): Signal | null {
    this.has = false;
    switch (node.kind) {
      case NodeKind.HostVariable: {
        const hostValue = this.hostValues[node.index];
        if (hostValue === undefined) return Signal.error(Errors.variableNotDefined(node.name));
        this.has = true;
        this.value = hostValue;
        return null;
      }
      case NodeKind.EnvVariable:
        this.has = true;
        this.value = this.environment ??= this.readEnvironment();
        return null;
      case NodeKind.Format: {
        const formatted = Formats.tryApply(node.name, input);
        if (formatted instanceof Fail) return Signal.error(formatted.error);
        this.has = true;
        this.value = formatted;
        return null;
      }
      default:
        return Signal.break(Env.up(env, (node as Break).hops)!.label);
    }
  }

  private evalLabelOne(node: Label, env: Env | null, input: JqValue, path: Path | null): Signal | null {
    const label = ++this.labels;
    const signal = this.evalOne(node.body, Env.ofLabel(env, label), input, path);
    if (signal === null || signal.kind !== SignalKind.Break || signal.label !== label) return signal;
    this.has = false;
    return null;
  }

  /**
   * Schedules the evaluation of `node` with each output going to `each`; a direct node is
   * evaluated at once, without a step.
   */
  forEach(node: Node, env: Env | null, input: JqValue, path: Path | null, each: Emit): void {
    if (!node.direct) {
      this.eval(node, env, input, path, each);
      return;
    }

    const signal = this.evalOne(node, env, input, path);
    if (signal !== null || !this.has) this.ret(signal);
    else this.output(each, this.value, this.at);
  }

  // a // b: the true outputs of a; then b, when there was none.
  private evalAlternative(node: Alternative, env: Env | null, input: JqValue, path: Path | null, emit: Emit): void {
    let any = false;
    this.stack.push((signal) => {
      if (signal !== null || any) this.ret(signal);
      else this.eval(node.right, env, input, path, emit);
    });
    this.forEach(node.left, env, input, path, (value, at) => {
      if (!isTruthy(value)) {
        this.ret(null);
        return;
      }

      any = true;
      this.output(emit, value, at);
    });
  }

  // T[]: in path mode each element's path is the target's plus its index or key.
  private evalIterate(node: Iterate, env: Env | null, input: JqValue, path: Path | null, emit: Emit): void {
    this.forEach(node.target, env, input, path, (target, at) => {
      if (at !== null && !at.isTracked) {
        this.fail(Errors.invalidPathIterate(target));
        return;
      }

      const kind = kindOf(target);
      if (kind === JqKind.Array) this.iterate(target as JqArray, null, at, emit);
      else if (kind === JqKind.Object) this.iterate(null, target as JqObject, at, emit);
      else this.fail(Errors.cannotIterate(target));
    });
  }

  // The elements of an array, or the values of an object, one after the other; the last one in tail position.
  private iterate(array: JqArray | null, obj: JqObject | null, at: Path | null, emit: Emit): void {
    const count = array !== null ? array.count : obj!.count;
    if (count === 0) {
      this.ret(null);
      return;
    }

    let i = 0;
    const next: After = (signal) => {
      if (signal !== null) {
        this.ret(signal);
        return;
      }

      const index = i++;
      if (i < count) this.stack.push(next);
      if (array !== null) this.output(emit, array.get(index), at === null ? null : at.append(index));
      else this.output(emit, obj!.valueAt(index), at === null ? null : at.append(obj!.keyAt(index)));
    };
    next(null);
  }

  // T[e]: for each key (on the input of T, in value mode), for each target (§6.3).
  private evalIndex(node: Index, env: Env | null, input: JqValue, path: Path | null, emit: Emit): void {
    this.forEach(node.key, env, input, null, (key) =>
      this.forEach(node.target, env, input, path, (target, at) => {
        const signal = this.indexStep(target, at, key);
        if (signal !== null) this.ret(signal);
        else this.output(emit, this.value, this.at);
      }),
    );
  }

  // T[from:to]: from outermost, then to, then T (§6.3).
  private evalSlice(node: Slice, env: Env | null, input: JqValue, path: Path | null, emit: Emit): void {
    this.bound(node.from, env, input, (from) =>
      this.bound(node.to, env, input, (to) =>
        this.forEach(node.target, env, input, path, (target, at) => {
          const signal = this.sliceStep(target, at, from, to);
          if (signal !== null) this.ret(signal);
          else this.output(emit, this.value, this.at);
        }),
      ),
    );
  }

  // A slice bound: null when absent.
  private bound(bound: Node | null, env: Env | null, input: JqValue, each: Emit): void {
    if (bound === null) each(null, null);
    else this.forEach(bound, env, input, null, each);
  }

  // One index of a target with its path, into value and at: in path mode the target must be tracked.
  private indexStep(target: JqValue, at: Path | null, key: JqValue): Signal | null {
    if (at !== null && !at.isTracked) return Signal.error(Errors.invalidPathAccess(key, target));

    const result = ValueOps.tryIndex(target, key);
    if (result instanceof Fail) return Signal.error(result.error);
    this.value = result;
    this.at = at === null ? null : at.append(key);
    return null;
  }

  // One slice of a target with its path, into value and at; in a path the slice is the key {"start": from, "end": to} (§8.1).
  private sliceStep(target: JqValue, at: Path | null, from: JqValue, to: JqValue): Signal | null {
    let next = at;
    if (at !== null) {
      const key = JqObject.Empty.set("start", from).set("end", to);
      if (!at.isTracked) return Signal.error(Errors.invalidPathAccess(key, target));
      next = at.append(key);
    }

    const result = ValueOps.trySlice(target, from, to);
    if (result instanceof Fail) return Signal.error(result.error);
    this.value = result;
    this.at = next;
    return null;
  }

  // a op b: for each right output, for each left output (§6.3); operands in value mode. A right
  // operand that is direct is evaluated first, so the common case has one callback.
  private evalBinary(node: Binary, env: Env | null, input: JqValue, path: Path | null, emit: Emit): void {
    const at = Path.of(path);
    if (!node.right.direct) {
      this.eval(node.right, env, input, null, (right) => this.evalLeft(node, right, env, input, at, emit));
      return;
    }

    const signal = this.evalOne(node.right, env, input, null);
    if (signal !== null || !this.has) this.ret(signal);
    else this.evalLeft(node, this.value, env, input, at, emit);
  }

  private evalLeft(node: Binary, right: JqValue, env: Env | null, input: JqValue, at: Path | null, emit: Emit): void {
    if (!node.left.direct) {
      this.eval(node.left, env, input, null, (left) => this.apply(tryApply(node.op, left, right), at, emit));
      return;
    }

    const signal = this.evalOne(node.left, env, input, null);
    if (signal !== null || !this.has) this.ret(signal);
    else this.apply(tryApply(node.op, this.value, right), at, emit);
  }

  // a and b, a or b (§7.3): for each left output, the right side only when the left does not decide.
  private evalLogic(node: And | Or, env: Env | null, input: JqValue, path: Path | null, emit: Emit): void {
    const isAnd = node.kind === NodeKind.And;
    const at = Path.of(path);
    this.forEach(node.left, env, input, null, (first) => {
      if (isTruthy(first) !== isAnd) this.output(emit, !isAnd, at);
      else this.forEach(node.right, env, input, null, (second) => this.output(emit, isTruthy(second), at));
    });
  }

  // {k1: v1, …} from entry i on, with the entries before it in obj: keys and values in value
  // mode, k1 outermost (§6.3). Entries that are direct are taken in a loop.
  private evalObject(node: ObjectExpr, i: number, obj: JqObject, env: Env | null, input: JqValue, at: Path | null, emit: Emit): void {
    const entries = node.entries;
    for (; i < entries.length; i++) {
      const entry = entries[i]!;
      if (!entry.key.direct || (entry.value !== null && !entry.value.direct)) break;
      const signal = this.evalEntryOne(entry, env, input);
      if (signal !== null || !this.has) {
        this.ret(signal);
        return;
      }

      obj = obj.set(this.entryKey, this.value);
    }

    if (i === entries.length) {
      this.output(emit, obj, at);
      return;
    }

    const next = entries[i]!;
    this.forEach(next.key, env, input, null, (key) => {
      if (typeof key !== "string") {
        this.fail(Errors.cannotUseAsKey(key));
        return;
      }

      if (next.value === null) {
        const indexed = ValueOps.tryIndex(input, key);
        if (indexed instanceof Fail) this.fail(indexed.error);
        else this.evalObject(node, i + 1, obj.set(key, indexed), env, input, at, emit);
        return;
      }

      this.forEach(next.value, env, input, null, (value) => this.evalObject(node, i + 1, obj.set(key, value), env, input, at, emit));
    });
  }

  // One entry whose key and value are direct: the key in entryKey, the value in value.
  private evalEntryOne(entry: ObjectEntry, env: Env | null, input: JqValue): Signal | null {
    let signal = this.evalOne(entry.key, env, input, null);
    if (signal !== null || !this.has) return signal;
    const name = this.value;
    if (typeof name !== "string") return Signal.error(Errors.cannotUseAsKey(name));
    if (entry.value === null) {
      const indexed = ValueOps.tryIndex(input, name);
      if (indexed instanceof Fail) return Signal.error(indexed.error);
      this.entryKey = name;
      this.value = indexed;
      return null;
    }

    // The value may be an object too: the key is stored after it.
    signal = this.evalOne(entry.value, env, input, null);
    this.entryKey = name;
    return signal;
  }

  // "a\(x)b\(y)": the parts from part i down to the first, the last part outermost (§6.3).
  private evalTemplate(node: Template, i: number, texts: string[], env: Env | null, input: JqValue, at: Path | null, emit: Emit): void {
    if (i < 0) {
      this.output(emit, join(node, texts), at);
      return;
    }

    this.forEach(node.parts[i]!, env, input, null, (part) => {
      const text = convert(node.format, part);
      if (text instanceof Fail) {
        this.fail(text.error);
        return;
      }

      texts[i] = text;
      this.evalTemplate(node, i - 1, texts, env, input, at, emit);
    });
  }

  // try B catch H (§7.13): only B's own errors are caught, never one its consumer returned (§6.2).
  private evalTry(node: Try, env: Env | null, input: JqValue, path: Path | null, emit: Emit): void {
    const consumer = new Consumer(this, emit);
    this.stack.push((signal) => {
      if (signal === null || signal.kind !== SignalKind.Error || consumer.raised(signal)) this.ret(signal);
      else if (node.handler === null) this.ret(null);
      else this.eval(node.handler, env, signal.value, Path.of(path), emit);
    });
    this.eval(node.body, env, input, path, consumer.emit);
  }

  private evalLabel(node: Label, env: Env | null, input: JqValue, path: Path | null, emit: Emit): void {
    const label = ++this.labels;
    this.stack.push((signal) => this.ret(signal !== null && signal.kind === SignalKind.Break && signal.label === label ? null : signal));
    this.eval(node.body, Env.ofLabel(env, label), input, path, emit);
  }

  // E as P1 ?// P2 | B, in general: for each output of E (value mode), each binding, B in path mode.
  private evalBind(node: Bind, env: Env | null, input: JqValue, path: Path | null, emit: Emit): void {
    const consumer = node.binder.patterns.length > 1 ? new Consumer(this, emit) : null;
    const body = consumer === null ? emit : consumer.emit;
    this.forEach(node.source, env, input, null, (source) => this.bindEach(node.binder, source, env, (frame) => this.eval(node.body, frame, input, path, body), consumer, null));
  }

  // E as P1 ?// P2 | B where E, the keys and B are direct.
  private evalBindOne(node: Bind, env: Env | null, input: JqValue, path: Path | null): Signal | null {
    let signal = this.evalOne(node.source, env, input, null);
    if (signal !== null || !this.has) return signal;
    const source = this.value;
    const patterns = node.binder.patterns;
    for (let i = 0; i < patterns.length; i++) {
      const slots = newSlots(node.binder.slots);
      signal = this.matchOne(patterns[i]!, source, slots, env);
      this.has = this.matched;
      if (signal === null && this.matched) signal = this.evalOne(node.body, Env.ofValues(env, slots), input, path);
      if (signal === null || signal.kind !== SignalKind.Error || i === patterns.length - 1) return signal;
      this.has = false;
    }

    return null;
  }

  /**
   * Binds `value` by the binder's patterns and runs `body` (which schedules an evaluation) in
   * the environment with the new frame, once per binding (several when a key has several
   * outputs). With `?//` alternatives (§7.5), when matching fails or the body raises its own
   * error (not one of `consumer`), the next pattern is tried with the same value, all variables
   * null again, after restoring what `save` saved before the first (the state of reduce and
   * foreach); errors with the last pattern propagate.
   */
  bindEach(binder: Binder, value: JqValue, env: Env | null, body: (frame: Env) => void, consumer: Consumer | null, save: (() => () => void) | null): void {
    const patterns = binder.patterns;
    if (patterns.length === 1) {
      this.bindWith(binder, patterns[0]!, value, env, body);
      return;
    }

    const restore = save === null ? null : save();
    const last = patterns.length - 1;
    let i = 0;
    const next: After = (signal) => {
      if (signal === null || signal.kind !== SignalKind.Error || (consumer !== null && consumer.raised(signal))) {
        this.ret(signal);
        return;
      }

      if (restore !== null) restore();
      if (++i < last) this.stack.push(next);
      this.bindWith(binder, patterns[i]!, value, env, body);
    };
    this.stack.push(next);
    this.bindWith(binder, patterns[0]!, value, env, body);
  }

  // One pattern of bindEach: the body once per binding of the value.
  private bindWith(binder: Binder, pattern: Pattern, value: JqValue, env: Env | null, body: (frame: Env) => void): void {
    const slots = newSlots(binder.slots);
    if (!binder.keysDirect) {
      this.match(pattern, value, slots, env, () => body(Env.ofValues(env, slots.slice())));
      return;
    }

    const signal = this.matchOne(pattern, value, slots, env);
    if (signal !== null || !this.matched) this.ret(signal);
    else body(Env.ofValues(env, slots));
  }

  // Destructures value by a pattern whose keys are direct; matched is false when a key yields no
  // output. Array elements from the last, object entries from the first (§7.5); the keys are
  // evaluated in the binding's outer environment, on the value being matched.
  private matchOne(pattern: Pattern, value: JqValue, slots: JqValue[], env: Env | null): Signal | null {
    this.matched = true;
    switch (pattern.kind) {
      case PatternKind.Variable:
        if (pattern.slot >= 0) slots[pattern.slot] = value;
        return null;
      case PatternKind.Array:
        for (let i = pattern.elements.length - 1; i >= 0; i--) {
          const element = ValueOps.tryIndex(value, i);
          if (element instanceof Fail) return Signal.error(element.error);
          const signal = this.matchOne(pattern.elements[i]!, element, slots, env);
          if (signal !== null || !this.matched) return signal;
        }

        return null;
      default:
        for (const entry of pattern.entries) {
          let signal = this.evalOne(entry.key, env, value, null);
          this.matched = this.has;
          if (signal !== null || !this.matched) return signal;
          const member = ValueOps.tryIndex(value, this.value);
          if (member instanceof Fail) return Signal.error(member.error);
          if (entry.slot >= 0) slots[entry.slot] = member;
          if (entry.value !== null) {
            signal = this.matchOne(entry.value, member, slots, env);
            if (signal !== null || !this.matched) return signal;
          }
        }

        return null;
    }
  }

  // Destructures value when a key may yield several outputs: next runs once per complete binding.
  // The value of a key of an object pattern is v[k] with the indexing rules of §7.6, whatever the
  // kind of the key (§7.5; only literal keys are checked, statically).
  private match(pattern: Pattern, value: JqValue, slots: JqValue[], env: Env | null, next: () => void): void {
    if (pattern.kind === PatternKind.Variable) {
      if (pattern.slot >= 0) slots[pattern.slot] = value;
      next();
    } else {
      this.matchRest(Rest.of(pattern, value, null), slots, env, next);
    }
  }

  // Destructures what is left, in a loop: a pattern in a pattern is one more thing left, not a
  // call. A key is evaluated as a step of its own, and each of its outputs goes on from there
  // with what was left then.
  private matchRest(start: Rest | null, slots: JqValue[], env: Env | null, next: () => void): void {
    let rest = start;
    while (rest !== null) {
      const pattern = rest.pattern;
      const value = rest.value;
      const i = rest.index;
      if (pattern.kind === PatternKind.Array) {
        if (i < 0) {
          rest = rest.then;
          continue;
        }

        const element = ValueOps.tryIndex(value, i);
        if (element instanceof Fail) {
          this.fail(element.error);
          return;
        }

        const inner = pattern.elements[i]!;
        rest = new Rest(pattern, i - 1, value, rest.then);
        if (inner.kind !== PatternKind.Variable) rest = Rest.of(inner, element, rest);
        else if (inner.slot >= 0) slots[inner.slot] = element;
        continue;
      }

      if (i === pattern.entries.length) {
        rest = rest.then;
        continue;
      }

      const entry = pattern.entries[i]!;
      const after = new Rest(pattern, i + 1, value, rest.then);
      this.forEach(entry.key, env, value, null, (key) => {
        const member = ValueOps.tryIndex(value, key);
        if (member instanceof Fail) {
          this.fail(member.error);
          return;
        }

        if (entry.slot >= 0) slots[entry.slot] = member;
        const inner = entry.value;
        if (inner !== null && inner.kind === PatternKind.Variable && inner.slot >= 0) slots[inner.slot] = member;
        this.matchRest(inner === null || inner.kind === PatternKind.Variable ? after : Rest.of(inner, member, after), slots, env, next);
      });
      return;
    }

    next();
  }

  // reduce SRC as P (INIT; UPDATE) (§7.8): one reduction per initial value; the state is the last
  // output of UPDATE, or null. In path mode the state carries its path. With ?// alternatives, a
  // retried alternative starts from the state before the source value.
  private evalReduce(node: Reduce, env: Env | null, input: JqValue, path: Path | null, emit: Emit): void {
    this.forEach(node.init, env, input, path, (initial, initialAt) => {
      const state = new State(initial, initialAt);
      this.stack.push((signal) => {
        if (signal !== null) this.ret(signal);
        else this.output(emit, state.value, state.at);
      });
      this.eval(node.source, env, input, null, (source) =>
        this.bindEach(
          node.binder,
          source,
          env,
          (frame) => {
            if (node.update.direct) {
              const failed = this.evalOne(node.update, frame, state.value, state.at);
              if (failed === null) state.set(this.has ? this.value : null, this.has ? this.at : Path.of(path));
              this.ret(failed);
              return;
            }

            const last = new Capture(this);
            this.stack.push((stop) => {
              if (stop === null) state.set(last.has ? last.value : null, last.has ? last.at : Path.of(path));
              this.ret(stop);
            });
            this.eval(node.update, frame, state.value, state.at, last.emit);
          },
          null,
          restart(node.binder, state),
        ),
      );
    });
  }

  // foreach SRC as P (INIT; UPDATE; EXTRACT) (§7.9).
  private evalForeach(node: Foreach, env: Env | null, input: JqValue, path: Path | null, emit: Emit): void {
    const consumer = node.binder.patterns.length > 1 ? new Consumer(this, emit) : null;
    const output = consumer === null ? emit : consumer.emit;
    this.forEach(node.init, env, input, path, (initial, initialAt) => {
      const state = new State(initial, initialAt);
      this.eval(node.source, env, input, null, (source) =>
        this.bindEach(
          node.binder,
          source,
          env,
          (frame) => {
            let updated = false;
            this.stack.push((signal) => {
              if (signal === null && !updated) state.set(null, Path.of(path));
              this.ret(signal);
            });
            this.forEach(node.update, frame, state.value, state.at, (next, nextAt) => {
              updated = true;
              state.set(next, nextAt);
              this.eval(node.extract, frame, next, nextAt, output);
            });
          },
          consumer,
          restart(node.binder, state),
        ),
      );
    });
  }

  // A native: a closure native decides everything; a value native is called once per combination
  // of its arguments' outputs, the last argument outermost (§6.3, §9.1).
  private evalNative(node: CallNative, env: Env | null, input: JqValue, path: Path | null, emit: Emit): void {
    const native = node.native;
    if (native.takesClosures) {
      NativeTable.closure(native)(this, env, node.arguments, input, path, emit);
      return;
    }

    this.combine(NativeTable.value(native), node.arguments, native.arity - 1, new Array<JqValue>(native.arity), env, input, Path.of(path), emit);
  }

  private combine(func: ValueNative, nodes: readonly Node[], i: number, args: JqValue[], env: Env | null, input: JqValue, at: Path | null, emit: Emit): void {
    if (i < 0) {
      this.apply(func(this, input, args), at, emit);
      return;
    }

    this.forEach(nodes[i]!, env, input, null, (argument) => {
      args[i] = argument;
      this.combine(func, nodes, i - 1, args, env, input, at, emit);
    });
  }

  // A value native whose arguments are direct.
  private evalNativeOne(node: CallNative, env: Env | null, input: JqValue): Signal | null {
    const native = node.native;
    const args: JqValue[] = native.arity === 0 ? NoArguments : new Array<JqValue>(native.arity);
    for (let i = args.length - 1; i >= 0; i--) {
      const signal = this.evalOne(node.arguments[i]!, env, input, null);
      if (signal !== null || !this.has) return signal;
      args[i] = this.value;
    }

    const result = NativeTable.value(native)(this, input, args);
    if (result instanceof Fail) return Signal.error(result.error);
    this.has = true;
    this.value = result;
    return null;
  }

  // A chain of indexes and slices, T[k1][k2]…: the keys from the outermost in, each on the input
  // (§6.3: the key before its target), then the innermost target, then each index outward.
  private evalPostfixOne(node: Node, env: Env | null, input: JqValue, path: Path | null): Signal | null {
    const spineNodes = this.spineNodes;
    const spineValues = this.spineValues;
    const nodes = spineNodes.length;
    const keys = spineValues.length;
    let signal: Signal | null = null;
    this.has = true;
    while (this.has && signal === null && (node.kind === NodeKind.Index || node.kind === NodeKind.Slice)) {
      spineNodes.push(node);
      if (node.kind === NodeKind.Index) {
        signal = this.evalOne(node.key, env, input, null);
        spineValues.push(this.value);
        node = node.target;
      } else {
        let from: JqValue = null;
        let to: JqValue = null;
        if (node.from !== null) {
          signal = this.evalOne(node.from, env, input, null);
          from = this.value;
        }

        if (signal === null && this.has && node.to !== null) {
          signal = this.evalOne(node.to, env, input, null);
          to = this.value;
        }

        spineValues.push(from);
        spineValues.push(to);
        node = node.target;
      }
    }

    if (signal === null && this.has) signal = this.evalOne(node, env, input, path);
    for (let n = spineNodes.length - 1, k = spineValues.length; n >= nodes && signal === null && this.has; n--) {
      if (spineNodes[n]!.kind === NodeKind.Index) {
        signal = this.indexStep(this.value, this.at, spineValues[--k]!);
      } else {
        k -= 2;
        signal = this.sliceStep(this.value, this.at, spineValues[k]!, spineValues[k + 1]!);
      }
    }

    spineNodes.length = nodes;
    spineValues.length = keys;
    return signal;
  }

  // A left-deep chain of operators, ((a op1 b) op2 c)…: the right operands from the outermost in
  // (§6.3: the right operand first), then the innermost left one, then each operation outward.
  private evalBinaryOne(node: Binary, env: Env | null, input: JqValue): Signal | null {
    // One operator, the commonest case, needs no scratch stack.
    if (node.left.kind !== NodeKind.Binary) {
      let signal = this.evalOne(node.right, env, input, null);
      if (signal !== null || !this.has) return signal;
      const right = this.value;
      signal = this.evalOne(node.left, env, input, null);
      if (signal !== null || !this.has) return signal;
      const result = tryApply(node.op, this.value, right);
      if (result instanceof Fail) return Signal.error(result.error);
      this.value = result;
      return null;
    }

    const spineNodes = this.spineNodes;
    const spineValues = this.spineValues;
    const nodes = spineNodes.length;
    const operands = spineValues.length;
    let signal: Signal | null = null;
    this.has = true;
    let left: Node = node;
    while (this.has && signal === null && left.kind === NodeKind.Binary) {
      signal = this.evalOne(left.right, env, input, null);
      spineNodes.push(left);
      spineValues.push(this.value);
      left = left.left;
    }

    if (signal === null && this.has) signal = this.evalOne(left, env, input, null);
    for (let n = spineNodes.length - 1; n >= nodes && signal === null && this.has; n--) {
      const result = tryApply((spineNodes[n] as Binary).op, this.value, spineValues[n - nodes + operands]!);
      if (result instanceof Fail) signal = Signal.error(result.error);
      else this.value = result;
    }

    spineNodes.length = nodes;
    spineValues.length = operands;
    return signal;
  }

  // A left-deep chain of and/or: the innermost left operand first, then each operator outward,
  // evaluating its right operand only when the left value does not decide.
  private evalLogicOne(node: Node, env: Env | null, input: JqValue): Signal | null {
    const spineNodes = this.spineNodes;
    const nodes = spineNodes.length;
    while (node.kind === NodeKind.And || node.kind === NodeKind.Or) {
      spineNodes.push(node);
      node = node.left;
    }

    let signal = this.evalOne(node, env, input, null);
    for (let n = spineNodes.length - 1; n >= nodes && signal === null && this.has; n--) {
      const logic = spineNodes[n] as And | Or;
      const isAnd = logic.kind === NodeKind.And;
      if (isTruthy(this.value) === isAnd) signal = this.evalOne(logic.right, env, input, null);
      this.value = isTruthy(this.value);
    }

    spineNodes.length = nodes;
    return signal;
  }

  // -…-x: the operand, then each minus.
  private evalNegateOne(node: Negate, env: Env | null, input: JqValue): Signal | null {
    let count = 0;
    let operand: Node = node;
    for (; operand.kind === NodeKind.Negate; operand = operand.operand) count++;
    const signal = this.evalOne(operand, env, input, null);
    if (signal !== null) return signal;
    for (; this.has && count > 0; count--) {
      const result = ValueOps.tryNegate(this.value);
      if (result instanceof Fail) return Signal.error(result.error);
      this.value = result;
    }

    return null;
  }

  // [f] for a direct f: no element, or one.
  private evalArrayOne(node: ArrayExpr, env: Env | null, input: JqValue): Signal | null {
    if (node.body === null) {
      this.has = true;
      this.value = JqArray.Empty;
      return null;
    }

    const signal = this.evalOne(node.body, env, input, null);
    if (signal !== null) return signal;
    this.value = this.has ? JqArray.of(this.value) : JqArray.Empty;
    this.has = true;
    return null;
  }

  private evalObjectOne(node: ObjectExpr, env: Env | null, input: JqValue): Signal | null {
    let obj = JqObject.Empty;
    for (const entry of node.entries) {
      const signal = this.evalEntryOne(entry, env, input);
      if (signal !== null || !this.has) return signal;
      obj = obj.set(this.entryKey, this.value);
    }

    this.has = true;
    this.value = obj;
    return null;
  }

  private evalTemplateOne(node: Template, env: Env | null, input: JqValue): Signal | null {
    const texts = new Array<string>(node.parts.length);
    for (let i = texts.length - 1; i >= 0; i--) {
      const signal = this.evalOne(node.parts[i]!, env, input, null);
      if (signal !== null || !this.has) return signal;
      const text = convert(node.format, this.value);
      if (text instanceof Fail) return Signal.error(text.error);
      texts[i] = text;
    }

    this.has = true;
    this.value = join(node, texts);
    return null;
  }
}

const NoArguments: JqValue[] = [];

function newSlots(count: number): JqValue[] {
  const slots: JqValue[] = [];
  for (let i = 0; i < count; i++) slots.push(null);
  return slots;
}

// A binding the step takes in tail position: one pattern, a source and keys that are direct.
function isSimple(node: Bind): boolean {
  return node.source.direct && node.binder.keysDirect && node.binder.patterns.length === 1;
}

// The environment of a call: the caller's with the frames between the call and the definition
// removed, plus the closures of the arguments when there are any.
function callEnv(hops: number, args: readonly Node[], env: Env | null): Env | null {
  const defined = Env.up(env, hops);
  if (args.length === 0) return defined;
  const closures: Closure[] = [];
  for (let i = 0; i < args.length; i++) closures.push(new Closure(args[i]!, env));
  return Env.ofClosures(defined, closures);
}

/** The arithmetic operators and comparisons of §4. */
export function tryApply(op: BinaryOp, left: JqValue, right: JqValue): JqValue | Fail {
  switch (op) {
    case BinaryOp.Add:
      return ValueOps.tryAdd(left, right);
    case BinaryOp.Subtract:
      return ValueOps.trySubtract(left, right);
    case BinaryOp.Multiply:
      return ValueOps.tryMultiply(left, right);
    case BinaryOp.Divide:
      return ValueOps.tryDivide(left, right);
    case BinaryOp.Modulo:
      return ValueOps.tryModulo(left, right);
    case BinaryOp.Equal:
      return ValueOps.areEqual(left, right);
    case BinaryOp.NotEqual:
      return !ValueOps.areEqual(left, right);
    case BinaryOp.Less:
      return ValueOps.compare(left, right) < 0;
    case BinaryOp.LessEqual:
      return ValueOps.compare(left, right) <= 0;
    case BinaryOp.Greater:
      return ValueOps.compare(left, right) > 0;
    default:
      return ValueOps.compare(left, right) >= 0;
  }
}

// An interpolated value as text: tostring, or the template's format.
function convert(format: string | null, part: JqValue): string | Fail {
  if (format === null) return typeof part === "string" ? part : ValueText.toJson(part);
  const formatted = Formats.tryApply(format, part);
  return formatted instanceof Fail ? formatted : (formatted as string);
}

function join(node: Template, parts: string[]): string {
  let text = node.texts[0]!;
  for (let i = 0; i < parts.length; i++) text += parts[i]! + node.texts[i + 1]!;
  return text;
}

/**
 * Tells an operand's own signals from those of its consumer (§6.2). It stands between the
 * operand and the consumer and remembers the last signal the consumer returned; every construct
 * passes a signal up unchanged and stops at once, so when the operand returns that very object,
 * it came from the consumer. Comparing identities costs nothing per output.
 */
export class Consumer {
  private last: Signal | null = null;

  /** The emit to give the operand in place of the consumer's. */
  readonly emit: Emit;

  constructor(interpreter: Interpreter, emit: Emit) {
    const record: After = (signal) => {
      this.last = signal;
      interpreter.ret(signal);
    };
    this.emit = (value, path) => {
      interpreter.after(record);
      interpreter.output(emit, value, path);
    };
  }

  raised(signal: Signal): boolean {
    return signal === this.last;
  }
}

/** Keeps the output of an evaluation that yields at most one, or the last output of one that yields more. */
export class Capture {
  has = false;
  value: JqValue = null;
  at: Path | null = null;

  /** The emit to give the evaluation. */
  readonly emit: Emit;

  constructor(interpreter: Interpreter) {
    this.emit = (value, at) => {
      this.has = true;
      this.value = value;
      this.at = at;
      interpreter.ret(null);
    };
  }
}

// What is left of a destructuring (Interpreter.matchRest): the elements of an array pattern from
// `index` down, or the entries of an object pattern from `index` up, on `value`; then `then`. It
// never changes: every output of a key goes on from the same one.
class Rest {
  constructor(
    readonly pattern: ArrayPattern | ObjectPattern,
    readonly index: number,
    readonly value: JqValue,
    readonly then: Rest | null,
  ) {}

  // All of a pattern: the elements of an array from the last, the entries of an object from the first (§7.5).
  static of(pattern: ArrayPattern | ObjectPattern, value: JqValue, then: Rest | null): Rest {
    return new Rest(pattern, pattern.kind === PatternKind.Array ? pattern.elements.length - 1 : 0, value, then);
  }
}

// The state of a reduce or foreach, with its path.
class State {
  constructor(
    public value: JqValue,
    public at: Path | null,
  ) {}

  set(value: JqValue, at: Path | null): void {
    this.value = value;
    this.at = at;
  }

  // What takes the state back to what it is now.
  saved(): () => void {
    const value = this.value;
    const at = this.at;
    return () => this.set(value, at);
  }
}

// With ?// alternatives: what restores the state before a source value, for a retried alternative (§7.8).
function restart(binder: Binder, state: State): (() => () => void) | null {
  return binder.patterns.length > 1 ? () => state.saved() : null;
}
