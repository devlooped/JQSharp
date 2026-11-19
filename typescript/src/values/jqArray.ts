import { JqKind, type JqValue } from "./jqValue.js";

/**
 * An immutable sequence of values.
 *
 * Storage is a shared buffer plus a window (`start`, `count`) on it, as a Go slice. A slot of a
 * buffer is written once, before any array that covers it exists, and never again, so every
 * `JqArray` is immutable however many arrays share its buffer.
 *
 * Appending (`append`, `concat`) to the array whose window ends where the buffer ends (its
 * latest version) writes the next slots: amortized O(1), so
 * `reduce range(n) as $i ([]; . + [$i])` is O(n). When the slots are already taken (an older
 * version), the window is copied to a new buffer. Replacing one element copies one level. A
 * slice shares the buffer and keeps all of it alive, as in Go.
 *
 * The reference keeps a `Used` count beside a fixed-size buffer and claims slots with a
 * compare-and-swap. Here the buffer is a JavaScript array, which grows by itself, so its length
 * is the used count; and one thread runs at a time, so claiming a slot is a comparison.
 */
export class JqArray {
  static readonly Empty = new JqArray([], 0, 0);

  readonly kind = JqKind.Array;

  private constructor(
    private readonly items: JqValue[],
    private readonly start: number,
    readonly count: number,
  ) {}

  /** Creates an array with a copy of `items`. */
  static of(...items: JqValue[]): JqArray {
    return items.length === 0 ? JqArray.Empty : new JqArray(items.slice(), 0, items.length);
  }

  /** Creates an array over `items` without copying: the caller gives up the array and never writes it again. */
  static fromOwned(items: JqValue[]): JqArray {
    return items.length === 0 ? JqArray.Empty : new JqArray(items, 0, items.length);
  }

  /** The element at `index`, 0 <= index < count. */
  get(index: number): JqValue {
    if (index < 0 || index >= this.count) throw new RangeError("index");
    return this.items[this.start + index]!;
  }

  append(value: JqValue): JqArray {
    const end = this.start + this.count;
    // The buffer of the empty array is shared by every empty array: never appended in place.
    if (this.count > 0 && end === this.items.length) {
      this.items.push(value);
      return new JqArray(this.items, this.start, this.count + 1);
    }

    const items = this.items.slice(this.start, end);
    items.push(value);
    return new JqArray(items, 0, items.length);
  }

  /** This array followed by `other` (jq's `+` on arrays). */
  concat(other: JqArray): JqArray {
    if (other.count === 0) return this;
    if (this.count === 0) return other;

    const end = this.start + this.count;
    const inPlace = end === this.items.length;
    const items = inPlace ? this.items : this.items.slice(this.start, end);
    // other may share this buffer: its window is fixed before the buffer grows.
    const source = other.items;
    for (let i = other.start, last = other.start + other.count; i < last; i++) items.push(source[i]!);
    return new JqArray(items, inPlace ? this.start : 0, this.count + other.count);
  }

  /** The elements `from` (included) to `to` (excluded), sharing this array's buffer; 0 <= from <= to <= count. */
  slice(from: number, to: number): JqArray {
    if (from < 0 || from > this.count || to < from || to > this.count) throw new RangeError("from");
    if (from === 0 && to === this.count) return this;
    return to === from ? JqArray.Empty : new JqArray(this.items, this.start + from, to - from);
  }

  /** A copy with `value` at `index` (index >= 0); an index past the end pads with nulls, as jq does. */
  setItem(index: number, value: JqValue): JqArray {
    if (index < 0) throw new RangeError("index");
    if (index === this.count) return this.append(value);

    const items = this.items.slice(this.start, this.start + this.count);
    while (items.length < index) items.push(null);
    items[index] = value;
    return JqArray.fromOwned(items);
  }

  /** A copy of the elements as a host array. */
  toArray(): JqValue[] {
    return this.items.slice(this.start, this.start + this.count);
  }
}
