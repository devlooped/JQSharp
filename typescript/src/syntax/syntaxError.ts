import * as Errors from "../values/errors.js";
import type { JqValue } from "../values/jqValue.js";
import type { Token } from "./token.js";

/**
 * The one error a lexing, parsing or binding run returns: a message and the 1-based position of
 * the token where it was detected, in the main program or, when `module` is set, in the module
 * of that identity (§9.11).
 */
export class SyntaxError {
  constructor(
    readonly message: string,
    readonly line: number,
    readonly column: number,
    readonly module: string | null = null,
  ) {}
}

// Every message text of the syntax layer, in one place (the reference's SyntaxMessages). The
// texts of spec §10.2 are fixed; the others are this implementation's.

// Fixed by spec §10.2.
export const InvalidEscape = "Invalid escape";
export const MayNeedParentheses = "May need parentheses around object key expression";
export const MetadataNotConstant = "Module metadata must be constant";
export const MetadataNotObject = "Module metadata must be an object";
export const ImportPathNotConstant = "Import path must be constant";

/** `Cannot use number (1) as object key`. */
export function invalidObjectKey(key: JqValue): string {
  return Errors.cannotUseAsKey(key) as string;
}

// Fixed by spec §11.
export const TooDeeplyNested = "Program too deeply nested";

// This implementation's.
export const UnterminatedString = "Unterminated string";
export const VariableNameExpected = "Expected a variable name after '$'";
export const FormatNameExpected = "Expected a format name after '@'";
export const ModuleHasBody = "A module contains only definitions";

export function invalidCharacter(codePoint: number): string {
  return codePoint < 0x20 || codePoint === 0x7f || (codePoint >= 0xd800 && codePoint <= 0xdfff)
    ? `Invalid character U+${codePoint.toString(16).toUpperCase().padStart(4, "0")}`
    : `Invalid character '${String.fromCodePoint(codePoint)}'`;
}

export function unexpected(token: Token): string {
  return `Unexpected ${token.describe()}`;
}

export function expected(what: string, token: Token): string {
  return `Expected ${what} but found ${token.describe()}`;
}

export function notAssociative(token: Token): string {
  return `Operator ${token.describe()} cannot be chained: add parentheses`;
}

export function questionAlt(token: Token): string {
  return `Unexpected ${token.describe()}: write '? //' for an optional followed by an alternative`;
}
