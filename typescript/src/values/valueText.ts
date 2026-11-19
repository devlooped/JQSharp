// The one JSON writer, number formatter and JSON reader of jqlite.
//
// Canonical text is compact JSON. Strings escape only `"`, `\`, the control characters below
// U+0020 (`\b \f \n \r \t`, the others `\u00xx`) and U+007F, as jq does. Numbers are the
// shortest digits that read back to the same double, laid out as ECMAScript's
// Number::toString; NaN is `null` and ±Infinity are ±1.7976931348623157e+308.
//
// Host-specific (porting guide §3.1): the shortest digits and their layout are the host's
// `String(number)`, which is ECMAScript's Number::toString itself, and the decimal-to-double
// step of the reader is the host's `Number(text)`, after the grammar of §5.3 is checked here.
// Neither depends on a locale.

import * as CodePoints from "./codePoints.js";
import * as Errors from "./errors.js";
import { JqArray } from "./jqArray.js";
import type { JqObject } from "./jqObject.js";
import { JqObjectBuilder } from "./jqObjectBuilder.js";
import { Fail, JqKind, type JqValue, kindOf } from "./jqValue.js";

const MaxDouble = "1.7976931348623157e+308";

/** The deepest level the writer writes (§5.1, §11). */
export const MaxWriteDepth = 10001;

const TooDeep = "<skipped: too deep>";

/**
 * The canonical text of `value`. No recursion: the containers being written are on an explicit
 * stack, so nesting depth is not limited by the host stack. A value nested deeper than
 * `MaxWriteDepth` levels (the outermost value is level 1) is written as `<skipped: too deep>`,
 * as jq 1.8.2 does for scalars and containers alike.
 */
export function toJson(value: JqValue): string {
  // Scalars, the most common case, need no stack.
  if (value === null || typeof value !== "object") return scalarText(value);

  let text = "";
  const containers: (JqArray | JqObject)[] = [];
  const indexes: number[] = [];
  let current: JqValue = value;
  while (true) {
    // containers.length is the number of containers around current: its level minus one.
    if (containers.length >= MaxWriteDepth) {
      text += TooDeep;
    } else if (current === null || typeof current !== "object") {
      text += scalarText(current);
    } else if (current.count === 0) {
      text += current.kind === JqKind.Array ? "[]" : "{}";
    } else {
      // A container with elements: write its first one next.
      containers.push(current);
      indexes.push(0);
      if (current.kind === JqKind.Array) {
        text += "[";
        current = current.get(0);
      } else {
        text += "{" + writeString(current.keyAt(0)) + ":";
        current = current.valueAt(0);
      }

      continue;
    }

    // current is written: go on with the next element, closing the containers that end.
    while (true) {
      const top = containers.length - 1;
      if (top < 0) return text;

      const container: JqArray | JqObject = containers[top]!;
      const index = indexes[top]! + 1;
      if (index < container.count) {
        indexes[top] = index;
        if (container.kind === JqKind.Array) {
          text += ",";
          current = container.get(index);
        } else {
          text += "," + writeString(container.keyAt(index)) + ":";
          current = container.valueAt(index);
        }

        break;
      }

      text += container.kind === JqKind.Array ? "]" : "}";
      containers.pop();
      indexes.pop();
    }
  }
}

function scalarText(value: null | boolean | number | string): string {
  if (value === null) return "null";
  switch (typeof value) {
    case "boolean":
      return value ? "true" : "false";
    case "number":
      return formatNumber(value);
    default:
      return writeString(value);
  }
}

/** `text` as a JSON string literal. */
export function writeString(text: string): string {
  let result = '"';
  let run = 0;
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i);
    if (c >= 0x20 && c !== 0x22 && c !== 0x5c && c !== 0x7f) continue;

    result += text.substring(run, i);
    run = i + 1;
    switch (c) {
      case 0x22:
        result += '\\"';
        break;
      case 0x5c:
        result += "\\\\";
        break;
      case 0x08:
        result += "\\b";
        break;
      case 0x0c:
        result += "\\f";
        break;
      case 0x0a:
        result += "\\n";
        break;
      case 0x0d:
        result += "\\r";
        break;
      case 0x09:
        result += "\\t";
        break;
      default:
        result += "\\u" + c.toString(16).padStart(4, "0");
        break;
    }
  }

  return run === 0 ? result + text + '"' : result + text.substring(run) + '"';
}

/**
 * The JSON text of `value`; when longer than `maxBytes` UTF-8 bytes, its longest prefix of at
 * most `keepBytes` bytes that ends on a code point boundary, then "..." and the closing
 * delimiter of the value (`"`, `]` or `}`), as jq 1.8.2 writes them: `"aaa..."`, `[1,2,...]`,
 * `{"a":...}`.
 */
