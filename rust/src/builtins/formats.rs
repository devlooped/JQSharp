//! The `@name` formats of §9.9.

use crate::values::value_ops::Fallible;
use crate::values::{JqArray, JqValue, errors, utf8, value_text};

const HEX: &[u8; 16] = b"0123456789ABCDEF";
const BASE64_ALPHABET: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

// `tostring`: a string as it is, any other value as its canonical text.
fn to_text(value: &JqValue) -> std::borrow::Cow<'_, str> {
    match value {
        JqValue::String(text) => std::borrow::Cow::Borrowed(text.as_str()),
        other => std::borrow::Cow::Owned(value_text::to_json(other)),
    }
}

/// Applies the format `name` (without '@') to `value`, giving a string.
pub fn try_apply(name: &str, value: &JqValue) -> Fallible {
    match name {
        "text" => Ok(match value {
            JqValue::String(_) => value.clone(),
            other => JqValue::from_string(value_text::to_json(other)),
        }),
        "json" => Ok(JqValue::from_string(value_text::to_json(value))),
        "html" => Ok(JqValue::from_string(html(&to_text(value)))),
        "uri" => Ok(JqValue::from_string(uri(&to_text(value)))),
        "urid" => uri_decode(&to_text(value)),
        "csv" => match value {
            JqValue::Array(items) => row(items, false),
            other => Err(errors::cannot_be_csv_formatted(other)),
        },
        "tsv" => match value {
            JqValue::Array(items) => row(items, true),
            other => Err(errors::cannot_be_tsv_formatted(other)),
        },
        "sh" => match value {
            JqValue::Array(items) => shell(items.as_slice()),
            other => shell(std::slice::from_ref(other)),
        },
        "base64" => Ok(JqValue::from_string(base64(&to_text(value)))),
        "base64d" => base64_decode(&to_text(value)),
        _ => Err(errors::invalid_format(name)),
    }
}

// @html: & < > ' " as entities.
fn html(text: &str) -> String {
    let mut builder = String::with_capacity(text.len());
    for c in text.chars() {
        match c {
            '&' => builder.push_str("&amp;"),
            '<' => builder.push_str("&lt;"),
            '>' => builder.push_str("&gt;"),
            '\'' => builder.push_str("&apos;"),
            '"' => builder.push_str("&quot;"),
            _ => builder.push(c),
        }
    }

    builder
}

// @uri: the UTF-8 bytes, unreserved ones (A-Z a-z 0-9 - _ . ~) kept, the others as %XX.
fn uri(text: &str) -> String {
    let mut bytes = Vec::with_capacity(text.len());
    utf8::encode(text, &mut bytes);
    let mut builder = String::with_capacity(bytes.len());
    for b in bytes {
        if b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_' | b'.' | b'~') {
            builder.push(b as char);
        } else {
            builder.push('%');
            builder.push(HEX[(b >> 4) as usize] as char);
            builder.push(HEX[(b & 0xF) as usize] as char);
        }
    }

    builder
}

// @urid: %XX (either case) is that byte, any other code point its UTF-8 bytes; the bytes must be well-formed UTF-8.
fn uri_decode(text: &str) -> Fallible {
    let source = text.as_bytes();
    let mut bytes = Vec::with_capacity(source.len());
    let mut i = 0;
    while i < source.len() {
        let c = source[i];
        if c == b'%' {
            let high = source.get(i + 1).and_then(|byte| hex_value(*byte));
            let low = source.get(i + 2).and_then(|byte| hex_value(*byte));
            let (Some(high), Some(low)) = (high, low) else {
                return Err(errors::not_valid_uri_encoding(&JqValue::from_text(text)));
            };
            bytes.push((high << 4) | low);
            i += 3;
            continue;
        }

        // Any other code point: its UTF-8 bytes, which are the text's own.
        bytes.push(c);
        i += 1;
    }

    match utf8::try_decode(bytes) {
        Some(decoded) => Ok(JqValue::from_string(decoded)),
        None => Err(errors::not_valid_uri_encoding(&JqValue::from_text(text))),
    }
}

fn hex_value(c: u8) -> Option<u8> {
    match c {
        b'0'..=b'9' => Some(c - b'0'),
        b'a'..=b'f' => Some(c - b'a' + 10),
        b'A'..=b'F' => Some(c - b'A' + 10),
        _ => None,
    }
}

