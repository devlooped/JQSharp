"""The regex dialect of §9.10 (RegexSyntax.cs).

Reads a jqlite pattern, rejects everything outside the dialect, and writes an equivalent pattern for
the host engine (regex_engine.py) together with the names of its capturing groups in the order of
their '('. The output spells out the meaning the spec demands so that no engine default leaks in:
every code point is written as an escape, `.`, `\\d \\w \\s` and their complements as explicit
classes, `$` as the end of the input only, named groups as plain groups. The `i` flag is done
here, never by the engine: it folds ASCII letters only (RangeSet.add_ascii_cases).

Difference from the reference: Python's `re` works on code points, so classes are written
directly (the reference, for UTF-16 engines, splits them into a BMP part and surrogate pairs).
`\\b \\B` are ASCII lookarounds, as in the reference (Python's own `\\B` never matches in an
empty string).
"""

from __future__ import annotations

from typing import NamedTuple

MAX_REPEAT = 1000
MAX_CODE_POINT = 0x10FFFF


class Options(NamedTuple):
    """The flags of §9.10 that change the pattern itself."""

    extended: bool
    dot_all: bool
    ignore_case: bool


class Translation(NamedTuple):
    """A translated pattern: the host pattern, one name (or None) per capturing group, and whether
    it uses lookaround (for \\b and \\B)."""

    pattern: str
    groups: tuple[str | None, ...]
    lookaround: bool


# What precedes a quantifier: only an atom may be repeated.
_NONE = 0
_ATOM = 1
_QUANTIFIER = 2
_ANCHOR = 3

# The ASCII word boundaries of §9.10, by the ASCII \w; the engine's own \B never matches in an empty string.
_WORD = "[0-9A-Za-z_]"
_BOUNDARY = f"(?:(?<={_WORD})(?!{_WORD})|(?<!{_WORD})(?={_WORD}))"
_NOT_BOUNDARY = f"(?:(?<={_WORD})(?={_WORD})|(?<!{_WORD})(?!{_WORD}))"

_METACHARACTERS = frozenset("\\.*+?()[]{}|^$/-")
_SIMPLE_ESCAPES = {"t": 0x09, "n": 0x0A, "r": 0x0D, "f": 0x0C, "v": 0x0B}
_SHORTHANDS = "dwsDWS"


class _Failure:
    __slots__ = ("reason",)

    def __init__(self, reason: str) -> None:
        self.reason = reason


# Translations by pattern and options, emptied when it reaches CACHE_LIMIT entries: a program that
# runs one regex builtin per value translates its pattern once (the reference translates per call,
# which costs most of a short match in Python). A translation is a pure function of its key, so
# threads that race on an entry store equal values; a port keeps or drops this cache freely.
CACHE_LIMIT = 256
_cache: dict[tuple[str, Options], Translation | str] = {}


def try_translate(pattern: str, options: Options) -> Translation | str:
    """The translation, or the reason the pattern is outside the dialect."""
    key = (pattern, options)
    cached = _cache.get(key)
    if cached is None:
        cached = _translate(pattern, options)
        if len(_cache) >= CACHE_LIMIT:
            _cache.clear()
        _cache[key] = cached
    return cached


