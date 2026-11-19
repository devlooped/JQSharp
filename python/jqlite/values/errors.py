"""The message catalogue of the runtime errors of spec §10.3 and of the builtins of §9 (Errors.cs).

Every text an evaluation can raise, in one place. A jqlite error is a value, usually the message
string. (Syntax and binding errors, §10.2, are SyntaxMessages and BindingMessages.)
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from . import value_text
from .jq_value import type_name

if TYPE_CHECKING:
    from .jq_value import JqValue

ADDED = "added"
SUBTRACTED = "subtracted"
MULTIPLIED = "multiplied"
DIVIDED = "divided"
REMAINDER = "divided (remainder)"

# The error dump D(v): the canonical text, cut when longer than DUMP_LIMIT UTF-8 bytes (§10.1).
DUMP_LIMIT = 29
DUMP_KEEP = 25


def cannot_be(a: JqValue, b: JqValue, operation: str) -> str:
    """``number (1) and string ("a") cannot be added``; ``operation`` is one of the constants above."""
    return f"{describe(a)} and {describe(b)} cannot be {operation}"


def divisor_is_zero(a: JqValue, b: JqValue, operation: str) -> str:
    return f"{describe(a)} and {describe(b)} cannot be {operation} because the divisor is zero"


def cannot_negate(value: JqValue) -> str:
    return f"{describe(value)} cannot be negated"


REPEAT_TOO_LONG = "Repeat string result too long"
IMPLODE_NOT_ARRAY = "implode input must be an array"


def codepoint_not_numeric(codepoint: JqValue) -> str:
    return f"{describe(codepoint)} can't be imploded, unicode codepoint needs to be numeric"


EXPLODE_NOT_STRING = "explode input must be a string"


def cannot_index(input: JqValue, key: JqValue) -> str:
    """``Cannot index number with object ({"start":1,"end":2})``: the dump is the key's."""
    return f"Cannot index {type_name(input)} with {describe(key)}"


SLICE_INDICES_NOT_INTEGERS = "Array/string slice indices must be integers"

# JSON reader reasons (implementation-defined text, §5.3); the reader adds the position and
# while_parsing wraps them.
EXPECTED_VALUE = "Expected JSON value"
EXTRA_VALUES = "Unexpected extra JSON values"
UNFINISHED_AT_EOF = "Unfinished JSON term at EOF"
INVALID_LITERAL = "Invalid literal"
INVALID_NUMERIC_LITERAL = "Invalid numeric literal"
INVALID_ESCAPE = "Invalid escape"
INVALID_UNICODE_ESCAPE = "Invalid \\uXXXX escape"
CONTROL_CHARACTER = "Invalid string: control characters from U+0000 through U+001F must be escaped"
KEYS_MUST_BE_STRINGS = "Object keys must be strings"
KEY_VALUE_PAIRS = "Objects must consist of key:value pairs"
EXPECTED_SEPARATOR = "Expected separator between values"
EXPECTED_ARRAY_ELEMENT = "Expected another array element"
DEPTH_LIMIT = "Exceeds depth limit for parsing"


def expected_value_before(c: str) -> str:
    return f"Expected value before '{c}'"


def unmatched(c: str) -> str:
    return f"Unmatched '{c}'"


def at_position(reason: str, line: int, column: int) -> str:
    return f"{reason} at line {line}, column {column}"


def while_parsing(reason: str, text: str) -> str:
    """A reader error: ``<reason> (while parsing '<text>')``, ``text`` the whole input (§5.3)."""
    return f"{reason} (while parsing '{text}')"


# The interpreter (§10.3).
def cannot_iterate(value: JqValue) -> str:
    return f"Cannot iterate over {describe(value)}"


def cannot_use_as_key(key: JqValue) -> str:
    return f"Cannot use {describe(key)} as object key"


def variable_not_defined(name: str) -> str:
    return f"${name} is not defined"


def invalid_format(name: str) -> str:
    return f"{name} is not a valid format"


DEPTH_EXCEEDED = "Maximum call depth exceeded"
MODULEMETA_NOT_STRING = "modulemeta input module name must be a string"


# Paths (§8).
def invalid_path_result(value: JqValue) -> str:
    return f"Invalid path expression with result {dump(value)}"


def invalid_path_access(key: JqValue, target: JqValue) -> str:
    return f"Invalid path expression near attempt to access element {dump(key)} of {dump(target)}"


def invalid_path_iterate(target: JqValue) -> str:
    return f"Invalid path expression near attempt to iterate through {dump(target)}"


PATH_NOT_ARRAY = "Path must be specified as an array"
PATHS_NOT_ARRAY = "Paths must be specified as an array"


def path_element_not_array(path: JqValue) -> str:
    return f"Path must be specified as array, not {type_name(path)}"


NEGATIVE_INDEX = "Out of bounds negative array index"
INDEX_TOO_LARGE = "Array index too large"
NAN_INDEX = "Cannot set array element at NaN index"
SLICE_NEEDS_ARRAY = "A slice of an array can only be assigned another array"
CANNOT_UPDATE_STRING_SLICES = "Cannot update string slices"


def cannot_update_field(key: JqValue, container: JqValue) -> str:
    return f"Cannot update field at {type_name(key)} index of {type_name(container)}"


def cannot_delete_element(key: JqValue) -> str:
    return f"Cannot delete {type_name(key)} element of array"


def cannot_delete_field(key: JqValue) -> str:
    return f"Cannot delete {type_name(key)} field of object"


def cannot_delete_from(container: JqValue) -> str:
    return f"Cannot delete fields from {type_name(container)}"


