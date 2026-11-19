// §9.7 Dates, on the calendar and directive tables of §9.12. The time and the local zone come
// only from the host's IClock; without one, the local zone is UTC and `now` is an error.

import type { Interpreter } from "../runtime/interpreter.js";
import * as CodePoints from "../values/codePoints.js";
import * as Errors from "../values/errors.js";
import { JqArray } from "../values/jqArray.js";
import { Fail, JqKind, type JqValue, kindOf } from "../values/jqValue.js";
import * as Calendar from "./calendar.js";
import { add } from "./nativeTable.js";
import * as Strftime from "./strftime.js";

export function register(): void {
  add("now", 0, (interpreter) => (interpreter.clock !== null ? interpreter.clock.now() : new Fail(Errors.noClock)));
  add("gmtime", 0, (_, input) => (typeof input !== "number" ? new Fail(Errors.gmtimeNumeric) : breakDown(input)));
  add("localtime", 0, (interpreter, input) => (typeof input !== "number" ? new Fail(Errors.localtimeNumeric) : breakDown(local(interpreter, input))));
  add("mktime", 0, makeTime);
  add("strftime", 1, (interpreter, input, args) => format(interpreter, input, args[0]!, false));
  add("strflocaltime", 1, (interpreter, input, args) => format(interpreter, input, args[0]!, true));
  add("strptime", 1, parse);
}

function breakDown(t: number): JqValue | Fail {
  return Calendar.inRange(t) ? toValue(Calendar.breakDown(t)) : new Fail(Errors.cannotConvertSeconds);
}

// t + offset(t): the local time of t as if it were UTC (NaN and infinities stay out of range).
function local(interpreter: Interpreter, t: number): number {
  return Calendar.inRange(t) ? t + offset(interpreter, t) : t;
}

function offset(interpreter: Interpreter, t: number): number {
  return interpreter.clock === null ? 0 : interpreter.clock.offset(t);
}

function makeTime(_: Interpreter, input: JqValue): JqValue | Fail {
  if (kindOf(input) !== JqKind.Array) return new Fail(Errors.mktimeArray);
  const seconds = trySeconds(input as JqArray);
  return seconds === null ? new Fail(Errors.mktimeParsed) : seconds;
}

// mktime of a broken-down time: missing elements are 0, each truncated toward zero; an element
// that is not a number, or is NaN or infinite, makes the time invalid (null).
function trySeconds(time: JqArray): number | null {
  const fields = [0, 0, 0, 0, 0, 0];
  for (let i = 0; i < time.count && i < 8; i++) {
    const field = time.get(i);
    if (typeof field !== "number" || !Number.isFinite(field)) return null;
    // + 0: the truncation of a small negative number is -0.
    if (i < 6) fields[i] = Math.min(Math.max(Math.trunc(field), -Calendar.MaxSeconds), Calendar.MaxSeconds) + 0;
  }

  return Calendar.seconds(fields[0]!, fields[1]!, fields[2]!, fields[3]!, fields[4]!, fields[5]!);
}

function format(interpreter: Interpreter, input: JqValue, pattern: JqValue, isLocal: boolean): JqValue | Fail {
  const name = isLocal ? "strflocaltime" : "strftime";
  const kind = kindOf(input);
  if (kind !== JqKind.Number && kind !== JqKind.Array) return new Fail(Errors.requiresParsedDatetime(name));
  if (typeof pattern !== "string") return new Fail(Errors.requiresStringFormat(name));

  // The local seconds (the broken-down time read as UTC) and the instant.
  let localSeconds: number;
  let instant: number;
  if (kind === JqKind.Number) {
    instant = input as number;
    localSeconds = isLocal ? local(interpreter, instant) : instant;
  } else {
    const seconds = trySeconds(input as JqArray);
    if (seconds === null) return new Fail(Errors.requiresParsedDatetime(name));
    localSeconds = seconds;
    instant = isLocal ? localSeconds - offset(interpreter, localSeconds - offset(interpreter, localSeconds)) : localSeconds;
  }

  if (!Calendar.inRange(localSeconds) || !Calendar.inRange(instant)) return new Fail(Errors.cannotConvertSeconds);

  const time = Calendar.breakDown(localSeconds);
  const zoneOffset = isLocal ? offset(interpreter, instant) : 0;
  const epoch = Calendar.seconds(time.year, time.month, time.day, time.hours, time.minutes, Math.floor(time.seconds)) - zoneOffset;
  const abbreviation = isLocal && interpreter.clock !== null ? interpreter.clock.abbreviation(instant) : null;
  const zone: Strftime.Zone = { local: isLocal, offset: zoneOffset, abbreviation: abbreviation === null ? null : CodePoints.sanitize(abbreviation) };
  return Strftime.tryFormat(time, epoch, zone, pattern);
}

function parse(_: Interpreter, input: JqValue, args: JqValue[]): JqValue | Fail {
  const pattern = args[0]!;
  if (typeof input !== "string" || typeof pattern !== "string") return new Fail(Errors.strptimeStrings);
  const fields = Strftime.tryParse(input, pattern);
  if (fields === null) return new Fail(Errors.dateDoesNotMatch(input, pattern));
  return JqArray.fromOwned(fields);
}

function toValue(time: Calendar.Fields): JqValue {
  return JqArray.of(time.year, time.month, time.day, time.hours, time.minutes, time.seconds, time.weekday, time.yearDay);
}
