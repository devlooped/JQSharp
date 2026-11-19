"""Converts Python's JSON values to jqlite values and back (the counterpart of JsonElementAdapter.cs).

The only place outside the core that knows both representations. Python's JSON values are what
`json.loads` gives: `None`, `bool`, `int`, `float`, `str`, `list` and `dict` (tuples are
taken as lists, and any `Mapping` with string keys as an object). Both directions are loops with
an explicit stack, so values 10,000 levels deep convert (§11).

In: `int` becomes `float` (the nearest double, an overflow ±Infinity); a lone surrogate in a string
or a key becomes U+FFFD (§4.1). Out: the value as `json.loads` reads its canonical text (§5.1): a
number without fraction below 1e21 in magnitude is an `int` (`-0` is `0`), any other number a
`float`, NaN is `None` and ±Infinity ±1.7976931348623157e+308; objects keep their key order.
"""

from __future__ import annotations

import math
from collections.abc import Mapping
from typing import Any

from .values.code_points import sanitize
from .values.jq_array import JqArray
from .values.jq_object import JqObject
from .values.jq_object_builder import JqObjectBuilder

_MAX = 1.7976931348623157e308


def _number(value: int) -> float:
    try:
        return float(value)
    except OverflowError:
        return math.inf if value > 0 else -math.inf


_CONTAINER = object()
_END = object()


def to_value(value: Any) -> Any:
    """A jqlite value from a Python JSON value; TypeError for anything else."""
    converted = _scalar_in(value)
    if converted is not _CONTAINER:
        return converted
    # A frame: [the container's items (an iterator), the sink (a list or a builder), the key being converted].
    stack: list[list[Any]] = [_frame(value)]
    while True:
        frame = stack[-1]
        sink = frame[1]
        item: Any = next(frame[0], _END)
        if item is _END:
            stack.pop()
            done = JqArray.from_owned(sink) if type(sink) is list else sink.to_object()
            if not stack:
                return done
            parent = stack[-1]
            if type(parent[1]) is list:
                parent[1].append(done)
            else:
                parent[1].set(parent[2], done)
            continue
        if type(sink) is list:
            member = item
        else:
            name, member = item
            if not isinstance(name, str):
                raise TypeError(f"object keys must be strings, not {type(name).__name__}")
            frame[2] = sanitize(str(name))
        converted = _scalar_in(member)
        if converted is _CONTAINER:
            stack.append(_frame(member))
        elif type(sink) is list:
            sink.append(converted)
        else:
            sink.set(frame[2], converted)


def _frame(value: Any) -> list:
    if isinstance(value, (list, tuple)):
        return [iter(value), [], None]
    return [iter(value.items()), JqObjectBuilder(), None]


def _scalar_in(value: Any) -> Any:
    t = type(value)
    if value is None or t is bool or t is float:
        return value
    if t is str:
        return sanitize(value)
    if t is int:
        return _number(value)
    if isinstance(value, bool):
        return bool(value)
    if isinstance(value, int):
        return _number(int(value))
    if isinstance(value, float):
        return float(value)
    if isinstance(value, str):
        return sanitize(str(value))
    if isinstance(value, (list, tuple, Mapping)):
        return _CONTAINER
    raise TypeError(f"{t.__name__} is not a JSON value")


def _scalar_out(value: Any) -> Any:
    if type(value) is float:
        if value != value:
            return None
        if value == math.inf:
            return _MAX
        if value == -math.inf:
            return -_MAX
        if -1e21 < value < 1e21 and value.is_integer():
            return int(value)
    return value


def to_python(value: Any) -> Any:
    """A Python JSON value from a jqlite value."""
    t = type(value)
    if t is not JqArray and t is not JqObject:
        return _scalar_out(value)
    root = _open(value)
    # A frame: [the container's values, the index of the next one, its keys or None, the sink].
    stack: list[list] = [root]
    while stack:
        frame = stack[-1]
        items = frame[0]
        index = frame[1]
        if index == len(items):
            stack.pop()
            continue
        frame[1] = index + 1
        item = items[index]
        it = type(item)
        converted = _open(item) if it is JqArray or it is JqObject else None
        result = _scalar_out(item) if converted is None else converted[3]
        keys = frame[2]
        if keys is None:
            frame[3].append(result)
        else:
            frame[3][keys[index]] = result
        if converted is not None:
            stack.append(converted)
    return root[3]


def _open(value: Any) -> list:
    if type(value) is JqArray:
        return [value.to_list(), 0, None, []]
    n = value.count
    return [value.values[:n], 0, value.keys[:n], {}]
