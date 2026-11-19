import type { JqArray } from "./jqArray.js";
import type { JqObject } from "./jqObject.js";

/** The kinds of jqlite values, declared in their sort order. */
export const enum JqKind {
  Null,
  False,
  True,
  Number,
  String,
  Array,
  Object,
}

/**
 * An immutable jqlite value: null, false, true, a number (IEEE-754 double, NaN and ±Infinity
 * included), a string, an array or an object.
 *
 * The reference packs a kind and a payload in a struct; here a value is the host's own
 * primitive (`null`, `boolean`, `number`, `string`) or one of the two container classes, so the
 * reference's `JqValue.FromNumber(x)`, `FromBool(b)`, `FromArray(a)` and `value.AsNumber()` are
 * the value itself. A string of a value is well-formed UTF-16: text that enters the engine goes
 * through `CodePoints.sanitize` (what the reference's `JqValue.FromString` does).
 */
export type JqValue = null | boolean | number | string | JqArray | JqObject;

export function kindOf(value: JqValue): JqKind {
  if (value === null) return JqKind.Null;
  switch (typeof value) {
    case "boolean":
      return value ? JqKind.True : JqKind.False;
    case "number":
      return JqKind.Number;
    case "string":
      return JqKind.String;
    default:
      return value.kind;
  }
}

/** False for null and false, true for every other value. */
export function isTruthy(value: JqValue): boolean {
  return value !== null && value !== false;
}

/** The name jq's `type` gives the kind: false and true are both "boolean". */
export function typeName(value: JqValue): string {
  switch (kindOf(value)) {
    case JqKind.Null:
      return "null";
    case JqKind.False:
    case JqKind.True:
      return "boolean";
    case JqKind.Number:
      return "number";
    case JqKind.String:
      return "string";
    case JqKind.Array:
      return "array";
    default:
      return "object";
  }
}

/**
 * The failure of an operation that jq can fail (porting guide §2.2): such an operation returns
 * its result, or a `Fail` holding the jqlite error value (usually the message string). Nothing
 * throws as part of jqlite semantics.
 */
export class Fail {
  constructor(readonly error: JqValue) {}
}
