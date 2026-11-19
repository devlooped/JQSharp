//! The one JSON writer, number formatter and JSON reader of jqlite.
//!
//! Canonical text is compact JSON. Strings escape only `"`, `\`, the control characters below
//! U+0020 (`\b \f \n \r \t`, the others `\u00xx`) and U+007F, as jq does. Numbers are the shortest
//! digits that read back to the same double, laid out as ECMAScript's `Number::toString`; NaN is
//! `null` and ±Infinity are ±`1.7976931348623157e+308`.
//!
//! Host-specific: two steps use the host. The shortest round-trip digits of a double come from
//! `{:e}` formatting (`format_number`), and the conversion of numeric text that this file has
//! already checked comes from `str::parse::<f64>` (`try_parse_number`). Both are independent of
//! any locale in Rust.
//!
//! Rust: creating a string is two allocations, and the keys of a document repeat from one object
//! to the next, so the reader gives equal keys one shared string (`shared_key`: a small table per
//! thread, of the short keys read last). The reference allocates each key.

use std::borrow::Cow;
use std::cell::RefCell;
use std::fmt::Write;
use std::sync::Arc;

use super::errors;
use super::jq_array::JqArray;
use super::jq_object::JqObject;
use super::jq_value::{JqString, JqValue};

const MAX_DOUBLE: &str = "1.7976931348623157e+308";

/// The deepest level the writer writes (§5.1, §11).
pub const MAX_WRITE_DEPTH: usize = 10001;

const TOO_DEEP: &str = "<skipped: too deep>";

pub fn to_json(value: &JqValue) -> String {
    // A container starts with room for a small one, instead of growing from nothing.
    let mut builder = if value.is_container() { String::with_capacity(128) } else { String::new() };
    write(&mut builder, value);
    builder
}

/// Writes the canonical text of `value`. No recursion: the containers being written are on an
/// explicit stack, so nesting depth is not limited by the host stack. A value nested deeper than
/// [`MAX_WRITE_DEPTH`] levels (the outermost value is level 1) is written as
/// `<skipped: too deep>`, as jq 1.8.2 does for scalars and containers alike.
pub fn write(builder: &mut String, value: &JqValue) {
    if !write_opening(builder, value) {
        return;
    }

    let mut stack: Vec<(&JqValue, usize)> = vec![(value, 0)];
    let mut current = element(builder, value, 0);
    loop {
        // stack.len() is the number of containers around current: its level minus one.
        if stack.len() >= MAX_WRITE_DEPTH {
            builder.push_str(TOO_DEEP);
        } else if write_opening(builder, current) {
            // A container with elements: write its first one next.
            stack.push((current, 0));
            current = element(builder, current, 0);
            continue;
        }

        // current is written: go on with the next element, closing the containers that end.
        loop {
            let Some((container, index)) = stack.last_mut() else {
                return;
            };
            let container: &JqValue = container;
            *index += 1;
            if *index < count(container) {
                builder.push(',');
                current = element(builder, container, *index);
                break;
            }

            builder.push(if matches!(container, JqValue::Array(_)) { ']' } else { '}' });
            stack.pop();
        }
    }
}

// Writes a scalar or an empty container whole and returns false; writes the opening bracket of a
// container with elements and returns true.
fn write_opening(builder: &mut String, value: &JqValue) -> bool {
    match value {
        JqValue::Null => builder.push_str("null"),
        JqValue::False => builder.push_str("false"),
        JqValue::True => builder.push_str("true"),
        JqValue::Number(number) => write_number(builder, *number),
        JqValue::String(text) => write_string(builder, text),
        JqValue::Array(array) => {
            builder.push('[');
            if !array.is_empty() {
                return true;
            }
            builder.push(']');
        }
        JqValue::Object(object) => {
            builder.push('{');
            if !object.is_empty() {
                return true;
            }
            builder.push('}');
        }
    }

    false
}

fn count(container: &JqValue) -> usize {
    match container {
        JqValue::Array(array) => array.count(),
        JqValue::Object(object) => object.count(),
        _ => 0,
    }
}

