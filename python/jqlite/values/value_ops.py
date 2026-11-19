"""The one implementation of equality, ordering, arithmetic, slicing and sorting of values (ValueOps.cs).

An operation that can fail in jq returns a :class:`Fail` holding the jqlite error value (the
message string); on success it returns the result. Nothing here raises as part of jqlite semantics.
The operations the interpreter applies take a :class:`JqString` wherever they take a ``str``
(jq_string.py says where one may be).
"""

from __future__ import annotations

import math
from functools import cmp_to_key
from typing import TYPE_CHECKING

from . import errors
from .jq_array import JqArray
from .jq_object import JqObject
from .jq_object_builder import JqObjectBuilder
from .jq_string import MIN_LENGTH, JqString, plain
from .jq_value import Fail, kind

if TYPE_CHECKING:
    from .jq_value import JqValue

_MAX_INT32 = 2147483647
_LONG_MIN = -(2**63)
_LONG_MAX = 2**63 - 1


def are_equal(a: JqValue, b: JqValue) -> bool:
    """jq's ``==``: same kind and same content. Numbers compare as doubles, so ``nan == nan`` is
    false and so is ``[nan] == [nan]``; objects are equal whatever their key order."""
    t = type(a)
    o = type(b)
    if t is not o:
        # A JqString and a str: their texts (any other pair of types differs in kind, and the
        # JqString's text is not built).
        if (t is JqString and o is str) or (t is str and o is JqString):
            return plain(a) == plain(b)
        return False
    if t is float or t is str or t is bool or a is None:
        return a == b
    if t is JqString:
        return a.text == b.text  # type: ignore[no-any-return]
    return _walk(a, b, True) == 0


def compare(a: JqValue, b: JqValue) -> int:
    """jq's total order; returns -1, 0 or 1 (§4.3). NaN is below every other number and equal to NaN."""
    t = type(a)
    if t is type(b):
        if t is float:
            if a != a:  # NaN
                return 0 if b != b else -1
            if b != b:
                return 1
            return -1 if a < b else (0 if a == b else 1)  # type: ignore[operator]
        if t is str:
            return -1 if a < b else (0 if a == b else 1)  # type: ignore[operator]
    if (t is JqString or type(b) is JqString) and kind(a) == kind(b):
        return compare(plain(a), plain(b))
    return _walk(a, b, False)


def _shallow(a: JqValue, b: JqValue, equality: bool) -> tuple[int, list | None]:
    # Compares what can be compared without looking at children. A non-zero result decides. With
    # zero, a frame [A, B, count, index, tail, keys] means the children must be compared, and its
    # tail is the result once they are all equal.
    k = kind(a)
    other = kind(b)
    if k != other:
        return (-1 if k < other else 1), None
    t = type(a)
    if t is float:
        x = a
        y = b
        if equality:
            return (0 if x == y else 1), None
        if x != x:
            return (0 if y != y else -1), None
        if y != y:
            return 1, None
        return (-1 if x < y else (0 if x == y else 1)), None  # type: ignore[operator]
    if t is str:
        if equality:
            return (0 if a == b else 1), None
        return (-1 if a < b else (0 if a == b else 1)), None  # type: ignore[operator]
    if t is JqArray:
        p = a.count  # type: ignore[union-attr]
        q = b.count  # type: ignore[union-attr]
        if equality and p != q:
            return 1, None
        m = p if p < q else q
        tail = 0 if p == q else (-1 if p < q else 1)
        if m == 0:
            return tail, None
        return 0, [a.to_list(), b.to_list(), m, 0, tail, None]  # type: ignore[union-attr]
    if t is JqObject:
        m_ = a
        n_ = b
        if equality:
            # Same size, then each key of a found in b with an equal value: no sorting.
            if m_.count != n_.count:  # type: ignore[union-attr]
                return 1, None
            if m_.count == 0:  # type: ignore[union-attr]
                return 0, None
            return 0, [m_, n_, m_.count, 0, 0, None]  # type: ignore[union-attr]
        keys = m_.sorted_keys()  # type: ignore[union-attr]
        others = n_.sorted_keys()  # type: ignore[union-attr]
        if keys != others:
            length = min(len(keys), len(others))
            for i in range(length):
                x = keys[i]
                y = others[i]
                if x != y:
                    return (-1 if x < y else 1), None
            return (-1 if len(keys) < len(others) else 1), None
        if not keys:
            return 0, None
        return 0, [m_, n_, len(keys), 0, 0, keys]
    return 0, None


