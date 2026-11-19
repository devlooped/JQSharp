//! Strings by Unicode code point. jqlite strings are sequences of code points: length, slices,
//! ordering, explode and implode count code points, never bytes. A Rust `str` holds them as
//! well-formed UTF-8, so nothing here has a lone surrogate to replace: text is sanitized where
//! bytes or escapes are decoded (`utf8`, the lexer, the JSON reader).

use std::cmp::Ordering;

use super::jq_array::JqArray;
use super::jq_value::JqValue;

pub const MAX: u32 = 0x10FFFF;
pub const REPLACEMENT: char = '\u{FFFD}';

/// The number of code points of a string.
pub fn length(text: &str) -> usize {
    if text.is_ascii() {
        return text.len();
    }
    // Every byte but the continuation bytes (10xxxxxx) starts a code point.
    text.bytes().filter(|byte| (byte & 0xC0) != 0x80).count()
}

/// The byte offset of the code point at `index` (0 <= index <= length), counting from the byte
/// offset `from`.
pub fn offset(text: &str, from: usize, index: usize) -> usize {
    if index == 0 {
        return from;
    }
    match text[from..].char_indices().nth(index) {
        Some((at, _)) => from + at,
        None => text.len(),
    }
}

/// The code points `start` (included) to `end` (excluded); 0 <= start <= end <= length.
pub fn substring(text: &str, start: usize, end: usize) -> &str {
    if text.is_ascii() {
        return &text[start..end];
    }
    let from = offset(text, 0, start);
    let to = offset(text, from, end - start);
    &text[from..to]
}

/// Orders two strings by code point, which is the order of their UTF-8 bytes.
pub fn compare(a: &str, b: &str) -> Ordering {
    a.as_bytes().cmp(b.as_bytes())
}

/// The code points of a string, as numbers.
pub fn explode(text: &str) -> JqArray {
    JqArray::from_owned(text.chars().map(|c| JqValue::Number(c as u32 as f64)).collect())
}

/// Whether `code_point` is a Unicode scalar value (a code point that is not a surrogate).
pub fn is_scalar(code_point: i64) -> bool {
    (0..=MAX as i64).contains(&code_point) && !(0xD800..=0xDFFF).contains(&code_point)
}

/// Appends a code point; one that is not a Unicode scalar value becomes U+FFFD.
pub fn append(builder: &mut String, code_point: u32) {
    builder.push(char::from_u32(code_point).unwrap_or(REPLACEMENT));
}
