"""The regex flavor of §9.10 (RegexSyntax.cs).

Reads a jqlite pattern, rejects everything outside the flavor, and writes an equivalent program for
the engine (regex_engine.py) together with the names of its capturing groups in the order of their
'('.

The program is made of the instructions of `Op`, with the meaning the spec demands spelled out:
`.`, classes and `\\d \\w \\s` as sets of code points, `$` as the end of the input, `\\b \\B` as
ASCII word boundaries. The `i` flag is done here: it folds ASCII letters only
(RangeSet.add_ascii_cases, the one place of the rule). No host regex engine reads the flavor with
one meaning, so none is used (docs/porting.md §3.3).

The limits of §11 are checked here, while the pattern is read (_Measure), so that the engine never
meets a pattern beyond them and takes every one within.

The pattern is read in one pass, without recursion, and each instruction that jumps says how far
(not where to), so a piece of the program can be copied or moved as it is: a quantifier wraps or
repeats the instructions of the atom before it.

A Python `str` is a sequence of code points already: the pattern is read as it is (the reference
decodes its UTF-16 text first).
"""

from __future__ import annotations

from typing import NamedTuple

# The largest count of a quantifier, and of the counts of nested quantifiers multiplied.
MAX_REPEAT = 1000
# The largest size of a pattern (§9.10): its items, counted repetitions written out.
MAX_SIZE = 10000
# The most groups open at once.
MAX_NESTING = 100
# The most capturing groups.
MAX_GROUPS = 1000
# The reason of a pattern beyond MAX_SIZE, MAX_NESTING or MAX_GROUPS: a fixed text (§10.3).
TOO_LARGE = "the pattern is too large"

MAX_CODE_POINT = 0x10FFFF


class Options(NamedTuple):
    """The flags of §9.10 that change the pattern itself."""

    extended: bool
    dot_all: bool
    ignore_case: bool


class Op:
    """The instructions of a program; `a` and `b` are their operands. The four that read a code
    point come first: the engine tells them from the others by `op <= ANY_BUT_LINE_FEED`."""

    # The code point a.
    CHAR = 0
    # A code point of the set a of the program.
    SET = 1
    # Any code point.
    ANY = 2
    # Any code point but U+000A.
    ANY_BUT_LINE_FEED = 3
    # Go on a instructions further; if that fails, b instructions further.
    SPLIT = 4
    # Go on a instructions further.
    JUMP = 5
    # Store the position in slot a (2g: the start of group g; 2g + 1: its end; group 0 is the match).
    SAVE = 6
    # The start of the input.
    BEGIN = 7
    # The end of the input.
    END = 8
    # An ASCII word boundary.
    WORD_BOUNDARY = 9
    # Not an ASCII word boundary.
    NOT_WORD_BOUNDARY = 10
    # Store the position in mark a: where an iteration of a repetition starts.
    MARK = 11
    # Go on b instructions further when the position is the one of mark a (an iteration that
    # matched nothing ends its repetition); else to the next instruction.
    EMPTY_EXIT = 12
    # The pattern matched.
    MATCH = 13


class Translation:
    """A translated pattern: the program (an instruction and its operands `a`, `b` per index), its
    sets of code points (sorted pairs low, high), the number of its marks, and one name (or None)
    per capturing group."""

    __slots__ = ("ops", "a", "b", "sets", "marks", "groups")

    def __init__(self, ops: list[int], a: list[int], b: list[int], sets: list[list[int]], marks: int, groups: tuple[str | None, ...]) -> None:
        self.ops = ops
        self.a = a
        self.b = b
        self.sets = sets
        self.marks = marks
        self.groups = groups


# What precedes a quantifier: only an atom may be repeated.
_NONE = 0
_ATOM = 1
_QUANTIFIER = 2
_ANCHOR = 3

_METACHARACTERS = frozenset("\\.*+?()[]{}|^$/-")
_SIMPLE_ESCAPES = {"t": 0x09, "n": 0x0A, "r": 0x0D, "f": 0x0C, "v": 0x0B}
_SHORTHANDS = "dwsDWS"


class _Failure:
    __slots__ = ("reason",)

    def __init__(self, reason: str) -> None:
        self.reason = reason


