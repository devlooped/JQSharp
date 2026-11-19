import * as Natives from "../builtins/natives.js";
import type { Native } from "../builtins/natives.js";
import * as S from "../syntax/ast.js";
import { LocationVariable, Parser } from "../syntax/parser.js";
import { SyntaxError } from "../syntax/syntaxError.js";
import { JqObjectBuilder } from "../values/jqObjectBuilder.js";
import type { JqValue } from "../values/jqValue.js";
import {
  Alternative,
  And,
  ArrayExpr,
  ArrayPattern,
  Binary,
  Bind,
  Binder,
  BoundProgram,
  Break,
  CallFunction,
  CallNative,
  CallParameter,
  Comma,
  EnvVariable,
  Foreach,
  Format,
  Function,
  HostVariable,
  Identity,
  If,
  Index,
  Iterate,
  Label,
  Literal,
  Negate,
  type Node,
  ObjectEntry,
  ObjectExpr,
  ObjectPattern,
  Or,
  type Pattern,
  PatternEntry,
  Pipe,
  Reduce,
  Slice,
  Template,
  Try,
  Variable,
  VariablePattern,
  allSingle,
} from "./bound.js";
import { type IModuleSource, type LoadedModule, ModuleLoader, bindImports } from "./modules.js";
import { Prelude } from "./prelude.js";

const enum EntryKind {
  Variable,
  Parameter,
  Label,
  Function,
  Constant,
}

// Variable, Parameter, Label: depth is the frame holding it, slot its slot there.
// Constant: a data module's value (modules.ts), constants[slot].
// Function: depth is the number of frames where it is defined, slot its index in functions,
// topLevel whether it is a top-level definition of the program or module.
class Entry {
  constructor(
    readonly kind: EntryKind,
    readonly name: string,
    readonly arity: number,
    readonly depth: number,
    readonly slot: number,
    readonly topLevel: boolean = false,
  ) {}
}

const enum Step {
  // Resolve node, or schedule its children and its build.
  Visit,
  // Pop the bound children of node and push the bound node.
  Build,
  // Pop the arguments of the call node and push the call of functions[a], defined b frames out.
  BuildCall,
  // Pop the arguments of the call node and push the call of Natives.All[a].
  BuildNative,
  // Pop the bound pattern keys of the binding node and push its binder.
  MakeBinder,
  // Push the frame of the binder on top of binders.
  EnterBinder,
  // Define definition at this point of the scope (a = 1 at the top level of the program).
  Define,
  // Pop the body of definition, which is functions[a].
  EndFunction,
  // Restore the scope to a entries and the depth to b.
  Leave,
}

class Task {
  constructor(
    readonly step: Step,
    readonly node: S.Node | null = null,
    readonly definition: S.Definition | null = null,
    readonly a: number = 0,
    readonly b: number = 0,
  ) {}
}

// A function whose body is being bound. single: whether its calls of itself in that body are
// bound as single (spec §7.7): assumed at first, and checked when the body is bound, unless the
// answer is known already. selfCalls counts the calls bound as single on that assumption.
class Defining {
  selfCalls = 0;

  constructor(
    readonly index: number,
    readonly valueParameters: readonly number[],
    public single: boolean,
    public known: boolean,
  ) {}
}

const identity: Node = new Identity();

const parameterBinder = new Binder([new VariablePattern(0)], 1);

