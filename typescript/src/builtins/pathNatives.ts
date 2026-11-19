// §9.5 and §8: path, getpath, setpath, delpaths.

import type { Node } from "../binding/bound.js";
import type { Env } from "../runtime/env.js";
import type { Emit, Interpreter } from "../runtime/interpreter.js";
import { Path } from "../runtime/path.js";
import * as Errors from "../values/errors.js";
import { JqArray } from "../values/jqArray.js";
import { JqObject } from "../values/jqObject.js";
import { Fail, JqKind, type JqValue, kindOf } from "../values/jqValue.js";
import * as ValueOps from "../values/valueOps.js";
import { add, addClosure } from "./nativeTable.js";

// setpath's index limit (§11).
const MaxIndex = 536870911;

export function register(): void {
  addClosure("path", 1, pathOf);
  addClosure("getpath", 1, getPath);
  add("setpath", 2, (_, input, args) => trySetPath(input, args[0]!, args[1]!));
  add("delpaths", 1, (_, input, args) => tryDeletePaths(input, args[0]!));
}

// path(f): f in path mode from the empty path; each output must have a path.
function pathOf(interpreter: Interpreter, env: Env | null, args: readonly Node[], input: JqValue, path: Path | null, emit: Emit): void {
  const at = Path.of(path);
  interpreter.forEach(args[0]!, env, input, Path.Root, (value, valueAt) => {
    if (valueAt !== null && valueAt.isTracked) interpreter.output(emit, valueAt.toValue(), at);
    else interpreter.fail(Errors.invalidPathResult(value));
  });
}

// getpath(p): p in value mode; the output's path is the input's followed by p, even when absent.
function getPath(interpreter: Interpreter, env: Env | null, args: readonly Node[], input: JqValue, path: Path | null, emit: Emit): void {
  interpreter.forEach(args[0]!, env, input, null, (keys) => {
    if (kindOf(keys) !== JqKind.Array) {
      interpreter.fail(Errors.pathNotArray);
      return;
    }

    const steps = keys as JqArray;
    let value: JqValue = input;
    for (let i = 0; i < steps.count; i++) {
      const next = ValueOps.tryIndex(value, steps.get(i));
      if (next instanceof Fail) {
        interpreter.fail(next.error);
        return;
      }

      value = next;
    }

    interpreter.output(emit, value, path !== null && path.isTracked ? path.appendAll(steps) : path);
  });
}

/**
 * setpath(p; v) (§8.2): each container on the way is indexed first (so its errors come first),
 * then the containers are rebuilt from the innermost out, each copied one level.
 */
export function trySetPath(root: JqValue, keys: JqValue, value: JqValue): JqValue | Fail {
  if (kindOf(keys) !== JqKind.Array) return new Fail(Errors.pathNotArray);
  const path = keys as JqArray;
  const containers: JqValue[] = [];
  let current: JqValue = root;
  for (let i = 0; i < path.count; i++) {
    containers.push(current);
    const next = ValueOps.tryIndex(current, path.get(i));
    if (next instanceof Fail) return next;
    current = next;
  }

  let result: JqValue | Fail = value;
  for (let i = path.count - 1; i >= 0; i--) {
    result = trySet(containers[i]!, path.get(i), result);
    if (result instanceof Fail) return result;
  }

  return result;
}

/** One level of setpath: the table of §8.2. */
export function trySet(container: JqValue, key: JqValue, value: JqValue): JqValue | Fail {
  const kind = kindOf(container);
  switch (kindOf(key)) {
    case JqKind.String:
      if (kind === JqKind.Object || kind === JqKind.Null) {
        const obj = kind === JqKind.Null ? JqObject.Empty : (container as JqObject);
        return obj.set(key as string, value);
      }

      break;
    case JqKind.Number:
      if (kind === JqKind.Array || kind === JqKind.Null) {
        const array = kind === JqKind.Null ? JqArray.Empty : (container as JqArray);
        const number = key as number;
        if (Number.isNaN(number)) return new Fail(Errors.nanIndex);
        let index = Math.trunc(number);
        if (index < 0) index += array.count;
        if (index < 0) return new Fail(Errors.negativeIndex);
        if (index > MaxIndex) return new Fail(Errors.indexTooLarge);
        return array.setItem(index, value);
      }

      break;
    case JqKind.Object:
      if (kind === JqKind.Array || kind === JqKind.Null) return trySetSlice(kind === JqKind.Null ? JqArray.Empty : (container as JqArray), key as JqObject, value);
      if (kind === JqKind.String) return new Fail(Errors.cannotUpdateStringSlices);
      break;
  }

  return new Fail(Errors.cannotUpdateField(key, container));
}

// .[s:e] = v on an array: v (an array) replaces the elements the bounds select.
function trySetSlice(array: JqArray, slice: JqObject, value: JqValue): JqValue | Fail {
  const start = slice.get("start");
  const end = slice.get("end");
  if (start === undefined || end === undefined) return new Fail(Errors.sliceIndicesNotIntegers);
  const failed = ValueOps.trySliceRange(array.count, start, end);
  if (failed !== null) return failed;
  const first = ValueOps.sliceFirst;
  const last = ValueOps.sliceLast;
  if (kindOf(value) !== JqKind.Array) return new Fail(Errors.sliceNeedsArray);
  return array
    .slice(0, first)
    .concat(value as JqArray)
    .concat(array.slice(last, array.count));
}

