import type { JqResolver } from "./jqResolver.js";
import type { JsonValue } from "./json/jsonValueAdapter.js";

/** The source of `now` and of the local time zone (`localtime`, `strflocaltime`, `%z`, `%Z`). */
export interface JqClock {
  /** The current time, in seconds since the Unix epoch (a fraction is allowed). */
  now(): number;

  /** The offset of the local zone at the instant `seconds`, in seconds east of UTC. */
  offset(seconds: number): number;

  /** The abbreviation of the local zone at the instant `seconds`; without one, `%Z` prints the offset. */
  abbreviation?(seconds: number): string | null;
}

/**
 * What an expression takes from its host besides the input and the variables: modules, `$ENV`,
 * the clock, the depth limit and the handlers of `debug` and `stderr`. Options are read when the
 * expression is parsed (`Jq.parse`) and apply to every evaluation of it.
 */
export interface JqOptions {
  /** Resolves `import` and `include` directives and `modulemeta`; without one, no module exists. */
  readonly resolver?: JqResolver;

  /**
   * The variables of `$ENV` and `env`, in this order (a plain object, or a Map). Without it, the
   * process environment where there is one (Node's `process.env`, read when an evaluation first
   * uses `$ENV`), else no variable.
   */
  readonly environment?: Readonly<Record<string, string>> | ReadonlyMap<string, string>;

  /** The clock and the local zone; without one, the system's (`Date`), which has no zone abbreviations. */
  readonly clock?: JqClock;

  /**
   * The maximum number of calls of jq-defined functions active at once (spec §11), 1000 by
   * default; beyond it, the catchable error `Maximum call depth exceeded`. No recursion, however
   * deep, uses the JavaScript stack: the limit can be raised as far as memory allows.
   */
  readonly maxDepth?: number;

  /**
   * Receives the value of each `debug` (its input) and each message of `debug(msgs)`, as is: on
   * the input `1`, `debug, debug("m")` gives the handler `1`, then `"m"`. Without one, the values
   * are dropped.
   */
  readonly debug?: (value: JsonValue) => void;

  /** Receives the input of each `stderr`, as is. Without one, the values are dropped. */
  readonly stderr?: (value: JsonValue) => void;
}
