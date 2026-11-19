// _modify/2 and _assign/2 (§8.3) implemented natively, overriding their prelude definitions
// (Native.overridesPrelude): same results, errors, order of evaluation and path behavior, but
// linear in the number of paths, where the prelude's one setpath per path copies the container
// each time. The value being updated is a WorkingTree.

import type { Node } from "../binding/bound.js";
import type { Env } from "../runtime/env.js";
import type { Emit, Interpreter } from "../runtime/interpreter.js";
import { Path } from "../runtime/path.js";
import { Signal } from "../runtime/signal.js";
import * as Errors from "../values/errors.js";
import { JqArray } from "../values/jqArray.js";
import { JqArrayBuilder } from "../values/jqArrayBuilder.js";
import { JqObject } from "../values/jqObject.js";
import { JqObjectBuilder } from "../values/jqObjectBuilder.js";
import { Fail, JqKind, type JqValue, kindOf } from "../values/jqValue.js";
import * as ValueOps from "../values/valueOps.js";
import { addClosure } from "./nativeTable.js";
import * as PathNatives from "./pathNatives.js";

export function register(): void {
  addClosure("_modify", 2, modify);
  addClosure("_assign", 2, assign);
}

// def _modify(paths; update): for each path of paths (on the input, lazily), the first output of
// update applied to the current value there replaces it; no output: the path is deleted at the
// end. update runs in value mode; the result has no path.
function modify(interpreter: Interpreter, env: Env | null, args: readonly Node[], input: JqValue, path: Path | null, emit: Emit): void {
  const tree = new WorkingTree(input);
  const deletions: JqValue[] = [];
  const update = args[1]!;
  const first = Signal.break(interpreter.newLabel());
  interpreter.after((signal) => {
    if (signal !== null) interpreter.ret(signal);
    else interpreter.apply(PathNatives.tryDeletePaths(tree.freeze(), JqArray.fromOwned(deletions)), Path.of(path), emit);
  });
  interpreter.eval(args[0]!, env, input, Path.Root, (value, at) => {
    if (at === null || !at.isTracked) {
      interpreter.fail(Errors.invalidPathResult(value));
      return;
    }

    const keys = at.toValue() as JqArray;
    const current = tree.tryGet(keys);
    if (current instanceof Fail) {
      interpreter.fail(current.error);
      return;
    }

    // The path is deleted (updated false), or set to next.
    const store = (updated: boolean, next: JqValue): void => {
      if (!updated) {
        deletions.push(keys);
        interpreter.ret(null);
        return;
      }

      const failed = tree.trySet(keys, next);
      interpreter.ret(failed === null ? null : Signal.error(failed.error));
    };

    // A direct update has its one output, or none, at once.
    if (update.direct) {
      const signal = interpreter.evalOne(update, env, current, null);
      if (signal !== null) interpreter.ret(signal);
      else store(interpreter.has, interpreter.value);
      return;
    }

    let updated = false;
    let next: JqValue = null;
    interpreter.after((stop) => {
      if (stop !== null && stop !== first) interpreter.ret(stop);
      else store(updated, next);
    });
    interpreter.eval(update, env, current, null, (output) => {
      updated = true;
      next = output;
      interpreter.ret(first);
    });
  });
}

// def _assign(paths; $value): for each output of value (on the input), every path of paths set
// to it. With no path the result is the input itself, with its path (the reduce's initial state).
function assign(interpreter: Interpreter, env: Env | null, args: readonly Node[], input: JqValue, path: Path | null, emit: Emit): void {
  interpreter.forEach(args[1]!, env, input, null, (value) => {
    const tree = new WorkingTree(input);
    let any = false;
    interpreter.after((signal) => {
      if (signal !== null) interpreter.ret(signal);
      else if (any) interpreter.output(emit, tree.freeze(), Path.of(path));
      else interpreter.output(emit, input, path);
    });
    interpreter.eval(args[0]!, env, input, Path.Root, (target, at) => {
      if (at === null || !at.isTracked) {
        interpreter.fail(Errors.invalidPathResult(target));
        return;
      }

      any = true;
      const failed = tree.trySet(at.toValue() as JqArray, value);
      interpreter.ret(failed === null ? null : Signal.error(failed.error));
    });
  });
}

// setpath's index limit (§11).
const MaxIndex = 536870911;

// A container copied from a value: its builder, the drafts below it by slot, and where it hangs.
class Draft {
  items: Map<number, Draft> | null = null;
  fields: Map<string, Draft> | null = null;

