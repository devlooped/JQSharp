//! The message catalogue of the runtime errors of spec §10.3 and of the builtins of §9: every text
//! an evaluation can raise, in one place. It lives in the value layer, the lowest one, because the
//! value operations raise most of these errors; the interpreter and the natives use it too. A
//! jqlite error is a value, usually the message string. (Syntax and binding errors, §10.2, are
//! `syntax_messages` and `binding_messages`: they are reported before evaluation, with a position.)

use super::jq_value::JqValue;
use super::value_text;

pub const ADDED: &str = "added";
pub const SUBTRACTED: &str = "subtracted";
pub const MULTIPLIED: &str = "multiplied";
pub const DIVIDED: &str = "divided";
pub const REMAINDER: &str = "divided (remainder)";

// The error dump D(v): the canonical text, cut when longer than DUMP_LIMIT UTF-8 bytes (§10.1).
const DUMP_LIMIT: usize = 29;
const DUMP_KEEP: usize = 25;

/// `number (1) and string ("a") cannot be added`; `operation` is one of the constants above.
pub fn cannot_be(a: &JqValue, b: &JqValue, operation: &str) -> JqValue {
    message(format!("{} and {} cannot be {operation}", describe(a), describe(b)))
}

/// `... cannot be divided because the divisor is zero` (`operation` [`DIVIDED`]) or
/// `... cannot be divided (remainder) because the divisor is zero` ([`REMAINDER`]).
pub fn divisor_is_zero(a: &JqValue, b: &JqValue, operation: &str) -> JqValue {
    message(format!("{} and {} cannot be {operation} because the divisor is zero", describe(a), describe(b)))
}

pub fn cannot_negate(value: &JqValue) -> JqValue {
    message(format!("{} cannot be negated", describe(value)))
}

pub fn repeat_too_long() -> JqValue {
    text("Repeat string result too long")
}

pub fn implode_not_array() -> JqValue {
    text("implode input must be an array")
}

/// A code point that is not a number (or is NaN), described by its own dump.
pub fn codepoint_not_numeric(codepoint: &JqValue) -> JqValue {
    message(format!("{} can't be imploded, unicode codepoint needs to be numeric", describe(codepoint)))
}

pub fn explode_not_string() -> JqValue {
    text("explode input must be a string")
}

/// `Cannot index number with object ({"start":1,"end":2})`: the dump is the key's.
pub fn cannot_index(input: &JqValue, key: &JqValue) -> JqValue {
    message(format!("Cannot index {} with {}", input.type_name(), describe(key)))
}

pub fn slice_indices_not_integers() -> JqValue {
    text("Array/string slice indices must be integers")
}

// JSON reader reasons (implementation-defined text, §5.3); the reader adds the position and
// while_parsing wraps them.
pub const EXPECTED_VALUE: &str = "Expected JSON value";
pub const EXTRA_VALUES: &str = "Unexpected extra JSON values";
pub const UNFINISHED_AT_EOF: &str = "Unfinished JSON term at EOF";
pub const INVALID_LITERAL: &str = "Invalid literal";
pub const INVALID_NUMERIC_LITERAL: &str = "Invalid numeric literal";
pub const INVALID_ESCAPE: &str = "Invalid escape";
pub const INVALID_UNICODE_ESCAPE: &str = "Invalid \\uXXXX escape";
pub const CONTROL_CHARACTER: &str = "Invalid string: control characters from U+0000 through U+001F must be escaped";
pub const KEYS_MUST_BE_STRINGS: &str = "Object keys must be strings";
pub const KEY_VALUE_PAIRS: &str = "Objects must consist of key:value pairs";
pub const EXPECTED_SEPARATOR: &str = "Expected separator between values";
pub const EXPECTED_ARRAY_ELEMENT: &str = "Expected another array element";
pub const DEPTH_LIMIT: &str = "Exceeds depth limit for parsing";

pub fn expected_value_before(c: char) -> String {
    format!("Expected value before '{c}'")
}

pub fn unmatched(c: char) -> String {
    format!("Unmatched '{c}'")
}

pub fn at_position(reason: &str, line: usize, column: usize) -> String {
    format!("{reason} at line {line}, column {column}")
}

/// A reader error: `<reason> (while parsing '<text>')`, `text` the whole input (§5.3).
pub fn while_parsing(reason: &str, input: &str) -> JqValue {
    message(format!("{reason} (while parsing '{input}')"))
}

// The interpreter (§10.3).
pub fn cannot_iterate(value: &JqValue) -> JqValue {
    message(format!("Cannot iterate over {}", describe(value)))
}

pub fn cannot_use_as_key(key: &JqValue) -> JqValue {
    message(format!("Cannot use {} as object key", describe(key)))
}

pub fn variable_not_defined(name: &str) -> JqValue {
    message(format!("${name} is not defined"))
}

