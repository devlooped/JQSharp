//! The strftime and strptime directive tables of §9.12: English names, no locale. Formats and
//! inputs are read as code points.

use crate::values::{JqValue, errors};

use super::calendar::{self, Fields};

const DAYS: [&str; 7] = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
const MONTHS: [&str; 12] = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

/// The zone a time is formatted in: UTC, or the host's local zone at the instant.
pub struct Zone {
    pub local: bool,
    pub offset: i64,
    pub abbreviation: Option<String>,
}

/// Formats normalized `time` (wday and yday consistent with the date); `epoch` is the instant in
/// whole seconds, for `%s`.
pub fn try_format(time: &Fields, epoch: i64, zone: &Zone, format: &str) -> Result<String, JqValue> {
    let mut builder = String::with_capacity(format.len() + 16);
    let format: Vec<char> = format.chars().collect();
    try_append(&mut builder, time, epoch, zone, &format)?;
    Ok(builder)
}

// The directives made of others, by their conversion character; strptime reads them the same way.
fn expansion(conversion: char) -> Option<&'static str> {
    match conversion {
        'c' => Some("%a %b %e %H:%M:%S %Y"),
        'D' | 'x' => Some("%m/%d/%y"),
        'F' => Some("%Y-%m-%d"),
        'r' => Some("%I:%M:%S %p"),
        'R' => Some("%H:%M"),
        'T' | 'X' => Some("%H:%M:%S"),
        _ => None,
    }
}

fn try_append(builder: &mut String, time: &Fields, epoch: i64, zone: &Zone, format: &[char]) -> Result<(), JqValue> {
    let mut i = 0;
    while i < format.len() {
        if format[i] != '%' {
            builder.push(format[i]);
            i += 1;
            continue;
        }

        i += 1;
        let start = i;
        if start == format.len() {
            return Err(errors::trailing_percent());
        }

        // GNU flags: the last of _ - 0 decides the padding of the numbers, ^ upper-cases.
        let mut padding = None;
        let mut upper = false;
        while i < format.len() && matches!(format[i], '_' | '-' | '0' | '^') {
            if format[i] == '^' {
                upper = true;
            } else {
                padding = Some(format[i]);
            }
            i += 1;
        }

        // E and O select a locale's alternative forms, which jqlite does not have.
        let mut modifier = '\0';
        if i < format.len() && matches!(format[i], 'E' | 'O') {
            modifier = format[i];
            i += 1;
        }
        if i == format.len() {
            return Err(errors::unsupported_strftime(&text(&format[start..])));
        }

        let conversion = format[i];
        let mark = builder.len();
        let known = match modifier {
            'E' => "cCxXyY".contains(conversion),
            'O' => "deHImMSuUVwWy".contains(conversion),
            _ => true,
        };
        match expansion(conversion) {
            Some(expansion) if known => {
                // Made of directives of the table: it cannot fail.
                let expansion: Vec<char> = expansion.chars().collect();
                try_append(builder, time, epoch, zone, &expansion)?;
            }
            _ => {
                if !known || !try_directive(builder, conversion, padding, time, epoch, zone) {
                    return Err(errors::unsupported_strftime(&text(&format[start..=i])));
                }
            }
        }

        if upper && conversion != 'P' {
            let upper_cased = builder[mark..].to_ascii_uppercase();
            builder.truncate(mark);
            builder.push_str(&upper_cased);
        }
        i += 1;
    }

    Ok(())
}

fn text(points: &[char]) -> String {
    points.iter().collect()
}

