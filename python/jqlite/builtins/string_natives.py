"""§9.6: text conversion and the string natives the prelude builds on (StringNatives.cs)."""

from __future__ import annotations

from typing import TYPE_CHECKING, Any

from ..runtime.native_table import add_value
from ..values import errors, value_ops, value_text
from ..values.jq_array import JqArray
from ..values.jq_value import Fail
from . import formats

if TYPE_CHECKING:
    from ..values.jq_value import JqValue

# The Unicode White_Space code points (trim, ltrim, rtrim).
_WHITE_SPACE = "".join(chr(c) for c in [*range(0x09, 0x0E), 0x20, 0x85, 0xA0, 0x1680, *range(0x2000, 0x200B),
                                        0x2028, 0x2029, 0x202F, 0x205F, 0x3000])

_UPPER = {c: c - 32 for c in range(ord("a"), ord("z") + 1)}
_LOWER = {c: c + 32 for c in range(ord("A"), ord("Z") + 1)}


def register() -> None:
    add_value("tostring", 0, lambda interpreter, input, arguments: to_text(input))
    add_value("tojson", 0, lambda interpreter, input, arguments: value_text.to_json(input))
    add_value("fromjson", 0, lambda interpreter, input, arguments: value_text.try_parse(input) if type(input) is str
              else Fail(errors.only_strings_parse(input)))
    add_value("tonumber", 0, _to_number)
    add_value("toboolean", 0, _to_boolean)
    add_value("format", 1, lambda interpreter, input, arguments: formats.try_apply(arguments[0], input)
              if type(arguments[0]) is str else Fail(errors.not_a_valid_format(arguments[0])))
    add_value("explode", 0, lambda interpreter, input, arguments: value_ops.try_explode(input))
    add_value("implode", 0, lambda interpreter, input, arguments: value_ops.try_implode(input))
    add_value("ascii_downcase", 0, lambda interpreter, input, arguments: input.translate(_LOWER) if type(input) is str
              else Fail(errors.EXPLODE_NOT_STRING))
    add_value("ascii_upcase", 0, lambda interpreter, input, arguments: input.translate(_UPPER) if type(input) is str
              else Fail(errors.EXPLODE_NOT_STRING))
    add_value("startswith", 1, lambda interpreter, input, arguments: input.startswith(arguments[0])
              if type(input) is str and type(arguments[0]) is str else Fail(errors.STARTS_WITH_STRINGS))
    add_value("endswith", 1, lambda interpreter, input, arguments: input.endswith(arguments[0])
              if type(input) is str and type(arguments[0]) is str else Fail(errors.ENDS_WITH_STRINGS))
    add_value("trim", 0, lambda interpreter, input, arguments: input.strip(_WHITE_SPACE) if type(input) is str
              else Fail(errors.TRIM_NOT_STRING))
    add_value("ltrim", 0, lambda interpreter, input, arguments: input.lstrip(_WHITE_SPACE) if type(input) is str
              else Fail(errors.TRIM_NOT_STRING))
    add_value("rtrim", 0, lambda interpreter, input, arguments: input.rstrip(_WHITE_SPACE) if type(input) is str
              else Fail(errors.TRIM_NOT_STRING))
    add_value("split", 1, lambda interpreter, input, arguments: value_ops.split(input, arguments[0])
              if type(input) is str and type(arguments[0]) is str else Fail(errors.SPLIT_STRINGS))
    add_value("_strindices", 1, _string_indices)


def to_text(value: JqValue) -> str:
    """`tostring`: a string as it is, any other value as its canonical text."""
    return value if type(value) is str else value_text.to_json(value)  # type: ignore[return-value]


def _to_number(interpreter: Any, input: JqValue, arguments: Any) -> JqValue | Fail:
    if type(input) is float:
        return input
    if type(input) is str:
        number = value_text.try_parse_number(input)  # type: ignore[arg-type]
        if number is not None:
            return number
    return Fail(errors.cannot_parse_number(input))


def _to_boolean(interpreter: Any, input: JqValue, arguments: Any) -> JqValue | Fail:
    if type(input) is bool:
        return input
    if input == "true" or input == "false":
        return input == "true"
    return Fail(errors.cannot_parse_boolean(input))


def _string_indices(interpreter: Any, input: JqValue, arguments: Any) -> JqValue | Fail:
    # _strindices(needle): the code point offsets of every occurrence, overlapping ones included.
    if type(input) is not str:
        return Fail(errors.cannot_search_not_string(input))
    needle = arguments[0]
    if type(needle) is not str:
        return Fail(errors.not_a_string(needle))
    offsets: list = []
    if needle:
        i = input.find(needle)  # type: ignore[union-attr]
        while i >= 0:
            offsets.append(float(i))
            i = input.find(needle, i + 1)  # type: ignore[union-attr]
    return JqArray.from_owned(offsets)