def _translate(pattern: str, options: Options) -> Translation | str:
    points = pattern
    output: list[str] = []
    groups: list[str | None] = []
    open_groups = 0
    previous = _NONE
    lookaround = False
    n = len(points)
    i = 0
    while i < n:
        c = points[i]
        if options.extended and (_is_extended_space(c) or c == "#"):
            i = _skip_extended(points, i)
            continue
        if c == "\\":
            result = _try_escape(points, i, output, options)
            if isinstance(result, _Failure):
                return result.reason
            i, previous = result
            lookaround = lookaround or previous == _ANCHOR
            continue
        if c == "[":
            result2 = _try_class(points, i, output, options)
            if isinstance(result2, _Failure):
                return result2.reason
            i = result2
            previous = _ATOM
            continue
        if c == ".":
            output.append("[\\x00-\\U0010FFFF]" if options.dot_all else "[^\\n]")
            previous = _ATOM
            i += 1
            continue
        if c == "^":
            output.append("^")
            previous = _ANCHOR
            i += 1
            continue
        if c == "$":
            output.append("\\Z")
            previous = _ANCHOR
            i += 1
            continue
        if c == "|":
            output.append("|")
            previous = _NONE
            i += 1
            continue
        if c == "(":
            result3 = _try_group(points, i, output, groups)
            if isinstance(result3, _Failure):
                return result3.reason
            i = result3
            open_groups += 1
            previous = _NONE
            continue
        if c == ")":
            if open_groups == 0:
                return "unmatched close parenthesis"
            open_groups -= 1
            output.append(")")
            previous = _ATOM
            i += 1
            continue
        if c in "*+?{":
            if previous != _ATOM:
                return "nested quantifier" if previous == _QUANTIFIER else "quantifier without an operand"
            result4 = _try_quantifier(points, i, output, options)
            if isinstance(result4, _Failure):
                return result4.reason
            i = result4
            previous = _QUANTIFIER
            continue
        if c == "]" or c == "}":
            return f"unescaped '{c}'"
        _literal(output, ord(c), options)
        previous = _ATOM
        i += 1
    if open_groups > 0:
        return "missing close parenthesis"
    return Translation("".join(output), tuple(groups), lookaround)


def _try_escape(points: str, i: int, output: list[str], options: Options) -> tuple[int, int] | _Failure:
    # \x outside a class: a literal, a shorthand class or an anchor.
    if i + 1 == len(points):
        return _Failure("trailing backslash")
    c = points[i + 1]
    if c == "b" or c == "B":
        output.append(_BOUNDARY if c == "b" else _NOT_BOUNDARY)
        return i + 2, _ANCHOR
    if c in _SHORTHANDS:
        ranges = RangeSet()
        ranges.add_shorthand(c)
        _class(output, ranges, False)
        return i + 2, _ATOM
    result = _try_escaped_literal(points, i)
    if isinstance(result, _Failure):
        return result
    i, literal = result
    _literal(output, literal, options)
    return i, _ATOM


def _try_escaped_literal(points: str, i: int) -> tuple[int, int] | _Failure:
    # The escapes that stand for one code point: the metacharacters, \t \n \r \f \v and \xHH.
    if i + 1 == len(points):
        return _Failure("trailing backslash")
    c = points[i + 1]
    if c in _METACHARACTERS:
        return i + 2, ord(c)
    simple = _SIMPLE_ESCAPES.get(c)
    if simple is not None:
        return i + 2, simple
    if c == "x":
        high = _hex_value(points[i + 2]) if i + 2 < len(points) else -1
        low = _hex_value(points[i + 3]) if i + 3 < len(points) else -1
        if high < 0 or low < 0:
            return _Failure("\\x must be followed by two hex digits")
        return i + 4, high * 16 + low
    return _Failure(f"invalid escape \\{c}")


def _try_group(points: str, i: int, output: list[str], groups: list[str | None]) -> int | _Failure:
    # ( (?: or (?<name>
    n = len(points)
    if i + 1 < n and points[i + 1] == "?":
        if i + 2 < n and points[i + 2] == ":":
            output.append("(?:")
            return i + 3
        if i + 2 < n and points[i + 2] == "<":
            start = i + 3
            end = start
            while end < n and points[end] != ">":
                end += 1
            if end == n or not _is_group_name(points, start, end):
                return _Failure("invalid group name")
            name = points[start:end]
            if name in groups:
                return _Failure(f"duplicate group name {name}")
            groups.append(name)
            output.append("(")
            return end + 1
        return _Failure("unsupported group syntax (?")
    groups.append(None)
    output.append("(")
    return i + 1