export function toJsonTruncated(value: JqValue, maxBytes: number, keepBytes: number): string {
  const text = toJson(value);
  if (text.length * 3 <= maxBytes || CodePoints.utf8ByteCount(text) <= maxBytes) return text;

  let bytes = 0;
  let end = 0;
  while (end < text.length) {
    const size = CodePoints.utf8Length(text, end);
    if (bytes + size > keepBytes) break;
    bytes += size;
    end += size === 4 ? 2 : 1;
  }

  const kind = kindOf(value);
  return text.substring(0, end) + (kind === JqKind.String ? '..."' : kind === JqKind.Array ? "...]" : "...}");
}

/** The canonical text of a number: shortest round-trip digits in ECMAScript Number::toString layout. */
export function formatNumber(number: number): string {
  if (Number.isNaN(number)) return "null";
  if (number === Infinity) return MaxDouble;
  if (number === -Infinity) return "-" + MaxDouble;
  if (number === 0) return Object.is(number, -0) ? "-0" : "0";
  return String(number);
}

/**
 * Reads the numeric text of §5.3 (`tonumber`, and the numbers of JSON text): an optional sign,
 * then digits with an optional fraction (`1.` and `.5` included) and exponent, or, in any ASCII
 * case, `nan` or `snan` (NaN), `inf` or `infinity`. No white space. Returns null when `text` is
 * not numeric text.
 */
export function tryParseNumber(text: string): number | null {
  let i = 0;
  let negative = false;
  if (i < text.length && (text[i] === "+" || text[i] === "-")) {
    negative = text[i] === "-";
    i++;
  }

  const first = text.charCodeAt(i);
  if (!isDigit(first) && first !== 0x2e) {
    const word = asciiLower(text.substring(i));
    if (word === "nan" || word === "snan") return NaN;
    if (word === "inf" || word === "infinity") return negative ? -Infinity : Infinity;
    return null;
  }

  let digits = 0;
  for (; i < text.length && isDigit(text.charCodeAt(i)); i++) digits++;
  if (i < text.length && text[i] === ".") {
    for (i++; i < text.length && isDigit(text.charCodeAt(i)); i++) digits++;
  }

  if (digits === 0) return null;

  if (i < text.length && (text[i] === "e" || text[i] === "E")) {
    i++;
    if (i < text.length && (text[i] === "+" || text[i] === "-")) i++;
    let exponentDigits = 0;
    for (; i < text.length && isDigit(text.charCodeAt(i)); i++) exponentDigits++;
    if (exponentDigits === 0) return null;
  }

  // The grammar is checked above; the host only converts (out of range gives ±Infinity or 0).
  return i === text.length ? Number(text) : null;
}

function isDigit(c: number): boolean {
  return c >= 0x30 && c <= 0x39;
}

function isAsciiLetter(c: number): boolean {
  return (c >= 0x41 && c <= 0x5a) || (c >= 0x61 && c <= 0x7a);
}

// The ASCII letters in lower case, every other code point unchanged (no locale).
function asciiLower(text: string): string {
  let result = "";
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i);
    result += c >= 0x41 && c <= 0x5a ? String.fromCharCode(c + 0x20) : text[i];
  }

  return result;
}

/**
 * Reads exactly one JSON value from `text` (§5.3; jq's `fromjson`). On failure the error is the
 * whole message, `<reason> (while parsing '<text>')`, the reason with the line and column where
 * reading stopped (except for `Expected JSON value` and `Unexpected extra JSON values`, as in jq).
 */
export function tryParse(text: string): JqValue | Fail {
  return new JsonReader(text, false).read();
}

/**
 * Reads a sequence of JSON values separated by white space (a data module, §9.11) as one array;
 * no value at all is the empty array. Errors as `tryParse`.
 */
export function tryParseSequence(text: string): JqValue | Fail {
  return new JsonReader(text, true).read();
}

const MaxDepth = 10000;

// A container being read.
class Frame {
  array: JqValue[] | null;
  object: JqObjectBuilder | null;
  key = "";

  constructor(readonly isObject: boolean) {
    this.array = isObject ? null : [];
    this.object = isObject ? new JqObjectBuilder() : null;
  }

  add(value: JqValue): void {
    if (this.object !== null) this.object.set(this.key, value);
    else this.array!.push(value);
  }

  toValue(): JqValue {
    return this.object !== null ? this.object.toObject() : JqArray.fromOwned(this.array!);
  }
}

