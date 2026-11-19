import type { JsonValue } from "./json/jsonValueAdapter.js";

/** An error of a jq expression: an uncaught runtime error, or (as JqParseError) a syntax or binding error. */
export class JqError extends Error {
  /**
   * The error value of an uncaught runtime error (what `error(v)` raised, or the message string
   * of a builtin's error), or undefined. The message is the value itself when it is a string,
   * else its JSON text followed by `(not a string)`.
   */
  readonly value: JsonValue | undefined;

  constructor(message: string, value?: JsonValue) {
    super(message);
    this.name = "JqError";
    this.value = value;
  }
}

/** A syntax or binding error, thrown by `Jq.parse` before any evaluation. */
export class JqParseError extends JqError {
  constructor(
    message: string,
    /** The 1-based line of the error, in the expression or in the module the message names. */
    readonly line: number,
    /** The 1-based column of the error, counting code points. */
    readonly column: number,
  ) {
    super(message);
    this.name = "JqParseError";
  }
}

/**
 * `halt_error` ended the evaluation: `value` is the value it was given and `exitCode` its code.
 * (`halt` ends an evaluation normally, without an error.)
 */
export class JqHaltError extends JqError {
  constructor(
    message: string,
    value: JsonValue,
    /** The exit code given to `halt_error`. */
    readonly exitCode: number,
  ) {
    super(message, value);
    this.name = "JqHaltError";
  }
}
