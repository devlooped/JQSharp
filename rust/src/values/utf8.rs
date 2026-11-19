//! UTF-8 encoding and decoding for the formats that work on bytes (§9.9: `@uri`, `@urid`,
//! `@base64`, `@base64d`).
//!
//! Host-specific: the reference decodes with its own loop (`Utf8.Next`), because .NET strings are
//! UTF-16. A Rust string is UTF-8 already, and the standard library's decoder follows the table of
//! spec §4.1: `String::from_utf8` accepts exactly the well-formed sequences, and
//! `String::from_utf8_lossy` replaces each maximal subpart of an ill-formed one with U+FFFD. So
//! this file uses `std` for both and keeps only the reference's function names.

/// Appends the UTF-8 bytes of a string.
pub fn encode(text: &str, bytes: &mut Vec<u8>) {
    bytes.extend_from_slice(text.as_bytes());
}

/// Appends the UTF-8 bytes of a Unicode scalar value.
pub fn encode_code_point(code_point: char, bytes: &mut Vec<u8>) {
    let mut buffer = [0u8; 4];
    bytes.extend_from_slice(code_point.encode_utf8(&mut buffer).as_bytes());
}

/// The text of well-formed UTF-8 bytes; `None` when any sequence is ill-formed.
pub fn try_decode(bytes: Vec<u8>) -> Option<String> {
    String::from_utf8(bytes).ok()
}

/// The text of UTF-8 bytes, each maximal subpart of an ill-formed sequence replaced by U+FFFD.
pub fn decode_replacing(bytes: Vec<u8>) -> String {
    match String::from_utf8(bytes) {
        Ok(text) => text,
        Err(error) => String::from_utf8_lossy(error.as_bytes()).into_owned(),
    }
}
