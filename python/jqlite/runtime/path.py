"""Paths of path mode (Path.cs; spec §8.1).

Where a value came from: the keys from the root input, as an immutable list linked from the last
key, so appending is O(1) and shares the prefix. Evaluation takes and emits a path: None in value
mode (no path is ever built), and in path mode either a tracked path (ROOT and what is appended
to it) or UNTRACKED, for an output that has no path (a literal, a variable, an arithmetic result).
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from ..values.jq_array import JqArray
from ..values.jq_string import JqString

if TYPE_CHECKING:
    from ..values.jq_value import JqValue


class Path:
    __slots__ = ("parent", "key", "length")

    ROOT: Path
    UNTRACKED: Path

    def __init__(self, parent: Path | None, key: JqValue, length: int) -> None:
        self.parent = parent
        self.key = key
        self.length = length

    @property
    def is_tracked(self) -> bool:
        return self.length >= 0

    @staticmethod
    def of(mode: Path | None) -> Path | None:
        """The path of an output without one: None in value mode, else UNTRACKED."""
        return None if mode is None else Path.UNTRACKED

    def append(self, nxt: JqValue) -> Path:
        """This path followed by `nxt`; this path must be tracked. A key is kept as a str (values/jq_string.py)."""
        if type(nxt) is JqString:
            nxt = nxt.text
        return Path(self, nxt, self.length + 1)

    def append_all(self, keys: JqArray) -> Path:
        """This path followed by the elements of `keys`; this path must be tracked."""
        path = self
        for key in keys.to_list():
            path = Path(path, key, path.length + 1)
        return path

    def to_value(self) -> JqArray:
        """The keys as a jqlite array, first key first; this path must be tracked."""
        keys: list[JqValue] = [None] * self.length
        path: Path = self
        for i in range(self.length - 1, -1, -1):
            keys[i] = path.key
            path = path.parent  # type: ignore[assignment]
        return JqArray.from_owned(keys)


Path.ROOT = Path(None, None, 0)
Path.UNTRACKED = Path(None, None, -1)
