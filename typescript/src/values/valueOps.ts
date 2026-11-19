// The one implementation of equality, ordering, arithmetic, slicing and sorting of values, with
// jq 1.8 semantics.
//
// An operation that can fail in jq returns a Fail with the jqlite error value (the message
// string); on success it returns the result. Nothing here throws as part of jqlite semantics.

import * as CodePoints from "./codePoints.js";
import * as Errors from "./errors.js";
import { JqArray } from "./jqArray.js";
import { JqObject } from "./jqObject.js";
import { JqObjectBuilder } from "./jqObjectBuilder.js";
import { Fail, JqKind, type JqValue, kindOf } from "./jqValue.js";

// The longest string a repeat may build (V8's string length limit, the smallest of the engines').
const MaxStringLength = 0x1fffffe8;

const MaxInt32 = 0x7fffffff;

/**
 * jq's `==`: same kind and same content. Numbers compare as doubles, so `nan == nan` is false
 * and so is `[nan] == [nan]`; objects are equal whatever their key order.
 */
export function areEqual(a: JqValue, b: JqValue): boolean {
  return walk(a, b, true) === 0;
}

/**
 * jq's total order; returns -1, 0 or 1. Kinds first: null < false < true < numbers < strings <
 * arrays < objects. Strings by code point; arrays lexicographically; objects by their sorted key
 * lists, then by their values in that key order. NaN is below every other number and equal to
 * NaN, so the order is total (jq 1.8 has `nan < nan`; spec §13).
 */
export function compare(a: JqValue, b: JqValue): number {
  return walk(a, b, false);
}

// A pair of containers being walked: children 0 to count - 1 are compared in turn, then tail
// (the length comparison of two arrays) decides.
interface Frame {
  a: JqArray | JqObject;
  b: JqArray | JqObject;
  count: number;
  index: number;
  tail: number;
  keys: readonly string[] | null;
}

// The results of shallow() and child() beside what they return.
let shallowFrame: Frame | null = null;
let shallowTail = 0;
let childA: JqValue = null;
let childB: JqValue = null;

/**
 * Compares (equality false) or tests equality (equality true, any non-zero result means
 * "different") without recursion: the containers being compared are on an explicit stack, so
 * nesting depth is not limited by the host stack.
 */
function walk(a: JqValue, b: JqValue, equality: boolean): number {
  let c = shallow(a, b, equality);
  if (c !== 0 || shallowFrame === null) return c !== 0 ? c : shallowTail;

  const stack: Frame[] = [shallowFrame];
  if (!child(shallowFrame, equality)) return 1;

  while (true) {
    c = shallow(childA, childB, equality);
    if (c !== 0) return c;
    const frame: Frame | null = shallowFrame;
    if (frame !== null) {
      stack.push(frame);
      if (!child(frame, equality)) return 1;
      continue;
    }

    if (shallowTail !== 0) return shallowTail;

    // The pair is equal: go on with the next pair, leaving the containers that end.
    while (true) {
      if (stack.length === 0) return 0;

      const top = stack[stack.length - 1]!;
      top.index++;
      if (top.index < top.count) {
        if (!child(top, equality)) return 1;
        break;
      }

      stack.pop();
      if (top.tail !== 0) return top.tail;
    }
  }
}