/**
 * Binds a parsed program (spec §7): walks the AST once and resolves every variable, closure
 * parameter, function call and label to its definition, producing the BoundProgram described at
 * the top of bound.ts. Unknown names are binding errors with a position; nothing throws.
 *
 * The scope is a stack of entries searched from the top, so the innermost definition wins. A
 * call resolves to the innermost user definition or closure parameter of that name and arity,
 * then to the prelude, then to the natives (§7.7). Entries record the frame depth at which they
 * live, so the distance to a definition is the current depth minus the entry's.
 *
 * The walk does not recurse: the AST is up to 1,000 levels deep through nesting and 10,000 or
 * more along sequences (the parser folds `|`, `,`, operators, suffixes, `as`, `def` and `label`
 * chains iteratively), and between them an operand chain of each precedence level can add a
 * level, which no host stack budget covers in every language. So the walk is a loop over a stack
 * of tasks: `visit` resolves what it can at once and pushes the node's children, the scope
 * changes between them and the final `build`; bound children wait on a stack of results. Only
 * patterns are walked recursively, one small frame per level of pattern nesting, which the
 * parser limits.
 *
 * The members marked internal are those the modules group uses (modules.ts holds the part of
 * this class that the reference keeps in Modules.cs).
 */
export class Resolver {
  /** @internal */ readonly prelude: Prelude | null;
  // Whether builtins with a native override bind to it (Native.overridesPrelude).
  /** @internal */ readonly nativeOverrides: boolean;
  /** @internal */ readonly scope: Entry[] = [];
  // Shared by the main program and its modules (modules.ts): one function table, one list of host variables.
  /** @internal */ readonly functions: (Function | null)[];
  /** @internal */ readonly exports: number[] = [];
  /** @internal */ readonly hostVariables: string[];
  /** @internal */ readonly constants: JqValue[];
  private readonly tasks: Task[] = [];
  private readonly results: Node[] = [];
  // The binders made and not yet built into their node, with the names of their slots.
  private readonly binders: { binder: Binder; names: string[] }[] = [];
  // The functions being defined, innermost last.
  private readonly defining: Defining[] = [];
  // While the body of a function is bound a second time (endFunction): whether each function
  // nested in it is single, as the first time found, by index from replayStart.
  private replay: boolean[] | null = null;
  private replayStart = 0;
  private depth = 0;
  /** @internal */ error: SyntaxError | null = null;

  // The modules of this binding (shared by the main program and every module), and the module
  // this resolver binds (null for the main program and the prelude).
  /** @internal */ readonly loader: ModuleLoader | null;
  /** @internal */ readonly module: LoadedModule | null;

  /** @internal */
  constructor(
    prelude: Prelude | null,
    nativeOverrides: boolean,
    loader: ModuleLoader | null,
    functions: (Function | null)[] | null = null,
    hostVariables: string[] | null = null,
    constants: JqValue[] | null = null,
    module: LoadedModule | null = null,
  ) {
    this.prelude = prelude;
    this.nativeOverrides = nativeOverrides;
    this.loader = loader;
    this.module = module;
    this.hostVariables = hostVariables ?? [];
    this.constants = constants ?? [];
    this.functions = functions ?? [];
    if (functions === null && prelude !== null) {
      for (const func of prelude.functions) this.functions.push(func);
    }
  }

  /** @internal A resolver for a module: a scope of its own, the functions, host variables and constants of the whole program. */
  static forModule(main: Resolver, module: LoadedModule): Resolver {
    return new Resolver(main.prelude, main.nativeOverrides, main.loader, main.functions, main.hostVariables, main.constants, module);
  }

  /**
   * Binds a program against the prelude and the natives, loading its modules from `modules`
   * (§9.11). Without `nativeOverrides` every prelude builtin binds to its definition, even where
   * a native overrides it.
   */
  static tryBind(program: S.Program, nativeOverrides: boolean = true, modules: IModuleSource | null = null): BoundProgram | SyntaxError {
    const prelude = nativeOverrides ? Prelude.instance : Prelude.withoutOverrides;
    const resolver = new Resolver(prelude, nativeOverrides, new ModuleLoader(modules));
    const body = resolver.bindProgram(program);
    return body === null ? resolver.error! : new BoundProgram(resolver.allFunctions(), resolver.hostVariables.slice(), body, resolver.loader!.table());
  }

