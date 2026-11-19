//! §9.6: text conversion and the string natives the prelude builds on.

use std::sync::Arc;

use crate::runtime::interpreter::{Input, Interpreter};
use crate::values::value_ops::Fallible;
use crate::values::{JqArray, JqString, JqValue, errors, value_ops, value_text};

use super::formats;
use super::native_table::Registry;

pub fn register(table: &mut Registry) {
    table.value("tostring", 0, |_, input, _| Ok(JqValue::String(to_text(input.into_owned()))));
    table.value("tojson", 0, |_, input, _| Ok(JqValue::from_string(value_text::to_json(&input))));
    table.value("fromjson", 0, |_, input, _| match &*input {
        JqValue::String(text) => value_text::try_parse(text),
        other => Err(errors::only_strings_parse(other)),
    });
    table.value("tonumber", 0, to_number);
    table.value("toboolean", 0, to_boolean);
    table.value("format", 1, |_, input, arguments| match &arguments[0] {
        JqValue::String(name) => formats::try_apply(name, &input),
        other => Err(errors::not_a_valid_format(other)),
    });
    table.value("explode", 0, |_, input, _| value_ops::try_explode(&input));
    table.value("implode", 0, |_, input, _| value_ops::try_implode(&input));
    table.value("ascii_downcase", 0, |_, input, _| ascii_case(input, false));
    table.value("ascii_upcase", 0, |_, input, _| ascii_case(input, true));
    table.value("startswith", 1, |_, input, arguments| match (&*input, &arguments[0]) {
        (JqValue::String(text), JqValue::String(prefix)) => Ok(JqValue::from_bool(text.starts_with(prefix.as_str()))),
        _ => Err(errors::starts_with_strings()),
    });
    table.value("endswith", 1, |_, input, arguments| match (&*input, &arguments[0]) {
        (JqValue::String(text), JqValue::String(suffix)) => Ok(JqValue::from_bool(text.ends_with(suffix.as_str()))),
        _ => Err(errors::ends_with_strings()),
    });
    table.value("trim", 0, |_, input, _| trim(input, true, true));
    table.value("ltrim", 0, |_, input, _| trim(input, true, false));
    table.value("rtrim", 0, |_, input, _| trim(input, false, true));
    table.value("split", 1, |_, input, arguments| match (&*input, &arguments[0]) {
        (JqValue::String(text), JqValue::String(separator)) => Ok(JqValue::Array(value_ops::split(text, separator))),
        _ => Err(errors::split_strings()),
    });
    table.value("_strindices", 1, string_indices);
}

/// `tostring`: a string as it is, any other value as its canonical text.
pub fn to_text(value: JqValue) -> JqString {
    match value {
        JqValue::String(text) => text,
        other => Arc::new(value_text::to_json(&other)),
    }
}

fn to_number(_: &Interpreter<'_>, input: Input<'_>, _: &mut [JqValue]) -> Fallible {
    match &*input {
        JqValue::Number(number) => Ok(JqValue::Number(*number)),
        JqValue::String(text) => match value_text::try_parse_number(text) {
            Some(number) => Ok(JqValue::Number(number)),
            None => Err(errors::cannot_parse_number(&input)),
        },
        other => Err(errors::cannot_parse_number(other)),
    }
}

fn to_boolean(_: &Interpreter<'_>, input: Input<'_>, _: &mut [JqValue]) -> Fallible {
    match &*input {
        JqValue::True => Ok(JqValue::True),
        JqValue::False => Ok(JqValue::False),
        JqValue::String(text) if text.as_str() == "true" => Ok(JqValue::True),
        JqValue::String(text) if text.as_str() == "false" => Ok(JqValue::False),
        other => Err(errors::cannot_parse_boolean(other)),
    }
}

// trim, ltrim, rtrim: the Unicode White_Space code points.
fn trim(input: Input<'_>, start: bool, end: bool) -> Fallible {
    let JqValue::String(text) = &*input else {
        return Err(errors::trim_not_string());
    };
    let mut trimmed = text.as_str();
    if start {
        trimmed = trimmed.trim_start_matches(is_white_space);
    }
    if end {
        trimmed = trimmed.trim_end_matches(is_white_space);
    }
    if trimmed.len() == text.len() {
        return Ok(input.into_owned());
    }
    Ok(JqValue::from_text(trimmed))
}

fn is_white_space(c: char) -> bool {
    matches!(c, '\u{9}'..='\u{D}' | '\u{20}' | '\u{85}' | '\u{A0}' | '\u{1680}' | '\u{2000}'..='\u{200A}' | '\u{2028}' | '\u{2029}' | '\u{202F}' | '\u{205F}' | '\u{3000}')
}

fn ascii_case(input: Input<'_>, upper: bool) -> Fallible {
    match input.into_owned() {
        JqValue::String(mut text) => {
            // In place when the string is not shared; only the ASCII letters change.
            let changes = text.bytes().any(|byte| if upper { byte.is_ascii_lowercase() } else { byte.is_ascii_uppercase() });
            if changes {
                let owned = Arc::make_mut(&mut text);
                if upper {
                    owned.make_ascii_uppercase();
                } else {
                    owned.make_ascii_lowercase();
                }
            }
            Ok(JqValue::String(text))
        }
        _ => Err(errors::explode_not_string()),
    }
}

// _strindices(needle): the code point offsets of every occurrence, overlapping ones included.
fn string_indices(_: &Interpreter<'_>, input: Input<'_>, arguments: &mut [JqValue]) -> Fallible {
    let JqValue::String(text) = &*input else {
        return Err(errors::cannot_search_not_string(&input));
    };
    let JqValue::String(needle) = &arguments[0] else {
        return Err(errors::not_a_string(&arguments[0]));
    };

    let mut offsets = Vec::new();
    if !needle.is_empty() {
        let bytes = text.as_bytes();
        let pattern = needle.as_bytes();
        // Every occurrence starts on a code point boundary, since the needle is well-formed.
        for (code_point, (at, _)) in text.char_indices().enumerate() {
            if at + pattern.len() > bytes.len() {
                break;
            }
            if &bytes[at..at + pattern.len()] == pattern {
                offsets.push(JqValue::Number(code_point as f64));
            }
        }
    }

    Ok(JqValue::Array(JqArray::from_owned(offsets)))
}