// Compares what can be compared without looking at children. A non-zero result decides. With
// zero, shallowFrame not null means the children must be compared, and its tail (or shallowTail
// without children) is the result once they are all equal.
function shallow(a: JqValue, b: JqValue, equality: boolean): number {
  shallowFrame = null;
  shallowTail = 0;
  const kind = kindOf(a);
  const other = kindOf(b);
  if (kind !== other) return kind < other ? -1 : 1;

  switch (kind) {
    case JqKind.Number: {
      const x = a as number;
      const y = b as number;
      if (equality) return x === y ? 0 : 1;
      if (Number.isNaN(x)) return Number.isNaN(y) ? 0 : -1;
      if (Number.isNaN(y)) return 1;
      return x < y ? -1 : x === y ? 0 : 1;
    }
    case JqKind.String:
      return equality ? (a === b ? 0 : 1) : CodePoints.compare(a as string, b as string);
    case JqKind.Array: {
      const p = (a as JqArray).count;
      const q = (b as JqArray).count;
      if (equality && p !== q) return 1;
      const count = Math.min(p, q);
      const tail = p === q ? 0 : p < q ? -1 : 1;
      if (count > 0) shallowFrame = { a: a as JqArray, b: b as JqArray, count, index: 0, tail, keys: null };
      else shallowTail = tail;
      return 0;
    }
    case JqKind.Object: {
      const m = a as JqObject;
      const n = b as JqObject;
      if (equality) {
        // Same size, then each key of a found in b with an equal value: no sorting.
        if (m.count !== n.count) return 1;
        if (m.count > 0) shallowFrame = { a: m, b: n, count: m.count, index: 0, tail: 0, keys: null };
        return 0;
      }

      const keys = m.sortedKeys();
      const others = n.sortedKeys();
      const length = Math.min(keys.length, others.length);
      for (let i = 0; i < length; i++) {
        const c = CodePoints.compare(keys[i]!, others[i]!);
        if (c !== 0) return c;
      }

      if (keys.length !== others.length) return keys.length < others.length ? -1 : 1;
      if (keys.length > 0) shallowFrame = { a: m, b: n, count: keys.length, index: 0, tail: 0, keys };
      return 0;
    }
    default:
      return 0;
  }
}

// The children at frame.index, in childA and childB; false when b lacks a key of a (equality of objects only).
function child(frame: Frame, equality: boolean): boolean {
  if (frame.a.kind === JqKind.Array) {
    childA = frame.a.get(frame.index);
    childB = (frame.b as JqArray).get(frame.index);
    return true;
  }

  const x = frame.a;
  const y = frame.b as JqObject;
  if (equality) {
    childA = x.valueAt(frame.index);
    const found = y.get(x.keyAt(frame.index));
    if (found === undefined) return false;
    childB = found;
    return true;
  }

  const key = frame.keys![frame.index]!;
  childA = x.get(key)!;
  childB = y.get(key)!;
  return true;
}

/** jq's `+`: null is the identity; numbers add; strings and arrays concatenate; objects merge (right wins, insertion order kept). */
export function tryAdd(a: JqValue, b: JqValue): JqValue | Fail {
  if (typeof a === "number" && typeof b === "number") return a + b;
  if (a === null) return b;
  if (b === null) return a;

  const kind = kindOf(a);
  if (kind === kindOf(b)) {
    switch (kind) {
      case JqKind.String:
        return (a as string) + (b as string);
      case JqKind.Array:
        return (a as JqArray).concat(b as JqArray);
      case JqKind.Object:
        return (a as JqObject).merge(b as JqObject);
    }
  }

  return new Fail(Errors.cannotBe(a, b, Errors.Added));
}

/** jq's `-`: numbers subtract; arrays drop every element equal to one of the right-hand array. */
export function trySubtract(a: JqValue, b: JqValue): JqValue | Fail {
  if (typeof a === "number" && typeof b === "number") return a - b;

  if (kindOf(a) === JqKind.Array && kindOf(b) === JqKind.Array) {
    const items = a as JqArray;
    const remove = b as JqArray;
    const kept: JqValue[] = [];
    for (let i = 0; i < items.count; i++) {
      const item = items.get(i);
      let found = false;
      for (let j = 0; j < remove.count && !found; j++) found = areEqual(item, remove.get(j));
      if (!found) kept.push(item);
    }

    return JqArray.fromOwned(kept);
  }

  return new Fail(Errors.cannotBe(a, b, Errors.Subtracted));
}

/** jq's unary `-`: IEEE negation of a number (`0 | -.` is -0). */
export function tryNegate(value: JqValue): JqValue | Fail {
  if (typeof value !== "number") return new Fail(Errors.cannotNegate(value));
  return -value;
}

/**
 * jq's `*`: numbers multiply; a string and a number (either order) repeat the string; objects
 * merge recursively.
 */
export function tryMultiply(a: JqValue, b: JqValue): JqValue | Fail {
  if (typeof a === "number" && typeof b === "number") return a * b;

  if (typeof a === "string" && typeof b === "number") return tryRepeat(a, b);
  if (typeof a === "number" && typeof b === "string") return tryRepeat(b, a);

  if (kindOf(a) === JqKind.Object && kindOf(b) === JqKind.Object) return deepMerge(a as JqObject, b as JqObject);

  return new Fail(Errors.cannotBe(a, b, Errors.Multiplied));
}