  /** Parses and binds a program. */
  static tryCompile(source: string, nativeOverrides: boolean = true, modules: IModuleSource | null = null): BoundProgram | SyntaxError {
    const program = Parser.tryParse(source);
    return program instanceof SyntaxError ? program : Resolver.tryBind(program, nativeOverrides, modules);
  }

  /** Binds the prelude module against the natives only: all its functions, and its top-level definitions in order. */
  static tryBindPrelude(module: S.Program, nativeOverrides: boolean): { functions: Function[]; definitions: Function[] } | SyntaxError {
    const resolver = new Resolver(null, nativeOverrides, null);
    if (resolver.bindProgram(module) === null) return resolver.error!;

    const functions = resolver.allFunctions();
    const definitions: Function[] = [];
    for (const index of resolver.exports) definitions.push(functions[index]!);
    return { functions, definitions };
  }

  private allFunctions(): Function[] {
    const all: Function[] = [];
    for (const func of this.functions) all.push(func!);
    return all;
  }

  /** @internal A data import's constant, constants[slot], visible from this point of the scope (modules.ts). */
  addConstant(name: string, slot: number): void {
    this.scope.push(new Entry(EntryKind.Constant, name, 0, 0, slot));
  }

  /** @internal An export of an imported module, defined at this point of the scope (modules.ts). */
  addFunction(name: string, arity: number, func: number): void {
    this.scope.push(new Entry(EntryKind.Function, name, arity, 0, func));
  }

  /** @internal The body of a program (Identity for a module, whose result is its definitions), or null. */
  bindProgram(program: S.Program): Node | null {
    if (!bindImports(this, program.imports)) return null;

    // Pushed last first: the definitions in order, then the body.
    if (program.body === null) this.results.push(identity);
    else this.tasks.push(new Task(Step.Visit, program.body));
    for (let i = program.definitions.length - 1; i >= 0; i--) this.tasks.push(new Task(Step.Define, null, program.definitions[i]!, 1));

    while (this.tasks.length > 0 && this.error === null) this.run(this.tasks.pop()!);

    return this.error === null ? this.results[0]! : null;
  }

  private run(task: Task): void {
    switch (task.step) {
      case Step.Visit:
        this.visit(task.node!);
        break;
      case Step.Build:
        this.results.push(this.build(task.node!, task.a));
        break;
      case Step.BuildCall:
        this.results.push(this.call(task.a, task.b, this.popAll((task.node as S.Call).arguments.length)));
        break;
      case Step.BuildNative:
        this.results.push(new CallNative(Natives.All[task.a]!, this.popAll((task.node as S.Call).arguments.length)));
        break;
      case Step.MakeBinder:
        this.makeBinder(task.node!);
        break;
      case Step.EnterBinder:
        this.enterBinder();
        break;
      case Step.Define:
        this.define(task.definition!, task.a === 1);
        break;
      case Step.EndFunction:
        this.endFunction(task.definition!, task.a);
        break;
      default:
        this.scope.length = task.a;
        this.depth = task.b;
        break;
    }
  }