def _child(frame: list, equality: bool) -> tuple[bool, JqValue, JqValue]:
    # The children at the frame's index; false when b lacks a key of a (equality of objects only).
    a = frame[0]
    b = frame[1]
    i = frame[3]
    if type(a) is list:
        return True, a[i], b[i]
    if equality:
        slot = b.index.get(a.keys[i])
        if slot is None or slot >= b.count:
            return False, None, None
        return True, a.values[i], b.values[slot]
    key = frame[5][i]
    return True, a.get(key), b.get(key)


def _walk(a: JqValue, b: JqValue, equality: bool) -> int:
    """Compares (equality false) or tests equality (equality true, any non-zero result means
    "different") without recursion: the containers being compared are on an explicit stack, so
    nesting depth is not limited by the host stack."""
    c, frame = _shallow(a, b, equality)
    if c != 0 or frame is None:
        return c
    stack = [frame]
    ok, a, b = _child(frame, equality)
    if not ok:
        return 1
    while True:
        c, frame = _shallow(a, b, equality)
        if c != 0:
            return c
        if frame is not None:
            stack.append(frame)
            ok, a, b = _child(frame, equality)
            if not ok:
                return 1
            continue
        # The pair is equal: go on with the next pair, leaving the containers that end.
        while True:
            if not stack:
                return 0
            top = stack[-1]
            top[3] += 1
            if top[3] < top[2]:
                ok, a, b = _child(top, equality)
                if not ok:
                    return 1
                break
            stack.pop()
            if top[4] != 0:
                return top[4]


def try_add(a: JqValue, b: JqValue) -> JqValue | Fail:
    """jq's ``+``: null is the identity; numbers add; strings and arrays concatenate; objects merge."""
    if a is None:
        return b
    if b is None:
        return a
    t = type(a)
    o = type(b)
    if t is o:
        if t is float:
            return a + b  # type: ignore[operator]
        if t is str:
            if len(a) + len(b) < MIN_LENGTH:  # type: ignore[arg-type]
                return a + b  # type: ignore[operator]
            return concat(a, b)
        if t is JqArray:
            return a.concat(b)  # type: ignore[union-attr,arg-type]
        if t is JqObject:
            return a.merge(b)  # type: ignore[union-attr,arg-type]
    if (t is str or t is JqString) and (o is str or o is JqString):
        return concat(a, b)
    return Fail(errors.cannot_be(a, b, errors.ADDED))


def concat(left: JqValue, right: JqValue) -> JqValue:
    """jq's ``+`` on two strings (JqValue.Concat). Both are well-formed, so their concatenation is
    too and is not sanitized again. A long result is a JqString, which the next concatenation
    extends in place."""
    tail = right.text if type(right) is JqString else right
    if not tail:
        return left
    if type(left) is JqString:
        return left.append(tail)
    if not left:
        return right
    if len(left) + len(tail) < MIN_LENGTH:
        return left + tail  # type: ignore[no-any-return]
    return JqString.concat(left, tail)


def try_subtract(a: JqValue, b: JqValue) -> JqValue | Fail:
    """jq's ``-``: numbers subtract; arrays drop every element equal to one of the right-hand array."""
    t = type(a)
    if t is float and type(b) is float:
        return a - b  # type: ignore[operator]
    if t is JqArray and type(b) is JqArray:
        remove = b.to_list()  # type: ignore[union-attr]
        kept = [x for x in a.to_list() if not any(are_equal(x, r) for r in remove)]  # type: ignore[union-attr]
        return JqArray.from_owned(kept)
    return Fail(errors.cannot_be(a, b, errors.SUBTRACTED))


def try_negate(value: JqValue) -> JqValue | Fail:
    """jq's unary ``-``: IEEE negation of a number (``0 | -.`` is -0)."""
    if type(value) is not float:
        return Fail(errors.cannot_negate(value))
    return -value  # type: ignore[operator]


