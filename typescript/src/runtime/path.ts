import { JqArray } from "../values/jqArray.js";
import type { JqValue } from "../values/jqValue.js";

/**
 * The path of an output in path mode (§8.1): an immutable list of keys linked from the last one,
 * so appending is O(1) and shares the prefix. Three states: null is value mode (no path is
 * built), `Root` and what is appended to it are tracked paths, `Untracked` is an output without
 * a path in path mode.
 */
export class Path {
  /** The path of the input of `path(f)`: `[]`. */
  static readonly Root = new Path(null, null, 0);

  /** An output that has no path, in path mode. */
  static readonly Untracked = new Path(null, null, -1);

  private constructor(
    private readonly parent: Path | null,
    private readonly key: JqValue,
    private readonly length: number,
  ) {}

  get isTracked(): boolean {
    return this.length >= 0;
  }

  /** The path of an output that carries none: null in value mode, `Untracked` in path mode. */
  static of(mode: Path | null): Path | null {
    return mode === null ? null : Path.Untracked;
  }

  append(next: JqValue): Path {
    return new Path(this, next, this.length + 1);
  }

  appendAll(keys: JqArray): Path {
    let path: Path = this;
    for (let i = 0; i < keys.count; i++) path = path.append(keys.get(i));
    return path;
  }

  /** The path as an array of keys. */
  toValue(): JqValue {
    const keys: JqValue[] = new Array(this.length);
    let path: Path = this;
    for (let i = this.length - 1; i >= 0; i--) {
      keys[i] = path.key;
      path = path.parent!;
    }

    return JqArray.fromOwned(keys);
  }
}