  // Tasks run last pushed first, so each case pushes its steps in reverse order.
  private visit(node: S.Node): void {
    switch (node.kind) {
      case S.NodeKind.Identity:
        this.results.push(identity);
        break;
      case S.NodeKind.Literal:
        this.results.push(new Literal(node.value));
        break;
      case S.NodeKind.Format:
        this.results.push(new Format(node.name));
        break;
      case S.NodeKind.Variable:
        this.add(this.bindVariable(node));
        break;
      case S.NodeKind.Break:
        this.add(this.bindBreak(node));
        break;
      case S.NodeKind.Call:
        this.visitCall(node);
        break;
      case S.NodeKind.Template:
        this.push(Step.Build, node);
        this.pushAll(node.parts);
        break;
      case S.NodeKind.Pipe:
        this.push(Step.Build, node, node.right, node.left);
        break;
      case S.NodeKind.Comma:
        this.visitComma(node);
        break;
      case S.NodeKind.Binary:
        this.push(Step.Build, node, node.right, node.left);
        break;
      case S.NodeKind.And:
        this.push(Step.Build, node, node.right, node.left);
        break;
      case S.NodeKind.Or:
        this.push(Step.Build, node, node.right, node.left);
        break;
      case S.NodeKind.Alternative:
        this.push(Step.Build, node, node.right, node.left);
        break;
      case S.NodeKind.Negate:
        this.push(Step.Build, node, node.operand);
        break;
      case S.NodeKind.Index:
        this.push(Step.Build, node, node.key, node.target);
        break;
      case S.NodeKind.Slice:
        this.push(Step.Build, node, node.to, node.from, node.target);
        break;
      case S.NodeKind.Iterate:
        this.push(Step.Build, node, node.target);
        break;
      case S.NodeKind.Try:
        this.push(Step.Build, node, node.handler, node.body);
        break;
      case S.NodeKind.If:
        this.push(Step.Build, node, node.else, node.then, node.condition);
        break;
      case S.NodeKind.ArrayExpr:
        this.push(Step.Build, node, node.body);
        break;
      case S.NodeKind.ObjectExpr:
        this.push(Step.Build, node);
        for (let i = node.entries.length - 1; i >= 0; i--) this.pushNodes(node.entries[i]!.value, node.entries[i]!.key);
        break;
      case S.NodeKind.Bind:
        // The source and the pattern keys in the current scope, the body with the variables' frame.
        this.leave();
        this.push(Step.Build, node, node.body);
        this.tasks.push(new Task(Step.EnterBinder));
        this.pushBinder(node, node.patterns);
        this.pushNodes(node.source);
        break;
      case S.NodeKind.Reduce:
        this.leave();
        this.push(Step.Build, node, node.update);
        this.tasks.push(new Task(Step.EnterBinder));
        this.pushNodes(node.init);
        this.pushBinder(node, node.patterns);
        this.pushNodes(node.source);
        break;
      case S.NodeKind.Foreach:
        this.leave();
        this.push(Step.Build, node, node.extract, node.update);
        this.tasks.push(new Task(Step.EnterBinder));
        this.pushNodes(node.init);
        this.pushBinder(node, node.patterns);
        this.pushNodes(node.source);
        break;
      case S.NodeKind.Label:
        this.leave();
        this.push(Step.Build, node, node.body);
        this.depth++;
        this.scope.push(new Entry(EntryKind.Label, node.name, 0, this.depth, 0));
        break;
      case S.NodeKind.FuncDef:
        // The definition is in functions; the node's result is the rest's.
        this.leave();
        this.pushNodes(node.rest);
        this.tasks.push(new Task(Step.Define, null, node.definition));
        break;
    }
  }

  // A left-nested chain of commas becomes one node with the items in order.
  private visitComma(comma: S.Comma): void {
    const items: S.Node[] = [];
    while (true) {
      items.push(comma.right);
      if (comma.left.kind !== S.NodeKind.Comma) break;
      comma = comma.left;
    }

    items.push(comma.left);
    items.reverse();
    this.tasks.push(new Task(Step.Build, comma, null, items.length));
    this.pushAll(items);
  }

