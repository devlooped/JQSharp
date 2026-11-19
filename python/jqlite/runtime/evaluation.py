"""What an evaluation takes from its host and gives back (Evaluation.cs; spec §12)."""

from __future__ import annotations

from collections.abc import Callable, Mapping
from typing import TYPE_CHECKING, Protocol

from ..values.jq_object import JqObject

if TYPE_CHECKING:
    from ..values.jq_value import JqValue


class IClock(Protocol):
    """The host's clock and time zone (§12): `now`, and the local zone of `localtime`, `strflocaltime`, `%z`, `%Z`."""

    def now(self) -> float:
        """The current time, in seconds since the Unix epoch."""
        ...

    def offset(self, seconds: float) -> int:
        """The offset of the local zone from UTC at the instant `seconds`, in seconds east of UTC."""
        ...

    def abbreviation(self, seconds: float) -> str | None:
        """The abbreviation of the local zone at the instant `seconds`, or None."""
        ...


# The largest MaxDepth this port honours (Python only; the host boundary rejects a larger one):
# 100,000 levels of calls are measured to run on a thread with a 256 KB stack, using about 0.7 KB
# of memory per level (runtime/stack_guard.py).
MAX_DEPTH = 100_000


def _empty_environment() -> JqValue:
    return JqObject.EMPTY


class EvaluationOptions:
    """What an evaluation takes from its host besides the program and the input (§12)."""

    __slots__ = ("variables", "environment", "max_depth", "clock", "debug", "stderr")

    def __init__(self, variables: Mapping[str, JqValue] | None = None,
                 environment: Callable[[], JqValue] = _empty_environment, max_depth: int = 1000,
                 clock: IClock | None = None, debug: Callable[[JqValue], None] | None = None,
                 stderr: Callable[[JqValue], None] | None = None) -> None:
        # The host variables, by name without '$'; names are validated by the host boundary.
        self.variables = variables
        # The object `$ENV` is (string to string, in the order the host gives), asked for once per
        # evaluation, when the program first reads it.
        self.environment = environment
        # The call depth limit of §11, 0 to MAX_DEPTH (checked by the host boundary).
        self.max_depth = max_depth
        # The clock and zone, for the date builtins.
        self.clock = clock
        # Receives the values of `debug`; they are dropped when None.
        self.debug = debug
        # Receives the values of `stderr`; they are dropped when None.
        self.stderr = stderr


class OutcomeKind:
    # Every output was produced.
    COMPLETED = 0
    # The host stopped the stream.
    STOPPED = 1
    # An error reached the top: value.
    ERROR = 2
    # `halt` or `halt_error`: code, and value when has_value.
    HALT = 3


class Outcome:
    """How an evaluation ended (§12)."""

    __slots__ = ("kind", "value", "has_value", "code")

    def __init__(self, kind: int, value: JqValue = None, has_value: bool = False, code: int = 0) -> None:
        self.kind = kind
        self.value = value
        self.has_value = has_value
        self.code = code

    def __repr__(self) -> str:
        return f"Outcome({self.kind}, {self.value!r}, {self.has_value}, {self.code})"