def try_multiply(a: JqValue, b: JqValue) -> JqValue | Fail:
    """jq's ``*``: numbers multiply; a string and a number (either order) repeat the string; objects merge recursively."""
    t = type(a)
    o = type(b)
    if t is float and o is float:
        return a * b  # type: ignore[operator]
    if t is str and o is float:
        return _try_repeat(a, b)  # type: ignore[arg-type]
    if t is float and o is str:
        return _try_repeat(b, a)  # type: ignore[arg-type]
    if t is JqObject and o is JqObject:
        return _deep_merge(a, b)  # type: ignore[arg-type]
    if t is JqString or o is JqString:
        return try_multiply(plain(a), plain(b))
    return Fail(errors.cannot_be(a, b, errors.MULTIPLIED))


def _try_repeat(text: str, times: float) -> JqValue | Fail:
    # s * n: null for a NaN or negative n; otherwise s repeated trunc(n) times ("" below 1).
    if times != times or times < 0:
        return None
    count = _MAX_INT32 if times >= _MAX_INT32 else int(times)
    if count == 0 or not text:
        return ""
    size = len(text) if text.isascii() else len(text.encode("utf-8"))
    if size * count > _MAX_INT32:
        return Fail(errors.REPEAT_TOO_LONG)
    return text * count


class _Merge:
    # One pair of objects being merged: the result so far, the right side's next key, and the key
    # whose values are being merged below.
    __slots__ = ("result", "right", "next", "key")

    def __init__(self, left: JqObject, right: JqObject) -> None:
        self.result = JqObjectBuilder(left)
        self.right = right
        self.next = 0
        self.key = ""


def _deep_merge(a: JqObject, b: JqObject) -> JqObject:
    # a * b on objects: like a + b, except that a key whose two values are objects gets their merge.
    # The objects being merged are on an explicit stack, so nesting depth is not limited by the host stack.
    stack = [_Merge(a, b)]
    while True:
        top = stack[-1]
        if top.next < top.right.count:
            key = top.right.keys[top.next]
            value = top.right.values[top.next]
            top.next += 1
            if type(value) is JqObject:
                existing = top.result.get(key)
                if type(existing) is JqObject:
                    top.key = key
                    stack.append(_Merge(existing, value))
                    continue
            top.result.set(key, value)
            continue
        merged = top.result.to_object()
        stack.pop()
        if not stack:
            return merged
        stack[-1].result.set(stack[-1].key, merged)


def try_divide(a: JqValue, b: JqValue) -> JqValue | Fail:
    """jq's ``/``: numbers divide (a zero divisor is an error, ``0/0`` too); strings split on the right-hand string."""
    t = type(a)
    if t is float and type(b) is float:
        if b == 0:
            return Fail(errors.divisor_is_zero(a, b, errors.DIVIDED))
        return a / b  # type: ignore[operator]
    if t is str and type(b) is str:
        return split(a, b)  # type: ignore[arg-type]
    if t is JqString or type(b) is JqString:
        return try_divide(plain(a), plain(b))
    return Fail(errors.cannot_be(a, b, errors.DIVIDED))


def split(text: str, separator: str) -> JqArray:
    """``split/1`` and ``/`` on strings: the empty string gives []; an empty separator splits into code points."""
    if not text:
        return JqArray.EMPTY
    if not separator:
        return JqArray.from_owned(list(text))
    return JqArray.from_owned(text.split(separator))  # type: ignore[arg-type]


def try_modulo(a: JqValue, b: JqValue) -> JqValue | Fail:
    """jq's ``%``: both numbers truncate to integers (saturating at the 64-bit range), the sign
    follows the dividend; NaN gives NaN; a divisor that truncates to 0 is an error."""
    if type(a) is not float or type(b) is not float:
        return Fail(errors.cannot_be(a, b, errors.REMAINDER))
    x = a
    y = b
    if x != x or y != y:
        return math.nan
    divisor = _to_integer(y)  # type: ignore[arg-type]
    if divisor == 0:
        return Fail(errors.divisor_is_zero(a, b, errors.REMAINDER))
    # -1 divides everything.
    if divisor == -1:
        return 0.0
    dividend = _to_integer(x)  # type: ignore[arg-type]
    r = abs(dividend) % abs(divisor)
    return float(-r if dividend < 0 else r)


def _to_integer(value: float) -> int:
    # Truncation toward zero, clamped to [-2^63, 2^63 - 1] (±Infinity clamp to the ends).
    if value <= -9.223372036854775808e18:
        return _LONG_MIN
    if value >= 9.223372036854775807e18:
        return _LONG_MAX
    return int(value)