// A directive of the table that is not made of others; false when there is none.
fn try_directive(builder: &mut String, conversion: char, padding: Option<char>, time: &Fields, epoch: i64, zone: &Zone) -> bool {
    let hours12 = if time.hours % 12 == 0 { 12 } else { time.hours % 12 };
    match conversion {
        'a' => builder.push_str(&DAYS[time.weekday as usize][..3]),
        'A' => builder.push_str(DAYS[time.weekday as usize]),
        'b' | 'h' => builder.push_str(&MONTHS[time.month as usize][..3]),
        'B' => builder.push_str(MONTHS[time.month as usize]),
        'C' => number(builder, calendar::floor_div(time.year, 100), 2, '0', padding),
        'd' => number(builder, time.day, 2, '0', padding),
        'e' => number(builder, time.day, 2, ' ', padding),
        'g' => number(builder, calendar::floor_mod(calendar::iso_week(time.year, time.weekday, time.year_day).0, 100), 2, '0', padding),
        'G' => number(builder, calendar::iso_week(time.year, time.weekday, time.year_day).0, 4, '0', padding),
        'H' => number(builder, time.hours, 2, '0', padding),
        'I' => number(builder, hours12, 2, '0', padding),
        'k' => number(builder, time.hours, 2, ' ', padding),
        'l' => number(builder, hours12, 2, ' ', padding),
        'j' => number(builder, time.year_day + 1, 3, '0', padding),
        'm' => number(builder, time.month + 1, 2, '0', padding),
        'M' => number(builder, time.minutes, 2, '0', padding),
        'n' => builder.push('\n'),
        't' => builder.push('\t'),
        'p' => builder.push_str(if time.hours < 12 { "AM" } else { "PM" }),
        'P' => builder.push_str(if time.hours < 12 { "am" } else { "pm" }),
        's' => number(builder, epoch, 1, '0', None),
        'S' => number(builder, time.seconds.floor() as i64, 2, '0', padding),
        'u' => number(builder, calendar::floor_mod(time.weekday + 6, 7) + 1, 1, '0', padding),
        'w' => number(builder, time.weekday, 1, '0', padding),
        'U' => number(builder, calendar::floor_div(time.year_day + 7 - time.weekday, 7), 2, '0', padding),
        'W' => number(builder, calendar::floor_div(time.year_day + 7 - calendar::floor_mod(time.weekday + 6, 7), 7), 2, '0', padding),
        'V' => number(builder, calendar::iso_week(time.year, time.weekday, time.year_day).1, 2, '0', padding),
        'y' => number(builder, calendar::floor_mod(time.year, 100), 2, '0', padding),
        'Y' => number(builder, time.year, 4, '0', padding),
        'z' => offset(builder, if zone.local { zone.offset } else { 0 }, true),
        'Z' => {
            if !zone.local {
                builder.push_str("UTC");
            } else if let Some(abbreviation) = &zone.abbreviation {
                builder.push_str(abbreviation);
            } else {
                offset(builder, zone.offset, zone.offset / 60 % 60 != 0);
            }
        }
        '%' => builder.push('%'),
        _ => return false,
    }

    true
}

// +hhmm (or +hh) from an offset in seconds east of UTC.
fn offset(builder: &mut String, offset: i64, minutes: bool) {
    builder.push(if offset < 0 { '-' } else { '+' });
    let total = offset.abs() / 60;
    number(builder, total / 60, 2, '0', None);
    if minutes {
        number(builder, total % 60, 2, '0', None);
    }
}

// Decimal, padded to the width with pad, unless the flag says otherwise (- no padding, _ spaces,
// 0 zeros); a negative number is '-' and the padded digits.
fn number(builder: &mut String, value: i64, width: usize, pad: char, padding: Option<char>) {
    let mut width = width;
    let mut pad = pad;
    match padding {
        Some('-') => width = 1,
        Some(flag) => pad = if flag == '_' { ' ' } else { '0' },
        None => {}
    }
    if value < 0 {
        builder.push('-');
    }
    let digits = value.unsigned_abs().to_string();
    for _ in digits.len()..width {
        builder.push(pad);
    }
    builder.push_str(&digits);
}

/// Parses `input` by `format` into a broken-down time; `None` on any mismatch.
pub fn try_parse(input: &str, format: &str) -> Option<[i64; 8]> {
    let (mut year, mut month, mut day, mut hours, mut minutes, mut seconds) = (1900, 0, 0, 0, 0, 0);
    let (mut weekday, mut year_day) = (-1, -1);
    // %I and %l read a 12-hour clock, which a PM read by %p moves to the afternoon.
    let (mut twelve_hour, mut pm) = (false, false);
    let input: Vec<char> = input.chars().collect();
    let format = expand(format);
    let mut at = 0;
    let mut i = 0;
    while i < format.len() {
        let c = format[i];
        if c != '%' {
            if is_space(c) {
                skip_spaces(&input, &mut at);
            } else {
                if at == input.len() || input[at] != c {
                    return None;
                }
                at += 1;
            }

            i += 1;
            continue;
        }

        i += 1;
        if i == format.len() {
            return None;
        }
        match format[i] {
            'Y' => year = read_number(&input, &mut at, 4, 0, 9999)?,
            'y' => {
                year = read_number(&input, &mut at, 2, 0, 99)?;
                year += if year >= 69 { 1900 } else { 2000 };
            }
            'm' => month = read_number(&input, &mut at, 2, 1, 12)? - 1,
            'd' | 'e' => day = read_number(&input, &mut at, 2, 1, 31)?,
            'H' | 'k' => {
                hours = read_number(&input, &mut at, 2, 0, 23)?;
                twelve_hour = false;
            }
            'I' | 'l' => {
                hours = read_number(&input, &mut at, 2, 1, 12)? % 12;
                twelve_hour = true;
            }
            'p' => pm = read_meridian(&input, &mut at)?,
            'M' => minutes = read_number(&input, &mut at, 2, 0, 59)?,
            'S' => seconds = read_number(&input, &mut at, 2, 0, 60)?,
            'j' => year_day = read_number(&input, &mut at, 3, 1, 366)? - 1,
            'a' | 'A' => weekday = read_name(&input, &mut at, &DAYS)?,
            'u' => weekday = read_number(&input, &mut at, 1, 1, 7)? % 7,
            'w' => weekday = read_number(&input, &mut at, 1, 0, 6)?,
            'b' | 'h' | 'B' => month = read_name(&input, &mut at, &MONTHS)?,
            'z' => read_offset(&input, &mut at)?,
            'Z' => read_word(&input, &mut at)?,
            'n' | 't' => skip_spaces(&input, &mut at),
            '%' => {
                if at == input.len() || input[at] != '%' {
                    return None;
                }
                at += 1;
            }
            _ => return None,
        }
        i += 1;
    }

    skip_spaces(&input, &mut at);
    if at != input.len() {
        return None;
    }
    if twelve_hour && pm {
        hours += 12;
    }

    let (computed_weekday, computed_year_day) = calendar::weekday_and_year_day(year, month, day);
    Some([
        year,
        month,
        day,
        hours,
        minutes,
        seconds,
        if weekday >= 0 { weekday } else { computed_weekday },
        if year_day >= 0 { year_day } else { computed_year_day },
    ])
}