pub fn invalid_format(name: &str) -> JqValue {
    message(format!("{name} is not a valid format"))
}

pub fn depth_exceeded() -> JqValue {
    text("Maximum call depth exceeded")
}

pub fn modulemeta_not_string() -> JqValue {
    text("modulemeta input module name must be a string")
}

// Paths (§8).
pub fn invalid_path_result(value: &JqValue) -> JqValue {
    message(format!("Invalid path expression with result {}", dump(value)))
}

pub fn invalid_path_access(key: &JqValue, target: &JqValue) -> JqValue {
    message(format!("Invalid path expression near attempt to access element {} of {}", dump(key), dump(target)))
}

pub fn invalid_path_iterate(target: &JqValue) -> JqValue {
    message(format!("Invalid path expression near attempt to iterate through {}", dump(target)))
}

pub fn path_not_array() -> JqValue {
    text("Path must be specified as an array")
}

pub fn paths_not_array() -> JqValue {
    text("Paths must be specified as an array")
}

pub fn path_element_not_array(path: &JqValue) -> JqValue {
    message(format!("Path must be specified as array, not {}", path.type_name()))
}

pub fn negative_index() -> JqValue {
    text("Out of bounds negative array index")
}

pub fn index_too_large() -> JqValue {
    text("Array index too large")
}

pub fn nan_index() -> JqValue {
    text("Cannot set array element at NaN index")
}

pub fn slice_needs_array() -> JqValue {
    text("A slice of an array can only be assigned another array")
}

pub fn cannot_update_string_slices() -> JqValue {
    text("Cannot update string slices")
}

pub fn cannot_update_field(key: &JqValue, container: &JqValue) -> JqValue {
    message(format!("Cannot update field at {} index of {}", key.type_name(), container.type_name()))
}

pub fn cannot_delete_element(key: &JqValue) -> JqValue {
    message(format!("Cannot delete {} element of array", key.type_name()))
}

pub fn cannot_delete_field(key: &JqValue) -> JqValue {
    message(format!("Cannot delete {} field of object", key.type_name()))
}

pub fn cannot_delete_from(container: &JqValue) -> JqValue {
    message(format!("Cannot delete fields from {}", container.type_name()))
}

// Builtins (§9), by section.
pub fn has_no_length(value: &JqValue) -> JqValue {
    message(format!("{} has no length", describe(value)))
}

pub fn only_strings_have_utf8_length(value: &JqValue) -> JqValue {
    message(format!("{} only strings have UTF-8 byte length", describe(value)))
}

pub fn halt_error_number_required(input: &JqValue) -> JqValue {
    message(format!("{} halt_error/1: number required", describe(input)))
}

pub fn has_no_keys(value: &JqValue) -> JqValue {
    message(format!("{} has no keys", describe(value)))
}

pub fn cannot_check_has(input: &JqValue, key: &JqValue) -> JqValue {
    message(format!("Cannot check whether {} has a {} key", input.type_name(), key.type_name()))
}

pub fn range_not_numeric() -> JqValue {
    text("Range bounds must be numeric")
}

pub fn negative_limit() -> JqValue {
    text("limit doesn't support negative count")
}

pub fn negative_skip() -> JqValue {
    text("skip doesn't support negative count")
}

pub fn cannot_be_sorted(value: &JqValue) -> JqValue {
    message(format!("{} cannot be sorted, as it is not an array", describe(value)))
}

pub fn cannot_be_sorted_by(a: &JqValue, b: &JqValue) -> JqValue {
    message(format!("{} and {} cannot be sorted, as they are not both arrays", describe(a), describe(b)))
}

pub fn cannot_be_iterated(a: &JqValue, b: &JqValue) -> JqValue {
    message(format!("{} and {} cannot be iterated over", describe(a), describe(b)))
}

pub fn wrong_length(a: &JqValue, b: &JqValue) -> JqValue {
    message(format!("{} and {} have wrong length", describe(a), describe(b)))
}

pub fn cannot_be_searched(value: &JqValue) -> JqValue {
    message(format!("{} cannot be searched from", describe(value)))
}

pub fn only_strings_parse(value: &JqValue) -> JqValue {
    message(format!("{} only strings can be parsed", describe(value)))
}

pub fn cannot_parse_number(value: &JqValue) -> JqValue {
    message(format!("{} cannot be parsed as a number", describe(value)))
}

pub fn cannot_parse_boolean(value: &JqValue) -> JqValue {
    message(format!("{} cannot be parsed as a boolean", describe(value)))
}

/// `format/1` with a name that is not a string.
pub fn not_a_valid_format(value: &JqValue) -> JqValue {
    message(format!("{} is not a valid format", describe(value)))
}

pub fn trim_not_string() -> JqValue {
    text("trim input must be a string")
}

