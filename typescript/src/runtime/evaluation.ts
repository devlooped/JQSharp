// The host interface of the core (spec §12): what an evaluation takes from its host and how it
// ends.

import { JqObject } from "../values/jqObject.js";
import type { JqValue } from "../values/jqValue.js";

/** The host's clock and local zone (§9.7, §9.12). */
export interface IClock {
  /** The current time, in seconds since the Unix epoch (fraction allowed). */
  now(): number;

  /** The offset of the local zone at the instant `seconds`, in seconds east of UTC. */
  offset(seconds: number): number;

  /** The abbreviation of the local zone at the instant `seconds`, or null when the host has none. */
  abbreviation(seconds: number): string | null;
}

/** The options of one evaluation. */
export class EvaluationOptions {
  /** The host variables, by name without `$`; null when there are none. */
  variables: ReadonlyMap<string, JqValue> | null = null;

  /** Gives the object of `$ENV`; called at most once per evaluation, when `$ENV` is first used. */
  environment: () => JqValue = () => JqObject.Empty;

  /** The call depth limit of §11. */
  maxDepth = 1000;

  /** The clock; null: `now` is an error and the local zone is UTC. */
  clock: IClock | null = null;

  /** Receives the values of `debug`; null drops them. */
  debug: ((value: JqValue) => void) | null = null;

  /** Receives the values of `stderr`; null drops them. */
  stderr: ((value: JqValue) => void) | null = null;
}

export const enum OutcomeKind {
  /** The program ended normally. */
  Completed,

  /** The host stopped the stream. */
  Stopped,

  /** An uncaught error: `value` is the error value. */
  Error,

  /** `halt` (no value, code 0) or `halt_error` (`value`, `code`). */
  Halt,
}

/** How an evaluation ended. */
export class Outcome {
  constructor(
    readonly kind: OutcomeKind,
    readonly value: JqValue = null,
    readonly hasValue: boolean = false,
    readonly code: number = 0,
  ) {}
}