// The element at index; for an object, writes its key and colon first.
fn element<'a>(builder: &mut String, container: &'a JqValue, index: usize) -> &'a JqValue {
    match container {
        JqValue::Array(array) => array.get(index),
        _ => {
            let object = container.as_object();
            write_string(builder, object.key_at(index));
            builder.push(':');
            object.value_at(index)
        }
    }
}

/// Writes `text` as a JSON string literal.
pub fn write_string(builder: &mut String, text: &str) {
    builder.push('"');
    let mut run = 0;
    // Every character to escape is ASCII, so cutting the text at one keeps it well-formed.
    for (i, c) in text.bytes().enumerate() {
        if c >= 0x20 && c != b'"' && c != b'\\' && c != 0x7F {
            continue;
        }

        builder.push_str(&text[run..i]);
        run = i + 1;
        match c {
            b'"' => builder.push_str("\\\""),
            b'\\' => builder.push_str("\\\\"),
            0x08 => builder.push_str("\\b"),
            0x0C => builder.push_str("\\f"),
            b'\n' => builder.push_str("\\n"),
            b'\r' => builder.push_str("\\r"),
            b'\t' => builder.push_str("\\t"),
            _ => {
                let _ = write!(builder, "\\u{c:04x}");
            }
        }
    }

    builder.push_str(&text[run..]);
    builder.push('"');
}

/// The JSON text of `value`; when longer than `max_bytes` UTF-8 bytes, its longest prefix of at
/// most `keep_bytes` bytes that ends on a code point boundary, then "..." and the closing
/// delimiter of the value (`"`, `]` or `}`), as jq 1.8.2 writes them: `"aaa..."`, `[1,2,...]`,
/// `{"a":...}`.
pub fn to_json_truncated(value: &JqValue, max_bytes: usize, keep_bytes: usize) -> String {
    let mut text = to_json(value);
    if text.len() <= max_bytes {
        return text;
    }

    let mut end = keep_bytes;
    while !text.is_char_boundary(end) {
        end -= 1;
    }
    text.truncate(end);
    text.push_str(match value {
        JqValue::String(_) => "...\"",
        JqValue::Array(_) => "...]",
        _ => "...}",
    });
    text
}

/// The canonical text of a number: shortest round-trip digits in ECMAScript `Number::toString`
/// layout.
pub fn format_number(number: f64) -> String {
    let mut builder = String::new();
    write_number(&mut builder, number);
    builder
}

// The text of a number in the host's exponent form: at most 17 digits, a point, 'e', a sign and
// three digits.
struct Digits {
    bytes: [u8; 32],
    length: usize,
}

impl Write for Digits {
    fn write_str(&mut self, text: &str) -> std::fmt::Result {
        let end = self.length + text.len();
        if end > self.bytes.len() {
            return Err(std::fmt::Error);
        }
        self.bytes[self.length..end].copy_from_slice(text.as_bytes());
        self.length = end;
        Ok(())
    }
}

/// Appends the canonical text of a number.
pub fn write_number(builder: &mut String, number: f64) {
    if number.is_nan() {
        builder.push_str("null");
        return;
    }
    if number.is_infinite() {
        if number < 0.0 {
            builder.push('-');
        }
        builder.push_str(MAX_DOUBLE);
        return;
    }
    if number == 0.0 {
        builder.push_str(if number.is_sign_negative() { "-0" } else { "0" });
        return;
    }
    // Fast path: integers below 1e15 print as themselves.
    if number.abs() < 1e15 && number == number.trunc() {
        let _ = write!(builder, "{}", number as i64);
        return;
    }

    // Rust's {:e} gives the shortest round-trip digits, as d[.ddd]e[-]x; only the layout changes below.
    let mut text = Digits { bytes: [0; 32], length: 0 };
    let _ = write!(text, "{:e}", number.abs());
    let text = &text.bytes[..text.length];
    let e = text.iter().position(|byte| *byte == b'e').unwrap_or(text.len());
    let mut exponent: i32 = 0;
    let mut negative_exponent = false;
    for byte in &text[(e + 1).min(text.len())..] {
        if *byte == b'-' {
            negative_exponent = true;
        } else {
            exponent = exponent * 10 + (*byte - b'0') as i32;
        }
    }
    if negative_exponent {
        exponent = -exponent;
    }

    let mut digits = [0u8; 20];
    let mut k = 0;
    for byte in &text[..e] {
        if *byte != b'.' {
            digits[k] = *byte;
            k += 1;
        }
    }
    let digits = std::str::from_utf8(&digits[..k]).unwrap_or("0");
    // n: the position of the decimal point relative to the first digit (value = 0.digits * 10^n).
    let n = exponent + 1;
    let k = k as i32;

    if number < 0.0 {
        builder.push('-');
    }
    if k <= n && n <= 21 {
        builder.push_str(digits);
        for _ in 0..n - k {
            builder.push('0');
        }
    } else if 0 < n && n <= 21 {
        builder.push_str(&digits[..n as usize]);
        builder.push('.');
        builder.push_str(&digits[n as usize..]);
    } else if -6 < n && n <= 0 {
        builder.push_str("0.");
        for _ in 0..-n {
            builder.push('0');
        }
        builder.push_str(digits);
    } else {
        builder.push_str(&digits[..1]);
        if k > 1 {
            builder.push('.');
            builder.push_str(&digits[1..]);
        }
        builder.push('e');
        builder.push(if n - 1 < 0 { '-' } else { '+' });
        let _ = write!(builder, "{}", (n - 1).abs());
    }
}

