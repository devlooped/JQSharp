"""The one JSON writer, number formatter and JSON reader of jqlite (ValueText.cs; §5).

Canonical text is compact JSON. Strings escape only ``"``, ``\\``, the control characters below
U+0020 (``\\b \\f \\n \\r \\t``, the others ``\\u00xx``) and U+007F, as jq does. Numbers are the
shortest digits that read back to the same double, laid out as ECMAScript's ``Number::toString``;
NaN is ``null`` and ±Infinity are ±``1.7976931348623157e+308``.

Host-specific (**H**): the shortest round-trip digits come from ``repr(float)`` (shortest since
Python 3.1), and the decimal-to-double step from ``float(str)`` once the grammar of §5.3 is checked.
"""

from __future__ import annotations

import math
from typing import TYPE_CHECKING

from . import errors
from .code_points import sanitize
from .jq_array import JqArray
from .jq_object import JqObject
from .jq_value import Fail

if TYPE_CHECKING:
    from .jq_value import JqValue

MAX_DOUBLE = "1.7976931348623157e+308"

# The deepest level the writer writes (§5.1, §11).
MAX_WRITE_DEPTH = 10001

TOO_DEEP = "<skipped: too deep>"

_ESCAPES: dict[int, str] = {i: f"\\u{i:04x}" for i in range(0x20)}
_ESCAPES.update({
    0x22: '\\"', 0x5C: "\\\\", 0x08: "\\b", 0x0C: "\\f", 0x0A: "\\n", 0x0D: "\\r", 0x09: "\\t",
    0x7F: "\\u007f",
})

_INF = math.inf


def to_json(value: JqValue) -> str:
    t = type(value)
    if t is str:
        return '"' + value.translate(_ESCAPES) + '"'  # type: ignore[union-attr]
    if t is float:
        return format_number(value)  # type: ignore[arg-type]
    if value is None:
        return "null"
    if t is bool:
        return "true" if value else "false"
    parts: list[str] = []
    write(parts, value)
    return "".join(parts)


def write(parts: list[str], value: JqValue) -> None:
    """Writes the canonical text of ``value`` to ``parts``.

    No recursion: the containers being written are on an explicit stack, so nesting depth is not
    limited by the host stack. A value nested deeper than MAX_WRITE_DEPTH levels (the outermost
    value is level 1) is written as ``<skipped: too deep>``, as jq 1.8.2 does.
    """
    append = parts.append
    escapes = _ESCAPES
    # A frame: [elements, index of the current one, keys or None for an array].
    stack: list[list] = []
    current = value
    while True:
        # len(stack) is the number of containers around current: its level minus one.
        if len(stack) >= MAX_WRITE_DEPTH:
            append(TOO_DEEP)
        else:
            t = type(current)
            if t is str:
                append('"' + current.translate(escapes) + '"')  # type: ignore[union-attr]
            elif t is float:
                append(format_number(current))  # type: ignore[arg-type]
            elif t is JqArray:
                n = current.count  # type: ignore[union-attr]
                if n:
                    items = current.to_list()  # type: ignore[union-attr]
                    append("[")
                    stack.append([items, 0, None])
                    current = items[0]
                    continue
                append("[]")
            elif t is JqObject:
                n = current.count  # type: ignore[union-attr]
                if n:
                    keys = current.keys[:n]  # type: ignore[union-attr]
                    values = current.values[:n]  # type: ignore[union-attr]
                    append('{"' + keys[0].translate(escapes) + '":')
                    stack.append([values, 0, keys])
                    current = values[0]
                    continue
                append("{}")
            elif current is None:
                append("null")
            else:
                append("true" if current else "false")

        # current is written: go on with the next element, closing the containers that end.
        while True:
            if not stack:
                return
            frame = stack[-1]
            items = frame[0]
            i = frame[1] + 1
            if i < len(items):
                frame[1] = i
                keys = frame[2]
                if keys is None:
                    append(",")
                else:
                    append(',"' + keys[i].translate(escapes) + '":')
                current = items[i]
                break
            append("]" if frame[2] is None else "}")
            stack.pop()


def write_string(text: str) -> str:
    """``text`` as a JSON string literal."""
    return '"' + text.translate(_ESCAPES) + '"'


def to_json_truncated(value: JqValue, max_bytes: int, keep_bytes: int) -> str:
    """The JSON text of ``value``; when longer than ``max_bytes`` UTF-8 bytes, its longest prefix
    of at most ``keep_bytes`` bytes that ends on a code point boundary, then "..." and the closing
    delimiter of the value (``"``, ``]`` or ``}``), as jq 1.8.2 writes them."""
    text = to_json(value)
    if len(text) * 3 <= max_bytes:
        return text
    data = text.encode("utf-8")
    if len(data) <= max_bytes:
        return text
    # Dropping the incomplete sequence at the end keeps the prefix on a code point boundary.
    prefix = data[:keep_bytes].decode("utf-8", "ignore")
    t = type(value)
    return prefix + ('..."' if t is str else ("...]" if t is JqArray else "...}"))