def _try_quantifier(points: str, i: int, output: list[str], options: Options) -> int | _Failure:
    # * + ? {n} {n,} {n,m}, each optionally followed by ? (lazy).
    n = len(points)
    c = points[i]
    if c == "{":
        at = i + 1
        number = _try_number(points, at)
        if number is None:
            return _Failure("'{' is not a quantifier")
        at, low = number
        high = low
        unbounded = False
        if at < n and points[at] == ",":
            at += 1
            if at < n and points[at] == "}":
                unbounded = True
            else:
                number = _try_number(points, at)
                if number is None:
                    return _Failure("'{' is not a quantifier")
                at, high = number
        if at == n or points[at] != "}":
            return _Failure("'{' is not a quantifier")
        if low > MAX_REPEAT or (not unbounded and (high > MAX_REPEAT or high < low)):
            return _Failure("invalid repetition count")
        output.append("{" + str(low))
        if unbounded:
            output.append(",")
        elif high != low:
            output.append("," + str(high))
        output.append("}")
        i = at + 1
    else:
        output.append(c)
        i += 1
    if options.extended:
        while i < n and (_is_extended_space(points[i]) or points[i] == "#"):
            i = _skip_extended(points, i)
    if i < n and points[i] == "?":
        output.append("?")
        i += 1
    return i


def _try_number(points: str, at: int) -> tuple[int, int] | None:
    value = 0
    start = at
    while at < len(points) and "0" <= points[at] <= "9":
        value = min(value * 10 + (ord(points[at]) - 48), MAX_REPEAT + 1)
        at += 1
    return (at, value) if at > start else None


def _try_class(points: str, i: int, output: list[str], options: Options) -> int | _Failure:
    # [...] or [^...]: literals, escapes, ranges of literals and shorthands.
    n = len(points)
    at = i + 1
    negated = at < n and points[at] == "^"
    if negated:
        at += 1
    first = at
    ranges = RangeSet()
    previous_literal = -1
    while True:
        if at == n:
            return _Failure("missing ']'")
        c = points[at]
        if c == "]":
            break
        if c == "[":
            return _Failure("unescaped '[' in a class")
        if c == "-" and at != first and at + 1 < n and points[at + 1] != "]":
            # A range: the item before must be a single literal, the one after a literal too.
            if previous_literal < 0:
                return _Failure("'-' must be first, last, escaped or between two literals")
            at += 1
            result = _try_class_literal(points, at)
            if isinstance(result, _Failure):
                return result
            at, high = result
            if high < previous_literal:
                return _Failure("range out of order")
            ranges.add(previous_literal, high)
            previous_literal = -1
            continue
        if c == "\\" and at + 1 < n and points[at + 1] in _SHORTHANDS:
            ranges.add_shorthand(points[at + 1])
            previous_literal = -1
            at += 2
            continue
        result = _try_class_literal(points, at)
        if isinstance(result, _Failure):
            return result
        at, literal = result
        ranges.add(literal, literal)
        previous_literal = literal
    if at == first:
        return _Failure("empty class")
    # The i flag folds before a negation applies: [^a] matches neither a nor A.
    if options.ignore_case:
        ranges.add_ascii_cases()
    _class(output, ranges, negated)
    return at + 1


def _try_class_literal(points: str, at: int) -> tuple[int, int] | _Failure:
    c = points[at]
    if c == "\\":
        return _try_escaped_literal(points, at)
    if c == "[" or c == "]":
        return _Failure(f"unescaped '{c}' in a class")
    return at + 1, ord(c)


def _class(output: list[str], ranges: RangeSet, negated: bool) -> None:
    # A class of code point ranges; the engine's negation is the complement over all code points.
    items = ranges.ranges()
    if not items:
        # An empty set: a class nothing is in.
        output.append("[^\\x00-\\U0010FFFF]" if not negated else "[\\x00-\\U0010FFFF]")
        return
    parts = ["[^" if negated else "["]
    for low, high in items:
        parts.append(_escape(low))
        if high > low:
            parts.append("-")
            parts.append(_escape(high))
    parts.append("]")
    output.append("".join(parts))