/// Reads the numeric text of §5.3 (`tonumber`, and the numbers of JSON text): an optional sign,
/// then digits with an optional fraction (`1.` and `.5` included) and exponent, or, in any ASCII
/// case, `nan` or `snan` (NaN), `inf` or `infinity`. No white space.
pub fn try_parse_number(text: &str) -> Option<f64> {
    let bytes = text.as_bytes();
    let mut i = 0;
    let mut negative = false;
    if i < bytes.len() && (bytes[i] == b'+' || bytes[i] == b'-') {
        negative = bytes[i] == b'-';
        i += 1;
    }

    let word = &text[i..];
    if word.eq_ignore_ascii_case("nan") || word.eq_ignore_ascii_case("snan") {
        return Some(f64::NAN);
    }
    if word.eq_ignore_ascii_case("inf") || word.eq_ignore_ascii_case("infinity") {
        return Some(if negative { f64::NEG_INFINITY } else { f64::INFINITY });
    }

    let mut digits = 0;
    while i < bytes.len() && bytes[i].is_ascii_digit() {
        digits += 1;
        i += 1;
    }
    if i < bytes.len() && bytes[i] == b'.' {
        i += 1;
        while i < bytes.len() && bytes[i].is_ascii_digit() {
            digits += 1;
            i += 1;
        }
    }
    if digits == 0 {
        return None;
    }

    if i < bytes.len() && (bytes[i] == b'e' || bytes[i] == b'E') {
        i += 1;
        if i < bytes.len() && (bytes[i] == b'+' || bytes[i] == b'-') {
            i += 1;
        }
        let mut exponent_digits = 0;
        while i < bytes.len() && bytes[i].is_ascii_digit() {
            exponent_digits += 1;
            i += 1;
        }
        if exponent_digits == 0 {
            return None;
        }
    }

    if i != bytes.len() {
        return None;
    }
    // The grammar is checked above; the host parser only converts (out of range gives ±Infinity or 0).
    text.parse::<f64>().ok()
}

/// Reads exactly one JSON value from `text` (§5.3; jq's `fromjson`). On failure the error is the
/// whole message, `<reason> (while parsing '<text>')`, the reason with the line and column where
/// reading stopped (except for `Expected JSON value` and `Unexpected extra JSON values`, as in jq).
pub fn try_parse(text: &str) -> Result<JqValue, JqValue> {
    JsonReader::new(text, false).read()
}

/// Reads a sequence of JSON values separated by white space (a data module, §9.11) as one array;
/// no value at all is the empty array. Errors as [`try_parse`].
pub fn try_parse_sequence(text: &str) -> Result<JqValue, JqValue> {
    JsonReader::new(text, true).read()
}

const MAX_DEPTH: usize = 10000;

// A container being read: its elements or entries are the ones from `start` in the reader's
// `items` or `entries`, so that the container is created once, with its size.
struct Frame {
    is_object: bool,
    start: usize,
    // The key whose value is being read.
    key: Option<JqString>,
}

// The keys of at most this many bytes are shared.
const SHARED_KEY_BYTES: usize = 32;
const SHARED_KEYS: usize = 256;