def try_translate(pattern: str, options: Options) -> Translation | str:
    """The translation, or the reason the pattern is outside the flavor."""
    return _Translator(pattern, options).try_translate()


class _Frame:
    # An open group, or the whole pattern: where it starts, where its current alternative starts,
    # the jumps of its earlier alternatives to its end, and whether it can match nothing (for the
    # loops around it): `_before` is whether every item of the current alternative but the last
    # can, `last` whether the last can (None: no item), `any` whether an earlier alternative can.
    __slots__ = ("start", "group", "alternative", "jumps", "last", "any", "_before")

    def __init__(self, start: int, group: int) -> None:
        self._before = True
        self.start = start
        self.group = group
        self.alternative = start
        self.jumps: list[int] = []
        self.last: bool | None = None
        self.any = False

    def add(self, nullable: bool) -> None:
        self._before = self._before and self.last is not False
        self.last = nullable

    def close(self) -> None:
        # Ends the current alternative.
        self.any = self.any or (self._before and self.last is not False)
        self._before = True
        self.last = None


class _Body:
    # The instructions of a repeated atom; its mark (-1 when its iterations are not marked) and
    # the index where the repetition ends.
    __slots__ = ("ops", "a", "b", "mark", "end")

    def __init__(self, ops: list[int], a: list[int], b: list[int], mark: int, end: int) -> None:
        self.ops = ops
        self.a = a
        self.b = b
        self.mark = mark
        self.end = end