def _literal(output: list[str], c: int, options: Options) -> None:
    # A code point; under the i flag an ASCII letter becomes the class of its two cases.
    if options.ignore_case and (0x41 <= c <= 0x5A or 0x61 <= c <= 0x7A):
        ranges = RangeSet()
        ranges.add(c, c)
        ranges.add_ascii_cases()
        _class(output, ranges, False)
        return
    output.append(_escape(c))


def _escape(c: int) -> str:
    return f"\\x{c:02X}" if c < 0x100 else (f"\\u{c:04X}" if c < 0x10000 else f"\\U{c:08X}")


def _skip_extended(points: str, i: int) -> int:
    # With the x flag: whitespace and a # comment up to the next line feed.
    if points[i] != "#":
        return i + 1
    while i < len(points) and points[i] != "\n":
        i += 1
    return i


def _is_extended_space(c: str) -> bool:
    return "\t" <= c <= "\r" or c == " "


def _is_group_name(points: str, start: int, end: int) -> bool:
    if end == start or "0" <= points[start] <= "9":
        return False
    for i in range(start, end):
        c = points[i]
        if not (("a" <= c <= "z") or ("A" <= c <= "Z") or ("0" <= c <= "9") or c == "_"):
            return False
    return True


def _hex_value(c: str) -> int:
    if "0" <= c <= "9":
        return ord(c) - 48
    if "a" <= c <= "f":
        return ord(c) - 87
    if "A" <= c <= "F":
        return ord(c) - 55
    return -1


class RangeSet:
    """A set of code points as sorted, disjoint, non-adjacent ranges."""

    __slots__ = ("_ranges", "_normalized")

    def __init__(self) -> None:
        self._ranges: list[tuple[int, int]] = []
        self._normalized = True

    def ranges(self) -> list[tuple[int, int]]:
        self._normalize()
        return self._ranges

    def add(self, low: int, high: int) -> None:
        self._ranges.append((low, high))
        self._normalized = False

    def add_shorthand(self, c: str) -> None:
        # The ASCII classes of §9.10 and their complements.
        ranges = RangeSet()
        if c in "dD":
            ranges.add(0x30, 0x39)
        elif c in "wW":
            ranges.add(0x30, 0x39)
            ranges.add(0x41, 0x5A)
            ranges.add(0x61, 0x7A)
            ranges.add(0x5F, 0x5F)
        else:
            ranges.add(0x09, 0x0D)
            ranges.add(0x20, 0x20)
        for low, high in (ranges.complement() if c in "DWS" else ranges).ranges():
            self.add(low, high)

    def add_ascii_cases(self) -> None:
        """The i flag of §9.10: adds the other case of every ASCII letter in the set (A-Z and a-z,
        32 apart); no other code point folds."""
        added: list[tuple[int, int]] = []
        for low, high in self.ranges():
            upper_low = max(low, 0x41)
            upper_high = min(high, 0x5A)
            if upper_low <= upper_high:
                added.append((upper_low + 32, upper_high + 32))
            lower_low = max(low, 0x61)
            lower_high = min(high, 0x7A)
            if lower_low <= lower_high:
                added.append((lower_low - 32, lower_high - 32))
        for low, high in added:
            self.add(low, high)

    def complement(self) -> RangeSet:
        result = RangeSet()
        nxt = 0
        for low, high in self.ranges():
            if low > nxt:
                result.add(nxt, low - 1)
            nxt = high + 1
        if nxt <= MAX_CODE_POINT:
            result.add(nxt, MAX_CODE_POINT)
        return result

    def _normalize(self) -> None:
        if self._normalized:
            return
        self._ranges.sort()
        merged: list[tuple[int, int]] = []
        for low, high in self._ranges:
            if merged and low <= merged[-1][1] + 1:
                merged[-1] = (merged[-1][0], max(merged[-1][1], high))
            else:
                merged.append((low, high))
        self._ranges = merged
        self._normalized = True