  // Pops the bound children of node, last first, and builds its bound node; count is the number of items of a comma.
  private build(node: S.Node, count: number): Node {
    switch (node.kind) {
      case S.NodeKind.Template:
        return new Template(node.format, node.texts, this.popAll(node.parts.length));
      case S.NodeKind.Pipe: {
        const right = this.pop();
        return new Pipe(this.pop(), right);
      }
      case S.NodeKind.Comma:
        return new Comma(this.popAll(count));
      case S.NodeKind.Binary: {
        const right = this.pop();
        return new Binary(node.op, this.pop(), right);
      }
      case S.NodeKind.And: {
        const right = this.pop();
        return new And(this.pop(), right);
      }
      case S.NodeKind.Or: {
        const right = this.pop();
        return new Or(this.pop(), right);
      }
      case S.NodeKind.Alternative: {
        const right = this.pop();
        return new Alternative(this.pop(), right);
      }
      case S.NodeKind.Negate:
        return new Negate(this.pop());
      case S.NodeKind.Index: {
        const key = this.pop();
        return new Index(this.pop(), key);
      }
      case S.NodeKind.Slice: {
        const to = node.to === null ? null : this.pop();
        const from = node.from === null ? null : this.pop();
        return new Slice(this.pop(), from, to);
      }
      case S.NodeKind.Iterate:
        return new Iterate(this.pop());
      case S.NodeKind.Try: {
        const handler = node.handler === null ? null : this.pop();
        return new Try(this.pop(), handler);
      }
      case S.NodeKind.If: {
        const otherwise = this.pop();
        const then = this.pop();
        return new If(this.pop(), then, otherwise);
      }
      case S.NodeKind.ArrayExpr:
        return new ArrayExpr(node.body === null ? null : this.pop());
      case S.NodeKind.ObjectExpr: {
        const entries: ObjectEntry[] = new Array(node.entries.length);
        for (let i = entries.length - 1; i >= 0; i--) {
          const value = node.entries[i]!.value === null ? null : this.pop();
          entries[i] = new ObjectEntry(this.pop(), value);
        }

        return new ObjectExpr(entries);
      }
      case S.NodeKind.Bind: {
        const body = this.pop();
        return new Bind(this.pop(), this.popBinder(), body);
      }
      case S.NodeKind.Reduce: {
        const update = this.pop();
        const init = this.pop();
        return new Reduce(this.pop(), this.popBinder(), init, update);
      }
      case S.NodeKind.Foreach: {
        const extract = this.pop();
        const update = this.pop();
        const init = this.pop();
        return new Foreach(this.pop(), this.popBinder(), init, update, extract);
      }
      default:
        return new Label(this.pop());
    }
  }

  // Defines a function at the current point of the scope, visible in its own body and after it.
  private define(definition: S.Definition, topLevel: boolean): void {
    const index = this.functions.length;
    this.functions.push(null);
    if (topLevel) this.exports.push(index);
    this.scope.push(new Entry(EntryKind.Function, definition.name, definition.params.length, this.depth, index, topLevel));

    // Single until its body says otherwise, or what the first binding of this body found.
    const replayed = this.replay !== null && index - this.replayStart < this.replay.length;
    this.defining.push(new Defining(index, valueParameters(definition), !replayed || this.replay![index - this.replayStart]!, replayed));

    this.leave();
    this.tasks.push(new Task(Step.EndFunction, null, definition, index));
    this.pushNodes(definition.body);
    if (definition.params.length === 0) return;
    this.depth++;
    for (let slot = 0; slot < definition.params.length; slot++) this.scope.push(new Entry(EntryKind.Parameter, definition.params[slot]!.name, 0, this.depth, slot));
    // Each $name parameter then has a frame of its own, first parameter outermost (bindParameters).
    for (const parameter of definition.params) {
      if (parameter.isVariable) {
        this.depth++;
        this.scope.push(new Entry(EntryKind.Variable, parameter.name, 0, this.depth, 0));
      }
    }
  }

  // The body of functions[index] is bound: the function is single when its body is (spec §7.7).
  private endFunction(definition: S.Definition, index: number): void {
    const body = this.pop();
    const own = this.defining[this.defining.length - 1]!;
    if (own.single && !body.atMostOne && !own.known) {
      // It was assumed single and is not. The calls of itself bound as single are wrong, and so
      // is every node built on them: bind the body again, this time knowing. The functions
      // nested in it do not depend on the assumption (their calls of this one are never single,
      // call), so they keep what this binding found and are not bound a third time.
      own.single = false;
      own.known = true;
      if (own.selfCalls > 0) {
        this.replay = [];
        this.replayStart = index + 1;
        for (let i = this.replayStart; i < this.functions.length; i++) this.replay.push(this.functions[i]!.single);
        this.functions.length = this.replayStart;
        this.tasks.push(new Task(Step.EndFunction, null, definition, index));
        this.pushNodes(definition.body);
        return;
      }
    }

    this.defining.pop();
    if (this.replay !== null && this.replayStart === index + 1) this.replay = null;
    this.functions[index] = new Function(definition.name, definition.params.length, bindParameters(definition, body), body.atMostOne, own.valueParameters);
  }