# Builtins (§9), by section.
def has_no_length(value: JqValue) -> str:
    return f"{describe(value)} has no length"


def only_strings_have_utf8_length(value: JqValue) -> str:
    return f"{describe(value)} only strings have UTF-8 byte length"


def halt_error_number_required(input: JqValue) -> str:
    return f"{describe(input)} halt_error/1: number required"


def has_no_keys(value: JqValue) -> str:
    return f"{describe(value)} has no keys"


def cannot_check_has(input: JqValue, key: JqValue) -> str:
    return f"Cannot check whether {type_name(input)} has a {type_name(key)} key"


RANGE_NOT_NUMERIC = "Range bounds must be numeric"
NEGATIVE_LIMIT = "limit doesn't support negative count"
NEGATIVE_SKIP = "skip doesn't support negative count"


def cannot_be_sorted(value: JqValue) -> str:
    return f"{describe(value)} cannot be sorted, as it is not an array"


def cannot_be_sorted_by(a: JqValue, b: JqValue) -> str:
    return f"{describe(a)} and {describe(b)} cannot be sorted, as they are not both arrays"


def cannot_be_iterated(a: JqValue, b: JqValue) -> str:
    return f"{describe(a)} and {describe(b)} cannot be iterated over"


def wrong_length(a: JqValue, b: JqValue) -> str:
    return f"{describe(a)} and {describe(b)} have wrong length"


def cannot_be_searched(value: JqValue) -> str:
    return f"{describe(value)} cannot be searched from"


def only_strings_parse(value: JqValue) -> str:
    return f"{describe(value)} only strings can be parsed"


def cannot_parse_number(value: JqValue) -> str:
    return f"{describe(value)} cannot be parsed as a number"


def cannot_parse_boolean(value: JqValue) -> str:
    return f"{describe(value)} cannot be parsed as a boolean"


def not_a_valid_format(value: JqValue) -> str:
    """``format/1`` with a name that is not a string."""
    return f"{describe(value)} is not a valid format"


TRIM_NOT_STRING = "trim input must be a string"
STARTS_WITH_STRINGS = "startswith() requires string inputs"
ENDS_WITH_STRINGS = "endswith() requires string inputs"
SPLIT_STRINGS = "split input and separator must be strings"


def cannot_search_not_string(value: JqValue) -> str:
    return f"{describe(value)} cannot be searched, as it is not a string"


def not_a_string(value: JqValue) -> str:
    return f"{describe(value)} is not a string"


def number_required(value: JqValue) -> str:
    """"num req" (§9.1): ``string ("a") number required``."""
    return f"{describe(value)} number required"


# Regular expressions (§9.10).
def cannot_be_matched(value: JqValue) -> str:
    return f"{describe(value)} cannot be matched, as it is not a string"


def invalid_modifiers(flags: str) -> str:
    return f"{flags} is not a valid modifier string"


def regex_failure(reason: str) -> str:
    """A pattern outside the dialect or that the engine rejects; the reason is implementation-defined."""
    return f"Regex failure: {reason}"


# Dates (§9.7, §9.12).
GMTIME_NUMERIC = "gmtime requires numeric inputs"
LOCALTIME_NUMERIC = "localtime requires numeric inputs"
CANNOT_CONVERT_SECONDS = "error converting number of seconds since epoch to datetime"
MKTIME_ARRAY = "mktime requires array inputs"
MKTIME_PARSED = "mktime requires parsed datetime inputs"


def requires_parsed_datetime(name: str) -> str:
    """``name`` is ``strftime`` or ``strflocaltime``."""
    return f"{name}/1 requires parsed datetime inputs"


def requires_string_format(name: str) -> str:
    return f"{name}/1 requires a string format"


TRAILING_PERCENT = "Invalid format: trailing %"


def unsupported_strftime(directive: str) -> str:
    return f"Unsupported strftime format specifier %{directive}"


STRPTIME_STRINGS = "strptime/1 requires string inputs and arguments"


def date_does_not_match(input: str, format: str) -> str:
    return f'date "{input}" does not match format "{format}"'


NO_CLOCK = "now/0 requires a host clock"


def cannot_check_containment(a: JqValue, b: JqValue) -> str:
    return f"{describe(a)} and {describe(b)} cannot have their containment checked"


# Formats (§9.9).
def not_valid_uri_encoding(text: JqValue) -> str:
    return f"{describe(text)} is not a valid uri encoding"


def cannot_be_csv_formatted(value: JqValue) -> str:
    return f"{describe(value)} cannot be csv-formatted, only array"


def cannot_be_tsv_formatted(value: JqValue) -> str:
    return f"{describe(value)} cannot be tsv-formatted, only array"


def not_valid_in_csv_row(value: JqValue) -> str:
    """An array or object in a row of ``@csv`` or ``@tsv`` (the text says csv for both)."""
    return f"{describe(value)} is not valid in a csv row"


def cannot_be_escaped_for_shell(value: JqValue) -> str:
    return f"{describe(value)} can not be escaped for shell"


def not_valid_base64(text: JqValue) -> str:
    return f"{describe(text)} is not valid base64 data"


def trailing_base64_byte(text: JqValue) -> str:
    return f"{describe(text)} trailing base64 byte found"


def describe(value: JqValue) -> str:
    """``T(v) (D(v))``, as in ``object ({"a":1})``."""
    return f"{type_name(value)} ({dump(value)})"


def dump(value: JqValue) -> str:
    """The error dump D(v) (§10.1)."""
    return value_text.to_json_truncated(value, DUMP_LIMIT, DUMP_KEEP)

