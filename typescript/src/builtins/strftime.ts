// The strftime and strptime directive tables of §9.12: English names, no locale.

import * as Errors from "../values/errors.js";
import * as CodePoints from "../values/codePoints.js";
import { Fail } from "../values/jqValue.js";
import * as Calendar from "./calendar.js";

const days = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
const months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

/** The zone a time is formatted in: UTC, or the host's local zone at the instant. */
export interface Zone {
  readonly local: boolean;
  readonly offset: number;
  readonly abbreviation: string | null;
}

/**
 * Formats normalized `time` (wday and yday consistent with the date); `epoch` is the instant in
 * whole seconds, for `%s`. The text, or the error.
 */
export function tryFormat(time: Calendar.Fields, epoch: number, zone: Zone, format: string): string | Fail {
  return tryAppend(time, epoch, zone, format);
}

// The directives made of others, by their conversion character; strptime reads them the same way.
function expansion(conversion: string): string | null {
  switch (conversion) {
    case "c":
      return "%a %b %e %H:%M:%S %Y";
    case "D":
    case "x":
      return "%m/%d/%y";
    case "F":
      return "%Y-%m-%d";
    case "r":
      return "%I:%M:%S %p";
    case "R":
      return "%H:%M";
    case "T":
    case "X":
      return "%H:%M:%S";
    default:
      return null;
  }
}

function tryAppend(time: Calendar.Fields, epoch: number, zone: Zone, format: string): string | Fail {
  let text = "";
  for (let i = 0; i < format.length; i++) {
    if (format[i] !== "%") {
      text += format[i];
      continue;
    }

    const start = ++i;
    if (start === format.length) return new Fail(Errors.trailingPercent);

    // GNU flags: the last of _ - 0 decides the padding of the numbers, ^ upper-cases.
    let padding: string | null = null;
    let upper = false;
    for (; i < format.length && (format[i] === "_" || format[i] === "-" || format[i] === "0" || format[i] === "^"); i++) {
      if (format[i] === "^") upper = true;
      else padding = format[i]!;
    }

    // E and O select a locale's alternative forms, which jqlite does not have.
    const modifier = i < format.length && (format[i] === "E" || format[i] === "O") ? format[i++]! : "";
    if (i === format.length) return new Fail(Errors.unsupportedStrftime(format.substring(start)));

    const conversion = format[i]!;
    const known = modifier === "E" ? "cCxXyY".includes(conversion) : modifier === "O" ? "deHImMSuUVwWy".includes(conversion) : true;
    const made = known ? expansion(conversion) : null;
    // Made of directives of the table: it cannot fail.
    let piece: string | null = made !== null ? (tryAppend(time, epoch, zone, made) as string) : known ? directive(conversion, padding, time, epoch, zone) : null;
    if (piece === null) {
      const length = CodePoints.isHighSurrogate(conversion.charCodeAt(0)) && i + 1 < format.length ? 2 : 1;
      return new Fail(Errors.unsupportedStrftime(format.substring(start, i + length)));
    }

    if (upper && conversion !== "P") piece = asciiUpper(piece);
    text += piece;
  }

  return text;
}

function asciiUpper(text: string): string {
  let result = "";
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i);
    result += c >= 0x61 && c <= 0x7a ? String.fromCharCode(c - 32) : text[i];
  }

  return result;
}

