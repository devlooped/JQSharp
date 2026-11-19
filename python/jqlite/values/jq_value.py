"""The jqlite value (spec §4.1) and the shape of fallible operations (guide §2.1, §2.2).

A value is one of Python's own ``None``, ``bool``, ``float`` and ``str``, or a :class:`JqArray`
or :class:`JqObject`. ``bool`` is a subclass of ``int`` in Python: the engine always tests
``type(v)`` exactly (never ``isinstance``), so a boolean is never taken for a number, and no
``int`` ever enters the engine (the reader, the natives and the host boundary give ``float``).
"""

from __future__ import annotations

from typing import Any

# A jqlite value: None, bool, float, str, JqArray or JqObject. For a type checker the alias is
# Any: the engine dispatches on a saved type(v) (t = type(v); if t is float: ...), which checkers
# do not follow to narrow a union, so the union would only add casts to the hot paths.
JqValue = Any


# The kinds of jqlite values, in their sort order (JqKind).
NULL = 0
FALSE = 1
TRUE = 2
NUMBER = 3
STRING = 4
ARRAY = 5
OBJECT = 6

NoneType = type(None)


class Fail:
    """The failure of a fallible operation: its jqlite error value (usually the message string)."""

    __slots__ = ("error",)

    def __init__(self, error: JqValue) -> None:
        self.error = error

    def __repr__(self) -> str:
        return f"Fail({self.error!r})"


# Filled by values/__init__.py once JqArray and JqObject exist (type -> kind, type -> name).
KIND_OF_TYPE: dict[type, int] = {}
TYPE_NAMES: dict[type, str] = {}


def kind(value: JqValue) -> int:
    """The kind of a value (JqValue.Kind)."""
    t = type(value)
    if t is bool:
        return TRUE if value else FALSE
    return KIND_OF_TYPE[t]


def is_truthy(value: JqValue) -> bool:
    """False for null and false, true for every other value."""
    return value is not None and value is not False


def type_name(value: JqValue) -> str:
    """The name jq's ``type`` gives the kind: false and true are both "boolean"."""
    return TYPE_NAMES[type(value)]