class _Translator:
    __slots__ = ("points", "options", "ops", "a", "b", "sets", "groups", "frames", "measure", "marks", "i", "previous", "atom", "reason")

    def __init__(self, points: str, options: Options) -> None:
        self.points = points
        self.options = options
        self.ops: list[int] = []
        self.a: list[int] = []
        self.b: list[int] = []
        self.sets: list[list[int]] = []
        self.groups: list[str | None] = []
        self.frames: list[_Frame] = []
        self.measure = _Measure()
        self.marks = 0
        self.i = 0
        self.previous = _NONE
        # Where the instructions of the last atom start.
        self.atom = 0
        self.reason = ""

    def try_translate(self) -> Translation | str:
        if not self._translate():
            return self.reason
        return Translation(self.ops, self.a, self.b, self.sets, self.marks, tuple(self.groups))

    def _translate(self) -> bool:
        points = self.points
        options = self.options
        measure = self.measure
        n = len(points)
        self._emit(Op.SAVE, 0)
        frame = _Frame(len(self.ops), 0)
        self.frames.append(frame)
        while self.i < n:
            c = points[self.i]
            if options.extended and (_is_extended_space(c) or c == "#"):
                self.i = _skip_extended(points, self.i)
                continue
            if c == "\\":
                if not self._try_escape():
                    return False
                if not measure.add(1):
                    return self._fail(TOO_LARGE)
                continue
            if c == "[":
                items = self._try_class()
                if items < 0:
                    return False
                if not measure.add(items):
                    return self._fail(TOO_LARGE)
                continue
            if c == ".":
                self._start_atom()
                self._emit(Op.ANY if options.dot_all else Op.ANY_BUT_LINE_FEED)
                if not measure.add(1):
                    return self._fail(TOO_LARGE)
                self.i += 1
                continue
            if c == "^":
                self._anchor(Op.BEGIN)
                if not measure.add(1):
                    return self._fail(TOO_LARGE)
                self.i += 1
                continue
            if c == "$":
                self._anchor(Op.END)
                if not measure.add(1):
                    return self._fail(TOO_LARGE)
                self.i += 1
                continue
            if c == "|":
                if not measure.add(1):
                    return self._fail(TOO_LARGE)
                # The alternative so far, or the rest: it ends with a jump to the end of the group.
                self._insert(frame.alternative, Op.SPLIT, 1, len(self.ops) - frame.alternative + 2)
                frame.jumps.append(len(self.ops))
                self._emit(Op.JUMP)
                frame.alternative = len(self.ops)
                frame.close()
                self.previous = _NONE
                self.i += 1
                continue
            if c == "(":
                if measure.depth == MAX_NESTING:
                    return self._fail(TOO_LARGE)
                start = len(self.ops)
                if not self._try_group():
                    return False
                if not measure.open():
                    return self._fail(TOO_LARGE)
                frame = _Frame(start, len(self.groups) if len(self.ops) > start else 0)
                frame.alternative = len(self.ops)
                self.frames.append(frame)
                self.previous = _NONE
                continue
            if c == ")":
                if len(self.frames) == 1:
                    return self._fail("unmatched close parenthesis")
                measure.close()
                self._end(frame)
                if frame.group > 0:
                    self._emit(Op.SAVE, 2 * frame.group + 1)
                self.frames.pop()
                group = frame
                frame = self.frames[-1]
                frame.add(group.any)
                self.atom = group.start
                self.previous = _ATOM
                self.i += 1
                continue
            if c in "*+?{":
                if self.previous != _ATOM:
                    return self._fail("nested quantifier" if self.previous == _QUANTIFIER else "quantifier without an operand")
                if not self._try_quantifier(frame):
                    return False
                self.previous = _QUANTIFIER
                continue
            if c == "]" or c == "}":
                return self._fail(f"unescaped '{c}'")
            self._literal(ord(c))
            if not measure.add(1):
                return self._fail(TOO_LARGE)
            self.i += 1
        if len(self.frames) > 1:
            return self._fail("missing close parenthesis")
        self._end(frame)
        self._emit(Op.SAVE, 1)
        self._emit(Op.MATCH)
        return True

    def _end(self, frame: _Frame) -> None:
        # The end of a group or of the pattern: the jumps of its alternatives arrive here.
        for jump in frame.jumps:
            self.a[jump] = len(self.ops) - jump
        frame.close()

    def _emit(self, op: int, first: int = 0, second: int = 0) -> None:
        self.ops.append(op)
        self.a.append(first)
        self.b.append(second)

    def _insert(self, at: int, op: int, first: int, second: int) -> None:
        self.ops.insert(at, op)
        self.a.insert(at, first)
        self.b.insert(at, second)

    def _start_atom(self) -> None:
        # An atom starts here: a quantifier after it repeats the instructions from here on.
        self.atom = len(self.ops)
        self.previous = _ATOM
        self.frames[-1].add(False)

    def _anchor(self, op: int) -> None:
        self._emit(op)
        self.previous = _ANCHOR
        self.frames[-1].add(True)

    def _try_escape(self) -> bool:
        # \x outside a class: a literal, a shorthand class or an anchor.
        points = self.points
        if self.i + 1 == len(points):
            return self._fail("trailing backslash")
        c = points[self.i + 1]
        if c == "b" or c == "B":
            self._anchor(Op.WORD_BOUNDARY if c == "b" else Op.NOT_WORD_BOUNDARY)
            self.i += 2
            return True
        if c in _SHORTHANDS:
            ranges = RangeSet()
            ranges.add_shorthand(c)
            self._start_atom()
            self._class(ranges, False)
            self.i += 2
            return True
        result = _try_escaped_literal(points, self.i)
        if isinstance(result, _Failure):
            return self._fail(result.reason)
        self.i, literal = result
        self._literal(literal)
        return True

    def _try_group(self) -> bool:
        # ( (?: or (?<name>: a capturing group starts with the instruction that stores its start.
        points = self.points
        i = self.i
        n = len(points)
        if i + 1 < n and points[i + 1] == "?":
            if i + 2 < n and points[i + 2] == ":":
                self.i = i + 3
                return True
            if i + 2 < n and points[i + 2] == "<":
                start = i + 3
                end = start
                while end < n and points[end] != ">":
                    end += 1
                if end == n or not _is_group_name(points, start, end):
                    return self._fail("invalid group name")
                name = points[start:end]
                if name in self.groups:
                    return self._fail(f"duplicate group name {name}")
                if len(self.groups) == MAX_GROUPS:
                    return self._fail(TOO_LARGE)
                self.groups.append(name)
                self._emit(Op.SAVE, 2 * len(self.groups))
                self.i = end + 1
                return True
            return self._fail("unsupported group syntax (?")
        if len(self.groups) == MAX_GROUPS:
            return self._fail(TOO_LARGE)
        self.groups.append(None)
        self._emit(Op.SAVE, 2 * len(self.groups))
        self.i = i + 1
        return True

    def _try_quantifier(self, frame: _Frame) -> bool:
        # * + ? {n} {n,} {n,m}, each optionally followed by ? (lazy).
        points = self.points
        n = len(points)
        c = points[self.i]
        # The count of §9.10: how many times the operand is written out.
        count = 1
        if c == "{":
            number = _try_number(points, self.i + 1)
            if number is None:
                return self._fail("'{' is not a quantifier")
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
                        return self._fail("'{' is not a quantifier")
                    at, high = number
            if at == n or points[at] != "}":
                return self._fail("'{' is not a quantifier")
            if low > MAX_REPEAT or (not unbounded and (high > MAX_REPEAT or high < low)):
                return self._fail("invalid repetition count")
            count = max(low if unbounded else high, 1)
            if not self.measure.multiply(count):
                return self._fail("invalid repetition count")
            # -1: no maximum.
            if unbounded:
                high = -1
            self.i = at + 1
        else:
            low = 1 if c == "+" else 0
            high = 1 if c == "?" else -1
            self.i += 1
        if not self.measure.repeat(count):
            return self._fail(TOO_LARGE)
        if self.options.extended:
            while self.i < n and (_is_extended_space(points[self.i]) or points[self.i] == "#"):
                self.i = _skip_extended(points, self.i)
        lazy = False
        if self.i < n and points[self.i] == "?":
            lazy = True
            self.i += 1
        return self._repeat(frame, low, high, lazy)

    def _repeat(self, frame: _Frame, low: int, high: int, lazy: bool) -> bool:
        # Repeats the last atom: its instructions `low` times, then a loop (no maximum) or
        # high - low optional copies. Each copy is an iteration. When the atom can match nothing
        # and there can be several iterations, each marks where it starts, and one that matched
        # nothing ends the repetition, whatever its counts (§9.10): `"ca" | match("(c??){2}a")`
        # captures "" at 1.
        start = self.atom
        length = len(self.ops) - start
        nullable = frame.last is not False
        marked = nullable and (high < 0 or high > 1)
        iteration = length + (2 if marked else 0)
        size = low * iteration + (iteration + 2 if high < 0 else (high - low) * (iteration + 1))
        mark = -1
        if marked:
            mark = self.marks
            self.marks += 1
        body = _Body(self.ops[start:], self.a[start:], self.b[start:], mark, start + size)
        del self.ops[start:]
        del self.a[start:]
        del self.b[start:]
        for _ in range(low):
            self._copy(body)
        if high < 0:
            # again: split (iteration, end); iteration; jump again; end:
            again = len(self.ops)
            self._emit(Op.SPLIT, body.end - again if lazy else 1, 1 if lazy else body.end - again)
            self._copy(body)
            self._emit(Op.JUMP, again - len(self.ops))
        else:
            # split (iteration, end); iteration; split (iteration, end); iteration; …; end:
            for _ in range(high - low):
                exit = body.end - len(self.ops)
                self._emit(Op.SPLIT, exit if lazy else 1, 1 if lazy else exit)
                self._copy(body)
        frame.last = low == 0 or nullable
        return True

    def _copy(self, body: _Body) -> None:
        # One iteration of a repetition: the atom, between its mark and the exit of an empty iteration.
        if body.mark >= 0:
            self._emit(Op.MARK, body.mark)
        self.ops.extend(body.ops)
        self.a.extend(body.a)
        self.b.extend(body.b)
        if body.mark >= 0:
            self._emit(Op.EMPTY_EXIT, body.mark, body.end - len(self.ops))

    def _try_class(self) -> int:
        # [...] or [^...]: literals, escapes, ranges of literals and shorthands; the number of its
        # items, or -1 when it fails.
        items = 0
        points = self.points
        n = len(points)
        at = self.i + 1
        negated = at < n and points[at] == "^"
        if negated:
            at += 1
        first = at
        ranges = RangeSet()
        previous_literal = -1
        while True:
            if at == n:
                return self._fail_class("missing ']'")
            c = points[at]
            if c == "]":
                break
            if c == "[":
                return self._fail_class("unescaped '[' in a class")
            if c == "-" and at != first and at + 1 < n and points[at + 1] != "]":
                # A range: the item before must be a single literal, the one after a literal too.
                if previous_literal < 0:
                    return self._fail_class("'-' must be first, last, escaped or between two literals")
                result = _try_class_literal(points, at + 1)
                if isinstance(result, _Failure):
                    return self._fail_class(result.reason)
                at, high = result
                if high < previous_literal:
                    return self._fail_class("range out of order")
                ranges.add(previous_literal, high)
                previous_literal = -1
                continue
            if c == "\\" and at + 1 < n and points[at + 1] in _SHORTHANDS:
                ranges.add_shorthand(points[at + 1])
                previous_literal = -1
                items += 1
                at += 2
                continue
            result = _try_class_literal(points, at)
            if isinstance(result, _Failure):
                return self._fail_class(result.reason)
            at, literal = result
            ranges.add(literal, literal)
            previous_literal = literal
            items += 1
        if at == first:
            return self._fail_class("empty class")
        # The i flag folds before a negation applies: [^a] matches neither a nor A.
        if self.options.ignore_case:
            ranges.add_ascii_cases()
        self._start_atom()
        self._class(ranges, negated)
        self.i = at + 1
        return items

    def _class(self, ranges: RangeSet, negated: bool) -> None:
        # A class as a set of the program: a negated class is its complement.
        pairs: list[int] = []
        for low, high in (ranges.complement() if negated else ranges).ranges():
            pairs.append(low)
            pairs.append(high)
        self.sets.append(pairs)
        self._emit(Op.SET, len(self.sets) - 1)

    def _literal(self, c: int) -> None:
        # A code point; under the i flag an ASCII letter becomes the class of its two cases.
        self._start_atom()
        if self.options.ignore_case and (0x41 <= c <= 0x5A or 0x61 <= c <= 0x7A):
            ranges = RangeSet()
            ranges.add(c, c)
            ranges.add_ascii_cases()
            self._class(ranges, False)
            return
        self._emit(Op.CHAR, c)

    def _fail(self, text: str) -> bool:
        self.reason = text
        return False

    def _fail_class(self, text: str) -> int:
        self.reason = text
        return -1


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