// A directive of the table that is not made of others; null when there is none.
function directive(conversion: string, padding: string | null, time: Calendar.Fields, epoch: number, zone: Zone): string | null {
  const hours12 = time.hours % 12 === 0 ? 12 : time.hours % 12;
  switch (conversion) {
    case "a":
      return days[time.weekday]!.substring(0, 3);
    case "A":
      return days[time.weekday]!;
    case "b":
    case "h":
      return months[time.month]!.substring(0, 3);
    case "B":
      return months[time.month]!;
    case "C":
      return number(Calendar.floorDiv(time.year, 100), 2, "0", padding);
    case "d":
      return number(time.day, 2, "0", padding);
    case "e":
      return number(time.day, 2, " ", padding);
    case "g":
      return number(Calendar.floorMod(Calendar.isoWeek(time.year, time.weekday, time.yearDay)[0], 100), 2, "0", padding);
    case "G":
      return number(Calendar.isoWeek(time.year, time.weekday, time.yearDay)[0], 4, "0", padding);
    case "H":
      return number(time.hours, 2, "0", padding);
    case "I":
      return number(hours12, 2, "0", padding);
    case "k":
      return number(time.hours, 2, " ", padding);
    case "l":
      return number(hours12, 2, " ", padding);
    case "j":
      return number(time.yearDay + 1, 3, "0", padding);
    case "m":
      return number(time.month + 1, 2, "0", padding);
    case "M":
      return number(time.minutes, 2, "0", padding);
    case "n":
      return "\n";
    case "t":
      return "\t";
    case "p":
      return time.hours < 12 ? "AM" : "PM";
    case "P":
      return time.hours < 12 ? "am" : "pm";
    case "s":
      return number(epoch, 1, "0", null);
    case "S":
      return number(Math.floor(time.seconds), 2, "0", padding);
    case "u":
      return number(Calendar.floorMod(time.weekday + 6, 7) + 1, 1, "0", padding);
    case "w":
      return number(time.weekday, 1, "0", padding);
    case "U":
      return number(Calendar.floorDiv(time.yearDay + 7 - time.weekday, 7), 2, "0", padding);
    case "W":
      return number(Calendar.floorDiv(time.yearDay + 7 - Calendar.floorMod(time.weekday + 6, 7), 7), 2, "0", padding);
    case "V":
      return number(Calendar.isoWeek(time.year, time.weekday, time.yearDay)[1], 2, "0", padding);
    case "y":
      return number(Calendar.floorMod(time.year, 100), 2, "0", padding);
    case "Y":
      return number(time.year, 4, "0", padding);
    case "z":
      return offsetText(zone.local ? zone.offset : 0, true);
    case "Z":
      if (!zone.local) return "UTC";
      if (zone.abbreviation !== null) return zone.abbreviation;
      return offsetText(zone.offset, Math.trunc(zone.offset / 60) % 60 !== 0);
    case "%":
      return "%";
    default:
      return null;
  }
}

// +hhmm (or +hh) from an offset in seconds east of UTC.
function offsetText(offset: number, minutes: boolean): string {
  const total = Math.trunc(Math.abs(offset) / 60);
  let text = (offset < 0 ? "-" : "+") + number(Math.trunc(total / 60), 2, "0", null);
  if (minutes) text += number(total % 60, 2, "0", null);
  return text;
}

// Decimal, padded to the width with pad, unless the flag says otherwise (- no padding, _ spaces,
// 0 zeros); a negative number is '-' and the padded digits.
function number(value: number, width: number, pad: string, padding: string | null): string {
  if (padding === "-") width = 1;
  else if (padding !== null) pad = padding === "_" ? " " : "0";
  let digits = "";
  let rest = Math.abs(value);
  do {
    digits = String.fromCharCode(0x30 + (rest % 10)) + digits;
    rest = Math.trunc(rest / 10);
  } while (rest !== 0);

  return (value < 0 ? "-" : "") + pad.repeat(Math.max(0, width - digits.length)) + digits;
}

// Where tryParse is in the input.
let at = 0;
// The number or the index a read function read.
let read = 0;

/** Parses `input` by `format` into a broken-down time; null on any mismatch. */
export function tryParse(input: string, format: string): number[] | null {
  let year = 1900;
  let month = 0;
  let day = 0;
  let hours = 0;
  let minutes = 0;
  let seconds = 0;
  let weekday = -1;
  let yearDay = -1;
  // %I and %l read a 12-hour clock, which a PM read by %p moves to the afternoon.
  let twelveHour = false;
  let pm = false;
  format = expand(format);
  at = 0;
  for (let i = 0; i < format.length; i++) {
    const c = format.charCodeAt(i);
    if (c !== 0x25 /* % */) {
      if (isSpace(c)) {
        skipSpaces(input);
      } else {
        if (at === input.length || input.charCodeAt(at) !== c) return null;
        at++;
      }

      continue;
    }

    if (++i === format.length) return null;
    let ok: boolean;
    switch (format[i]) {
      case "Y":
        ok = readNumber(input, 4, 0, 9999);
        year = read;
        break;
      case "y":
        ok = readNumber(input, 2, 0, 99);
        year = read + (read >= 69 ? 1900 : 2000);
        break;
      case "m":
        ok = readNumber(input, 2, 1, 12);
        month = read - 1;
        break;
      case "d":
      case "e":
        ok = readNumber(input, 2, 1, 31);
        day = read;
        break;
      case "H":
      case "k":
        ok = readNumber(input, 2, 0, 23);
        hours = read;
        twelveHour = false;
        break;
      case "I":
      case "l":
        ok = readNumber(input, 2, 1, 12);
        hours = read % 12;
        twelveHour = true;
        break;
      case "p":
        ok = readMeridian(input);
        pm = read === 1;
        break;
      case "M":
        ok = readNumber(input, 2, 0, 59);
        minutes = read;
        break;
      case "S":
        ok = readNumber(input, 2, 0, 60);
        seconds = read;
        break;
      case "j":
        ok = readNumber(input, 3, 1, 366);
        yearDay = read - 1;
        break;
      case "a":
      case "A":
        ok = readName(input, days);
        weekday = read;
        break;
      case "u":
        ok = readNumber(input, 1, 1, 7);
        weekday = read % 7;
        break;
      case "w":
        ok = readNumber(input, 1, 0, 6);
        weekday = read;
        break;
      case "b":
      case "h":
      case "B":
        ok = readName(input, months);
        month = read;
        break;
      case "z":
        ok = readOffset(input);
        break;
      case "Z":
        ok = readWord(input);
        break;
      case "n":
      case "t":
        skipSpaces(input);
        ok = true;
        break;
      case "%":
        ok = at < input.length && input[at++] === "%";
        break;
      default:
        ok = false;
        break;
    }

    if (!ok) return null;
  }

  skipSpaces(input);
  if (at !== input.length) return null;
  if (twelveHour && pm) hours += 12;
  const [computedWeekday, computedYearDay] = Calendar.weekdayAndYearDay(year, month, day);
  return [year, month, day, hours, minutes, seconds, weekday >= 0 ? weekday : computedWeekday, yearDay >= 0 ? yearDay : computedYearDay];
}