/** delpaths(ps) (§8.2): the paths sorted, then deleted level by level. */
export function tryDeletePaths(root: JqValue, paths: JqValue): JqValue | Fail {
  if (kindOf(paths) !== JqKind.Array) return new Fail(Errors.pathsNotArray);
  const list = paths as JqArray;
  for (let i = 0; i < list.count; i++) {
    const path = list.get(i);
    if (kindOf(path) !== JqKind.Array) return new Fail(Errors.pathElementNotArray(path));
    if ((path as JqArray).count === 0) return null;
  }

  const sorted = ValueOps.sort(list);
  return tryDelete(root, sorted, 0, sorted.count, 0);
}

// One container being rebuilt by tryDelete: the paths [next, to) still to look at, at depth; the
// keys deleted at this level; and the key of the child being rebuilt below it, if any.
class Level {
  next: number;
  readonly deleted: JqValue[] = [];
  childKey: JqValue = null;
  childEnd = 0;

  constructor(
    public value: JqValue,
    from: number,
    readonly to: number,
    readonly depth: number,
  ) {
    this.next = from;
  }
}

// Deletes paths[from, to) (sorted, all longer than depth) from value at the given depth: groups
// by the key at that depth; a group holding the path that ends there deletes the key, the others
// go into the child first. All the keys of one level are removed at once. The levels being
// rebuilt are on an explicit stack, so a path of any length is no recursion.
function tryDelete(value: JqValue, paths: JqArray, from: number, to: number, depth: number): JqValue | Fail {
  const levels: Level[] = [new Level(value, from, to, depth)];
  while (true) {
    const level = levels[levels.length - 1]!;
    let descended = false;
    while (level.value !== null && level.next < level.to && !descended) {
      const path = paths.get(level.next) as JqArray;
      const key = path.get(level.depth);
      let end = level.next + 1;
      let ends = path.count === level.depth + 1;
      for (; end < level.to; end++) {
        const other = paths.get(end) as JqArray;
        if (ValueOps.compare(other.get(level.depth), key) !== 0) break;
        if (other.count === level.depth + 1) ends = true;
      }

      if (ends) {
        level.deleted.push(key);
        level.next = end;
        continue;
      }

      const child = ValueOps.tryIndex(level.value, key);
      if (child instanceof Fail) return child;
      if (child === null) {
        level.next = end;
        continue;
      }

      level.childKey = key;
      level.childEnd = end;
      levels.push(new Level(child, level.next, end, level.depth + 1));
      descended = true;
    }

    if (descended) continue;

    // This level is done: remove its keys, then put it back into its parent.
    const done = tryRemove(level.value, level.deleted);
    if (done instanceof Fail) return done;
    levels.pop();
    if (levels.length === 0) return done;
    const parent = levels[levels.length - 1]!;
    const replaced = trySet(parent.value, parent.childKey, done);
    if (replaced instanceof Fail) return replaced;
    parent.value = replaced;
    parent.next = parent.childEnd;
  }
}

// Removes keys from a container, all relative to the container as it is.
function tryRemove(container: JqValue, keys: readonly JqValue[]): JqValue | Fail {
  const kind = kindOf(container);
  if (keys.length === 0 || kind === JqKind.Null) return container;
  if (kind === JqKind.Object) {
    let obj = container as JqObject;
    for (const key of keys) {
      if (typeof key !== "string") return new Fail(Errors.cannotDeleteField(key));
      obj = obj.remove(key);
    }

    return obj;
  }

  if (kind !== JqKind.Array) return new Fail(Errors.cannotDeleteFrom(container));
  const array = container as JqArray;
  const removed = new Uint8Array(array.count);
  for (const key of keys) {
    const keyKind = kindOf(key);
    if (keyKind === JqKind.Number) {
      const number = key as number;
      if (Number.isNaN(number)) continue;
      let index = Math.trunc(number);
      if (index < 0) index += array.count;
      if (index >= 0 && index < array.count) removed[index] = 1;
    } else if (keyKind === JqKind.Object) {
      const slice = key as JqObject;
      const start = slice.get("start");
      const end = slice.get("end");
      if (start === undefined || end === undefined) return new Fail(Errors.sliceIndicesNotIntegers);
      const failed = ValueOps.trySliceRange(array.count, start, end);
      if (failed !== null) return failed;
      for (let i = ValueOps.sliceFirst, last = ValueOps.sliceLast; i < last; i++) removed[i] = 1;
    } else {
      return new Fail(Errors.cannotDeleteElement(key));
    }
  }

  const kept: JqValue[] = [];
  for (let i = 0; i < array.count; i++) {
    if (removed[i] === 0) kept.push(array.get(i));
  }

  return JqArray.fromOwned(kept);
}
