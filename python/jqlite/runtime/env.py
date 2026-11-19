"""Environment frames and closures (Env.cs).

One frame of the environment, linked to the frame outside it; the empty environment is None. A
frame holds the variables of one binding (`values`), the closures of one call of a function
with parameters (`closures`), or one label instance (`label`); the resolver numbered the frames,
so a reference walks a known number of frames outward (bound.py). Frames are never changed once
built, so they can be shared freely.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from ..binding.bound import Node
    from ..values.jq_value import JqValue


class Closure:
    """A filter argument: the argument's node with the caller's environment (§7.7)."""

    __slots__ = ("node", "env")

    def __init__(self, node: Node, env: Env | None) -> None:
        self.node = node
        self.env = env


class Env:
    __slots__ = ("parent", "values", "closures", "label")

    def __init__(self, parent: Env | None, values: list[JqValue] | None = None,
                 closures: list[Closure] | None = None, label: int = 0) -> None:
        self.parent = parent
        self.values = values
        self.closures = closures
        self.label = label

    @staticmethod
    def up(env: Env | None, hops: int) -> Env | None:
        """The environment `hops` frames out from `env`."""
        while hops:
            env = env.parent  # type: ignore[union-attr]
            hops -= 1
        return env
