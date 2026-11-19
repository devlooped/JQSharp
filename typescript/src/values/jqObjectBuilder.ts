import { JqObject, LinearLimit } from "./jqObject.js";
import type { JqValue } from "./jqValue.js";

/**
 * A private, mutable object under construction (object literals, `*`, the JSON reader, merges
 * that replace keys). `toObject` hands its storage to the new `JqObject` without copying; the
 * builder must not be used afterwards.
 */
export class JqObjectBuilder {
  private readonly keys: string[] = [];
  private readonly values: JqValue[] = [];
  private index: Map<string, number> | null = null;
  private built = false;

  /** Starts from the entries of `source`. */
  static from(source: JqObject): JqObjectBuilder {
    const builder = new JqObjectBuilder();
    for (let i = 0; i < source.count; i++) {
      builder.keys.push(source.keyAt(i));
      builder.values.push(source.valueAt(i));
    }

    if (source.count > LinearLimit) builder.buildIndex();
    return builder;
  }

  get count(): number {
    return this.keys.length;
  }

  /** The value of `key`, or undefined when the builder lacks it. */
  get(key: string): JqValue | undefined {
    const i = this.indexOf(key);
    return i >= 0 ? this.values[i] : undefined;
  }

  /** Sets `key`: an existing key keeps its position, a new key goes last. */
  set(key: string, value: JqValue): void {
    this.ensureNotBuilt();
    const i = this.indexOf(key);
    if (i >= 0) {
      this.values[i] = value;
      return;
    }

    this.keys.push(key);
    this.values.push(value);
    if (this.index !== null) this.index.set(key, this.keys.length - 1);
    else if (this.keys.length > LinearLimit) this.buildIndex();
  }

  remove(key: string): boolean {
    this.ensureNotBuilt();
    const i = this.indexOf(key);
    if (i < 0) return false;

    this.keys.splice(i, 1);
    this.values.splice(i, 1);
    this.index = null;
    if (this.keys.length > LinearLimit) this.buildIndex();
    return true;
  }

  toObject(): JqObject {
    this.ensureNotBuilt();
    this.built = true;
    return JqObject.fromOwned(this.keys, this.values, this.index);
  }

  private indexOf(key: string): number {
    if (this.index !== null) return this.index.get(key) ?? -1;

    const keys = this.keys;
    for (let i = 0; i < keys.length; i++) {
      if (keys[i] === key) return i;
    }

    return -1;
  }

  private buildIndex(): void {
    const index = new Map<string, number>();
    for (let i = 0; i < this.keys.length; i++) index.set(this.keys[i]!, i);
    this.index = index;
  }

  private ensureNotBuilt(): void {
    if (this.built) throw new Error("The builder was already turned into an object.");
  }
}
