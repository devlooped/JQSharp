import { Fail, type JqValue } from "../../src/values/jqValue.js";
import * as ValueText from "../../src/values/valueText.js";

/** The value of a JSON text of a test. */
export function V(json: string): JqValue {
  const value = ValueText.tryParse(json);
  if (value instanceof Fail) throw new Error(`Bad test JSON ${json}: ${String(value.error)}`);
  return value;
}

/** The canonical text of a value. */
export function J(value: JqValue): string {
  return ValueText.toJson(value);
}

/** The text of a fallible operation's result: its canonical text, or "error: " and the message. */
export function R(result: JqValue | Fail): string {
  return result instanceof Fail ? "error: " + (typeof result.error === "string" ? result.error : J(result.error)) : J(result);
}
