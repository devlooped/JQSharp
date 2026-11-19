"""A private, mutable array under construction (JqArrayBuilder.cs).

:meth:`to_array` hands its storage to the new :class:`JqArray` without copying; the builder must
not be used afterwards.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from .jq_array import JqArray

if TYPE_CHECKING:
    from .jq_value import JqValue


class JqArrayBuilder:
    __slots__ = ("items", "built")

    def __init__(self, source: JqArray) -> None:
        """Starts from the elements of ``source`` (one level copied)."""
        self.items: list[JqValue] = source.to_list()
        self.built = False

    @property
    def count(self) -> int:
        return len(self.items)

    def get(self, index: int) -> JqValue:
        return self.items[index]

    def set(self, index: int, value: JqValue) -> None:
        """Sets the element at ``index`` (>= 0); an index past the end pads with nulls, as jq does."""
        if self.built:
            raise RuntimeError("The builder was already turned into an array.")
        items = self.items
        n = len(items)
        if index < n:
            items[index] = value
        else:
            if index > n:
                items.extend([None] * (index - n))
            items.append(value)

    def to_array(self) -> JqArray:
        if self.built:
            raise RuntimeError("The builder was already turned into an array.")
        self.built = True
        return JqArray.from_owned(self.items)
