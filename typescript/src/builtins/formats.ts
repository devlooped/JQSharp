// The @name formats of §9.9.

import * as Errors from "../values/errors.js";
import { JqArray } from "../values/jqArray.js";
import { Fail, JqKind, type JqValue, kindOf } from "../values/jqValue.js";
import * as Utf8 from "../values/utf8.js";
import * as ValueText from "../values/valueText.js";
import { toText } from "./stringNatives.js";

const Hex = "0123456789ABCDEF";
const Base64Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

/** Applies the format `name` (without '@') to `value`, giving a string. */
export function tryApply(name: string, value: JqValue): JqValue | Fail {
  switch (name) {
    case "text":
      return toText(value);
    case "json":
      return ValueText.toJson(value);
    case "html":
      return html(toText(value));
    case "uri":
      return uri(toText(value));
    case "urid":
      return uriDecode(toText(value));
    case "csv":
      return kindOf(value) === JqKind.Array ? row(value as JqArray, false) : new Fail(Errors.cannotBeCsvFormatted(value));
    case "tsv":
      return kindOf(value) === JqKind.Array ? row(value as JqArray, true) : new Fail(Errors.cannotBeTsvFormatted(value));
    case "sh":
      return shell(kindOf(value) === JqKind.Array ? (value as JqArray) : JqArray.of(value));
    case "base64":
      return base64(toText(value));
    case "base64d":
      return base64Decode(toText(value));
    default:
      return new Fail(Errors.invalidFormat(name));
  }
}

// @html: & < > ' " as entities.
function html(text: string): string {
  let result = "";
  for (let i = 0; i < text.length; i++) {
    const c = text[i]!;
    switch (c) {
      case "&":
        result += "&amp;";
        break;
      case "<":
        result += "&lt;";
        break;
      case ">":
        result += "&gt;";
        break;
      case "'":
        result += "&apos;";
        break;
      case '"':
        result += "&quot;";
        break;
      default:
        result += c;
        break;
    }
  }

  return result;
}

// @uri: the UTF-8 bytes, unreserved ones (A-Z a-z 0-9 - _ . ~) kept, the others as %XX.
function uri(text: string): string {
  const bytes: number[] = [];
  Utf8.encode(text, bytes);
  let result = "";
  for (const b of bytes) {
    if ((b >= 0x41 && b <= 0x5a) || (b >= 0x61 && b <= 0x7a) || (b >= 0x30 && b <= 0x39) || b === 0x2d || b === 0x5f || b === 0x2e || b === 0x7e) result += String.fromCharCode(b);
    else result += "%" + Hex[b >> 4]! + Hex[b & 0xf]!;
  }

  return result;
}

// @urid: %XX (either case) is that byte, any other code point its UTF-8 bytes; the bytes must be well-formed UTF-8.
function uriDecode(text: string): JqValue | Fail {
  const bytes: number[] = [];
  for (let i = 0; i < text.length; i++) {
    const c = text.codePointAt(i)!;
    if (c === 0x25 /* % */) {
      const high = i + 1 < text.length ? hexValue(text.charCodeAt(i + 1)) : -1;
      const low = i + 2 < text.length ? hexValue(text.charCodeAt(i + 2)) : -1;
      if (high < 0 || low < 0) return new Fail(Errors.notValidUriEncoding(text));
      bytes.push((high << 4) | low);
      i += 2;
      continue;
    }

    if (c > 0xffff) i++;
    Utf8.encodeCodePoint(c, bytes);
  }

  const decoded = Utf8.tryDecode(bytes);
  return decoded === null ? new Fail(Errors.notValidUriEncoding(text)) : decoded;
}

function hexValue(c: number): number {
  return c >= 0x30 && c <= 0x39 ? c - 0x30 : c >= 0x61 && c <= 0x66 ? c - 0x61 + 10 : c >= 0x41 && c <= 0x46 ? c - 0x41 + 10 : -1;
}

