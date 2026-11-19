import type { JqValue } from "../values/jqValue.js";

export const enum SignalKind {
  /** An error: `value` is the error value (§6.2). */
  Error,

  /** `break $name`: `label` is the label instance. */
  Break,

  /** `halt` (no value) or `halt_error` (`value` and `code`). */
  Halt,

  /** The host stopped the stream. */
  Stop,
}

/**
 * Why an evaluation stopped (§6.2). Every construct passes a signal up unchanged, except the one
 * that handles it; signals are compared by identity (Interpreter's Consumer).
 */
export class Signal {
  static readonly Stop = new Signal(SignalKind.Stop, null, false, 0, 0);

  private constructor(
    readonly kind: SignalKind,
    readonly value: JqValue,
    readonly hasValue: boolean,
    readonly label: number,
    readonly code: number,
  ) {}

  static error(value: JqValue): Signal {
    return new Signal(SignalKind.Error, value, true, 0, 0);
  }

  static break(label: number): Signal {
    return new Signal(SignalKind.Break, null, false, label, 0);
  }

  /** `value` undefined: `halt`, which has none. */
  static halt(value: JqValue | undefined, code: number): Signal {
    return new Signal(SignalKind.Halt, value === undefined ? null : value, value !== undefined, 0, code);
  }
}
