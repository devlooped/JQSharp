import { JqArray } from "./jqArray.js";
import type { JqValue } from "./jqValue.js";

/**
 * A private, mutable array under construction, the array counterpart of `JqObjectBuilder`.
 * `toArray` hands its storage to the new `JqArray` without copying; the builder must not be
 * used afterwards.
 */
export class JqArrayBuilder {
  private readonly items: JqValue[];
  private built = false;

  /** Starts from the elements of `source` (one level copied). */
  constructor(source: JqArray) {
    this.items = source.toArray();
  }

  get count(): number {
    return this.items.length;
  }

  /** The element at `index`, 0 <= index < count. */
  get(index: number): JqValue {
    if (index < 0 || index >= this.items.length) throw new RangeError("index");
    return this.items[index]!;
  }

  /** Sets the element at `index` (>= 0); an index past the end pads with nulls, as jq does. */
  set(index: number, value: JqValue): void {
    if (this.built) throw new Error("The builder was already turned into an array.");
    while (this.items.length < index) this.items.push(null);
    this.items[index] = value;
  }

  toArray(): JqArray {
    if (this.built) throw new Error("The builder was already turned into an array.");
    this.built = true;
    return JqArray.fromOwned(this.items);
  }
}