thread_local! {
    // The short keys read last on this thread, each at the slot of its hash.
    static KEYS: RefCell<Vec<Option<JqString>>> = const { RefCell::new(Vec::new()) };
}

// The string of a key: the one given to the last equal key read on this thread, if it is still
// in the table.
fn shared_key(text: &str) -> JqString {
    if text.len() > SHARED_KEY_BYTES {
        return Arc::new(text.to_owned());
    }

    // FNV-1a.
    let mut hash: u32 = 2166136261;
    for byte in text.bytes() {
        hash = (hash ^ byte as u32).wrapping_mul(16777619);
    }
    KEYS.with_borrow_mut(|keys| {
        if keys.is_empty() {
            keys.resize(SHARED_KEYS, None);
        }
        let slot = &mut keys[hash as usize % SHARED_KEYS];
        match slot {
            Some(key) if key.as_str() == text => key.clone(),
            _ => {
                let key = Arc::new(text.to_owned());
                *slot = Some(key.clone());
                key
            }
        }
    })
}

/// A JSON reader without recursion (an explicit stack), so deep input cannot exhaust the host
/// stack. Like jq, a literal is any run of characters up to white space, a quote or one of
/// `[]{},:`; a raw control character inside a string is an error, and every lone surrogate escape
/// becomes U+FFFD.
struct JsonReader<'a> {
    text: &'a str,
    bytes: &'a [u8],
    pos: usize,
    // The values read so far, when reading a sequence.
    values: Option<Vec<JqValue>>,
    // The elements of the arrays being read, and the entries of the objects being read.
    items: Vec<JqValue>,
    entries: Vec<(JqString, JqValue)>,
}

