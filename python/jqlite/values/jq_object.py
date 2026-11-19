"""An immutable map from string keys to values that keeps the insertion order of its keys (JqObject.cs).

Storage is a shared buffer of keys and values (two Python lists) plus a count, as for
:class:`JqArray`. Adding a new key to the latest version of a buffer appends to it: amortized
O(1). The keys of a buffer are all distinct (a slot is only claimed for a key the version lacks),
so one index (a dict, key to slot) serves every version: a version with ``count`` entries owns
the key when its slot is below ``count``. Replacing or removing a key copies one level.

Difference from the reference: the index is kept for every object (Python's dict lookup is
faster than a scan in Python code), and it is extended when a slot is claimed, under the same
lock as the claim.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from .jq_array import CLAIM_LOCK

if TYPE_CHECKING:
    from .jq_value import JqValue


class JqObject:
    __slots__ = ("keys", "values", "count", "index", "_sorted")

    EMPTY: JqObject

    def __init__(self, keys: list[str], values: list[JqValue], count: int, index: dict[str, int]) -> None:
        # Read-only after construction: the lists and the index may be shared with other versions.
        self.keys = keys
        self.values = values
        self.count = count
        self.index = index
        self._sorted: list[str] | None = None

    @staticmethod
    def from_owned(keys: list[str], values: list[JqValue], index: dict[str, int]) -> JqObject:
        """An object over lists the caller gives up; keys distinct; ``index`` maps exactly these keys."""
        n = len(keys)
        return JqObject(keys, values, n, index) if n else JqObject.EMPTY

    def key_at(self, i: int) -> str:
        if 0 <= i < self.count:
            return self.keys[i]
        raise IndexError(i)

    def value_at(self, i: int) -> JqValue:
        if 0 <= i < self.count:
            return self.values[i]
        raise IndexError(i)

    def index_of(self, key: str) -> int:
        """The position of ``key`` in insertion order, or -1."""
        slot = self.index.get(key)
        if slot is not None and slot < self.count:
            return slot
        return -1

    def contains_key(self, key: str) -> bool:
        slot = self.index.get(key)
        return slot is not None and slot < self.count

    def get(self, key: str, default: JqValue = None) -> JqValue:
        """The value of ``key``, or ``default``."""
        slot = self.index.get(key)
        if slot is not None and slot < self.count:
            return self.values[slot]
        return default

    def set(self, key: str, value: JqValue) -> JqObject:
        """A copy with ``key`` set: an existing key keeps its position, a new key goes last."""
        count = self.count
        slot = self.index.get(key)
        if slot is None or slot >= count:
            return self._add(key, value)
        keys = self.keys[:count]
        values = self.values[:count]
        values[slot] = value
        return JqObject(keys, values, count, dict(zip(keys, range(count))))

    def _add(self, key: str, value: JqValue) -> JqObject:
        # Appends a key this version does not have.
        count = self.count
        keys = self.keys
        if count:
            with CLAIM_LOCK:
                if len(keys) == count:
                    keys.append(key)
                    self.values.append(value)
                    self.index[key] = count
                    return JqObject(keys, self.values, count + 1, self.index)
            keys = keys[:count]
            values = self.values[:count]
            index = dict(zip(keys, range(count)))
        else:
            keys = []
            values = []
            index = {}
        keys.append(key)
        values.append(value)
        index[key] = count
        return JqObject(keys, values, count + 1, index)

    def remove(self, key: str) -> JqObject:
        """A copy without ``key``; the other keys keep their order."""
        i = self.index_of(key)
        if i < 0:
            return self
        count = self.count
        keys = self.keys[:count]
        values = self.values[:count]
        del keys[i]
        del values[i]
        return JqObject.from_owned(keys, values, dict(zip(keys, range(len(keys)))))

    def merge(self, other: JqObject) -> JqObject:
        """jq's ``+`` on objects: the keys of ``other`` win; existing keys keep their position, new ones follow in ``other``'s order."""
        if other.count == 0:
            return self
        if self.count == 0:
            return other
        okeys = other.keys
        ovalues = other.values
        ocount = other.count
        index = self.index
        count = self.count
        overlaps = False
        for i in range(ocount):
            slot = index.get(okeys[i])
            if slot is not None and slot < count:
                overlaps = True
                break
        if not overlaps:
            # Only new keys: append them, in place when this is the latest version.
            result = self
            for i in range(ocount):
                result = result._add(okeys[i], ovalues[i])
            return result
        keys = self.keys[:count]
        values = self.values[:count]
        new_index = dict(zip(keys, range(count)))
        for i in range(ocount):
            k = okeys[i]
            slot = new_index.get(k)
            if slot is None:
                new_index[k] = len(keys)
                keys.append(k)
                values.append(ovalues[i])
            else:
                values[slot] = ovalues[i]
        return JqObject(keys, values, len(keys), new_index)

    def sorted_keys(self) -> list[str]:
        """The keys in code point order (jq's ``keys``); computed once per object."""
        keys = self._sorted
        if keys is None:
            keys = sorted(self.keys[:self.count])
            self._sorted = keys
        return keys

    def items(self) -> list[tuple[str, JqValue]]:
        """The entries in insertion order (a new list)."""
        n = self.count
        return list(zip(self.keys[:n], self.values[:n]))

    def __repr__(self) -> str:
        return f"JqObject({dict(self.items())!r})"


JqObject.EMPTY = JqObject([], [], 0, {})
