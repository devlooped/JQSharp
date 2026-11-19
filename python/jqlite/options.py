"""What an expression takes from its host besides the input and the variables (JqOptions.cs)."""

from __future__ import annotations

import math
import time
from collections.abc import Callable, Mapping
from dataclasses import dataclass
from typing import Any, Protocol

from .resolvers import Resolver
from .runtime.evaluation import MAX_DEPTH


class Clock(Protocol):
    """The source of `now` and of the local time zone (`localtime`, `strflocaltime`, `%z`, `%Z`)."""

    def now(self) -> float:
        """The current time, in seconds since the Unix epoch."""
        ...

    def offset(self, seconds: float) -> int:
        """The offset of the local zone from UTC at that instant, in seconds east of UTC."""
        ...

    def abbreviation(self, seconds: float) -> str | None:
        """The abbreviation of the local zone at that instant, or None (`%Z` then prints the offset)."""
        ...


class SystemClock:
    """The system's clock and local zone (Host/TimeProviderClock.cs): the zone's offset at each
    instant, so daylight saving time follows the date; instants the C library cannot represent
    take the offset of now. No abbreviation: `%Z` prints the offset, as the .NET boundary does."""

    def now(self) -> float:
        return time.time()

    def offset(self, seconds: float) -> int:
        try:
            return int(time.localtime(math.floor(seconds)).tm_gmtoff)
        except (OverflowError, OSError, ValueError):
            return int(time.localtime().tm_gmtoff)

    def abbreviation(self, seconds: float) -> str | None:
        return None


@dataclass(frozen=True)
class Options:
    """Options are read when the expression is parsed (`parse`) and apply to every evaluation of it.

    - `resolver`: resolves `import` and `include` directives and `modulemeta`; without one, no module exists.
    - `environment`: the variables of `$ENV` and `env`, in this order; None (the default) is the
      process environment, read when an evaluation first uses `$ENV`.
    - `clock`: the source of `now` and of the local zone; the system's by default.
    - `max_depth`: the maximum number of calls of jq-defined functions active at once (spec §11);
      beyond it, the catchable error `Maximum call depth exceeded`. An `int` from 0 to
      `MAX_DEPTH` (100,000); any other value raises `ValueError` when the options are created.
    - `debug`: receives the value of each `debug` (its input) and each message of `debug(msgs)`,
      as is; without one, the values are dropped.
    - `stderr`: receives the input of each `stderr`, as is; without one, the values are dropped.
    """

    resolver: Resolver | None = None
    environment: Mapping[str, str] | None = None
    clock: Clock = SystemClock()
    max_depth: int = 1000
    debug: Callable[[Any], None] | None = None
    stderr: Callable[[Any], None] | None = None

    def __post_init__(self) -> None:
        depth = self.max_depth
        if type(depth) is not int or not 0 <= depth <= MAX_DEPTH:
            raise ValueError(f"max_depth must be an int from 0 to {MAX_DEPTH}, not {depth!r}")
