"""A private, mutable object under construction (JqObjectBuilder.cs).

:meth:`to_object` hands its storage to the new :class:`JqObject` without copying; the builder must
not be used afterwards.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from .jq_object import JqObject

if TYPE_CHECKING:
    from .jq_value import JqValue


class JqObjectBuilder:
    __slots__ = ("keys", "values", "index", "built")

    def __init__(self, source: JqObject | None = None) -> None:
        """Starts empty, or from the entries of ``source``."""
        if source is None or source.count == 0:
            self.keys: list[str] = []
            self.values: list[JqValue] = []
            self.index: dict[str, int] = {}
        else:
            n = source.count
            self.keys = source.keys[:n]
            self.values = source.values[:n]
            self.index = dict(zip(self.keys, range(n)))
        self.built = False

    @property
    def count(self) -> int:
        return len(self.keys)

    def get(self, key: str, default: JqValue = None) -> JqValue:
        slot = self.index.get(key)
        return default if slot is None else self.values[slot]

    def contains_key(self, key: str) -> bool:
        return key in self.index

    def set(self, key: str, value: JqValue) -> None:
        """Sets ``key``: an existing key keeps its position, a new key goes last."""
        slot = self.index.get(key)
        if slot is None:
            self.index[key] = len(self.keys)
            self.keys.append(key)
            self.values.append(value)
        else:
            self.values[slot] = value

    def remove(self, key: str) -> bool:
        slot = self.index.get(key)
        if slot is None:
            return False
        del self.keys[slot]
        del self.values[slot]
        self.index = dict(zip(self.keys, range(len(self.keys))))
        return True

    def to_object(self) -> JqObject:
        if self.built:
            raise RuntimeError("The builder was already turned into an object.")
        self.built = True
        return JqObject.from_owned(self.keys, self.values, self.index)