// s * n: null for a NaN or negative n; otherwise s repeated trunc(n) times ("" below 1).
function tryRepeat(text: string, times: number): JqValue | Fail {
  if (Number.isNaN(times) || times < 0) return null;

  const count = times >= MaxInt32 ? MaxInt32 : Math.trunc(times);
  if (count === 0 || text.length === 0) return "";

  // The spec's limit is 2^31 - 1 UTF-8 bytes; the host's string length limit may be hit first.
  if (CodePoints.utf8ByteCount(text) * count > MaxInt32 || text.length * count > MaxStringLength) return new Fail(Errors.repeatTooLong);

  return text.repeat(count);
}

// a * b on objects: like a + b, except that a key whose two values are objects gets their merge.
// The objects being merged are on an explicit stack, so nesting depth is not limited by the host stack.
function deepMerge(a: JqObject, b: JqObject): JqObject {
  const stack: Merge[] = [new Merge(a, b)];
  while (true) {
    const top = stack[stack.length - 1]!;
    if (top.next < top.right.count) {
      const key = top.right.keyAt(top.next);
      const value = top.right.valueAt(top.next++);
      const existing = kindOf(value) === JqKind.Object ? top.result.get(key) : undefined;
      if (existing !== undefined && kindOf(existing) === JqKind.Object) {
        top.key = key;
        stack.push(new Merge(existing as JqObject, value as JqObject));
      } else {
        top.result.set(key, value);
      }

      continue;
    }

    const merged = top.result.toObject();
    stack.pop();
    if (stack.length === 0) return merged;
    const parent = stack[stack.length - 1]!;
    parent.result.set(parent.key, merged);
  }
}

// One pair of objects being merged: the result so far, the right side's next key, and the key
// whose values are being merged below.
class Merge {
  readonly result: JqObjectBuilder;
  next = 0;
  key = "";

  constructor(
    left: JqObject,
    readonly right: JqObject,
  ) {
    this.result = JqObjectBuilder.from(left);
  }
}

/** jq's `/`: numbers divide (a zero divisor is an error, `0/0` too); strings split on the right-hand string. */
export function tryDivide(a: JqValue, b: JqValue): JqValue | Fail {
  if (typeof a === "number" && typeof b === "number") {
    if (b === 0) return new Fail(Errors.divisorIsZero(a, b, Errors.Divided));
    return a / b;
  }

  if (typeof a === "string" && typeof b === "string") return split(a, b);

  return new Fail(Errors.cannotBe(a, b, Errors.Divided));
}

/**
 * `split/1` and `/` on strings: the empty string gives []; an empty separator splits into code
 * points.
 */
export function split(text: string, separator: string): JqArray {
  const parts: JqValue[] = [];
  if (text.length === 0) return JqArray.Empty;

  if (separator.length === 0) {
    for (let i = 0; i < text.length; i++) {
      const units = CodePoints.isHighSurrogate(text.charCodeAt(i)) ? 2 : 1;
      parts.push(text.substring(i, i + units));
      i += units - 1;
    }

    return JqArray.fromOwned(parts);
  }

  let start = 0;
  let found: number;
  while ((found = text.indexOf(separator, start)) >= 0) {
    parts.push(text.substring(start, found));
    start = found + separator.length;
  }

  parts.push(text.substring(start));
  return JqArray.fromOwned(parts);
}

const MaxSafe = 9007199254740992; // 2^53
const MinInt64 = -(2n ** 63n);
const MaxInt64 = 2n ** 63n - 1n;

/**
 * jq's `%`: both numbers truncate to integers (saturating at the 64-bit range), the sign follows
 * the dividend; NaN gives NaN; a divisor that truncates to 0 is an error.
 */
export function tryModulo(a: JqValue, b: JqValue): JqValue | Fail {
  if (typeof a !== "number" || typeof b !== "number") return new Fail(Errors.cannotBe(a, b, Errors.Remainder));

  if (Number.isNaN(a) || Number.isNaN(b)) return NaN;

  if (Math.abs(a) < MaxSafe && Math.abs(b) < MaxSafe) {
    // Both integers are exact doubles, and so is their remainder; + 0 turns -0 into 0.
    const divisor = Math.trunc(b);
    if (divisor === 0) return new Fail(Errors.divisorIsZero(a, b, Errors.Remainder));
    return (Math.trunc(a) % divisor) + 0;
  }

  const divisor = toInteger(b);
  if (divisor === 0n) return new Fail(Errors.divisorIsZero(a, b, Errors.Remainder));

  // -1 divides everything.
  return divisor === -1n ? 0 : Number(toInteger(a) % divisor);
}