// The format with the directives made of others replaced by what they are made of.
fn expand(format: &str) -> Vec<char> {
    let format: Vec<char> = format.chars().collect();
    let mut builder = Vec::with_capacity(format.len());
    let mut i = 0;
    while i < format.len() {
        if format[i] == '%' && i + 1 < format.len() {
            i += 1;
            let next = format[i];
            match expansion(next) {
                Some(expansion) => builder.extend(expansion.chars()),
                None => {
                    builder.push('%');
                    builder.push(next);
                }
            }
        } else {
            builder.push(format[i]);
        }
        i += 1;
    }

    builder
}

// AM or PM, ASCII case-insensitive.
fn read_meridian(input: &[char], at: &mut usize) -> Option<bool> {
    let pm = ascii_equals_ignore_case(input, *at, "PM");
    if !pm && !ascii_equals_ignore_case(input, *at, "AM") {
        return None;
    }
    *at += 2;
    Some(pm)
}

// Skips input whitespace, then reads 1 to `width` ASCII digits in [min, max].
fn read_number(input: &[char], at: &mut usize, width: usize, min: i64, max: i64) -> Option<i64> {
    skip_spaces(input, at);
    let mut value = 0;
    let mut count = 0;
    while count < width && *at < input.len() && input[*at].is_ascii_digit() {
        value = value * 10 + (input[*at] as i64 - '0' as i64);
        *at += 1;
        count += 1;
    }

    if count > 0 && value >= min && value <= max { Some(value) } else { None }
}

// A name of the table, full or its first three letters, ASCII case-insensitive; full names first.
fn read_name(input: &[char], at: &mut usize, names: &[&str]) -> Option<i64> {
    for length in 0..2 {
        for (i, full) in names.iter().enumerate() {
            let name = if length == 0 { full } else { &full[..3] };
            if ascii_equals_ignore_case(input, *at, name) {
                *at += name.len();
                return Some(i as i64);
            }
        }
    }

    None
}

// Whether the (ASCII) name is at the position, whatever the case of its letters.
fn ascii_equals_ignore_case(input: &[char], at: usize, name: &str) -> bool {
    if at + name.len() > input.len() {
        return false;
    }
    name.chars().enumerate().all(|(i, b)| input[at + i].eq_ignore_ascii_case(&b))
}

// Z, or + or - with hh, hhmm or hh:mm; read and ignored.
fn read_offset(input: &[char], at: &mut usize) -> Option<()> {
    if *at < input.len() && input[*at] == 'Z' {
        *at += 1;
        return Some(());
    }

    if *at == input.len() || !matches!(input[*at], '+' | '-') || !two_digits(input, *at + 1) {
        return None;
    }
    *at += 3;
    if two_digits(input, *at) {
        *at += 2;
    } else if *at < input.len() && input[*at] == ':' && two_digits(input, *at + 1) {
        *at += 3;
    }
    Some(())
}

fn two_digits(input: &[char], at: usize) -> bool {
    at + 2 <= input.len() && input[at].is_ascii_digit() && input[at + 1].is_ascii_digit()
}

// A run of non-whitespace characters; read and ignored.
fn read_word(input: &[char], at: &mut usize) -> Option<()> {
    let start = *at;
    while *at < input.len() && !is_space(input[*at]) {
        *at += 1;
    }
    if *at > start { Some(()) } else { None }
}

fn skip_spaces(input: &[char], at: &mut usize) {
    while *at < input.len() && is_space(input[*at]) {
        *at += 1;
    }
}

fn is_space(c: char) -> bool {
    c == ' ' || ('\t'..='\r').contains(&c)
}
