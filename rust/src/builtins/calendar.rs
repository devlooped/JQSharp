//! The proleptic Gregorian calendar of §9.12 and the broken-down time
//! `[year, month, mday, hours, minutes, seconds, wday, yday]` (month 0–11, wday Sunday = 0).
//! Integer arithmetic with floor division; no host date type.

/// |t| below 2^53: the range of seconds `gmtime` accepts.
pub const MAX_SECONDS: f64 = 9007199254740992.0;

const SECONDS_PER_DAY: i64 = 86400;

/// A broken-down time; `seconds` may have a fraction.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Fields {
    pub year: i64,
    pub month: i64,
    pub day: i64,
    pub hours: i64,
    pub minutes: i64,
    pub seconds: f64,
    pub weekday: i64,
    pub year_day: i64,
}

/// The days from 1970-01-01 to the date; `month` is 1–12, `day` may be 0 or exceed the month.
pub fn days(year: i64, month: i64, day: i64) -> i64 {
    let year = year - if month <= 2 { 1 } else { 0 };
    let era = floor_div(year, 400);
    let yoe = year - era * 400;
    let doy = (153 * (month + (if month > 2 { -3 } else { 9 })) + 2) / 5 + day - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146097 + doe - 719468
}

/// Whether `t` can be broken down: finite and |t| < 2^53.
pub fn in_range(t: f64) -> bool {
    t.is_finite() && t.abs() < MAX_SECONDS
}

/// The broken-down UTC time of `t` seconds since the epoch (in range), the fraction kept in the
/// seconds.
pub fn break_down(t: f64) -> Fields {
    let mut day = (t / SECONDS_PER_DAY as f64).floor() as i64;
    let mut rest = t - day as f64 * SECONDS_PER_DAY as f64;
    // The division may round across a day boundary: bring the rest into [0, 86400).
    if rest < 0.0 {
        day -= 1;
        rest += SECONDS_PER_DAY as f64;
    } else if rest >= SECONDS_PER_DAY as f64 {
        day += 1;
        rest -= SECONDS_PER_DAY as f64;
    }

    let (year, month, mday) = date(day);
    let minutes = (rest / 60.0).floor();
    Fields {
        year,
        month: month - 1,
        day: mday,
        hours: (rest / 3600.0).floor() as i64,
        minutes: minutes as i64 % 60,
        seconds: rest - minutes * 60.0,
        weekday: floor_mod(day + 4, 7),
        year_day: day - days(year, 1, 1),
    }
}

/// Seconds since the epoch of broken-down fields read as UTC, out-of-range fields rolling over
/// (C `timegm`).
pub fn seconds(year: i64, month: i64, day: i64, hours: i64, minutes: i64, seconds: i64) -> f64 {
    let y = year + floor_div(month, 12);
    let m = floor_mod(month, 12);
    (days(y, m + 1, 1) + day - 1) as f64 * SECONDS_PER_DAY as f64 + hours as f64 * 3600.0 + minutes as f64 * 60.0 + seconds as f64
}

/// The weekday (Sunday = 0) and day of the year of a date; `month` 0–11.
pub fn weekday_and_year_day(year: i64, month: i64, day: i64) -> (i64, i64) {
    let days_to = days(year, month + 1, day);
    (floor_mod(days_to + 4, 7), days_to - days(year, 1, 1))
}

/// The ISO 8601 week-based year and week number of a date given by year, wday and yday.
pub fn iso_week(year: i64, weekday: i64, year_day: i64) -> (i64, i64) {
    let iso_weekday = floor_mod(weekday + 6, 7) + 1;
    let week = floor_div(year_day + 1 - iso_weekday + 10, 7);
    if week < 1 {
        return (year - 1, weeks(year - 1));
    }
    if week > weeks(year) {
        return (year + 1, 1);
    }
    (year, week)
}

fn weeks(year: i64) -> i64 {
    if p(year) == 4 || p(year - 1) == 3 { 53 } else { 52 }
}

fn p(y: i64) -> i64 {
    floor_mod(y + floor_div(y, 4) - floor_div(y, 100) + floor_div(y, 400), 7)
}

// The inverse of days: the date (month 1–12) of a day number.
fn date(days: i64) -> (i64, i64, i64) {
    let z = days + 719468;
    let era = floor_div(z, 146097);
    let doe = z - era * 146097;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = doy - (153 * mp + 2) / 5 + 1;
    let month = if mp < 10 { mp + 3 } else { mp - 9 };
    (yoe + era * 400 + (if month <= 2 { 1 } else { 0 }), month, day)
}

pub fn floor_div(a: i64, b: i64) -> i64 {
    a / b - (if a % b != 0 && (a < 0) != (b < 0) { 1 } else { 0 })
}

pub fn floor_mod(a: i64, b: i64) -> i64 {
    a - floor_div(a, b) * b
}