def try_index(target: JqValue, key: JqValue) -> JqValue | Fail:
    """``.[key]`` (§7.6): an object by a string (absent: null), an array by a number (NaN and out of
    range: null; negative counts from the end) or by an array (the indices where it occurs), an
    array or a string by a slice object, null by a string, a number or an object (null); anything
    else is ``Cannot index T with T (D)``."""
    kt = type(key)
    t = type(target)
    if kt is str:
        if t is JqObject:
            return target.get(key)  # type: ignore[union-attr,arg-type]
        if target is None:
            return None
    elif kt is float:
        if t is JqArray:
            return element(target, key)  # type: ignore[arg-type]
        if target is None:
            return None
    elif kt is JqArray:
        if t is JqArray:
            return _indices(target, key)  # type: ignore[arg-type]
    elif kt is JqObject:
        if t is JqArray or t is str or t is JqString or target is None:
            return try_index_slice(target, key)
    elif kt is JqString:
        return try_index(target, key.text)  # type: ignore[union-attr]
    return Fail(errors.cannot_index(target, key))


def element(array: JqArray, index: float) -> JqValue:
    """``.[n]`` on an array: NaN and out of range give null, a negative index counts from the end."""
    if index != index:
        return None
    count = array.count
    if -1e18 < index < 1e18:
        i = int(index)
        if i < 0:
            i += count
        if 0 <= i < count:
            return array.buffer[array.start + i]
    return None


def _indices(items: JqArray, needle: JqArray) -> JqArray:
    # The positions at which needle occurs in items as a contiguous sub-array; none for an empty needle.
    found: list[JqValue] = []
    m = needle.count
    if m == 0:
        return JqArray.EMPTY
    values = items.to_list()
    pattern = needle.to_list()
    for start in range(len(values) - m + 1):
        i = 0
        while i < m and are_equal(values[start + i], pattern[i]):
            i += 1
        if i == m:
            found.append(float(start))
    return JqArray.from_owned(found)


def try_slice(input: JqValue, start: JqValue, end: JqValue) -> JqValue | Fail:
    """``.[start:end]`` (§7.6) on an array, a string (by code point) or null (gives null)."""
    t = type(input)
    if t is JqString:
        input = input.text  # type: ignore[union-attr]
        t = str
    if t is not JqArray and t is not str and input is not None:
        # The key in the message is the slice object that `.[s:e]` stands for.
        key = JqObject.EMPTY.set("start", plain(start)).set("end", plain(end))
        return Fail(errors.cannot_index(input, key))
    return _try_slice_bounds(input, start, end)


def try_index_slice(input: JqValue, key: JqValue) -> JqValue | Fail:
    """``.[k]`` with an object key: on an array or a string the key is a slice when it has both
    ``start`` and ``end`` (other keys are ignored, as in jq), else ``Array/string slice indices
    must be integers``; null input gives null whatever the key."""
    t = type(input)
    if input is None:
        return None
    if t is JqString:
        input = input.text  # type: ignore[union-attr]
        t = str
    if t is not JqArray and t is not str:
        return Fail(errors.cannot_index(input, key))
    obj: JqObject = key  # type: ignore[assignment]
    if not obj.contains_key("start") or not obj.contains_key("end"):
        return Fail(errors.SLICE_INDICES_NOT_INTEGERS)
    return _try_slice_bounds(input, obj.get("start"), obj.get("end"))


def _try_slice_bounds(input: JqValue, start: JqValue, end: JqValue) -> JqValue | Fail:
    # Slices an array, a string or null (gives null).
    if input is None:
        return None
    is_text = type(input) is str
    length = len(input) if is_text else input.count  # type: ignore[union-attr,arg-type]
    r = try_slice_range(length, start, end)
    if type(r) is Fail:
        return r  # type: ignore[return-value]
    first, last = r  # type: ignore[misc]
    if is_text:
        return input[first:last]  # type: ignore[index]
    return input.slice(first, last)  # type: ignore[union-attr]


