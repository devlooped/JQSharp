// Strings by Unicode code point. jqlite strings are sequences of code points: length, slices,
// ordering, explode and implode count code points, never UTF-16 units. A JavaScript string
// holds them as UTF-16, so a code point above U+FFFF is a surrogate pair here; every string of
// a JqValue is well-formed (no lone surrogate, see sanitize).

import { JqArray } from "./jqArray.js";
import type { JqValue } from "./jqValue.js";

export const Max = 0x10ffff;
export const Replacement = 0xfffd;

export function isHighSurrogate(unit: number): boolean {
  return unit >= 0xd800 && unit <= 0xdbff;
}

export function isLowSurrogate(unit: number): boolean {
  return unit >= 0xdc00 && unit <= 0xdfff;
}

/** Replaces each lone surrogate with U+FFFD; returns `text` itself when it has none. */
export function sanitize(text: string): string {
  return text.isWellFormed() ? text : text.toWellFormed();
}

/** The number of code points of a well-formed string. */
export function length(text: string): number {
  let length = text.length;
  for (let i = 0; i < text.length; i++) {
    if (isHighSurrogate(text.charCodeAt(i))) length--;
  }

  return length;
}

/** The code points `start` (included) to `end` (excluded); 0 <= start <= end <= length. */
export function substring(text: string, start: number, end: number): string {
  const from = advance(text, 0, start);
  const to = advance(text, from, end - start);
  return text.substring(from, to);
}

function advance(text: string, offset: number, codePoints: number): number {
  for (let i = 0; i < codePoints; i++) offset += isHighSurrogate(text.charCodeAt(offset)) ? 2 : 1;
  return offset;
}

/** Orders two well-formed strings by code point (the order of their UTF-8 bytes); returns -1, 0 or 1. */
export function compare(a: string, b: string): number {
  const length = Math.min(a.length, b.length);
  for (let i = 0; i < length; i++) {
    const x = a.charCodeAt(i);
    const y = b.charCodeAt(i);
    if (x !== y) return rank(x) < rank(y) ? -1 : 1;
  }

  return a.length === b.length ? 0 : a.length < b.length ? -1 : 1;
}

// UTF-16 unit order differs from code point order in one place: a surrogate (D800-DFFF, part of
// a code point above U+FFFF) must sort after E000-FFFF. Moving surrogates to the top fixes it
// without decoding: at the first unit that differs, both strings are at a code point boundary.
function rank(unit: number): number {
  return unit >= 0xe000 ? unit - 0x800 : unit >= 0xd800 ? unit + 0x2000 : unit;
}

/** The code points of a well-formed string, as numbers. */
export function explode(text: string): JqArray {
  const items: JqValue[] = [];
  for (let i = 0; i < text.length; i++) {
    const c = text.codePointAt(i)!;
    if (c > 0xffff) i++;
    items.push(c);
  }

  return JqArray.fromOwned(items);
}

/** Whether `codePoint` is a Unicode scalar value (a code point that is not a surrogate). */
export function isScalar(codePoint: number): boolean {
  return codePoint >= 0 && codePoint <= Max && (codePoint < 0xd800 || codePoint > 0xdfff);
}

/** The text of a Unicode scalar value (the reference appends it to a builder). */
export function toText(codePoint: number): string {
  return String.fromCodePoint(codePoint);
}

/** The number of UTF-8 bytes of the code point that starts at the UTF-16 unit `index`: 4 means it takes two units. */
export function utf8Length(text: string, index: number): number {
  const c = text.charCodeAt(index);
  if (isHighSurrogate(c) && index + 1 < text.length) return 4;
  return c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
}

/** The number of UTF-8 bytes of a well-formed string. */
export function utf8ByteCount(text: string): number {
  let bytes = 0;
  for (let i = 0; i < text.length; i++) {
    const size = utf8Length(text, i);
    bytes += size;
    if (size === 4) i++;
  }

  return bytes;
}