  // A call of functions[func]: single when the function is and the argument of each of its $
  // parameters is. A function still being defined is single in its own body as long as its
  // Defining says so; seen from a function nested in it, it is not.
  private call(func: number, hops: number, args: Node[]): CallFunction {
    let single = false;
    const defined = this.functions[func] ?? null;
    if (defined !== null) {
      single = defined.single && allSingle(defined.valueParameters, args);
    } else {
      const own = this.defining[this.defining.length - 1]!;
      if (own.single && own.index === func && allSingle(own.valueParameters, args)) {
        single = true;
        if (!own.known) own.selfCalls++;
      }
    }

    return new CallFunction(func, hops, args, single);
  }

  private bindVariable(variable: S.Variable): Node | null {
    // The parser lets nothing bind $__loc__ (§7.5).
    if (variable.name === LocationVariable) return new Literal(this.location(variable.position));

    for (let i = this.scope.length - 1; i >= 0; i--) {
      const entry = this.scope[i]!;
      if (entry.kind === EntryKind.Variable && entry.name === variable.name) return new Variable(this.depth - entry.depth, entry.slot);
      if (entry.kind === EntryKind.Constant && entry.name === variable.name) return new Literal(this.constants[entry.slot]!);
    }

    if (variable.name === "ENV") return new EnvVariable();
    // A qualified name can only come from a data import (bindImports); the prelude has no host variables.
    if (variable.name.includes("::") || this.prelude === null) return this.fail(variableNotDefined(variable.name), variable.position);

    let index = this.hostVariables.indexOf(variable.name);
    if (index < 0) {
      index = this.hostVariables.length;
      this.hostVariables.push(variable.name);
    }

    return new HostVariable(variable.name, index);
  }

  // $__loc__: {"file": the module's identity or "<top-level>", "line": the line of its '$'}.
  private location(position: S.Position): JqValue {
    const builder = new JqObjectBuilder();
    builder.set("file", this.module?.identity ?? "<top-level>");
    builder.set("line", position.line);
    return builder.toObject();
  }

  private visitCall(call: S.Call): void {
    const arity = call.arguments.length;
    for (let i = this.scope.length - 1; i >= 0; i--) {
      const entry = this.scope[i]!;
      if (entry.name !== call.name || entry.arity !== arity) continue;
      if (entry.kind === EntryKind.Parameter) {
        this.results.push(new CallParameter(this.depth - entry.depth, entry.slot));
        return;
      }

      if (entry.kind === EntryKind.Function) {
        // Inside the prelude, its own builtins with a native override bind to the native too.
        const native = this.prelude === null && entry.topLevel ? this.override(call.name, arity) : null;
        if (native !== null) this.tasks.push(new Task(Step.BuildNative, call, null, native.index));
        else this.tasks.push(new Task(Step.BuildCall, call, null, entry.slot, this.depth - entry.depth));
        this.pushAll(call.arguments);
        return;
      }
    }

    // The prelude is defined in the outermost, empty environment: every frame of the caller is left.
    const func = this.prelude === null ? null : this.prelude.find(call.name, arity);
    if (func !== null) {
      const native = this.override(call.name, arity);
      if (native !== null) this.tasks.push(new Task(Step.BuildNative, call, null, native.index));
      else this.tasks.push(new Task(Step.BuildCall, call, null, func, this.depth));
    } else {
      const native = Natives.find(call.name, arity);
      if (native !== null) this.tasks.push(new Task(Step.BuildNative, call, null, native.index));
      else this.fail(notDefined(call.name, arity), call.position);
    }

    this.pushAll(call.arguments);
  }