// Truncation toward zero, clamped to [-2^63, 2^63 - 1] (±Infinity clamp to the ends).
function toInteger(value: number): bigint {
  return value <= -9223372036854775808 ? MinInt64 : value >= 9223372036854775808 ? MaxInt64 : BigInt(Math.trunc(value));
}

/**
 * `.[key]` (§7.6): an object by a string (absent: null), an array by a number (NaN and out of
 * range: null; negative counts from the end) or by an array (the indices where it occurs), an
 * array or a string by a slice object, null by a string, a number or an object (null); anything
 * else is `Cannot index T with T (D)`.
 */
export function tryIndex(target: JqValue, key: JqValue): JqValue | Fail {
  const kind = kindOf(target);
  switch (kindOf(key)) {
    case JqKind.String:
      if (kind === JqKind.Object) {
        const value = (target as JqObject).get(key as string);
        return value === undefined ? null : value;
      }

      if (kind === JqKind.Null) return null;
      break;
    case JqKind.Number:
      if (kind === JqKind.Array) return element(target as JqArray, key as number);
      if (kind === JqKind.Null) return null;
      break;
    case JqKind.Array:
      if (kind === JqKind.Array) return indices(target as JqArray, key as JqArray);
      break;
    case JqKind.Object:
      if (kind === JqKind.Array || kind === JqKind.String || kind === JqKind.Null) return tryIndexSlice(target, key);
      break;
  }

  return new Fail(Errors.cannotIndex(target, key));
}

// .[n] on an array: NaN and out of range give null, a negative index counts from the end.
function element(array: JqArray, index: number): JqValue {
  if (Number.isNaN(index)) return null;
  let i = Math.trunc(index);
  if (i < 0) i += array.count;
  return i >= 0 && i < array.count ? array.get(i) : null;
}

// The positions at which needle occurs in items as a contiguous sub-array; none for an empty needle.
function indices(items: JqArray, needle: JqArray): JqArray {
  const found: JqValue[] = [];
  for (let start = 0; needle.count > 0 && start + needle.count <= items.count; start++) {
    let i = 0;
    while (i < needle.count && areEqual(items.get(start + i), needle.get(i))) i++;
    if (i === needle.count) found.push(start);
  }

  return JqArray.fromOwned(found);
}

/**
 * `.[start:end]` (§7.6) on an array, a string (by code point) or null (gives null). A null or
 * NaN start is 0, a null or NaN end the length; a negative bound counts from the end; both are
 * clamped, then the start rounds down and the end up.
 */
export function trySlice(input: JqValue, start: JqValue, end: JqValue): JqValue | Fail {
  const kind = kindOf(input);
  if (kind !== JqKind.Null && kind !== JqKind.Array && kind !== JqKind.String) {
    // The key in the message is the slice object that `.[s:e]` stands for.
    const key = JqObject.Empty.set("start", start).set("end", end);
    return new Fail(Errors.cannotIndex(input, key));
  }

  return trySliceBounds(input, start, end);
}

/**
 * `.[k]` with an object key: on an array or a string the key is a slice when it has both
 * `start` and `end` (other keys are ignored, as in jq), else the error
 * `Array/string slice indices must be integers`; null input gives null whatever the key; any
 * other input is `Cannot index T with object (D)`.
 */
export function tryIndexSlice(input: JqValue, key: JqValue): JqValue | Fail {
  const kind = kindOf(input);
  if (kind === JqKind.Null) return null;
  if (kind !== JqKind.Array && kind !== JqKind.String) return new Fail(Errors.cannotIndex(input, key));

  const slice = key as JqObject;
  const start = slice.get("start");
  const end = slice.get("end");
  if (start === undefined || end === undefined) return new Fail(Errors.sliceIndicesNotIntegers);
  return trySliceBounds(input, start, end);
}

