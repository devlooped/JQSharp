import type { Node } from "../binding/bound.js";
import type { JqValue } from "../values/jqValue.js";

/** A filter argument: the node and the environment of the caller (§7.7). */
export class Closure {
  constructor(
    readonly node: Node,
    readonly env: Env | null,
  ) {}
}

/**
 * One frame of the run-time environment, immutable once built: the values of a binding, the
 * closures of a call, or a label instance (see the top of bound.ts). The chain ends with null.
 */
export class Env {
  private constructor(
    readonly parent: Env | null,
    readonly values: JqValue[] | null,
    readonly closures: Closure[] | null,
    readonly label: number,
  ) {}

  static ofValues(parent: Env | null, values: JqValue[]): Env {
    return new Env(parent, values, null, 0);
  }

  static ofClosures(parent: Env | null, closures: Closure[]): Env {
    return new Env(parent, null, closures, 0);
  }

  static ofLabel(parent: Env | null, label: number): Env {
    return new Env(parent, null, null, label);
  }

  /** The frame `hops` frames out of `env`. */
  static up(env: Env | null, hops: number): Env | null {
    for (; hops > 0; hops--) env = env!.parent;
    return env;
  }
}