  // The native override of a prelude builtin, when overrides are on.
  private override(name: string, arity: number): Native | null {
    return this.nativeOverrides ? Natives.override(name, arity) : null;
  }

  private bindBreak(node: S.Break): Node | null {
    for (let i = this.scope.length - 1; i >= 0; i--) {
      const entry = this.scope[i]!;
      if (entry.kind === EntryKind.Label && entry.name === node.name) return new Break(this.depth - entry.depth);
    }

    return this.fail(labelNotDefined(node.name), node.position);
  }

  // Schedules the binder of node: its pattern keys, in destructuring order, then makeBinder.
  private pushBinder(node: S.Node, patterns: readonly S.Pattern[]): void {
    this.tasks.push(new Task(Step.MakeBinder, node));
    const keys: S.Node[] = [];
    for (const pattern of patterns) collectKeys(pattern, keys);
    this.pushAll(keys);
  }

  // Builds the ?// alternatives of a binding from its bound keys (the last results, in the order
  // of collectKeys). Every distinct variable name of all the alternatives gets a slot. A name
  // repeated in one pattern (§7.5): without ?// its first occurrence in matching order gives the
  // value, so later ones discard theirs; with ?// every occurrence stores and the last one stays.
  private makeBinder(node: S.Node): void {
    const patterns = (node as S.Bind | S.Reduce | S.Foreach).patterns;

    let keys = 0;
    for (const pattern of patterns) keys += countKeys(pattern);
    this.next = this.results.length - keys;
    const names: string[] = [];
    const seen = patterns.length === 1 ? new Set<string>() : null;
    const bound: Pattern[] = [];
    for (const pattern of patterns) bound.push(this.bindPattern(pattern, names, seen));

    this.results.length -= keys;
    this.binders.push({ binder: new Binder(bound, names.length), names });
  }

  // The next bound key of bindPattern, in results.
  private next = 0;

  // Visits elements and entries in the order of collectKeys, which is matching order (spec §7.5).
  private bindPattern(pattern: S.Pattern, names: string[], seen: Set<string> | null): Pattern {
    switch (pattern.kind) {
      case S.PatternKind.Variable:
        return new VariablePattern(slotOf(pattern.name, names, seen));
      case S.PatternKind.Array: {
        const elements: Pattern[] = new Array(pattern.elements.length);
        for (let i = elements.length - 1; i >= 0; i--) elements[i] = this.bindPattern(pattern.elements[i]!, names, seen);
        return new ArrayPattern(elements);
      }
      default: {
        const bound: PatternEntry[] = [];
        for (const entry of pattern.entries) {
          const key = this.results[this.next++]!;
          const slot = entry.variable === null ? -1 : slotOf(entry.variable, names, seen);
          const value = entry.value === null ? null : this.bindPattern(entry.value, names, seen);
          bound.push(new PatternEntry(key, slot, value));
        }

        return new ObjectPattern(bound);
      }
    }
  }

  // Pushes the frame of the last binder made: one variable per name, in slot order.
  private enterBinder(): void {
    const names = this.binders[this.binders.length - 1]!.names;
    this.depth++;
    for (let slot = 0; slot < names.length; slot++) this.scope.push(new Entry(EntryKind.Variable, names[slot]!, 0, this.depth, slot));
  }

  private popBinder(): Binder {
    return this.binders.pop()!.binder;
  }

  // Schedules the restoring of the scope as it is now, after the tasks pushed later.
  private leave(): void {
    this.tasks.push(new Task(Step.Leave, null, null, this.scope.length, this.depth));
  }

  private push(step: Step, node: S.Node, last: S.Node | null = null, middle: S.Node | null = null, first: S.Node | null = null): void {
    this.tasks.push(new Task(step, node));
    this.pushNodes(last, middle, first);
  }

