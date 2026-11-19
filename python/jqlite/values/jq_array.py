"""An immutable sequence of values (JqArray.cs).

Storage is a shared buffer (a Python list) plus a window (``start``, ``count``) on it, as a Go
slice. A slot of a buffer is written once, before any array that covers it exists, and never
again, so every :class:`JqArray` is immutable however many arrays share its buffer.

Appending to the array whose window ends where the buffer ends (its latest version) appends to
the buffer in place: amortized O(1), so ``reduce range(n) as $i ([]; . + [$i])`` is O(n). Any
other append copies the window to a new buffer. The check and the append are one step under a
lock, the Python counterpart of the reference's compare-and-swap on ``Used`` (threads may share a
buffer: constants of a bound program, values a host gives several evaluations).
"""

from __future__ import annotations

import threading
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from .jq_value import JqValue

# Guards the claim of the next slots of a shared buffer (arrays and objects).
CLAIM_LOCK = threading.Lock()


class JqArray:
    __slots__ = ("buffer", "start", "count")

    EMPTY: JqArray

    def __init__(self, buffer: list[JqValue], start: int, count: int) -> None:
        # Read-only after construction.
        self.buffer = buffer
        self.start = start
        self.count = count

    @staticmethod
    def of(*items: JqValue) -> JqArray:
        """An array with a copy of ``items``."""
        if not items:
            return JqArray.EMPTY
        return JqArray(list(items), 0, len(items))

    @staticmethod
    def from_owned(items: list[JqValue]) -> JqArray:
        """An array over ``items`` without copying: the caller gives up the list and never writes it again."""
        n = len(items)
        return JqArray(items, 0, n) if n else JqArray.EMPTY

    def get(self, index: int) -> JqValue:
        """The element at ``index``, 0 <= index < count."""
        if 0 <= index < self.count:
            return self.buffer[self.start + index]
        raise IndexError(index)

    def append(self, value: JqValue) -> JqArray:
        start = self.start
        count = self.count
        buffer = self.buffer
        end = start + count
        if count:
            with CLAIM_LOCK:
                if len(buffer) == end:
                    buffer.append(value)
                    return JqArray(buffer, start, count + 1)
        items = buffer[start:end]
        items.append(value)
        return JqArray(items, 0, count + 1)

    def concat(self, other: JqArray) -> JqArray:
        """This array followed by ``other`` (jq's ``+`` on arrays)."""
        if other.count == 0:
            return self
        count = self.count
        if count == 0:
            return other
        start = self.start
        buffer = self.buffer
        end = start + count
        added = other.buffer[other.start:other.start + other.count]
        with CLAIM_LOCK:
            if len(buffer) == end:
                buffer.extend(added)
                return JqArray(buffer, start, count + other.count)
        items = buffer[start:end]
        items.extend(added)
        return JqArray(items, 0, len(items))

    def slice(self, start: int, end: int) -> JqArray:
        """The elements ``start`` (included) to ``end`` (excluded), sharing this array's buffer."""
        if start < 0 or end < start or end > self.count:
            raise IndexError(start)
        if start == 0 and end == self.count:
            return self
        if end == start:
            return JqArray.EMPTY
        return JqArray(self.buffer, self.start + start, end - start)

    def set_item(self, index: int, value: JqValue) -> JqArray:
        """A copy with ``value`` at ``index`` (>= 0); an index past the end pads with nulls, as jq does."""
        if index < 0:
            raise IndexError(index)
        count = self.count
        if index == count:
            return self.append(value)
        items = self.buffer[self.start:self.start + count]
        if index > count:
            items.extend([None] * (index - count))
            items.append(value)
        else:
            items[index] = value
        return JqArray(items, 0, len(items))

    def to_list(self) -> list[JqValue]:
        """A new list of the elements."""
        return self.buffer[self.start:self.start + self.count]

    def __repr__(self) -> str:
        return f"JqArray({self.to_list()!r})"


JqArray.EMPTY = JqArray([], 0, 0)
