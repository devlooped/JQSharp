"""A jq expression parsed and bound once, to evaluate on any number of inputs (JqExpression.cs)."""

from __future__ import annotations

import os
from collections.abc import Callable, Mapping
from typing import Any

from . import json_adapter
from .binding.bound import BoundProgram
from .exceptions import JqError, JqHaltError
from .options import Options
from .runtime.evaluation import EvaluationOptions, OutcomeKind
from .runtime.interpreter import Interpreter
from .values import value_text
from .values.code_points import sanitize
from .values.jq_object_builder import JqObjectBuilder


class Expression:
    """Immutable and safe to evaluate concurrently from many threads, each evaluation with its own
    input and variables. Inputs, variables and results are Python JSON values (json_adapter.py)."""

    __slots__ = ("_program", "_environment", "_clock", "_max_depth", "_debug", "_stderr")

    def __init__(self, program: BoundProgram, options: Options) -> None:
        self._program = program
        self._clock = options.clock
        self._max_depth = options.max_depth
        on_debug = options.debug
        on_stderr = options.stderr
        self._debug = None if on_debug is None else (lambda value: on_debug(json_adapter.to_python(value)))
        self._stderr = None if on_stderr is None else (lambda value: on_stderr(json_adapter.to_python(value)))
        if options.environment is not None:
            snapshot = _to_object(options.environment)
            self._environment: Callable[[], Any] = lambda: snapshot
        else:
            self._environment = lambda: _to_object(os.environ)

    def evaluate(self, input: Any, variables: Mapping[str, Any] | None = None) -> list[Any]:
        """Evaluates the expression on `input` and returns all its results, computed before the
        list is returned.

        `variables`: host variables, by name without `$` (`"root"` is `$root`): each name a jq
        identifier, `ENV` excluded. A variable the expression uses but the host does not give is
        the runtime error `$name is not defined`; a binding in the expression shadows a host variable.

        Raises ValueError for an invalid variable name, TypeError for a value that is not JSON,
        JqError when an error reaches the top of the expression (JqHaltError for `halt_error`).
        """
        results: list[Any] = []

        def collect(result: Any) -> bool:
            results.append(result)
            return True
        self.stream(input, collect, variables)
        return results

    def stream(self, input: Any, on_result: Callable[[Any], Any], variables: Mapping[str, Any] | None = None) -> None:
        """Evaluates the expression on `input`, giving each result to `on_result` as soon as it is
        produced; `on_result` returns False to stop the evaluation (stopping is not an error; any
        other return value, None included, goes on). Errors as `evaluate`, raised after the results
        produced before them were given."""
        options = EvaluationOptions(
            variables=None if variables is None else _convert(variables),
            environment=self._environment,
            max_depth=self._max_depth,
            clock=self._clock,
            debug=self._debug,
            stderr=self._stderr,
        )
        to_python = json_adapter.to_python

        def output(value: Any) -> bool:
            return on_result(to_python(value)) is not False
        outcome = Interpreter.run(self._program, json_adapter.to_value(input), options, output)
        if outcome.kind == OutcomeKind.ERROR:
            raise JqError(_message(outcome.value), to_python(outcome.value))
        if outcome.kind == OutcomeKind.HALT and outcome.has_value:
            raise JqHaltError(_message(outcome.value), to_python(outcome.value), outcome.code)


def _message(value: Any) -> str:
    # The message of an error that reaches the host (spec §10.1).
    return value if type(value) is str else value_text.to_json(value) + " (not a string)"


def _is_identifier(name: Any) -> bool:
    if type(name) is not str or not name or not (name[0] == "_" or ("a" <= name[0] <= "z") or ("A" <= name[0] <= "Z")):
        return False
    return all(c == "_" or ("a" <= c <= "z") or ("A" <= c <= "Z") or ("0" <= c <= "9") for c in name)


def _convert(variables: Mapping[str, Any]) -> dict[str, Any]:
    # Validates the names (spec §12) and converts the values, once per evaluation.
    converted: dict[str, Any] = {}
    for name, value in variables.items():
        if not _is_identifier(name):
            raise ValueError(f"'{name}' is not a valid variable name: give a jq identifier without '$'.")
        if name == "ENV":
            raise ValueError("$ENV cannot be a host variable: use Options.environment.")
        converted[name] = json_adapter.to_value(value)
    return converted


def _to_object(variables: Mapping[str, str]) -> Any:
    builder = JqObjectBuilder()
    for name, value in variables.items():
        builder.set(sanitize(name), sanitize(value))
    return builder.to_object()


def _unused(error: JqError) -> None:  # pragma: no cover
    pass