impl<'a> JsonReader<'a> {
    fn new(text: &'a str, sequence: bool) -> JsonReader<'a> {
        JsonReader { text, bytes: text.as_bytes(), pos: 0, values: if sequence { Some(Vec::new()) } else { None }, items: Vec::new(), entries: Vec::new() }
    }

    // The container of a frame that ends, or of an empty one.
    fn close(&mut self, frame: Frame) -> JqValue {
        if frame.is_object {
            JqValue::Object(JqObject::from_entries(self.entries.drain(frame.start..).collect()))
        } else {
            JqValue::Array(JqArray::from_owned(self.items.drain(frame.start..).collect()))
        }
    }

    fn read(mut self) -> Result<JqValue, JqValue> {
        if self.text.starts_with('\u{FEFF}') {
            // A byte order mark.
            self.pos = '\u{FEFF}'.len_utf8();
        }
        self.skip_space();
        if self.pos == self.bytes.len() {
            return match self.values.take() {
                Some(_) => Ok(JqValue::Array(JqArray::empty())),
                None => Err(self.fail_without_position(errors::EXPECTED_VALUE)),
            };
        }

        let mut stack: Vec<Frame> = Vec::new();
        loop {
            // A value starts at pos (white space skipped).
            if self.pos == self.bytes.len() {
                return Err(self.fail_at_end(errors::UNFINISHED_AT_EOF));
            }

            let mut current;
            let c = self.bytes[self.pos];
            if c == b'[' || c == b'{' {
                if stack.len() == MAX_DEPTH {
                    return Err(self.fail(errors::DEPTH_LIMIT, self.pos + 1));
                }
                self.pos += 1;
                self.skip_space();
                let is_object = c == b'{';
                let mut frame = Frame { is_object, start: if is_object { self.entries.len() } else { self.items.len() }, key: None };
                if self.pos < self.bytes.len() && self.bytes[self.pos] == (if is_object { b'}' } else { b']' }) {
                    self.pos += 1;
                    current = self.close(frame);
                } else {
                    if frame.is_object {
                        self.read_key(&mut frame)?;
                    }
                    stack.push(frame);
                    continue;
                }
            } else if c == b'"' {
                self.pos += 1;
                current = JqValue::from_string(self.read_string()?.into_owned());
            } else if c == b']' || c == b'}' {
                return Err(match stack.last() {
                    None => self.fail(&errors::unmatched(c as char), self.pos + 1),
                    Some(top) => self.fail(if top.is_object { errors::KEY_VALUE_PAIRS } else { errors::EXPECTED_ARRAY_ELEMENT }, self.pos + 1),
                });
            } else if c == b',' || c == b':' {
                return Err(self.fail(&errors::expected_value_before(c as char), self.pos + 1));
            } else {
                current = self.read_literal()?;
            }

            // Attach the value to its container; close containers as they end.
            loop {
                self.skip_space();
                let Some(top) = stack.last_mut() else {
                    match &mut self.values {
                        Some(values) => {
                            // A sequence goes on with the next value, if any.
                            values.push(current);
                            if self.pos < self.bytes.len() {
                                break;
                            }
                            let values = std::mem::take(values);
                            return Ok(JqValue::Array(JqArray::from_owned(values)));
                        }
                        None => {
                            if self.pos != self.bytes.len() {
                                return Err(self.fail_without_position(errors::EXTRA_VALUES));
                            }
                            return Ok(current);
                        }
                    }
                };

                match top.key.take() {
                    Some(key) => self.entries.push((key, current)),
                    None => self.items.push(current),
                }
                if self.pos == self.bytes.len() {
                    return Err(self.fail_at_end(errors::UNFINISHED_AT_EOF));
                }

                let d = self.bytes[self.pos];
                self.pos += 1;
                if d == (if top.is_object { b'}' } else { b']' }) {
                    let frame = stack.pop().expect("a container is open");
                    current = self.close(frame);
                    continue;
                }

                if d != b',' {
                    let reason = if d == b']' || d == b'}' { errors::unmatched(d as char) } else { errors::EXPECTED_SEPARATOR.to_owned() };
                    return Err(self.fail(&reason, self.pos));
                }

                self.skip_space();
                if top.is_object {
                    self.read_key(top)?;
                }
                break;
            }
        }
    }

    // Reads `"key" :` and the white space after it.
    fn read_key(&mut self, frame: &mut Frame) -> Result<(), JqValue> {
        if self.pos == self.bytes.len() {
            return Err(self.fail_at_end(errors::UNFINISHED_AT_EOF));
        }
        if self.bytes[self.pos] != b'"' {
            return Err(self.fail(errors::KEYS_MUST_BE_STRINGS, self.pos + 1));
        }
        self.pos += 1;
        let key = shared_key(&self.read_string()?);
        self.skip_space();
        if self.pos == self.bytes.len() {
            return Err(self.fail_at_end(errors::UNFINISHED_AT_EOF));
        }
        if self.bytes[self.pos] != b':' {
            return Err(self.fail(errors::KEY_VALUE_PAIRS, self.pos + 1));
        }
        self.pos += 1;
        self.skip_space();
        frame.key = Some(key);
        Ok(())
    }

    // Reads the rest of a string after its opening quote. A high surrogate escape followed by a
    // low surrogate escape is one code point; any other surrogate escape is U+FFFD. A string
    // without escapes is its text in the input.
    fn read_string(&mut self) -> Result<Cow<'a, str>, JqValue> {
        let text = self.text;
        let mut builder = String::new();
        let mut escaped = false;
        let mut run = self.pos;
        loop {
            if self.pos == self.bytes.len() {
                return Err(self.fail_at_end(errors::UNFINISHED_AT_EOF));
            }
            let c = self.bytes[self.pos];
            if c == b'"' {
                let last = &text[run..self.pos];
                self.pos += 1;
                if !escaped {
                    return Ok(Cow::Borrowed(last));
                }
                builder.push_str(last);
                return Ok(Cow::Owned(builder));
            }

            if c < 0x20 {
                return Err(self.fail(errors::CONTROL_CHARACTER, self.pos + 1));
            }

            if c != b'\\' {
                self.pos += 1;
                continue;
            }

            escaped = true;
            builder.push_str(&text[run..self.pos]);
            self.pos += 1;
            if self.pos == self.bytes.len() {
                return Err(self.fail_at_end(errors::UNFINISHED_AT_EOF));
            }
            let e = self.bytes[self.pos];
            self.pos += 1;
            match e {
                b'"' | b'\\' | b'/' => builder.push(e as char),
                b'b' => builder.push('\u{8}'),
                b'f' => builder.push('\u{c}'),
                b'n' => builder.push('\n'),
                b'r' => builder.push('\r'),
                b't' => builder.push('\t'),
                b'u' => {
                    let unit = self.read_hex()?;
                    if (0xD800..=0xDBFF).contains(&unit) {
                        match self.peek_low_surrogate() {
                            Some(low) => {
                                self.pos += 6;
                                let code_point = 0x10000 + ((unit - 0xD800) << 10) + (low - 0xDC00);
                                builder.push(char::from_u32(code_point).unwrap_or('\u{FFFD}'));
                            }
                            None => builder.push('\u{FFFD}'),
                        }
                    } else {
                        builder.push(char::from_u32(unit).unwrap_or('\u{FFFD}'));
                    }
                }
                _ => return Err(self.fail(errors::INVALID_ESCAPE, self.pos)),
            }

            run = self.pos;
        }
    }