// @csv and @tsv: one row; strings quoted (csv) or escaped (tsv), numbers as canonical text (NaN
// empty), booleans as such, null empty.
function row(items: JqArray, tsv: boolean): JqValue | Fail {
  let result = "";
  for (let i = 0; i < items.count; i++) {
    if (i > 0) result += tsv ? "\t" : ",";
    const item = items.get(i);
    switch (kindOf(item)) {
      case JqKind.Null:
        break;
      case JqKind.False:
      case JqKind.True:
        result += item === true ? "true" : "false";
        break;
      case JqKind.Number:
        if (!Number.isNaN(item as number)) result += ValueText.formatNumber(item as number);
        break;
      case JqKind.String:
        if (tsv) result += tsvEscape(item as string);
        else result += '"' + (item as string).replaceAll('"', '""') + '"';
        break;
      default:
        return new Fail(Errors.notValidInCsvRow(item));
    }
  }

  return result;
}

function tsvEscape(text: string): string {
  let result = "";
  for (let i = 0; i < text.length; i++) {
    const c = text[i]!;
    switch (c) {
      case "\\":
        result += "\\\\";
        break;
      case "\t":
        result += "\\t";
        break;
      case "\n":
        result += "\\n";
        break;
      case "\r":
        result += "\\r";
        break;
      default:
        result += c;
        break;
    }
  }

  return result;
}

// @sh: space-separated words; strings in single quotes (' as '\''), other scalars as canonical text.
function shell(items: JqArray): JqValue | Fail {
  let result = "";
  for (let i = 0; i < items.count; i++) {
    if (i > 0) result += " ";
    const item = items.get(i);
    switch (kindOf(item)) {
      case JqKind.Array:
      case JqKind.Object:
        return new Fail(Errors.cannotBeEscapedForShell(item));
      case JqKind.String:
        result += "'" + (item as string).replaceAll("'", "'\\''") + "'";
        break;
      default:
        result += ValueText.toJson(item);
        break;
    }
  }

  return result;
}

// @base64: RFC 4648 of the UTF-8 bytes, with '=' padding.
function base64(text: string): string {
  const bytes: number[] = [];
  Utf8.encode(text, bytes);
  let result = "";
  for (let i = 0; i < bytes.length; i += 3) {
    const left = bytes.length - i;
    const group = (bytes[i]! << 16) | (left > 1 ? bytes[i + 1]! << 8 : 0) | (left > 2 ? bytes[i + 2]! : 0);
    result += Base64Alphabet[group >> 18]!;
    result += Base64Alphabet[(group >> 12) & 0x3f]!;
    result += left > 1 ? Base64Alphabet[(group >> 6) & 0x3f]! : "=";
    result += left > 2 ? Base64Alphabet[group & 0x3f]! : "=";
  }

  return result;
}

// @base64d: the characters before the first '=' must be in the alphabet; a final group of 2 or
// 3 characters gives 1 or 2 bytes, of 1 is an error; the bytes are decoded as UTF-8 with
// replacement.
function base64Decode(text: string): JqValue | Fail {
  const bytes: number[] = [];
  let group = 0;
  let count = 0;
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i);
    if (c === 0x3d /* = */) break;
    const digit = base64Digit(c);
    if (digit < 0) return new Fail(Errors.notValidBase64(text));
    group = (group << 6) | digit;
    if (++count === 4) {
      bytes.push((group >> 16) & 0xff);
      bytes.push((group >> 8) & 0xff);
      bytes.push(group & 0xff);
      group = count = 0;
    }
  }

  if (count === 1) return new Fail(Errors.trailingBase64Byte(text));
  if (count === 2) {
    bytes.push((group >> 4) & 0xff);
  } else if (count === 3) {
    bytes.push((group >> 10) & 0xff);
    bytes.push((group >> 2) & 0xff);
  }

  return Utf8.decodeReplacing(bytes);
}

function base64Digit(c: number): number {
  return c >= 0x41 && c <= 0x5a ? c - 0x41 : c >= 0x61 && c <= 0x7a ? c - 0x61 + 26 : c >= 0x30 && c <= 0x39 ? c - 0x30 + 52 : c === 0x2b ? 62 : c === 0x2f ? 63 : -1;
}
