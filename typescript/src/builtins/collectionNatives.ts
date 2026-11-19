// §9.4: keys, membership, sorting and its relatives.

import type { Interpreter } from "../runtime/interpreter.js";
import * as Errors from "../values/errors.js";
import { JqArray } from "../values/jqArray.js";
import type { JqObject } from "../values/jqObject.js";
import { Fail, JqKind, type JqValue, kindOf } from "../values/jqValue.js";
import * as ValueOps from "../values/valueOps.js";
import { add } from "./nativeTable.js";

export function register(): void {
  add("keys", 0, (_, input) => keys(input, true));
  add("keys_unsorted", 0, (_, input) => keys(input, false));
  add("has", 1, has);
  add("sort", 0, (_, input) => (kindOf(input) === JqKind.Array ? ValueOps.sort(input as JqArray) : new Fail(Errors.cannotBeSorted(input))));
  add("unique", 0, unique);
  add("_sort_by_impl", 1, sortBy);
  add("_group_by_impl", 1, groupBy);
  add("_min_by_impl", 1, (_, input, args) => extreme(input, args[0]!, false));
  add("_max_by_impl", 1, (_, input, args) => extreme(input, args[0]!, true));
  add("min", 0, (_, input) => extreme(input, input, false));
  add("max", 0, (_, input) => extreme(input, input, true));
  add("bsearch", 1, binarySearch);
}

function keys(input: JqValue, sorted: boolean): JqValue | Fail {
  const kind = kindOf(input);
  if (kind === JqKind.Object) {
    const obj = input as JqObject;
    if (sorted) return JqArray.fromOwned(obj.sortedKeys().slice());
    const names: JqValue[] = [];
    for (let i = 0; i < obj.count; i++) names.push(obj.keyAt(i));
    return JqArray.fromOwned(names);
  }

  if (kind !== JqKind.Array) return new Fail(Errors.hasNoKeys(input));
  const indices: JqValue[] = [];
  for (let i = 0, count = (input as JqArray).count; i < count; i++) indices.push(i);
  return JqArray.fromOwned(indices);
}

function has(_: Interpreter, input: JqValue, args: JqValue[]): JqValue | Fail {
  const key = args[0]!;
  const kind = kindOf(input);
  if (kind === JqKind.Object && typeof key === "string") return (input as JqObject).containsKey(key);
  if (kind === JqKind.Array && typeof key === "number") {
    const index = Math.trunc(key);
    return index >= 0 && index < (input as JqArray).count;
  }

  return kind === JqKind.Null ? false : new Fail(Errors.cannotCheckHas(input, key));
}

function unique(_: Interpreter, input: JqValue): JqValue | Fail {
  if (kindOf(input) !== JqKind.Array) return new Fail(Errors.cannotBeSorted(input));
  const sorted = ValueOps.sort(input as JqArray);
  const kept: JqValue[] = [];
  for (let i = 0; i < sorted.count; i++) {
    if (i === 0 || ValueOps.compare(sorted.get(i - 1), sorted.get(i)) !== 0) kept.push(sorted.get(i));
  }

  return JqArray.fromOwned(kept);
}

// _sort_by_impl(keys): the input stably sorted by the corresponding keys.
function sortBy(_: Interpreter, input: JqValue, args: JqValue[]): JqValue | Fail {
  if (!sameLengthArrays(input, args[0]!)) return new Fail(Errors.cannotBeSortedBy(input, args[0]!));
  const items = input as JqArray;
  const order = ValueOps.sortOrder(args[0] as JqArray);
  const sorted: JqValue[] = [];
  for (let i = 0; i < order.length; i++) sorted.push(items.get(order[i]!));
  return JqArray.fromOwned(sorted);
}

// _group_by_impl(keys): sorted as _sort_by_impl, then consecutive elements with equal keys grouped.
function groupBy(_: Interpreter, input: JqValue, args: JqValue[]): JqValue | Fail {
  if (!sameLengthArrays(input, args[0]!)) return new Fail(Errors.cannotBeSortedBy(input, args[0]!));
  const items = input as JqArray;
  const by = args[0] as JqArray;
  const order = ValueOps.sortOrder(by);
  const groups: JqValue[] = [];
  let group: JqValue[] = [];
  for (let i = 0; i < order.length; i++) {
    if (i > 0 && ValueOps.compare(by.get(order[i - 1]!), by.get(order[i]!)) !== 0) {
      groups.push(JqArray.fromOwned(group));
      group = [];
    }

    group.push(items.get(order[i]!));
  }

  if (group.length > 0) groups.push(JqArray.fromOwned(group));
  return JqArray.fromOwned(groups);
}

// _min_by_impl, _max_by_impl, min, max: null for an empty input; else the first element with the
// smallest key, or the last with the largest.
function extreme(input: JqValue, keysOf: JqValue, max: boolean): JqValue | Fail {
  if (!bothArrays(input, keysOf)) return new Fail(Errors.cannotBeIterated(input, keysOf));
  const items = input as JqArray;
  const by = keysOf as JqArray;
  if (items.count !== by.count) return new Fail(Errors.wrongLength(input, keysOf));
  if (items.count === 0) return null;
  let best = 0;
  for (let i = 1; i < items.count; i++) {
    const c = ValueOps.compare(by.get(i), by.get(best));
    if (max ? c >= 0 : c < 0) best = i;
  }

  return items.get(best);
}

function binarySearch(_: Interpreter, input: JqValue, args: JqValue[]): JqValue | Fail {
  if (kindOf(input) !== JqKind.Array) return new Fail(Errors.cannotBeSearched(input));
  const items = input as JqArray;
  let low = 0;
  let high = items.count - 1;
  while (low <= high) {
    const middle = Math.floor((low + high) / 2);
    const c = ValueOps.compare(items.get(middle), args[0]!);
    if (c === 0) return middle;
    if (c < 0) low = middle + 1;
    else high = middle - 1;
  }

  return -1 - low;
}

function bothArrays(a: JqValue, b: JqValue): boolean {
  return kindOf(a) === JqKind.Array && kindOf(b) === JqKind.Array;
}

function sameLengthArrays(a: JqValue, b: JqValue): boolean {
  return bothArrays(a, b) && (a as JqArray).count === (b as JqArray).count;
}
