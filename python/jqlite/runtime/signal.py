"""Why evaluation stops besides reaching its end (Signal.cs; spec §6.2).

Evaluation returns None to go on, or one of these, which every construct passes up unchanged
except the one that handles it. A signal is an object, and its identity matters: an
error-handling construct recognizes the signals that came back from its consumer by identity
(Interpreter.Consumer), so a construct that passes a signal up must pass the same object.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from ..values.jq_value import JqValue


class SignalKind:
    # A jqlite error (§6.2); value is the error value. Caught by `try`, `?` and `?//`.
    ERROR = 0
    # `break` to the label instance `label`; handled by that label only.
    BREAK = 1
    # `halt` or `halt_error`: stops the whole evaluation; handled by the host only.
    HALT = 2
    # The host stopped the stream of outputs (not an error); handled by the host only.
    STOP = 3
    # Not a signal: what Interpreter.eval_one returns for "no output" (NOTHING).
    NONE = 4


class Signal:
    __slots__ = ("kind", "value", "has_value", "label", "code")

    STOP: Signal

    def __init__(self, kind: int, value: JqValue, has_value: bool, label: int, code: int) -> None:
        self.kind = kind
        # The error value, or the halt value when has_value.
        self.value = value
        # For HALT: whether a value goes with the exit code (`halt_error`).
        self.has_value = has_value
        # For BREAK: the label instance.
        self.label = label
        # For HALT: the exit code.
        self.code = code

    @staticmethod
    def error(value: JqValue) -> Signal:
        return Signal(SignalKind.ERROR, value, True, 0, 0)

    @staticmethod
    def break_(label: int) -> Signal:
        return Signal(SignalKind.BREAK, None, False, label, 0)

    @staticmethod
    def halt(value: JqValue, has_value: bool, code: int) -> Signal:
        return Signal(SignalKind.HALT, value, has_value, 0, code)

    def __repr__(self) -> str:
        return f"Signal({self.kind}, {self.value!r}, label={self.label}, code={self.code})"


# The one STOP signal.
Signal.STOP = Signal(SignalKind.STOP, None, False, 0, 0)

# Interpreter.eval_one's "no output": a Signal object, so that one type test tells a value from
# anything else, but never returned by eval or passed up as a signal.
NOTHING = Signal(SignalKind.NONE, None, False, 0, 0)