  // Schedules the visit of the given nodes, missing ones skipped, the last argument first.
  private pushNodes(last: S.Node | null, middle: S.Node | null = null, first: S.Node | null = null): void {
    if (last !== null) this.tasks.push(new Task(Step.Visit, last));
    if (middle !== null) this.tasks.push(new Task(Step.Visit, middle));
    if (first !== null) this.tasks.push(new Task(Step.Visit, first));
  }

  private pushAll(nodes: readonly S.Node[]): void {
    for (let i = nodes.length - 1; i >= 0; i--) this.tasks.push(new Task(Step.Visit, nodes[i]!));
  }

  private add(node: Node | null): void {
    if (node !== null) this.results.push(node);
  }

  private pop(): Node {
    return this.results.pop()!;
  }

  private popAll(count: number): Node[] {
    return this.results.splice(this.results.length - count, count);
  }

  private fail(message: string, position: S.Position): null {
    this.error ??= new SyntaxError(message, position.line, position.column, this.module?.identity ?? null);
    return null;
  }
}

// def f($a; $b): B is def f(a; b): a as $a | b as $b | B, except that each binding names its
// parameter by position: the k-th $ parameter's frame is k frames inside the parameters' frame.
function bindParameters(definition: S.Definition, body: Node): Node {
  let variables = 0;
  for (const parameter of definition.params) variables += parameter.isVariable ? 1 : 0;
  for (let slot = definition.params.length - 1; slot >= 0; slot--) {
    if (definition.params[slot]!.isVariable) body = new Bind(new CallParameter(--variables, slot), parameterBinder, body);
  }

  return body;
}

// The slots of the $ parameters.
function valueParameters(definition: S.Definition): number[] {
  const slots: number[] = [];
  for (let slot = 0; slot < definition.params.length; slot++) {
    if (definition.params[slot]!.isVariable) slots.push(slot);
  }

  return slots;
}

function collectKeys(pattern: S.Pattern, keys: S.Node[]): void {
  switch (pattern.kind) {
    case S.PatternKind.Array:
      for (let i = pattern.elements.length - 1; i >= 0; i--) collectKeys(pattern.elements[i]!, keys);
      break;
    case S.PatternKind.Object:
      for (const entry of pattern.entries) {
        keys.push(entry.key);
        if (entry.value !== null) collectKeys(entry.value, keys);
      }

      break;
  }
}

function countKeys(pattern: S.Pattern): number {
  const keys: S.Node[] = [];
  collectKeys(pattern, keys);
  return keys.length;
}

// The slot of a variable in names, or -1 when seen (first occurrence wins) already has the name.
function slotOf(name: string, names: string[], seen: Set<string> | null): number {
  if (seen !== null) {
    if (seen.has(name)) return -1;
    seen.add(name);
  }

  let slot = names.indexOf(name);
  if (slot < 0) {
    slot = names.length;
    names.push(name);
  }

  return slot;
}

// The messages of the binding layer (the reference's BindingMessages); the first three are fixed by spec §10.2.

export function notDefined(name: string, arity: number): string {
  return `${name}/${arity} is not defined`;
}

export function labelNotDefined(name: string): string {
  return `$*label-${name} is not defined`;
}

export function variableNotDefined(name: string): string {
  return `$${name} is not defined`;
}

// The modules group (modules.ts); the texts are jq's.
export function moduleNotFound(path: string): string {
  return `module not found: ${path}`;
}

export function circularImport(identity: string): string {
  return `circular import of ${identity}`;
}

export function invalidData(identity: string, reason: string): string {
  return `error loading data file ${identity}: ${reason}`;
}

/** An error of a module, with where it is: `… at <identity>, line L, column C`. */
export function inModule(message: string, identity: string, line: number, column: number): string {
  return `${message} at ${identity}, line ${line}, column ${column}`;
}