  constructor(
    readonly array: JqArrayBuilder | null,
    readonly object: JqObjectBuilder | null,
    readonly parent: Draft | null,
    readonly index: number,
    readonly key: string | null,
  ) {}

  build(): JqValue {
    return this.array !== null ? this.array.toArray() : this.object!.toObject();
  }
}

// A slot of the tree: the root (parent null), an element of an array draft or a field of an object draft.
class Slot {
  constructor(
    readonly parent: Draft | null,
    readonly index: number,
    readonly key: string | null,
  ) {}
}

const RootSlot = new Slot(null, 0, null);

// Whether the slot tryRead returned is of an array element out of range.
let missing = false;

/**
 * A value under update, owned by one call of `_modify` or `_assign`. Each node is either an
 * immutable value or a draft: a container the tree copied (one level) and then updates in
 * place, so a container is copied at most once however many paths go through it.
 *
 * Nothing a program can see is ever changed. `trySet` stores the new value as an immutable leaf
 * and opens drafts only along its path; `tryGet`, which hands a value to the update closure, and
 * `freeze` turn the drafts below the path back into values, giving them their storage without
 * copying, so a later write there copies that level again. Paths with keys the drafts do not
 * handle (slices, keys that `setpath` rejects) fall back to `getpath`/`setpath` on the frozen
 * value below that point, which keeps their results and errors, in their order, those of the
 * prelude definition. No walk recurses: paths and drafts are followed with loops and an explicit
 * stack.
 */
export class WorkingTree {
  private root: JqValue;
  private rootDraft: Draft | null = null;

  constructor(root: JqValue) {
    this.root = root;
  }

  /** The value of the whole tree; the tree holds no draft afterwards. */
  freeze(): JqValue {
    return this.freezeAt(RootSlot);
  }

  /**
   * `getpath(keys)` on the current value. The value returned is frozen in the tree, so the
   * caller may keep it: the tree never changes it.
   */
  tryGet(keys: JqArray): JqValue | Fail {
    let slot = RootSlot;
    for (let i = 0; i < keys.count; i++) {
      const draft = this.draftAt(slot);
      const next = draft === null ? null : tryRead(draft, keys.get(i));
      if (next !== null) {
        if (!missing) {
          slot = next;
          continue;
        }

        // An array element out of range: null, which the rest of the path indexes.
        return tryIndex(null, keys, i + 1);
      }

      return tryIndex(this.freezeAt(slot), keys, i);
    }

    return this.freezeAt(slot);
  }

  /** `setpath(keys; value)` on the current value: null, or the error. */
  trySet(keys: JqArray, value: JqValue): Fail | null {
    let slot = RootSlot;
    for (let i = 0; i < keys.count; i++) {
      const key = keys.get(i);
      let draft = this.draftAt(slot);
      let next = draft === null ? null : tryWrite(draft, key);
      if (next !== null) {
        slot = next;
        continue;
      }

      const current = draft === null ? this.valueAt(slot) : this.freezeAt(slot);
      draft = this.tryOpen(current, key, slot);
      next = draft === null ? null : tryWrite(draft, key);
      if (next !== null) {
        slot = next;
        continue;
      }

      // A key the drafts do not handle: setpath with the rest of the path, on the value here.
      const result = PathNatives.trySetPath(current, keys.slice(i, keys.count), value);
      if (result instanceof Fail) return result;
      this.store(slot, result);
      return null;
    }

    this.store(slot, value);
    return null;
  }

  // Copies one level of the value at a slot into a draft there, when setpath would write into it
  // with this key (null becomes the container the key asks for); null otherwise.
  private tryOpen(value: JqValue, key: JqValue, slot: Slot): Draft | null {
    let draft: Draft;
    const kind = kindOf(value);
    if (typeof key === "string" && (kind === JqKind.Object || kind === JqKind.Null)) {
      draft = new Draft(null, JqObjectBuilder.from(kind === JqKind.Null ? JqObject.Empty : (value as JqObject)), slot.parent, slot.index, slot.key);
    } else if (typeof key === "number" && (kind === JqKind.Array || kind === JqKind.Null) && tryArrayIndex(key, kind === JqKind.Null ? 0 : (value as JqArray).count) >= 0) {
      draft = new Draft(new JqArrayBuilder(kind === JqKind.Null ? JqArray.Empty : (value as JqArray)), null, slot.parent, slot.index, slot.key);
    } else {
      return null;
    }

    this.attach(slot, draft);
    return draft;
  }