def format_number(number: float) -> str:
    """The canonical text of a number: shortest round-trip digits in ECMAScript ``Number::toString`` layout."""
    if number != number:
        return "null"
    if number == _INF:
        return MAX_DOUBLE
    if number == -_INF:
        return "-" + MAX_DOUBLE
    if number == 0:
        return "-0" if math.copysign(1.0, number) < 0 else "0"
    # Fast path: integers below 1e15 print as themselves.
    if -1e15 < number < 1e15 and number.is_integer():
        return str(int(number))

    # repr gives the shortest round-trip digits; only the layout changes below.
    text = repr(number)
    negative = text[0] == "-"
    body = text[1:] if negative else text
    exponent = 0
    e = body.find("e")
    if e >= 0:
        exponent = int(body[e + 1:])
        body = body[:e]
    dot = body.find(".")
    digits = body if dot < 0 else body[:dot] + body[dot + 1:]
    # n: the position of the decimal point relative to the first digit (value = 0.digits * 10^n).
    n = (len(body) if dot < 0 else dot) + exponent
    lead = 0
    while lead < len(digits) - 1 and digits[lead] == "0":
        lead += 1
    digits = digits[lead:].rstrip("0")
    n -= lead
    k = len(digits)

    sign = "-" if negative else ""
    if k <= n <= 21:
        return sign + digits + "0" * (n - k)
    if 0 < n <= 21:
        return sign + digits[:n] + "." + digits[n:]
    if -6 < n <= 0:
        return sign + "0." + "0" * (-n) + digits
    mantissa = digits[0] + ("." + digits[1:] if k > 1 else "")
    return f"{sign}{mantissa}e{'-' if n - 1 < 0 else '+'}{abs(n - 1)}"


_WORDS_NAN = ("nan", "snan")
_WORDS_INF = ("inf", "infinity")


def try_parse_number(text: str) -> float | None:
    """Reads the numeric text of §5.3 (``tonumber`` and the numbers of JSON text); None if it does not match.

    An optional sign, then digits with an optional fraction (``1.`` and ``.5`` included) and
    exponent, or, in any ASCII case, ``nan`` or ``snan`` (NaN), ``inf`` or ``infinity``. No white space.
    """
    i = 0
    n = len(text)
    negative = False
    if i < n and (text[i] == "+" or text[i] == "-"):
        negative = text[i] == "-"
        i += 1
    if n - i <= 8 and text[i:].isascii():
        word = text[i:].lower()
        if word in _WORDS_NAN:
            return math.nan
        if word in _WORDS_INF:
            return -_INF if negative else _INF

    digits = 0
    while i < n and "0" <= text[i] <= "9":
        i += 1
        digits += 1
    if i < n and text[i] == ".":
        i += 1
        while i < n and "0" <= text[i] <= "9":
            i += 1
            digits += 1
    if digits == 0:
        return None
    if i < n and (text[i] == "e" or text[i] == "E"):
        i += 1
        if i < n and (text[i] == "+" or text[i] == "-"):
            i += 1
        exponent_digits = 0
        while i < n and "0" <= text[i] <= "9":
            i += 1
            exponent_digits += 1
        if exponent_digits == 0:
            return None
    if i != n:
        return None
    # The grammar is checked above; the host parser only converts (out of range gives ±Infinity or 0).
    return float(text)


def try_parse(text: str) -> JqValue | Fail:
    """Reads exactly one JSON value from ``text`` (§5.3; jq's ``fromjson``).

    On failure the error is the whole message, ``<reason> (while parsing '<text>')``, the reason
    with the line and column where reading stopped (except for ``Expected JSON value`` and
    ``Unexpected extra JSON values``, as in jq).
    """
    return _JsonReader(text, False).read()


def try_parse_sequence(text: str) -> JqValue | Fail:
    """Reads a sequence of JSON values separated by white space (a data module, §9.11) as one
    array; no value at all is the empty array. Errors as :func:`try_parse`."""
    return _JsonReader(text, True).read()


_SPACE = " \t\n\r"
_DELIMITERS = frozenset(" \t\n\r[]{},:\"")
_HEX = {c: int(c, 16) for c in "0123456789abcdefABCDEF"}
_SIMPLE_ESCAPES = {'"': '"', "\\": "\\", "/": "/", "b": "\b", "f": "\f", "n": "\n", "r": "\r", "t": "\t"}
_LITERALS: dict[str, JqValue] = {"null": None, "true": True, "false": False}

# The reader's nesting limit (§5.3, §11).
READER_MAX_DEPTH = 10000


