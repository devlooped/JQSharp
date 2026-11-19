// Converts between the host's plain JSON values and the core's JqValue: the only place with
// conversion logic on host values.
//
// Key order (spec §4.1) is where a plain object differs from a jqlite object: JavaScript
// enumerates the keys of an object that look like array indexes ("0", "42") first, in numeric
// order, and the others after them in insertion order. So an input `{"b": 1, "10": 2}` given as
// a plain object arrives with the keys "10", "b", and a result whose jqlite order is "b", "10"
// is returned as a plain object that enumerates "10", "b". Every other key keeps its position.
// A host that needs the exact order gives a Map as input (its entries in order) and takes the
// results as text (JqExpression.evaluateText), which the core writes itself.

import * as CodePoints from "../values/codePoints.js";
import { JqArray } from "../values/jqArray.js";
import type { JqObject } from "../values/jqObject.js";
import { JqObjectBuilder } from "../values/jqObjectBuilder.js";
import { JqKind, type JqValue } from "../values/jqValue.js";

/** A JSON value as JavaScript has it: what an evaluation returns. */
export type JsonValue = null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue };

/**
 * A value an evaluation takes: a JSON value, where an object may also be a Map from strings (its
 * entries in order), and a property that is `undefined` is left out.
 */
export type JsonInput = null | boolean | number | string | readonly JsonInput[] | ReadonlyMap<string, JsonInput> | { readonly [key: string]: JsonInput | undefined };

// An array, object or Map being converted to a value: its items and the values converted so far.
class Container {
  readonly keys: readonly string[] | null;
  readonly items: readonly (JsonInput | undefined)[];
  readonly array: JqValue[] | null;
  readonly object: JqObjectBuilder | null;
  next = 0;

  constructor(source: object) {
    if (Array.isArray(source)) {
      this.keys = null;
      this.items = source as JsonInput[];
    } else if (source instanceof Map) {
      this.keys = [];
      this.items = [];
      for (const [key, item] of source as Map<unknown, JsonInput>) {
        if (typeof key !== "string") throw new TypeError("A Map given as a JSON object must have string keys.");
        (this.keys as string[]).push(key);
        (this.items as JsonInput[]).push(item);
      }
    } else {
      this.keys = Object.keys(source);
      this.items = this.keys.map((key) => (source as { [key: string]: JsonInput | undefined })[key]);
    }

    this.array = this.keys === null ? [] : null;
    this.object = this.keys === null ? null : new JqObjectBuilder();
  }

  add(value: JqValue): void {
    if (this.object !== null) this.object.set(CodePoints.sanitize(this.keys![this.next - 1]!), value);
    else this.array!.push(value);
  }

  toValue(): JqValue {
    return this.object !== null ? this.object.toObject() : JqArray.fromOwned(this.array!);
  }
}

/**
 * The value of a host value: numbers as they are, strings with lone surrogates replaced by
 * U+FFFD (spec §4.1), objects in the order JavaScript enumerates their keys (see the top of this
 * file), `undefined` in an array as null. Containers are walked with an explicit stack: any
 * depth works. A value that is not JSON (a function, a symbol, a bigint, `undefined` itself) is a
 * TypeError.
 */
export function toValue(input: JsonInput): JqValue {
  if (input === null || typeof input !== "object") return scalar(input, false);
  const stack: Container[] = [new Container(input)];
  while (true) {
    const top = stack[stack.length - 1]!;
    if (top.next === top.items.length) {
      const done = top.toValue();
      stack.pop();
      if (stack.length === 0) return done;
      stack[stack.length - 1]!.add(done);
      continue;
    }

    const item = top.items[top.next++];
    // A property without a value is left out, as JSON.stringify does.
    if (item === undefined && top.object !== null) continue;
    if (item !== null && typeof item === "object") stack.push(new Container(item));
    else top.add(scalar(item, true));
  }
}

function scalar(input: unknown, inArray: boolean): JqValue {
  if (input === null) return null;
  switch (typeof input) {
    case "boolean":
    case "number":
      return input;
    case "string":
      return CodePoints.sanitize(input);
    case "undefined":
      if (inArray) return null;
      break;
  }

  throw new TypeError(`A ${typeof input} is not a JSON value.`);
}

/**
 * The host value of a value, as its canonical text would read back (spec §5.1): NaN is null and
 * ±Infinity is ±1.7976931348623157e+308, so that every result is a JSON value; objects are plain
 * objects with their keys added in order (see the top of this file for what JavaScript does with
 * keys that look like array indexes). Containers are walked with an explicit stack.
 */
export function toJson(value: JqValue): JsonValue {
  if (value === null || typeof value !== "object") return scalarJson(value);
  const result = newTarget(value);
  // Each entry: a container, the host container being filled, and the next item.
  const sources: (JqArray | JqObject)[] = [value];
  const targets: (JsonValue[] | { [key: string]: JsonValue })[] = [result];
  const indexes: number[] = [0];
  while (sources.length > 0) {
    const top = sources.length - 1;
    const source = sources[top]!;
    const index = indexes[top]!;
    if (index === source.count) {
      sources.pop();
      targets.pop();
      indexes.pop();
      continue;
    }

    indexes[top] = index + 1;
    const item = source.kind === JqKind.Array ? source.get(index) : source.valueAt(index);
    let converted: JsonValue;
    if (item !== null && typeof item === "object") {
      converted = newTarget(item);
      sources.push(item);
      targets.push(converted as JsonValue[] | { [key: string]: JsonValue });
      indexes.push(0);
    } else {
      converted = scalarJson(item);
    }

    if (source.kind === JqKind.Array) (targets[top] as JsonValue[]).push(converted);
    else setProperty(targets[top] as { [key: string]: JsonValue }, source.keyAt(index), converted);
  }

  return result;
}

function newTarget(value: JqArray | JqObject): JsonValue[] | { [key: string]: JsonValue } {
  return value.kind === JqKind.Array ? [] : {};
}

// An own property, also for "__proto__", which an assignment would take for the prototype.
function setProperty(target: { [key: string]: JsonValue }, key: string, value: JsonValue): void {
  if (key === "__proto__") Object.defineProperty(target, key, { value, writable: true, enumerable: true, configurable: true });
  else target[key] = value;
}

function scalarJson(value: null | boolean | number | string): JsonValue {
  if (typeof value !== "number") return value;
  if (Number.isNaN(value)) return null;
  if (value === Infinity) return Number.MAX_VALUE;
  if (value === -Infinity) return -Number.MAX_VALUE;
  return value;
}