const Space = 0x20;
const Tab = 0x09;
const LineFeed = 0x0a;
const CarriageReturn = 0x0d;
const Quote = 0x22;
const Comma = 0x2c;
const Colon = 0x3a;
const OpenBracket = 0x5b;
const Backslash = 0x5c;
const CloseBracket = 0x5d;
const OpenBrace = 0x7b;
const CloseBrace = 0x7d;

/**
 * A JSON reader without recursion (an explicit stack), so deep input cannot exhaust the host
 * stack. Like jq, a literal is any run of characters up to white space, a quote or one of
 * `[]{},:`; a raw control character inside a string is an error, and every lone surrogate
 * (escaped or raw) becomes U+FFFD.
 */
class JsonReader {
  private pos = 0;
  private failure: Fail | null = null;
  // The string readString read.
  private text_ = "";
  // The values read so far, when reading a sequence.
  private readonly values: JqValue[] | null;

  constructor(
    private readonly text: string,
    sequence: boolean,
  ) {
    this.values = sequence ? [] : null;
  }

  read(): JqValue | Fail {
    const result = this.readDocument();
    return result === undefined ? this.failure! : result;
  }

  // The value, or undefined after a failure.
  private readDocument(): JqValue | undefined {
    const text = this.text;
    if (text.length > 0 && text.charCodeAt(0) === 0xfeff) this.pos = 1; // a byte order mark
    this.skipSpace();
    if (this.pos === text.length && this.values !== null) return JqArray.Empty;

    if (this.pos === text.length) return this.failWithoutPosition(Errors.ExpectedValue);

    const stack: Frame[] = [];
    while (true) {
      // A value starts at pos (white space skipped).
      if (this.pos === text.length) return this.fail(Errors.UnfinishedAtEof);

      let current: JqValue;
      const c = text.charCodeAt(this.pos);
      if (c === OpenBracket || c === OpenBrace) {
        if (stack.length === MaxDepth) return this.failAt(Errors.DepthLimit, this.pos + 1);
        this.pos++;
        this.skipSpace();
        const frame = new Frame(c === OpenBrace);
        if (this.pos < text.length && text.charCodeAt(this.pos) === (c === OpenBrace ? CloseBrace : CloseBracket)) {
          this.pos++;
          current = frame.toValue();
        } else {
          stack.push(frame);
          if (frame.isObject && !this.readKey(frame)) return undefined;
          continue;
        }
      } else if (c === Quote) {
        this.pos++;
        if (!this.readString()) return undefined;
        current = CodePoints.sanitize(this.text_);
      } else if (c === CloseBracket || c === CloseBrace) {
        if (stack.length === 0) return this.failAt(Errors.unmatched(text[this.pos]!), this.pos + 1);
        return this.failAt(stack[stack.length - 1]!.isObject ? Errors.KeyValuePairs : Errors.ExpectedArrayElement, this.pos + 1);
      } else if (c === Comma || c === Colon) {
        return this.failAt(Errors.expectedValueBefore(text[this.pos]!), this.pos + 1);
      } else {
        const literal = this.readLiteral();
        if (literal === undefined) return undefined;
        current = literal;
      }

      // Attach the value to its container; close containers as they end.
      while (true) {
        this.skipSpace();
        if (stack.length === 0 && this.values !== null) {
          // A sequence goes on with the next value, if any.
          this.values.push(current);
          if (this.pos < text.length) break;
          return JqArray.fromOwned(this.values);
        }

        if (stack.length === 0) {
          if (this.pos !== text.length) return this.failWithoutPosition(Errors.ExtraValues);
          return current;
        }

        const top = stack[stack.length - 1]!;
        top.add(current);
        if (this.pos === text.length) return this.fail(Errors.UnfinishedAtEof);

        const d = text.charCodeAt(this.pos++);
        if (d === (top.isObject ? CloseBrace : CloseBracket)) {
          stack.pop();
          current = top.toValue();
          continue;
        }

        if (d !== Comma) {
          return this.failAt(d === CloseBracket || d === CloseBrace ? Errors.unmatched(String.fromCharCode(d)) : Errors.ExpectedSeparator, this.pos);
        }

        this.skipSpace();
        if (top.isObject && !this.readKey(top)) return undefined;
        break;
      }
    }
  }