    // The four hex digits of a \u escape.
    fn read_hex(&mut self) -> Result<u32, JqValue> {
        let mut unit = 0;
        for _ in 0..4 {
            if self.pos == self.bytes.len() {
                return Err(self.fail_at_end(errors::UNFINISHED_AT_EOF));
            }
            let digit = hex_value(self.bytes[self.pos]);
            self.pos += 1;
            let Some(digit) = digit else {
                return Err(self.fail(errors::INVALID_UNICODE_ESCAPE, self.pos));
            };
            unit = unit * 16 + digit;
        }

        Ok(unit)
    }

    // The low surrogate of a `\uXXXX` escape at pos, if there is one.
    fn peek_low_surrogate(&self) -> Option<u32> {
        let rest = &self.bytes[self.pos..];
        if rest.len() < 6 || rest[0] != b'\\' || rest[1] != b'u' {
            return None;
        }
        let mut unit = 0;
        for byte in &rest[2..6] {
            unit = unit * 16 + hex_value(*byte)?;
        }
        if (0xDC00..=0xDFFF).contains(&unit) { Some(unit) } else { None }
    }

    fn read_literal(&mut self) -> Result<JqValue, JqValue> {
        let start = self.pos;
        while self.pos < self.bytes.len() && !is_delimiter(self.bytes[self.pos]) {
            self.pos += 1;
        }
        let token = &self.text[start..self.pos];
        match token {
            "null" => return Ok(JqValue::Null),
            "true" => return Ok(JqValue::True),
            "false" => return Ok(JqValue::False),
            _ => {}
        }

        match try_parse_number(token) {
            Some(number) => Ok(JqValue::Number(number)),
            None => {
                let reason = if token.as_bytes()[0].is_ascii_alphabetic() { errors::INVALID_LITERAL } else { errors::INVALID_NUMERIC_LITERAL };
                Err(self.fail(reason, self.pos))
            }
        }
    }

    fn skip_space(&mut self) {
        while self.pos < self.bytes.len() && matches!(self.bytes[self.pos], b' ' | b'\t' | b'\n' | b'\r') {
            self.pos += 1;
        }
    }

    fn fail_at_end(&self, reason: &str) -> JqValue {
        self.fail(reason, self.bytes.len())
    }

    // consumed: how many bytes were read when the error was found; the column counts the code
    // points read on their line, as jq does ("{" fails at line 1, column 1).
    fn fail(&self, reason: &str, consumed: usize) -> JqValue {
        let mut line = 1;
        let mut column = 0;
        for byte in &self.bytes[..consumed.min(self.bytes.len())] {
            if *byte == b'\n' {
                line += 1;
                column = 0;
            } else if (*byte & 0xC0) != 0x80 {
                column += 1;
            }
        }

        self.fail_without_position(&errors::at_position(reason, line, column))
    }

    fn fail_without_position(&self, reason: &str) -> JqValue {
        errors::while_parsing(reason, self.text)
    }
}

fn is_delimiter(c: u8) -> bool {
    matches!(c, b' ' | b'\t' | b'\n' | b'\r' | b'[' | b']' | b'{' | b'}' | b',' | b':' | b'"')
}

fn hex_value(c: u8) -> Option<u32> {
    match c {
        b'0'..=b'9' => Some((c - b'0') as u32),
        b'a'..=b'f' => Some((c - b'a') as u32 + 10),
        b'A'..=b'F' => Some((c - b'A') as u32 + 10),
        _ => None,
    }
}
