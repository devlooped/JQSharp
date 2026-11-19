// §9.2 Core and §9.3 Types: the natives of the evaluation model itself and of its host (§12).

import type { Node } from "../binding/bound.js";
import { Prelude } from "../binding/prelude.js";
import type { Env } from "../runtime/env.js";
import type { Emit, Interpreter } from "../runtime/interpreter.js";
import type { Path } from "../runtime/path.js";
import { Signal } from "../runtime/signal.js";
import * as CodePoints from "../values/codePoints.js";
import * as Errors from "../values/errors.js";
import { JqArray } from "../values/jqArray.js";
import type { JqObject } from "../values/jqObject.js";
import { Fail, JqKind, type JqValue, kindOf, typeName } from "../values/jqValue.js";
import { add, addClosure } from "./nativeTable.js";
import * as Natives from "./natives.js";

// builtins/0: every non-internal builtin, natives and prelude, sorted by code point.
let builtins: JqValue | undefined;

// The smallest normal double, 2^-1022.
const MinNormal = 2.2250738585072014e-308;

export function register(): void {
  addClosure("empty", 0, (interpreter) => interpreter.ret(null));
  add("error", 0, (_, input) => new Fail(input));
  add("type", 0, (_, input) => typeName(input));
  add("length", 0, length);
  add("utf8bytelength", 0, utf8ByteLength);
  add("builtins", 0, () => (builtins ??= listBuiltins()));
  addClosure("halt", 0, (interpreter) => interpreter.ret(Signal.halt(undefined, 0)));
  addClosure("halt_error", 1, haltError);
  addClosure("last", 1, last);
  add("debug", 0, (interpreter, input) => {
    if (interpreter.debug !== null) interpreter.debug(input);
    return input;
  });
  add("stderr", 0, (interpreter, input) => {
    if (interpreter.stderr !== null) interpreter.stderr(input);
    return input;
  });
  add("infinite", 0, () => Infinity);
  add("nan", 0, () => NaN);
  add("isinfinite", 0, (_, input) => input === Infinity || input === -Infinity);
  add("isnan", 0, (_, input) => typeof input === "number" && Number.isNaN(input));
  add("isnormal", 0, (_, input) => typeof input === "number" && Number.isFinite(input) && Math.abs(input) >= MinNormal);
}

function length(_: Interpreter, input: JqValue): JqValue | Fail {
  switch (kindOf(input)) {
    case JqKind.Null:
      return 0;
    case JqKind.Number:
      return Math.abs(input as number);
    case JqKind.String:
      return CodePoints.length(input as string);
    case JqKind.Array:
      return (input as JqArray).count;
    case JqKind.Object:
      return (input as JqObject).count;
    default:
      return new Fail(Errors.hasNoLength(input));
  }
}

function utf8ByteLength(_: Interpreter, input: JqValue): JqValue | Fail {
  return typeof input === "string" ? CodePoints.utf8ByteCount(input) : new Fail(Errors.onlyStringsHaveUtf8Length(input));
}

// halt_error(c): halts with the input and trunc(c) for the first output c.
function haltError(interpreter: Interpreter, env: Env | null, args: readonly Node[], input: JqValue, _path: Path | null, _emit: Emit): void {
  const first = Signal.break(interpreter.newLabel());
  let code: JqValue = null;
  interpreter.after((signal) => {
    if (signal !== first) {
      interpreter.ret(signal);
      return;
    }

    if (typeof code !== "number") {
      interpreter.fail(Errors.haltErrorNumberRequired(input));
      return;
    }

    const number = Math.trunc(code);
    interpreter.ret(Signal.halt(input, Number.isNaN(number) ? 0 : Math.min(Math.max(number, -2147483648), 2147483647)));
  });
  interpreter.eval(args[0]!, env, input, null, (value) => {
    code = value;
    interpreter.ret(first);
  });
}

// last(f): the last output of f, with its path. *loop*
function last(interpreter: Interpreter, env: Env | null, args: readonly Node[], input: JqValue, path: Path | null, emit: Emit): void {
  let has = false;
  let lastValue: JqValue = null;
  let lastAt: Path | null = null;
  interpreter.after((signal) => {
    if (signal !== null || !has) interpreter.ret(signal);
    else interpreter.output(emit, lastValue, lastAt);
  });
  interpreter.eval(args[0]!, env, input, path, (value, at) => {
    has = true;
    lastValue = value;
    lastAt = at;
    interpreter.ret(null);
  });
}

function listBuiltins(): JqValue {
  const names: string[] = [];
  for (const native of Natives.All) {
    if (!native.isInternal && !native.overridesPrelude) names.push(native.toString());
  }

  for (const func of Prelude.instance.definitions) {
    if (!func.name.startsWith("_")) names.push(func.toString());
  }

  names.sort(CodePoints.compare);
  return JqArray.fromOwned(names);
}
