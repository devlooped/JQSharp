// §9.6: text conversion and the string natives the prelude builds on.

import type { Interpreter } from "../runtime/interpreter.js";
import * as CodePoints from "../values/codePoints.js";
import * as Errors from "../values/errors.js";
import { JqArray } from "../values/jqArray.js";
import { Fail, type JqValue } from "../values/jqValue.js";
import * as ValueOps from "../values/valueOps.js";
import * as ValueText from "../values/valueText.js";
import * as Formats from "./formats.js";
import { add } from "./nativeTable.js";

export function register(): void {
  add("tostring", 0, (_, input) => toText(input));
  add("tojson", 0, (_, input) => ValueText.toJson(input));
  add("fromjson", 0, (_, input) => (typeof input === "string" ? ValueText.tryParse(input) : new Fail(Errors.onlyStringsParse(input))));
  add("tonumber", 0, toNumber);
  add("toboolean", 0, toBoolean);
  add("format", 1, (_, input, args) => (typeof args[0] === "string" ? Formats.tryApply(args[0], input) : new Fail(Errors.notAValidFormat(args[0]!))));
  add("explode", 0, (_, input) => ValueOps.tryExplode(input));
  add("implode", 0, (_, input) => ValueOps.tryImplode(input));
  add("ascii_downcase", 0, (_, input) => asciiCase(input, false));
  add("ascii_upcase", 0, (_, input) => asciiCase(input, true));
  add("startswith", 1, (_, input, args) => {
    const prefix = args[0]!;
    return typeof input === "string" && typeof prefix === "string" ? input.startsWith(prefix) : new Fail(Errors.startsWithStrings);
  });
  add("endswith", 1, (_, input, args) => {
    const suffix = args[0]!;
    return typeof input === "string" && typeof suffix === "string" ? input.endsWith(suffix) : new Fail(Errors.endsWithStrings);
  });
  add("trim", 0, (_, input) => trim(input, true, true));
  add("ltrim", 0, (_, input) => trim(input, true, false));
  add("rtrim", 0, (_, input) => trim(input, false, true));
  add("split", 1, (_, input, args) => {
    const separator = args[0]!;
    return typeof input === "string" && typeof separator === "string" ? ValueOps.split(input, separator) : new Fail(Errors.splitStrings);
  });
  add("_strindices", 1, stringIndices);
}

/** `tostring`: a string as it is, any other value as its canonical text. */
export function toText(value: JqValue): string {
  return typeof value === "string" ? value : ValueText.toJson(value);
}

function toNumber(_: Interpreter, input: JqValue): JqValue | Fail {
  if (typeof input === "number") return input;
  if (typeof input === "string") {
    const number = ValueText.tryParseNumber(input);
    if (number !== null) return number;
  }

  return new Fail(Errors.cannotParseNumber(input));
}

function toBoolean(_: Interpreter, input: JqValue): JqValue | Fail {
  if (typeof input === "boolean") return input;
  if (input === "true" || input === "false") return input === "true";
  return new Fail(Errors.cannotParseBoolean(input));
}

// trim, ltrim, rtrim: the Unicode White_Space code points, all of them single UTF-16 units.
function trim(input: JqValue, start: boolean, end: boolean): JqValue | Fail {
  if (typeof input !== "string") return new Fail(Errors.trimNotString);
  let from = 0;
  let to = input.length;
  while (start && from < to && isWhiteSpace(input.charCodeAt(from))) from++;
  while (end && to > from && isWhiteSpace(input.charCodeAt(to - 1))) to--;
  return from === 0 && to === input.length ? input : input.substring(from, to);
}

function isWhiteSpace(c: number): boolean {
  return (
    (c >= 0x0009 && c <= 0x000d) ||
    c === 0x0020 ||
    c === 0x0085 ||
    c === 0x00a0 ||
    c === 0x1680 ||
    (c >= 0x2000 && c <= 0x200a) ||
    c === 0x2028 ||
    c === 0x2029 ||
    c === 0x202f ||
    c === 0x205f ||
    c === 0x3000
  );
}

// The ASCII letters in the other case, every other code point unchanged (no locale).
function asciiCase(input: JqValue, upper: boolean): JqValue | Fail {
  if (typeof input !== "string") return new Fail(Errors.explodeNotString);
  let text = "";
  let run = 0;
  for (let i = 0; i < input.length; i++) {
    const c = input.charCodeAt(i);
    if (upper ? c >= 0x61 && c <= 0x7a : c >= 0x41 && c <= 0x5a) {
      text += input.substring(run, i) + String.fromCharCode(upper ? c - 32 : c + 32);
      run = i + 1;
    }
  }

  return run === 0 ? input : text + input.substring(run);
}

// _strindices(needle): the code point offsets of every occurrence, overlapping ones included.
function stringIndices(_: Interpreter, input: JqValue, args: JqValue[]): JqValue | Fail {
  const needle = args[0]!;
  if (typeof input !== "string") return new Fail(Errors.cannotSearchNotString(input));
  if (typeof needle !== "string") return new Fail(Errors.notAString(needle));
  const offsets: JqValue[] = [];
  for (let i = 0, codePoint = 0; needle.length > 0 && i + needle.length <= input.length; codePoint++) {
    if (input.startsWith(needle, i)) offsets.push(codePoint);
    i += CodePoints.isHighSurrogate(input.charCodeAt(i)) && i + 1 < input.length ? 2 : 1;
  }

  return JqArray.fromOwned(offsets);
}