  // Reads `"key" :` and the white space after it.
  private readKey(frame: Frame): boolean {
    const text = this.text;
    if (this.pos === text.length) return this.fail(Errors.UnfinishedAtEof) !== undefined;
    if (text.charCodeAt(this.pos) !== Quote) return this.failAt(Errors.KeysMustBeStrings, this.pos + 1) !== undefined;
    this.pos++;
    if (!this.readString()) return false;
    const key = this.text_;
    this.skipSpace();
    if (this.pos === text.length) return this.fail(Errors.UnfinishedAtEof) !== undefined;
    if (text.charCodeAt(this.pos) !== Colon) return this.failAt(Errors.KeyValuePairs, this.pos + 1) !== undefined;
    this.pos++;
    this.skipSpace();
    frame.key = CodePoints.sanitize(key);
    return true;
  }

  // Reads the rest of a string after its opening quote, into text_. Escapes decode to UTF-16
  // units as they are; the caller then turns lone surrogates into U+FFFD (sanitize).
  private readString(): boolean {
    const text = this.text;
    let value = "";
    let run = this.pos;
    while (true) {
      if (this.pos === text.length) return this.fail(Errors.UnfinishedAtEof) !== undefined;
      const c = text.charCodeAt(this.pos);
      if (c === Quote) {
        this.text_ = run === this.pos && value.length === 0 ? "" : value + text.substring(run, this.pos);
        this.pos++;
        return true;
      }

      if (c < 0x20) return this.failAt(Errors.ControlCharacter, this.pos + 1) !== undefined;

      if (c !== Backslash) {
        this.pos++;
        continue;
      }

      value += text.substring(run, this.pos);
      this.pos++;
      if (this.pos === text.length) return this.fail(Errors.UnfinishedAtEof) !== undefined;
      const e = text[this.pos++]!;
      switch (e) {
        case '"':
        case "\\":
        case "/":
          value += e;
          break;
        case "b":
          value += "\b";
          break;
        case "f":
          value += "\f";
          break;
        case "n":
          value += "\n";
          break;
        case "r":
          value += "\r";
          break;
        case "t":
          value += "\t";
          break;
        case "u": {
          let unit = 0;
          for (let i = 0; i < 4; i++) {
            if (this.pos === text.length) return this.fail(Errors.UnfinishedAtEof) !== undefined;
            const digit = hexValue(text.charCodeAt(this.pos++));
            if (digit < 0) return this.failAt(Errors.InvalidUnicodeEscape, this.pos) !== undefined;
            unit = unit * 16 + digit;
          }

          value += String.fromCharCode(unit);
          break;
        }
        default:
          return this.failAt(Errors.InvalidEscape, this.pos) !== undefined;
      }

      run = this.pos;
    }
  }

  // The value of the literal at pos, or undefined after a failure.
  private readLiteral(): JqValue | undefined {
    const text = this.text;
    const start = this.pos;
    while (this.pos < text.length && !isDelimiter(text.charCodeAt(this.pos))) this.pos++;
    const token = text.substring(start, this.pos);
    switch (token) {
      case "null":
        return null;
      case "true":
        return true;
      case "false":
        return false;
    }

    const number = tryParseNumber(token);
    if (number === null) return this.failAt(isAsciiLetter(token.charCodeAt(0)) ? Errors.InvalidLiteral : Errors.InvalidNumericLiteral, this.pos);
    return number;
  }

  private skipSpace(): void {
    const text = this.text;
    while (this.pos < text.length) {
      const c = text.charCodeAt(this.pos);
      if (c !== Space && c !== Tab && c !== LineFeed && c !== CarriageReturn) break;
      this.pos++;
    }
  }

  private fail(message: string): undefined {
    return this.failAt(message, this.text.length);
  }

  // consumed: how many characters were read when the error was found; the column counts them
  // on their line, as jq does ("{" fails at line 1, column 1).
  private failAt(message: string, consumed: number): undefined {
    const text = this.text;
    let line = 1;
    let lineStart = 0;
    for (let i = 0; i < consumed && i < text.length; i++) {
      if (text.charCodeAt(i) === LineFeed) {
        line++;
        lineStart = i + 1;
      }
    }

    return this.failWithoutPosition(Errors.atPosition(message, line, consumed - lineStart));
  }

  private failWithoutPosition(reason: string): undefined {
    this.failure = new Fail(Errors.whileParsing(reason, this.text));
    return undefined;
  }
}

function isDelimiter(c: number): boolean {
  return (
    c === Space || c === Tab || c === LineFeed || c === CarriageReturn || c === OpenBracket || c === CloseBracket || c === OpenBrace || c === CloseBrace || c === Comma || c === Colon || c === Quote
  );
}

function hexValue(c: number): number {
  if (c >= 0x30 && c <= 0x39) return c - 0x30;
  if (c >= 0x61 && c <= 0x66) return c - 0x61 + 10;
  if (c >= 0x41 && c <= 0x46) return c - 0x41 + 10;
  return -1;
}