pub fn starts_with_strings() -> JqValue {
    text("startswith() requires string inputs")
}

pub fn ends_with_strings() -> JqValue {
    text("endswith() requires string inputs")
}

pub fn split_strings() -> JqValue {
    text("split input and separator must be strings")
}

pub fn cannot_search_not_string(value: &JqValue) -> JqValue {
    message(format!("{} cannot be searched, as it is not a string", describe(value)))
}

pub fn not_a_string(value: &JqValue) -> JqValue {
    message(format!("{} is not a string", describe(value)))
}

/// "num req" (§9.1): `string ("a") number required`.
pub fn number_required(value: &JqValue) -> JqValue {
    message(format!("{} number required", describe(value)))
}

// Regular expressions (§9.10).
pub fn cannot_be_matched(value: &JqValue) -> JqValue {
    message(format!("{} cannot be matched, as it is not a string", describe(value)))
}

pub fn invalid_modifiers(flags: &str) -> JqValue {
    message(format!("{flags} is not a valid modifier string"))
}

/// A pattern outside the dialect or that the engine rejects; the reason is implementation-defined.
pub fn regex_failure(reason: &str) -> JqValue {
    message(format!("Regex failure: {reason}"))
}

// Dates (§9.7, §9.12).
pub fn gmtime_numeric() -> JqValue {
    text("gmtime requires numeric inputs")
}

pub fn localtime_numeric() -> JqValue {
    text("localtime requires numeric inputs")
}

pub fn cannot_convert_seconds() -> JqValue {
    text("error converting number of seconds since epoch to datetime")
}

pub fn mktime_array() -> JqValue {
    text("mktime requires array inputs")
}

pub fn mktime_parsed() -> JqValue {
    text("mktime requires parsed datetime inputs")
}

/// `name` is `strftime` or `strflocaltime`.
pub fn requires_parsed_datetime(name: &str) -> JqValue {
    message(format!("{name}/1 requires parsed datetime inputs"))
}

pub fn requires_string_format(name: &str) -> JqValue {
    message(format!("{name}/1 requires a string format"))
}

pub fn trailing_percent() -> JqValue {
    text("Invalid format: trailing %")
}

pub fn unsupported_strftime(directive: &str) -> JqValue {
    message(format!("Unsupported strftime format specifier %{directive}"))
}

pub fn strptime_strings() -> JqValue {
    text("strptime/1 requires string inputs and arguments")
}

pub fn date_does_not_match(input: &str, format: &str) -> JqValue {
    message(format!("date \"{input}\" does not match format \"{format}\""))
}

/// `now` in an evaluation whose host gave no clock.
pub fn no_clock() -> JqValue {
    text("now/0 requires a host clock")
}

pub fn cannot_check_containment(a: &JqValue, b: &JqValue) -> JqValue {
    message(format!("{} and {} cannot have their containment checked", describe(a), describe(b)))
}

// Formats (§9.9).
pub fn not_valid_uri_encoding(value: &JqValue) -> JqValue {
    message(format!("{} is not a valid uri encoding", describe(value)))
}

pub fn cannot_be_csv_formatted(value: &JqValue) -> JqValue {
    message(format!("{} cannot be csv-formatted, only array", describe(value)))
}

pub fn cannot_be_tsv_formatted(value: &JqValue) -> JqValue {
    message(format!("{} cannot be tsv-formatted, only array", describe(value)))
}

/// An array or object in a row of `@csv` or `@tsv` (the text says csv for both).
pub fn not_valid_in_csv_row(value: &JqValue) -> JqValue {
    message(format!("{} is not valid in a csv row", describe(value)))
}

pub fn cannot_be_escaped_for_shell(value: &JqValue) -> JqValue {
    message(format!("{} can not be escaped for shell", describe(value)))
}

pub fn not_valid_base64(value: &JqValue) -> JqValue {
    message(format!("{} is not valid base64 data", describe(value)))
}

pub fn trailing_base64_byte(value: &JqValue) -> JqValue {
    message(format!("{} trailing base64 byte found", describe(value)))
}

/// `T(v) (D(v))`, as in `object ({"a":1})`.
pub fn describe(value: &JqValue) -> String {
    format!("{} ({})", value.type_name(), dump(value))
}

/// The error dump D(v) (§10.1): the canonical text; if longer than 29 UTF-8 bytes, its longest
/// prefix of at most 25 bytes ending on a code point boundary, then "..." and the value's closing
/// delimiter.
pub fn dump(value: &JqValue) -> String {
    value_text::to_json_truncated(value, DUMP_LIMIT, DUMP_KEEP)
}

fn message(text: String) -> JqValue {
    JqValue::from_string(text)
}

fn text(text: &str) -> JqValue {
    JqValue::from_text(text)
}