// The format with the directives made of others replaced by what they are made of.
function expand(format: string): string {
  let text = "";
  for (let i = 0; i < format.length; i++) {
    if (format[i] === "%" && i + 1 < format.length) {
      const next = format[++i]!;
      const made = expansion(next);
      text += made !== null ? made : "%" + next;
    } else {
      text += format[i];
    }
  }

  return text;
}

// AM or PM, ASCII case-insensitive: read is 1 for PM.
function readMeridian(input: string): boolean {
  const pm = at + 2 <= input.length && asciiEqualsIgnoreCase(input, at, "PM");
  read = pm ? 1 : 0;
  if (!pm && !(at + 2 <= input.length && asciiEqualsIgnoreCase(input, at, "AM"))) return false;
  at += 2;
  return true;
}

// Skips input whitespace, then reads 1 to `width` ASCII digits in [min, max], into read.
function readNumber(input: string, width: number, min: number, max: number): boolean {
  skipSpaces(input);
  let value = 0;
  let count = 0;
  while (count < width && at < input.length && isDigit(input.charCodeAt(at))) {
    value = value * 10 + (input.charCodeAt(at++) - 0x30);
    count++;
  }

  read = value;
  return count > 0 && value >= min && value <= max;
}

// A name of the table, full or its first three letters, ASCII case-insensitive; full names first. Its index into read.
function readName(input: string, names: readonly string[]): boolean {
  for (let length = 0; length < 2; length++) {
    for (let i = 0; i < names.length; i++) {
      const name = length === 0 ? names[i]! : names[i]!.substring(0, 3);
      if (at + name.length <= input.length && asciiEqualsIgnoreCase(input, at, name)) {
        at += name.length;
        read = i;
        return true;
      }
    }
  }

  read = -1;
  return false;
}

function asciiEqualsIgnoreCase(input: string, from: number, name: string): boolean {
  for (let i = 0; i < name.length; i++) {
    const a = input.charCodeAt(from + i);
    const b = name.charCodeAt(i);
    if ((a >= 0x41 && a <= 0x5a ? a + 32 : a) !== (b >= 0x41 && b <= 0x5a ? b + 32 : b)) return false;
  }

  return true;
}

// Z, or + or - with hh, hhmm or hh:mm; read and ignored.
function readOffset(input: string): boolean {
  if (at < input.length && input[at] === "Z") {
    at++;
    return true;
  }

  if (at === input.length || (input[at] !== "+" && input[at] !== "-") || !twoDigits(input, at + 1)) return false;
  at += 3;
  if (twoDigits(input, at)) at += 2;
  else if (at < input.length && input[at] === ":" && twoDigits(input, at + 1)) at += 3;
  return true;
}

function twoDigits(input: string, from: number): boolean {
  return from + 2 <= input.length && isDigit(input.charCodeAt(from)) && isDigit(input.charCodeAt(from + 1));
}

// A run of non-whitespace characters; read and ignored.
function readWord(input: string): boolean {
  const start = at;
  while (at < input.length && !isSpace(input.charCodeAt(at))) at++;
  return at > start;
}

function skipSpaces(input: string): void {
  while (at < input.length && isSpace(input.charCodeAt(at))) at++;
}

function isSpace(c: number): boolean {
  return c === 0x20 || (c >= 0x09 && c <= 0x0d);
}

function isDigit(c: number): boolean {
  return c >= 0x30 && c <= 0x39;
}