  private draftAt(slot: Slot): Draft | null {
    if (slot.parent === null) return this.rootDraft;
    if (slot.key !== null) return slot.parent.fields?.get(slot.key) ?? null;
    return slot.parent.items?.get(slot.index) ?? null;
  }

  // The value at a slot that holds no draft.
  private valueAt(slot: Slot): JqValue {
    if (slot.parent === null) return this.root;
    if (slot.key !== null) {
      const field = slot.parent.object!.get(slot.key);
      return field === undefined ? null : field;
    }

    return slot.index < slot.parent.array!.count ? slot.parent.array!.get(slot.index) : null;
  }

  // Puts a value at a slot, replacing whatever was there (a value or a draft).
  private store(slot: Slot, value: JqValue): void {
    if (slot.parent === null) {
      this.root = value;
      this.rootDraft = null;
    } else if (slot.key !== null) {
      slot.parent.object!.set(slot.key, value);
      slot.parent.fields?.delete(slot.key);
    } else {
      slot.parent.array!.set(slot.index, value);
      slot.parent.items?.delete(slot.index);
    }
  }

  // Hangs a draft at a slot. Its place in the parent is taken now (a new key goes last now,
  // missing elements become null now), as setpath would; the value is written when it freezes.
  private attach(slot: Slot, draft: Draft): void {
    this.store(slot, null);
    if (slot.parent === null) this.rootDraft = draft;
    else if (slot.key !== null) (slot.parent.fields ??= new Map()).set(slot.key, draft);
    else (slot.parent.items ??= new Map()).set(slot.index, draft);
  }

  // The value at a slot, freezing the drafts there first: children before parents, each written
  // into its parent's builder, the top one stored at the slot.
  private freezeAt(slot: Slot): JqValue {
    const top = this.draftAt(slot);
    if (top === null) return this.valueAt(slot);
    const order: Draft[] = [];
    const pending: Draft[] = [top];
    while (pending.length > 0) {
      const draft = pending.pop()!;
      order.push(draft);
      if (draft.items !== null) {
        for (const child of draft.items.values()) pending.push(child);
      }

      if (draft.fields !== null) {
        for (const child of draft.fields.values()) pending.push(child);
      }
    }

    for (let i = order.length - 1; i > 0; i--) {
      const draft = order[i]!;
      const value = draft.build();
      if (draft.key !== null) draft.parent!.object!.set(draft.key, value);
      else draft.parent!.array!.set(draft.index, value);
    }

    const frozen = top.build();
    this.store(slot, frozen);
    return frozen;
  }
}

// keys[from..] indexed from value, as getpath does.
function tryIndex(value: JqValue, keys: JqArray, from: number): JqValue | Fail {
  let result: JqValue | Fail = value;
  for (let i = from; i < keys.count; i++) {
    result = ValueOps.tryIndex(result, keys.get(i));
    if (result instanceof Fail) return result;
  }

  return result;
}

// The slot a read goes to next: a field (present or not) or an element in range; an element out
// of range is missing (the value there is null). Null for keys a draft does not read itself.
function tryRead(draft: Draft, key: JqValue): Slot | null {
  missing = false;
  if (draft.object !== null) return typeof key === "string" ? new Slot(draft, 0, key) : null;

  if (typeof key !== "number") return null;
  const count = draft.array!.count;
  let index = Number.isNaN(key) ? -1 : Math.trunc(key);
  if (index < 0) index += count;
  missing = Number.isNaN(key) || index < 0 || index >= count;
  return new Slot(draft, missing ? 0 : index, null);
}

// The slot a write goes to next, when setpath accepts the key for this container without error; null otherwise.
function tryWrite(draft: Draft, key: JqValue): Slot | null {
  if (draft.object !== null) return typeof key === "string" ? new Slot(draft, 0, key) : null;

  const index = tryArrayIndex(key, draft.array!.count);
  return index < 0 ? null : new Slot(draft, index, null);
}

// setpath's index of an array of the given length: not NaN, negative counted from the end, within the limit; else -1.
function tryArrayIndex(key: JqValue, length: number): number {
  if (typeof key !== "number" || Number.isNaN(key)) return -1;
  let i = Math.trunc(key);
  if (i < 0) i += length;
  if (i < 0 || i > MaxIndex) return -1;
  return i;
}