// @csv and @tsv: one row; strings quoted (csv) or escaped (tsv), numbers as canonical text
// (NaN empty), booleans as such, null empty.
fn row(items: &JqArray, tsv: bool) -> Fallible {
    let mut builder = String::new();
    for (i, item) in items.iter().enumerate() {
        if i > 0 {
            builder.push(if tsv { '\t' } else { ',' });
        }
        match item {
            JqValue::Null => {}
            JqValue::False => builder.push_str("false"),
            JqValue::True => builder.push_str("true"),
            JqValue::Number(number) => {
                if !number.is_nan() {
                    value_text::write_number(&mut builder, *number);
                }
            }
            JqValue::String(text) if tsv => tsv_escape(&mut builder, text),
            JqValue::String(text) => {
                builder.push('"');
                for c in text.chars() {
                    if c == '"' {
                        builder.push('"');
                    }
                    builder.push(c);
                }
                builder.push('"');
            }
            other => return Err(errors::not_valid_in_csv_row(other)),
        }
    }

    Ok(JqValue::from_string(builder))
}

fn tsv_escape(builder: &mut String, text: &str) {
    for c in text.chars() {
        match c {
            '\\' => builder.push_str("\\\\"),
            '\t' => builder.push_str("\\t"),
            '\n' => builder.push_str("\\n"),
            '\r' => builder.push_str("\\r"),
            _ => builder.push(c),
        }
    }
}

// @sh: space-separated words; strings in single quotes (' as '\''), other scalars as canonical text.
fn shell(items: &[JqValue]) -> Fallible {
    let mut builder = String::new();
    for (i, item) in items.iter().enumerate() {
        if i > 0 {
            builder.push(' ');
        }
        match item {
            JqValue::Array(_) | JqValue::Object(_) => return Err(errors::cannot_be_escaped_for_shell(item)),
            JqValue::String(text) => {
                builder.push('\'');
                for c in text.chars() {
                    if c == '\'' {
                        builder.push_str("'\\''");
                    } else {
                        builder.push(c);
                    }
                }
                builder.push('\'');
            }
            other => value_text::write(&mut builder, other),
        }
    }

    Ok(JqValue::from_string(builder))
}

// @base64: RFC 4648 of the UTF-8 bytes, with '=' padding.
fn base64(text: &str) -> String {
    let mut bytes = Vec::with_capacity(text.len());
    utf8::encode(text, &mut bytes);
    let mut builder = String::with_capacity(bytes.len().div_ceil(3) * 4);
    for chunk in bytes.chunks(3) {
        let left = chunk.len();
        let group = ((chunk[0] as u32) << 16) | (if left > 1 { (chunk[1] as u32) << 8 } else { 0 }) | (if left > 2 { chunk[2] as u32 } else { 0 });
        builder.push(BASE64_ALPHABET[(group >> 18) as usize] as char);
        builder.push(BASE64_ALPHABET[((group >> 12) & 0x3F) as usize] as char);
        builder.push(if left > 1 { BASE64_ALPHABET[((group >> 6) & 0x3F) as usize] as char } else { '=' });
        builder.push(if left > 2 { BASE64_ALPHABET[(group & 0x3F) as usize] as char } else { '=' });
    }

    builder
}

// @base64d: the characters before the first '=' must be in the alphabet; a final group of 2 or 3
// characters gives 1 or 2 bytes, of 1 is an error; the bytes are decoded as UTF-8 with replacement.
fn base64_decode(text: &str) -> Fallible {
    let mut bytes = Vec::with_capacity(text.len() * 3 / 4);
    let mut group: u32 = 0;
    let mut count = 0;
    for c in text.chars() {
        if c == '=' {
            break;
        }
        let Some(digit) = base64_digit(c) else {
            return Err(errors::not_valid_base64(&JqValue::from_text(text)));
        };
        group = (group << 6) | digit;
        count += 1;
        if count == 4 {
            bytes.push((group >> 16) as u8);
            bytes.push((group >> 8) as u8);
            bytes.push(group as u8);
            group = 0;
            count = 0;
        }
    }

    match count {
        1 => return Err(errors::trailing_base64_byte(&JqValue::from_text(text))),
        2 => bytes.push((group >> 4) as u8),
        3 => {
            bytes.push((group >> 10) as u8);
            bytes.push((group >> 2) as u8);
        }
        _ => {}
    }

    Ok(JqValue::from_string(utf8::decode_replacing(bytes)))
}

fn base64_digit(c: char) -> Option<u32> {
    match c {
        'A'..='Z' => Some(c as u32 - 'A' as u32),
        'a'..='z' => Some(c as u32 - 'a' as u32 + 26),
        '0'..='9' => Some(c as u32 - '0' as u32 + 52),
        '+' => Some(62),
        '/' => Some(63),
        _ => None,
    }
}
