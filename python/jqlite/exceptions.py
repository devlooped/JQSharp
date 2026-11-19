"""The exceptions of the host boundary: the core never raises, errors become exceptions only here."""

from __future__ import annotations

from typing import Any


class JqError(Exception):
    """An error of a jq expression: an uncaught runtime error, or (as JqParseError) a syntax or
    binding error. `value` is the error value of a runtime error (what `error(v)` raised, or the
    message string of a builtin's error); the message is the value itself when it is a string, else
    its JSON text followed by `(not a string)` (spec §10.1)."""

    def __init__(self, message: str, value: Any = None) -> None:
        super().__init__(message)
        self.message = message
        self.value = value


class JqParseError(JqError):
    """A syntax or binding error, raised by `parse` before any evaluation: `line` and `column`
    (1-based, columns counting code points) in the expression or in the module `module` names."""

    def __init__(self, message: str, line: int, column: int, module: str | None = None) -> None:
        super().__init__(message)
        self.line = line
        self.column = column
        self.module = module


class JqHaltError(JqError):
    """`halt_error` ended the evaluation: `value` is the value it was given and `exit_code` its
    code. (`halt` ends an evaluation normally, without an exception.)"""

    def __init__(self, message: str, value: Any, exit_code: int) -> None:
        super().__init__(message, value)
        self.exit_code = exit_code