// Slices an array, a string or null (gives null).
function trySliceBounds(input: JqValue, start: JqValue, end: JqValue): JqValue | Fail {
  if (input === null) return null;

  const text = typeof input === "string" ? input : null;
  const length = text === null ? (input as JqArray).count : CodePoints.length(text);
  const failed = trySliceRange(length, start, end);
  if (failed !== null) return failed;

  return text === null ? (input as JqArray).slice(sliceFirst, sliceLast) : CodePoints.substring(text, sliceFirst, sliceLast);
}

/** The results of `trySliceRange`: the positions [sliceFirst, sliceLast). */
export let sliceFirst = 0;
export let sliceLast = 0;

/**
 * The positions [`sliceFirst`, `sliceLast`) that the bounds of a slice select in a sequence of
 * `length` items (§7.6): null or NaN start is 0, null or NaN end the length, negative bounds
 * count from the end, both are clamped, the start rounds down and the end up. Bounds that are
 * not numbers or null: `Array/string slice indices must be integers`. Returns null on success.
 */
export function trySliceRange(length: number, start: JqValue, end: JqValue): Fail | null {
  sliceFirst = sliceLast = 0;
  if ((start !== null && typeof start !== "number") || (end !== null && typeof end !== "number")) return new Fail(Errors.sliceIndicesNotIntegers);

  let from = start === null ? 0 : start;
  let to = end === null ? length : end;
  if (Number.isNaN(from)) from = 0;
  if (Number.isNaN(to)) to = length;
  if (from < 0) from += length;
  if (to < 0) to += length;
  sliceFirst = Math.floor(clamp(from, 0, length));
  sliceLast = Math.ceil(clamp(to, sliceFirst, length));
  return null;
}

function clamp(value: number, min: number, max: number): number {
  return value < min ? min : value > max ? max : value;
}

/** jq's `explode`: the code points of a string. */
export function tryExplode(input: JqValue): JqValue | Fail {
  if (typeof input !== "string") return new Fail(Errors.explodeNotString);
  return CodePoints.explode(input);
}

/**
 * jq's `implode`: an array of numbers to the string of the code points `trunc(n)`; one outside
 * [0, 0x10FFFF] or a surrogate becomes U+FFFD (`-0.5` truncates to U+0000). A non-number or NaN
 * element is an error that describes the element.
 */
export function tryImplode(input: JqValue): JqValue | Fail {
  if (kindOf(input) !== JqKind.Array) return new Fail(Errors.implodeNotArray);

  const codes = input as JqArray;
  let text = "";
  for (let i = 0; i < codes.count; i++) {
    const code = codes.get(i);
    if (typeof code !== "number" || Number.isNaN(code)) return new Fail(Errors.codepointNotNumeric(code));
    // + 0: -0.5 truncates to -0, which is U+0000.
    const number = Math.trunc(code) + 0;
    text += CodePoints.toText(CodePoints.isScalar(number) ? number : CodePoints.Replacement);
  }

  return text;
}

/** A stably sorted copy of `items` (jq's `sort`). */
export function sort(items: JqArray): JqArray {
  const order = sortOrder(items);
  const sorted: JqValue[] = [];
  for (let i = 0; i < order.length; i++) sorted.push(items.get(order[i]!));
  return JqArray.fromOwned(sorted);
}

/**
 * The positions of `keys` in sorted order, equal keys in their original order (a stable sort,
 * for `sort_by`, `group_by` and `unique_by`): a bottom-up merge sort.
 */
export function sortOrder(keys: JqArray): Int32Array {
  const n = keys.count;
  const values = keys.toArray();
  let order = new Int32Array(n);
  for (let i = 0; i < n; i++) order[i] = i;
  let merged = new Int32Array(n);
  for (let width = 1; width < n; width *= 2) {
    for (let low = 0; low < n; low += 2 * width) {
      const middle = Math.min(low + width, n);
      const high = Math.min(low + 2 * width, n);
      let left = low;
      let right = middle;
      let target = low;
      while (left < middle && right < high) {
        // Take the left one unless the right one is strictly smaller: stable.
        merged[target++] = compare(values[order[left]!]!, values[order[right]!]!) <= 0 ? order[left++]! : order[right++]!;
      }

      while (left < middle) merged[target++] = order[left++]!;
      while (right < high) merged[target++] = order[right++]!;
    }

    const swap = order;
    order = merged;
    merged = swap;
  }

  return order;
}