def _try_number(points: str, at: int) -> tuple[int, int] | None:
    value = 0
    start = at
    while at < len(points) and "0" <= points[at] <= "9":
        value = min(value * 10 + (ord(points[at]) - 48), MAX_REPEAT + 1)
        at += 1
    return (at, value) if at > start else None


def _try_class_literal(points: str, at: int) -> tuple[int, int] | _Failure:
    c = points[at]
    if c == "\\":
        return _try_escaped_literal(points, at)
    if c == "[" or c == "]":
        return _Failure(f"unescaped '{c}' in a class")
    return at + 1, ord(c)


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


class _Measure:
    """The limits of §11 on the part of a pattern read so far: its size, the groups open, and the
    counts of the counted quantifiers nested in one another."""

    __slots__ = ("_starts", "_products", "_size", "_atom", "_atom_product")

    def __init__(self) -> None:
        # Per open group: the size when it opened.
        self._starts: list[int] = []
        # For the pattern and per open group: the largest product of the counts nested in it so far.
        self._products = [1]
        self._size = 0
        # The last item, the operand of a quantifier: its size and the largest product of the counts nested in it.
        self._atom = 0
        self._atom_product = 1

    @property
    def depth(self) -> int:
        """The groups open."""
        return len(self._starts)

    def add(self, item_size: int) -> bool:
        """An item of this size (a literal, a class, an anchor, a '|'); False when the pattern is too large."""
        self._atom = item_size
        self._atom_product = 1
        self._size += item_size
        return self._size <= MAX_SIZE

    def open(self) -> bool:
        """A group opens: it counts 1; False when the pattern is too large."""
        self._starts.append(self._size)
        self._products.append(1)
        return self.add(1)

    def close(self) -> None:
        """A group closes: it is the last item, with all it holds."""
        self._atom = self._size - self._starts.pop()
        self._atom_product = self._products.pop()
        self._products[-1] = max(self._products[-1], self._atom_product)

    def multiply(self, count: int) -> bool:
        """A counted quantifier on the last item; False when the nested counts multiply above MAX_REPEAT."""
        product = count * self._atom_product
        self._products[-1] = max(self._products[-1], product)
        return product <= MAX_REPEAT

    def repeat(self, count: int) -> bool:
        """A quantifier on the last item: it counts 1, and the item once more for each count above 1;
        False when the pattern is too large."""
        self._size += self._atom * (count - 1) + 1
        return self._size <= MAX_SIZE


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
