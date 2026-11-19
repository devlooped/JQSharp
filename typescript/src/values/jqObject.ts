import * as CodePoints from "./codePoints.js";
import { JqObjectBuilder } from "./jqObjectBuilder.js";
import { JqKind, type JqValue } from "./jqValue.js";

/** Up to this many entries, lookup scans the keys; above, it uses the index. */
export const LinearLimit = 8;

/** The storage of the versions of an object: entries, and the index (key to slot) of the first `indexed` of them. */
class EntryBuffer {
  index: Map<string, number> | null;
  indexed: number;

  constructor(
    readonly keys: string[],
    readonly values: JqValue[],
    index: Map<string, number> | null,
  ) {
    this.index = index;
    this.indexed = index === null ? 0 : index.size;
  }
}

/**
 * An immutable map from string keys to values that keeps the insertion order of its keys.
 *
 * Storage is a shared buffer of entries plus a count, as for `JqArray`. Adding a new key to the
 * latest version of a buffer writes the next slot: amortized O(1), so
 * `reduce ... (.; . + {($k): $v})` and `setpath` on new keys do not copy the object. The keys of
 * a buffer are all distinct (a slot is only taken for a key the version lacks), so one index
 * (key to slot) serves every version: a version with `count` entries owns the key when its slot
 * is below `count`.
 *
 * Replacing or removing a key copies one level. Up to `LinearLimit` entries, lookup scans the
 * keys; above, it uses the index, built lazily. (The reference claims slots with a
 * compare-and-swap and extends the index under a lock; one thread runs at a time here, and the
 * buffer's arrays grow by themselves, so their length is the used count.)
 */
export class JqObject {
  static readonly Empty = new JqObject(new EntryBuffer([], [], null), 0);

  readonly kind = JqKind.Object;

  // The keys in code point order, computed on first use.
  private sorted: string[] | null = null;

  private constructor(
    private readonly buffer: EntryBuffer,
    readonly count: number,
  ) {}

  /** Creates an object over arrays the caller gives up; keys must be distinct; `index`, if any, maps exactly the keys. */
  static fromOwned(keys: string[], values: JqValue[], index: Map<string, number> | null): JqObject {
    return keys.length === 0 ? JqObject.Empty : new JqObject(new EntryBuffer(keys, values, index), keys.length);
  }

  /** The key at `index` in insertion order. */
  keyAt(index: number): string {
    if (index < 0 || index >= this.count) throw new RangeError("index");
    return this.buffer.keys[index]!;
  }

  /** The value at `index` in insertion order. */
  valueAt(index: number): JqValue {
    if (index < 0 || index >= this.count) throw new RangeError("index");
    return this.buffer.values[index]!;
  }

  containsKey(key: string): boolean {
    return this.indexOf(key) >= 0;
  }

  /** The value of `key`, or undefined when the object lacks it (the reference's `TryGetValue`). */
  get(key: string): JqValue | undefined {
    const index = this.indexOf(key);
    return index >= 0 ? this.buffer.values[index] : undefined;
  }

  /** The position of `key` in insertion order, or -1. */
  indexOf(key: string): number {
    const buffer = this.buffer;
    const count = this.count;
    if (count <= LinearLimit) {
      const keys = buffer.keys;
      for (let i = 0; i < count; i++) {
        if (keys[i] === key) return i;
      }

      return -1;
    }

    const index = (buffer.index ??= new Map());
    // Slots below this version's count are written (it was created after them).
    for (; buffer.indexed < count; buffer.indexed++) index.set(buffer.keys[buffer.indexed]!, buffer.indexed);
    const slot = index.get(key);
    return slot !== undefined && slot < count ? slot : -1;
  }

  /** A copy with `key` set: an existing key keeps its position, a new key goes last. */
  set(key: string, value: JqValue): JqObject {
    const index = this.indexOf(key);
    if (index < 0) return this.add(key, value);

    const keys = this.buffer.keys.slice(0, this.count);
    const values = this.buffer.values.slice(0, this.count);
    values[index] = value;
    return JqObject.fromOwned(keys, values, null);
  }

  // Appends a key this version does not have.
  private add(key: string, value: JqValue): JqObject {
    const buffer = this.buffer;
    // The buffer of the empty object is shared by every empty object: never extended in place.
    if (this.count > 0 && this.count === buffer.keys.length) {
      buffer.keys.push(key);
      buffer.values.push(value);
      return new JqObject(buffer, this.count + 1);
    }

    const keys = buffer.keys.slice(0, this.count);
    const values = buffer.values.slice(0, this.count);
    keys.push(key);
    values.push(value);
    return JqObject.fromOwned(keys, values, null);
  }

  /** A copy without `key`; the other keys keep their order. */
  remove(key: string): JqObject {
    const index = this.indexOf(key);
    if (index < 0) return this;

    const keys = this.buffer.keys.slice(0, this.count);
    const values = this.buffer.values.slice(0, this.count);
    keys.splice(index, 1);
    values.splice(index, 1);
    return JqObject.fromOwned(keys, values, null);
  }

  /** jq's `+` on objects: the keys of `other` win; existing keys keep their position, new ones follow in `other`'s order. */
  merge(other: JqObject): JqObject {
    if (other.count === 0) return this;
    if (this.count === 0) return other;

    let overlaps = false;
    for (let i = 0; i < other.count && !overlaps; i++) overlaps = this.indexOf(other.buffer.keys[i]!) >= 0;

    if (!overlaps) {
      // Only new keys: append them, in place when this is the latest version.
      let result: JqObject = this;
      for (let i = 0; i < other.count; i++) result = result.add(other.buffer.keys[i]!, other.buffer.values[i]!);
      return result;
    }

    const builder = JqObjectBuilder.from(this);
    for (let i = 0; i < other.count; i++) builder.set(other.buffer.keys[i]!, other.buffer.values[i]!);
    return builder.toObject();
  }

  /** The keys in code point order (jq's `keys`); computed once per object. */
  sortedKeys(): readonly string[] {
    let keys = this.sorted;
    if (keys === null) {
      keys = this.buffer.keys.slice(0, this.count);
      keys.sort(CodePoints.compare);
      this.sorted = keys;
    }

    return keys;
  }
}