def try_slice_range(length: int, start: JqValue, end: JqValue) -> tuple[int, int] | Fail:
    """The positions [first, last) that the bounds of a slice select in a sequence of ``length``
    items (§7.6): null or NaN start is 0, null or NaN end the length, negative bounds count from
    the end, both are clamped, the start rounds down and the end up. Bounds that are not numbers
    or null: ``Array/string slice indices must be integers``."""
    ts = type(start)
    te = type(end)
    if (ts is not float and start is not None) or (te is not float and end is not None):
        return Fail(errors.SLICE_INDICES_NOT_INTEGERS)
    frm = 0.0 if start is None else start
    to = float(length) if end is None else end
    if frm != frm:
        frm = 0.0
    if to != to:
        to = float(length)
    if frm < 0:  # type: ignore[operator]
        frm += length  # type: ignore[operator]
    if to < 0:  # type: ignore[operator]
        to += length  # type: ignore[operator]
    frm = 0.0 if frm < 0 else (float(length) if frm > length else frm)  # type: ignore[operator]
    first = math.floor(frm)  # type: ignore[arg-type]
    to = float(first) if to < first else (float(length) if to > length else to)  # type: ignore[operator]
    last = math.ceil(to)  # type: ignore[arg-type]
    return first, last


def try_explode(input: JqValue) -> JqValue | Fail:
    """jq's ``explode``: the code points of a string."""
    if type(input) is not str:
        return Fail(errors.EXPLODE_NOT_STRING)
    return JqArray.from_owned([float(ord(c)) for c in input])  # type: ignore[union-attr]


def try_implode(input: JqValue) -> JqValue | Fail:
    """jq's ``implode``: an array of numbers to the string of the code points ``trunc(n)``; one
    outside [0, 0x10FFFF] or a surrogate becomes U+FFFD. A non-number or NaN element is an error
    that describes the element."""
    if type(input) is not JqArray:
        return Fail(errors.IMPLODE_NOT_ARRAY)
    chars: list[str] = []
    for code in input.to_list():  # type: ignore[union-attr]
        if type(code) is not float or code != code:
            return Fail(errors.codepoint_not_numeric(code))
        number = math.trunc(code) if -1e18 < code < 1e18 else -1  # type: ignore[operator,arg-type]
        scalar = 0 <= number <= 0x10FFFF and not (0xD800 <= number <= 0xDFFF)
        chars.append(chr(number) if scalar else "�")
    return "".join(chars)


def sort(items: JqArray) -> JqArray:
    """A stably sorted copy of ``items`` (jq's ``sort``)."""
    values = items.to_list()
    keys = _simple_keys(values)
    if keys is not None:
        order = sorted(range(len(values)), key=lambda i: keys[i])
        return JqArray.from_owned([values[i] for i in order])
    return JqArray.from_owned(sorted(values, key=cmp_to_key(compare)))


def sort_order(keys: JqArray) -> list[int]:
    """The positions of ``keys`` in sorted order, equal keys in their original order (a stable
    sort, for ``sort_by``, ``group_by`` and ``unique_by``).

    Difference from the reference: Python's own stable sort (Timsort) instead of the bottom-up
    merge sort; a stable sort by the same total preorder gives the same order. Keys that map to
    Python tuples ordering as ``compare`` does (scalars and arrays of them, no NaN) sort by those.
    """
    values = keys.to_list()
    simple = _simple_keys(values)
    if simple is not None:
        return sorted(range(len(values)), key=lambda i: simple[i])
    def by_key(i: int, j: int) -> int:
        return compare(values[i], values[j])
    return sorted(range(len(values)), key=cmp_to_key(by_key))


def _simple_key(value: JqValue, depth: int) -> tuple | None:
    # A Python tuple that orders as compare() does, for null, booleans, numbers other than NaN,
    # strings and arrays of these (two levels deep at most); None for anything else.
    t = type(value)
    if t is float:
        return None if value != value else (3, value)
    if t is str:
        return (4, value)
    if value is None:
        return (0,)
    if t is bool:
        return (2,) if value else (1,)
    if t is JqArray and depth < 2:
        parts = []
        for item in value.to_list():  # type: ignore[union-attr]
            k = _simple_key(item, depth + 1)
            if k is None:
                return None
            parts.append(k)
        return (5, tuple(parts))
    return None


def _simple_keys(values: list[JqValue]) -> list[tuple] | None:
    keys = []
    for v in values:
        k = _simple_key(v, 0)
        if k is None:
            return None
        keys.append(k)
    return keys

