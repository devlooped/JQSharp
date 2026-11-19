"""The `@name` formats of §9.9 (Formats.cs)."""

from __future__ import annotations

from typing import TYPE_CHECKING

from ..values import errors, utf8, value_text
from ..values.jq_array import JqArray
from ..values.jq_object import JqObject
from ..values.jq_value import Fail

if TYPE_CHECKING:
    from ..values.jq_value import JqValue

_HEX = "0123456789ABCDEF"
_BASE64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"
_BASE64_DIGITS = {c: i for i, c in enumerate(_BASE64)}
_HEX_VALUES = {c: int(c, 16) for c in "0123456789abcdefABCDEF"}
_HTML = {ord("&"): "&amp;", ord("<"): "&lt;", ord(">"): "&gt;", ord("'"): "&apos;", ord('"'): "&quot;"}
_TSV = {ord("\\"): "\\\\", ord("\t"): "\\t", ord("\n"): "\\n", ord("\r"): "\\r"}
_UNRESERVED = frozenset(b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~")
# The text of each byte in @uri.
_URI_BYTES = [chr(b) if b in _UNRESERVED else "%" + _HEX[b >> 4] + _HEX[b & 0xF] for b in range(256)]


def _text(value: JqValue) -> str:
    return value if type(value) is str else value_text.to_json(value)  # type: ignore[return-value]


def try_apply(name: str, value: JqValue) -> JqValue | Fail:
    """Applies the format `name` (without '@') to `value`, giving a string."""
    if name == "text":
        return _text(value)
    if name == "json":
        return value_text.to_json(value)
    if name == "html":
        return _text(value).translate(_HTML)
    if name == "uri":
        text = _text(value)
        if text.isascii() and text.isalnum():
            return text
        return "".join([_URI_BYTES[b] for b in utf8.encode(text)])
    if name == "urid":
        return _uri_decode(_text(value))
    if name == "csv":
        return _row(value, False) if type(value) is JqArray else Fail(errors.cannot_be_csv_formatted(value))
    if name == "tsv":
        return _row(value, True) if type(value) is JqArray else Fail(errors.cannot_be_tsv_formatted(value))
    if name == "sh":
        return _shell(value.to_list() if type(value) is JqArray else [value])  # type: ignore[union-attr]
    if name == "base64":
        return _base64(_text(value))
    if name == "base64d":
        return _base64_decode(_text(value))
    return Fail(errors.invalid_format(name))


def _uri_decode(text: str) -> JqValue | Fail:
    # @urid: %XX (either case) is that byte, any other code point its UTF-8 bytes; the bytes must be well-formed UTF-8.
    data = bytearray()
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == "%":
            high = _HEX_VALUES.get(text[i + 1], -1) if i + 1 < n else -1
            low = _HEX_VALUES.get(text[i + 2], -1) if i + 2 < n else -1
            if high < 0 or low < 0:
                return Fail(errors.not_valid_uri_encoding(text))
            data.append((high << 4) | low)
            i += 3
            continue
        data += c.encode("utf-8")
        i += 1
    decoded = utf8.try_decode(bytes(data))
    return decoded if decoded is not None else Fail(errors.not_valid_uri_encoding(text))


def _row(items: JqArray, tsv: bool) -> JqValue | Fail:
    # @csv and @tsv: one row; strings quoted (csv) or escaped (tsv), numbers as canonical text
    # (NaN empty), booleans as such, null empty.
    parts: list[str] = []
    for item in items.to_list():
        t = type(item)
        if item is None:
            parts.append("")
        elif t is bool:
            parts.append("true" if item else "false")
        elif t is float:
            parts.append("" if item != item else value_text.format_number(item))  # type: ignore[arg-type]
        elif t is str:
            parts.append(item.translate(_TSV) if tsv else '"' + item.replace('"', '""') + '"')  # type: ignore[union-attr]
        else:
            return Fail(errors.not_valid_in_csv_row(item))
    return ("\t" if tsv else ",").join(parts)


def _shell(items: list[JqValue]) -> JqValue | Fail:
    # @sh: space-separated words; strings in single quotes (' as '\''), other scalars as canonical text.
    parts: list[str] = []
    for item in items:
        t = type(item)
        if t is str:
            parts.append("'" + item.replace("'", "'\\''") + "'")  # type: ignore[union-attr]
        elif t is JqArray or t is JqObject:
            return Fail(errors.cannot_be_escaped_for_shell(item))
        else:
            parts.append(value_text.to_json(item))
    return " ".join(parts)


def _base64(text: str) -> str:
    # @base64: RFC 4648 of the UTF-8 bytes, with '=' padding.
    data = utf8.encode(text)
    out: list[str] = []
    n = len(data)
    for i in range(0, n, 3):
        left = n - i
        group = data[i] << 16 | (data[i + 1] << 8 if left > 1 else 0) | (data[i + 2] if left > 2 else 0)
        out.append(_BASE64[group >> 18])
        out.append(_BASE64[(group >> 12) & 0x3F])
        out.append(_BASE64[(group >> 6) & 0x3F] if left > 1 else "=")
        out.append(_BASE64[group & 0x3F] if left > 2 else "=")
    return "".join(out)


def _base64_decode(text: str) -> JqValue | Fail:
    # @base64d: the characters before the first '=' must be in the alphabet; a final group of 2 or 3
    # characters gives 1 or 2 bytes, of 1 is an error; the bytes are decoded as UTF-8 with replacement.
    data = bytearray()
    group = 0
    count = 0
    for c in text:
        if c == "=":
            break
        digit = _BASE64_DIGITS.get(c, -1)
        if digit < 0:
            return Fail(errors.not_valid_base64(text))
        group = group << 6 | digit
        count += 1
        if count == 4:
            data.append((group >> 16) & 0xFF)
            data.append((group >> 8) & 0xFF)
            data.append(group & 0xFF)
            group = 0
            count = 0
    if count == 1:
        return Fail(errors.trailing_base64_byte(text))
    if count == 2:
        data.append((group >> 4) & 0xFF)
    elif count == 3:
        data.append((group >> 10) & 0xFF)
        data.append((group >> 2) & 0xFF)
    return utf8.decode_replacing(bytes(data))
