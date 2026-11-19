// UTF-8 encoding and decoding as plain loops over code points and bytes, for the formats that
// work on bytes (§9.9: @uri, @urid, @base64, @base64d). Bytes are numbers in a plain array.

import * as CodePoints from "./codePoints.js";

/** Appends the UTF-8 bytes of a well-formed string. */
export function encode(text: string, bytes: number[]): void {
  for (let i = 0; i < text.length; i++) {
    const c = text.codePointAt(i)!;
    if (c > 0xffff) i++;
    encodeCodePoint(c, bytes);
  }
}

/** Appends the UTF-8 bytes of a Unicode scalar value. */
export function encodeCodePoint(codePoint: number, bytes: number[]): void {
  if (codePoint < 0x80) {
    bytes.push(codePoint);
  } else if (codePoint < 0x800) {
    bytes.push(0xc0 | (codePoint >> 6));
    bytes.push(0x80 | (codePoint & 0x3f));
  } else if (codePoint < 0x10000) {
    bytes.push(0xe0 | (codePoint >> 12));
    bytes.push(0x80 | ((codePoint >> 6) & 0x3f));
    bytes.push(0x80 | (codePoint & 0x3f));
  } else {
    bytes.push(0xf0 | (codePoint >> 18));
    bytes.push(0x80 | ((codePoint >> 12) & 0x3f));
    bytes.push(0x80 | ((codePoint >> 6) & 0x3f));
    bytes.push(0x80 | (codePoint & 0x3f));
  }
}

/** The text of well-formed UTF-8 bytes; null when any sequence is ill-formed. */
export function tryDecode(bytes: readonly number[]): string | null {
  let text = "";
  for (let i = 0; i < bytes.length; ) {
    i = next(bytes, i);
    if (decoded < 0) return null;
    text += CodePoints.toText(decoded);
  }

  return text;
}

/** The text of UTF-8 bytes, each ill-formed sequence (as `next` delimits it) replaced by U+FFFD. */
export function decodeReplacing(bytes: readonly number[]): string {
  let text = "";
  for (let i = 0; i < bytes.length; ) {
    i = next(bytes, i);
    text += CodePoints.toText(decoded < 0 ? CodePoints.Replacement : decoded);
  }

  return text;
}

// The code point next() decoded (its second result), -1 for an ill-formed sequence.
let decoded = -1;

/**
 * Decodes the sequence at `index` and returns the index after it; `decoded` is -1 when the
 * sequence is ill-formed, and the index is then the end of its maximal ill-formed subpart
 * (Unicode §3.9, §4.1 of the spec): the bytes up to the first one that cannot continue a
 * well-formed sequence, at least one byte. The second byte's range depends on the first (E0:
 * A0-BF, ED: 80-9F, F0: 90-BF, F4: 80-8F), which rules out overlong forms, surrogates and code
 * points above U+10FFFF.
 */
function next(bytes: readonly number[], index: number): number {
  const first = bytes[index]!;
  decoded = -1;
  if (first < 0x80) {
    decoded = first;
    return index + 1;
  }

  const length = first >= 0xc2 && first <= 0xdf ? 2 : first >= 0xe0 && first <= 0xef ? 3 : first >= 0xf0 && first <= 0xf4 ? 4 : 0;
  if (length === 0) return index + 1;

  let low = first === 0xe0 ? 0xa0 : first === 0xf0 ? 0x90 : 0x80;
  let high = first === 0xed ? 0x9f : first === 0xf4 ? 0x8f : 0xbf;
  let value = first & (0x7f >> length);
  for (let i = 1; i < length; i++) {
    if (index + i === bytes.length) return index + i;
    const byte = bytes[index + i]!;
    if (byte < low || byte > high) return index + i;
    value = (value << 6) | (byte & 0x3f);
    low = 0x80;
    high = 0xbf;
  }

  decoded = value;
  return index + length;
}
