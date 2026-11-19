// The proleptic Gregorian calendar of §9.12 and the broken-down time
// [year, month, mday, hours, minutes, seconds, wday, yday] (month 0–11, wday Sunday = 0).
// Integer arithmetic with floor division; no host date type.
//
// The reference computes with 64-bit integers. A JavaScript number is an exact integer up to
// 2^53 only, so the numbers here stay well below that: `seconds`, the one operation that takes
// fields of any size (mktime clamps them to ±2^53), goes through BigInt when a field is large.

/** |t| below 2^53: the range of seconds `gmtime` accepts. */
export const MaxSeconds = 9007199254740992;

const SecondsPerDay = 86400;

// Below this, the fields of a date keep every product of days() an exact number.
const SmallField = 1e9;

/** A broken-down time; `seconds` may have a fraction. */
export interface Fields {
  readonly year: number;
  readonly month: number;
  readonly day: number;
  readonly hours: number;
  readonly minutes: number;
  readonly seconds: number;
  readonly weekday: number;
  readonly yearDay: number;
}

/** The days from 1970-01-01 to the date; `month` is 1–12, `day` may be 0 or exceed the month. */
export function days(year: number, month: number, day: number): number {
  year -= month <= 2 ? 1 : 0;
  const era = floorDiv(year, 400);
  const yoe = year - era * 400;
  const doy = Math.trunc((153 * (month + (month > 2 ? -3 : 9)) + 2) / 5) + day - 1;
  const doe = yoe * 365 + Math.trunc(yoe / 4) - Math.trunc(yoe / 100) + doy;
  return era * 146097 + doe - 719468;
}

// days(year, month, 1) for a year of any size.
function daysOfLargeYear(year: bigint, month: number): bigint {
  year -= month <= 2 ? 1n : 0n;
  const era = year / 400n - (year % 400n !== 0n && year < 0n ? 1n : 0n);
  const yoe = Number(year - era * 400n);
  const doy = Math.trunc((153 * (month + (month > 2 ? -3 : 9)) + 2) / 5);
  const doe = yoe * 365 + Math.trunc(yoe / 4) - Math.trunc(yoe / 100) + doy;
  return era * 146097n + BigInt(doe) - 719468n;
}

/** Whether `t` can be broken down: finite and |t| < 2^53. */
export function inRange(t: number): boolean {
  return Number.isFinite(t) && Math.abs(t) < MaxSeconds;
}

/** The broken-down UTC time of `t` seconds since the epoch (in range), the fraction kept in the seconds. */
export function breakDown(t: number): Fields {
  let day = Math.floor(t / SecondsPerDay);
  let rest = t - day * SecondsPerDay;
  // The division may round across a day boundary: bring the rest into [0, 86400).
  if (rest < 0) {
    day--;
    rest += SecondsPerDay;
  } else if (rest >= SecondsPerDay) {
    day++;
    rest -= SecondsPerDay;
  }

  const [year, month, mday] = date(day);
  const minutes = Math.floor(rest / 60);
  return {
    year,
    month: month - 1,
    day: mday,
    hours: Math.floor(rest / 3600),
    minutes: minutes % 60,
    seconds: rest - minutes * 60,
    weekday: floorMod(day + 4, 7),
    yearDay: day - days(year, 1, 1),
  };
}

/** Seconds since the epoch of broken-down fields (integers) read as UTC, out-of-range fields rolling over (C `timegm`). */
export function seconds(year: number, month: number, day: number, hours: number, minutes: number, second: number): number {
  let dayNumber: number;
  if (Math.abs(year) < SmallField && Math.abs(month) < SmallField && Math.abs(day) < SmallField) {
    const y = year + floorDiv(month, 12);
    const m = floorMod(month, 12);
    dayNumber = days(y, m + 1, 1) + day - 1;
  } else {
    const months = BigInt(month);
    const carry = months / 12n - (months % 12n !== 0n && months < 0n ? 1n : 0n);
    const m = Number(months - carry * 12n);
    dayNumber = Number(daysOfLargeYear(BigInt(year) + carry, m + 1) + BigInt(day) - 1n);
  }

  return dayNumber * SecondsPerDay + hours * 3600 + minutes * 60 + second;
}

/** The weekday (Sunday = 0) and day of the year of a date; `month` 0–11. */
export function weekdayAndYearDay(year: number, month: number, day: number): [weekday: number, yearDay: number] {
  const dayNumber = days(year, month + 1, day);
  return [floorMod(dayNumber + 4, 7), dayNumber - days(year, 1, 1)];
}

/** The ISO 8601 week-based year and week number of a date given by year, wday and yday. */
export function isoWeek(year: number, weekday: number, yearDay: number): [year: number, week: number] {
  const isoWeekday = floorMod(weekday + 6, 7) + 1;
  const week = floorDiv(yearDay + 1 - isoWeekday + 10, 7);
  if (week < 1) return [year - 1, weeks(year - 1)];
  if (week > weeks(year)) return [year + 1, 1];
  return [year, week];
}

function weeks(year: number): number {
  return p(year) === 4 || p(year - 1) === 3 ? 53 : 52;
}

function p(y: number): number {
  return floorMod(y + floorDiv(y, 4) - floorDiv(y, 100) + floorDiv(y, 400), 7);
}

// The inverse of days: the date (month 1–12) of a day number.
function date(dayNumber: number): [year: number, month: number, day: number] {
  const z = dayNumber + 719468;
  const era = floorDiv(z, 146097);
  const doe = z - era * 146097;
  const yoe = Math.trunc((doe - Math.trunc(doe / 1460) + Math.trunc(doe / 36524) - Math.trunc(doe / 146096)) / 365);
  const doy = doe - (365 * yoe + Math.trunc(yoe / 4) - Math.trunc(yoe / 100));
  const mp = Math.trunc((5 * doy + 2) / 153);
  const day = doy - Math.trunc((153 * mp + 2) / 5) + 1;
  const month = mp < 10 ? mp + 3 : mp - 9;
  return [yoe + era * 400 + (month <= 2 ? 1 : 0), month, day];
}

/** The quotient rounded down, of integers; exact (the remainder is taken first). */
export function floorDiv(a: number, b: number): number {
  return (a - floorMod(a, b)) / b;
}

/** The remainder of floorDiv: it has the sign of `b`. */
export function floorMod(a: number, b: number): number {
  const r = a % b;
  return r !== 0 && r < 0 !== b < 0 ? r + b : r + 0;
}
