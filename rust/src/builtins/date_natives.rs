//! §9.7 Dates, on the calendar and directive tables of §9.12. The time and the local zone come
//! only from the host's `Clock`; without one, the local zone is UTC and `now` is an error.

use crate::runtime::interpreter::{Input, Interpreter};
use crate::values::value_ops::Fallible;
use crate::values::{JqArray, JqValue, errors};

use super::calendar::{self, Fields};
use super::native_table::Registry;
use super::strftime::{self, Zone};

pub fn register(table: &mut Registry) {
    table.value("now", 0, |interpreter, _, _| match interpreter.clock() {
        Some(clock) => Ok(JqValue::Number(clock.now())),
        None => Err(errors::no_clock()),
    });
    table.value("gmtime", 0, |_, input, _| match &*input {
        JqValue::Number(t) => break_down(*t),
        _ => Err(errors::gmtime_numeric()),
    });
    table.value("localtime", 0, |interpreter, input, _| match &*input {
        JqValue::Number(t) => break_down(local(interpreter, *t)),
        _ => Err(errors::localtime_numeric()),
    });
    table.value("mktime", 0, make_time);
    table.value("strftime", 1, |interpreter, input, arguments| format(interpreter, &input, &arguments[0], false));
    table.value("strflocaltime", 1, |interpreter, input, arguments| format(interpreter, &input, &arguments[0], true));
    table.value("strptime", 1, parse);
}

fn break_down(t: f64) -> Fallible {
    if calendar::in_range(t) { Ok(to_value(&calendar::break_down(t))) } else { Err(errors::cannot_convert_seconds()) }
}

// t + offset(t): the local time of t as if it were UTC (NaN and infinities stay out of range).
fn local(interpreter: &Interpreter<'_>, t: f64) -> f64 {
    if calendar::in_range(t) { t + offset(interpreter, t) as f64 } else { t }
}

fn offset(interpreter: &Interpreter<'_>, t: f64) -> i64 {
    interpreter.clock().map_or(0, |clock| clock.offset(t) as i64)
}

fn make_time(_: &Interpreter<'_>, input: Input<'_>, _: &mut [JqValue]) -> Fallible {
    let JqValue::Array(time) = &*input else {
        return Err(errors::mktime_array());
    };
    match try_seconds(time) {
        Some(seconds) => Ok(JqValue::Number(seconds)),
        None => Err(errors::mktime_parsed()),
    }
}

// mktime of a broken-down time: missing elements are 0, each truncated toward zero; an element
// that is not a number, or is NaN or infinite, makes the time invalid.
fn try_seconds(time: &JqArray) -> Option<f64> {
    let mut fields = [0i64; 6];
    for (i, element) in time.iter().enumerate().take(8) {
        let JqValue::Number(number) = element else {
            return None;
        };
        if !number.is_finite() {
            return None;
        }

        if i < 6 {
            fields[i] = number.trunc().clamp(-calendar::MAX_SECONDS, calendar::MAX_SECONDS) as i64;
        }
    }

    Some(calendar::seconds(fields[0], fields[1], fields[2], fields[3], fields[4], fields[5]))
}

fn format(interpreter: &Interpreter<'_>, input: &JqValue, format: &JqValue, is_local: bool) -> Fallible {
    let name = if is_local { "strflocaltime" } else { "strftime" };
    if !matches!(input, JqValue::Number(_) | JqValue::Array(_)) {
        return Err(errors::requires_parsed_datetime(name));
    }
    let JqValue::String(format) = format else {
        return Err(errors::requires_string_format(name));
    };

    // The local seconds (the broken-down time read as UTC) and the instant.
    let (local_seconds, instant) = match input {
        JqValue::Number(instant) => (if is_local { local(interpreter, *instant) } else { *instant }, *instant),
        JqValue::Array(time) => {
            let Some(local_seconds) = try_seconds(time) else {
                return Err(errors::requires_parsed_datetime(name));
            };
            let instant =
                if is_local { local_seconds - offset(interpreter, local_seconds - offset(interpreter, local_seconds) as f64) as f64 } else { local_seconds };
            (local_seconds, instant)
        }
        _ => return Err(errors::requires_parsed_datetime(name)),
    };

    if !calendar::in_range(local_seconds) || !calendar::in_range(instant) {
        return Err(errors::cannot_convert_seconds());
    }

    let time = calendar::break_down(local_seconds);
    let zone_offset = if is_local { offset(interpreter, instant) } else { 0 };
    let epoch = calendar::seconds(time.year, time.month, time.day, time.hours, time.minutes, time.seconds.floor() as i64) as i64 - zone_offset;
    let abbreviation = if is_local { interpreter.clock().and_then(|clock| clock.abbreviation(instant)) } else { None };
    let zone = Zone { local: is_local, offset: zone_offset, abbreviation };
    Ok(JqValue::from_string(strftime::try_format(&time, epoch, &zone, format)?))
}

fn parse(_: &Interpreter<'_>, input: Input<'_>, arguments: &mut [JqValue]) -> Fallible {
    let (JqValue::String(text), JqValue::String(format)) = (&*input, &arguments[0]) else {
        return Err(errors::strptime_strings());
    };
    match strftime::try_parse(text, format) {
        Some(fields) => Ok(JqValue::Array(JqArray::from_owned(fields.iter().map(|field| JqValue::Number(*field as f64)).collect()))),
        None => Err(errors::date_does_not_match(text, format)),
    }
}

fn to_value(time: &Fields) -> JqValue {
    JqValue::Array(JqArray::from_owned(vec![
        JqValue::Number(time.year as f64),
        JqValue::Number(time.month as f64),
        JqValue::Number(time.day as f64),
        JqValue::Number(time.hours as f64),
        JqValue::Number(time.minutes as f64),
        JqValue::Number(time.seconds),
        JqValue::Number(time.weekday as f64),
        JqValue::Number(time.year_day as f64),
    ]))
}
