// The implementations of the native catalogue, by Native.index. Each family of natives
// registers its entries; every entry of the catalogue has one, which the table checks once, when
// it is built (a missing one is a defect of the build, not a run-time error).

import type { Node } from "../binding/bound.js";
import type { Env } from "../runtime/env.js";
import type { Emit, Interpreter } from "../runtime/interpreter.js";
import type { Path } from "../runtime/path.js";
import type { Fail, JqValue } from "../values/jqValue.js";
import * as AssignNatives from "./assignNatives.js";
import * as CollectionNatives from "./collectionNatives.js";
import * as Containment from "./containment.js";
import * as CoreNatives from "./coreNatives.js";
import * as DateNatives from "./dateNatives.js";
import * as LoopNatives from "./loopNatives.js";
import * as MathNatives from "./mathNatives.js";
import * as ModuleNatives from "./moduleNatives.js";
import * as Natives from "./natives.js";
import type { Native } from "./natives.js";
import * as PathNatives from "./pathNatives.js";
import * as RegexNatives from "./regexNatives.js";
import * as StringNatives from "./stringNatives.js";

/**
 * A native with value arguments (§9.1): called once per combination of its arguments' outputs,
 * it gives one output, or an error (a Fail).
 */
export type ValueNative = (interpreter: Interpreter, input: JqValue, args: JqValue[]) => JqValue | Fail;

/**
 * A native that takes its arguments as closures (Native.takesClosures): it evaluates them itself
 * with `env`, gives its outputs to `emit` (with their paths in path mode, when it passes paths)
 * and returns to its caller, as Interpreter.eval does: it schedules steps of the interpreter
 * (`eval`, `forEach`, `output`, `ret`, `after`), and calls nothing that would recurse.
 */
export type ClosureNative = (interpreter: Interpreter, env: Env | null, args: readonly Node[], input: JqValue, path: Path | null, emit: Emit) => void;

const values: (ValueNative | undefined)[] = new Array(Natives.All.length);
const closures: (ClosureNative | undefined)[] = new Array(Natives.All.length);
let loaded = false;

// The reference fills the table in a static constructor; here on first use, because the modules
// of the families and this one import each other.
function load(): void {
  loaded = true;
  CoreNatives.register();
  LoopNatives.register();
  PathNatives.register();
  CollectionNatives.register();
  StringNatives.register();
  AssignNatives.register();
  ModuleNatives.register();
  Containment.register();
  MathNatives.register();
  DateNatives.register();
  RegexNatives.register();

  for (const native of Natives.All) {
    if (values[native.index] === undefined && closures[native.index] === undefined) throw new Error(`${native} has no implementation`);
  }
}

/** The implementation of a value native (one that does not take closures). */
export function value(native: Native): ValueNative {
  if (!loaded) load();
  return values[native.index]!;
}

/** The implementation of a native that takes closures. */
export function closure(native: Native): ClosureNative {
  if (!loaded) load();
  return closures[native.index]!;
}

/** Whether `native` has an implementation. */
export function isImplemented(native: Native): boolean {
  if (!loaded) load();
  return values[native.index] !== undefined || closures[native.index] !== undefined;
}

// Registration checks the kind against the catalogue: a mismatch is a defect of the build.

/** Registers a native with value arguments. */
export function add(name: string, arity: number, func: ValueNative): void {
  values[find(name, arity, false).index] = func;
}

/** Registers a native that takes closures. */
export function addClosure(name: string, arity: number, func: ClosureNative): void {
  closures[find(name, arity, true).index] = func;
}

function find(name: string, arity: number, takesClosures: boolean): Native {
  const native = Natives.find(name, arity);
  if (native === null) throw new Error(`${name}/${arity} is not in the native catalogue`);
  if (native.takesClosures !== takesClosures) throw new Error(`${name}/${arity} is registered with the wrong kind`);
  return native;
}