class _Frame:
    # A container being read: an array's items, or an object's keys, values and key index, with
    # the key whose value comes next.
    __slots__ = ("is_object", "items", "keys", "index", "key")

    def __init__(self, is_object: bool) -> None:
        self.is_object = is_object
        self.items: list[JqValue] = []
        self.keys: list[str] = []
        self.index: dict[str, int] = {}
        self.key = ""

    def add(self, value: JqValue) -> None:
        if not self.is_object:
            self.items.append(value)
            return
        # Duplicate keys: the later value replaces the earlier one, at the earlier position.
        key = self.key
        slot = self.index.get(key)
        if slot is None:
            self.index[key] = len(self.keys)
            self.keys.append(key)
            self.items.append(value)
        else:
            self.items[slot] = value

    def to_value(self) -> JqValue:
        if self.is_object:
            return JqObject.from_owned(self.keys, self.items, self.index)
        return JqArray.from_owned(self.items)


class _JsonReader:
    """A JSON reader without recursion (an explicit stack), so deep input cannot exhaust the host
    stack. Like jq, a literal is any run of characters up to white space, a quote or one of
    ``[]{},:``; a raw control character inside a string is an error, and every lone surrogate
    (escaped or raw) becomes U+FFFD."""

    __slots__ = ("text", "sequence", "pos", "failure", "values")

    def __init__(self, text: str, sequence: bool) -> None:
        self.text = text
        self.sequence = sequence
        self.pos = 0
        self.failure: JqValue = None
        # The values read so far, when reading a sequence.
        self.values: list[JqValue] | None = [] if sequence else None

    def read(self) -> JqValue | Fail:
        ok, result = self._read_document()
        return result if ok else Fail(self.failure)

    def _skip_space(self) -> None:
        text = self.text
        pos = self.pos
        n = len(text)
        while pos < n and text[pos] in _SPACE:
            pos += 1
        self.pos = pos

    def _read_document(self) -> tuple[bool, JqValue]:
        # The position is the local pos, kept in self.pos around the calls of the helpers.
        text = self.text
        n = len(text)
        pos = 1 if n > 0 and text[0] == "\ufeff" else 0  # a byte order mark
        while pos < n and text[pos] in _SPACE:
            pos += 1
        values = self.values
        if pos == n:
            if values is not None:
                return True, JqArray.EMPTY
            return self._fail_without_position(errors.EXPECTED_VALUE)

        stack: list[_Frame] = []
        while True:
            # A value starts at pos (white space skipped).
            if pos == n:
                return self._fail(errors.UNFINISHED_AT_EOF)
            c = text[pos]
            current: JqValue
            if c == '"':
                # A string without escape or control character: one slice.
                start = pos + 1
                quote = text.find('"', start)
                s = text[start:quote] if quote >= 0 else ""
                if quote >= 0 and (not s or (s.isprintable() and "\\" not in s)):
                    current = s if s.isascii() else sanitize(s)
                    pos = quote + 1
                else:
                    self.pos = start
                    current = self._read_string_slow()
                    if current is None:
                        return False, None
                    pos = self.pos
            elif c == "[" or c == "{":
                if len(stack) == READER_MAX_DEPTH:
                    return self._fail(errors.DEPTH_LIMIT, pos + 1)
                pos += 1
                while pos < n and text[pos] in _SPACE:
                    pos += 1
                frame = _Frame(c == "{")
                if pos < n and text[pos] == ("}" if c == "{" else "]"):
                    pos += 1
                    current = frame.to_value()
                else:
                    stack.append(frame)
                    if frame.is_object:
                        self.pos = pos
                        if not self._read_key(frame):
                            return False, None
                        pos = self.pos
                    continue
            elif c == "]" or c == "}":
                if not stack:
                    return self._fail(errors.unmatched(c), pos + 1)
                return self._fail(errors.KEY_VALUE_PAIRS if stack[-1].is_object else errors.EXPECTED_ARRAY_ELEMENT, pos + 1)
            elif c == "," or c == ":":
                return self._fail(errors.expected_value_before(c), pos + 1)
            else:
                self.pos = pos
                ok, current = self._read_literal()
                if not ok:
                    return False, None
                pos = self.pos

            # Attach the value to its container; close containers as they end.
            while True:
                while pos < n and text[pos] in _SPACE:
                    pos += 1
                if not stack:
                    if values is not None:
                        # A sequence goes on with the next value, if any.
                        values.append(current)
                        if pos < n:
                            break
                        return True, JqArray.from_owned(values)
                    if pos != n:
                        return self._fail_without_position(errors.EXTRA_VALUES)
                    return True, current
                top = stack[-1]
                top.add(current)
                if pos == n:
                    return self._fail(errors.UNFINISHED_AT_EOF)
                d = text[pos]
                pos += 1
                if d == ("}" if top.is_object else "]"):
                    stack.pop()
                    current = top.to_value()
                    continue
                if d != ",":
                    return self._fail(errors.unmatched(d) if d == "]" or d == "}" else errors.EXPECTED_SEPARATOR, pos)
                while pos < n and text[pos] in _SPACE:
                    pos += 1
                if top.is_object:
                    self.pos = pos
                    if not self._read_key(top):
                        return False, None
                    pos = self.pos
                break

    def _read_key(self, frame: _Frame) -> bool:
        # Reads `"key" :` and the white space after it.
        text = self.text
        n = len(text)
        if self.pos == n:
            self._fail(errors.UNFINISHED_AT_EOF)
            return False
        if text[self.pos] != '"':
            self._fail(errors.KEYS_MUST_BE_STRINGS, self.pos + 1)
            return False
        self.pos += 1
        key = self._read_string()
        if key is None:
            return False
        self._skip_space()
        if self.pos == n:
            self._fail(errors.UNFINISHED_AT_EOF)
            return False
        if text[self.pos] != ":":
            self._fail(errors.KEY_VALUE_PAIRS, self.pos + 1)
            return False
        self.pos += 1
        self._skip_space()
        frame.key = key
        return True

    def _read_string(self) -> str | None:
        # Reads the rest of a string after its opening quote; None on failure.
        text = self.text
        pos = self.pos
        quote = text.find('"', pos)
        if quote >= 0:
            # The common case: no escape before the closing quote.
            if text.find("\\", pos, quote) < 0:
                s = text[pos:quote]
                if not s or s.isprintable() or min(s) >= " ":
                    self.pos = quote + 1
                    return s if s.isascii() else sanitize(s)
        return self._read_string_slow()

    def _read_string_slow(self) -> str | None:
        text = self.text
        n = len(text)
        parts: list[str] = []
        pos = self.pos
        run = pos
        while True:
            if pos == n:
                self.pos = pos
                self._fail(errors.UNFINISHED_AT_EOF)
                return None
            c = text[pos]
            if c == '"':
                parts.append(text[run:pos])
                self.pos = pos + 1
                return sanitize("".join(parts))
            if c < " ":
                self.pos = pos
                self._fail(errors.CONTROL_CHARACTER, pos + 1)
                return None
            if c != "\\":
                pos += 1
                continue
            parts.append(text[run:pos])
            pos += 1
            if pos == n:
                self.pos = pos
                self._fail(errors.UNFINISHED_AT_EOF)
                return None
            e = text[pos]
            pos += 1
            simple = _SIMPLE_ESCAPES.get(e)
            if simple is not None:
                parts.append(simple)
            elif e == "u":
                unit = 0
                for _ in range(4):
                    if pos == n:
                        self.pos = pos
                        self._fail(errors.UNFINISHED_AT_EOF)
                        return None
                    digit = _HEX.get(text[pos], -1)
                    pos += 1
                    if digit < 0:
                        self.pos = pos
                        self._fail(errors.INVALID_UNICODE_ESCAPE, pos)
                        return None
                    unit = unit * 16 + digit
                # A lone surrogate is joined or replaced by sanitize above.
                parts.append(chr(unit))
            else:
                self.pos = pos
                self._fail(errors.INVALID_ESCAPE, pos)
                return None
            run = pos

    def _read_literal(self) -> tuple[bool, JqValue]:
        text = self.text
        n = len(text)
        start = pos = self.pos
        while pos < n and text[pos] not in _DELIMITERS:
            pos += 1
        self.pos = pos
        token = text[start:pos]
        literal = _LITERALS.get(token, _LITERALS)
        if literal is not _LITERALS:
            return True, literal
        if token.isdigit() and token.isascii():
            return True, float(token)
        number = try_parse_number(token)
        if number is None:
            first = token[0]
            letter = ("a" <= first <= "z") or ("A" <= first <= "Z")
            return self._fail(errors.INVALID_LITERAL if letter else errors.INVALID_NUMERIC_LITERAL, pos)
        return True, number

    def _fail(self, message: str, consumed: int = -1) -> tuple[bool, JqValue]:
        # consumed: how many characters were read when the error was found; the column counts
        # them on their line, as jq does ("{" fails at line 1, column 1).
        text = self.text
        if consumed < 0:
            consumed = len(text)
        line = 1
        line_start = 0
        limit = min(consumed, len(text))
        i = text.find("\n", 0, limit)
        while i >= 0:
            line += 1
            line_start = i + 1
            i = text.find("\n", i + 1, limit)
        return self._fail_without_position(errors.at_position(message, line, consumed - line_start))

    def _fail_without_position(self, reason: str) -> tuple[bool, JqValue]:
        self.failure = errors.while_parsing(reason, text=self.text)
        return False, None
