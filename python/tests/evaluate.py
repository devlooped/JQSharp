"""Runs programs on the core for the tests: outputs as canonical text, and how the evaluation ended (Evaluate.cs)."""

from __future__ import annotations

from typing import Any

from jqlite.binding.bound import BoundProgram
from jqlite.binding.resolver import Resolver
from jqlite.runtime.evaluation import EvaluationOptions, OutcomeKind
from jqlite.runtime.interpreter import Interpreter
from jqlite.syntax.syntax_error import SyntaxError
from jqlite.values import value_text
from jqlite.values.jq_value import Fail

from .test_values import on_small_stack


def json(text: str) -> Any:
    value = value_text.try_parse(text)
    if type(value) is Fail:
        raise AssertionError(f"bad JSON {text}: {value.error}")
    return value


def compile(program: str, native_overrides: bool = True, modules: Any = None) -> BoundProgram:
    bound = Resolver.try_compile(program, native_overrides, modules)
    if type(bound) is SyntaxError:
        raise AssertionError(f"'{program}': {bound.message} at {bound.line}:{bound.column}")
    return bound  # type: ignore[return-value]


def run(program: str | BoundProgram, input: Any = "null", options: EvaluationOptions | None = None, take: int = 1 << 62) -> list[str]:
    """The outputs, each as canonical text, then "error: <value>" or "halt ..." when the evaluation did not complete."""
    bound = compile(program) if isinstance(program, str) else program
    value = json(input) if isinstance(input, str) else input
    outputs: list[str] = []

    def output(v: Any) -> bool:
        outputs.append(value_text.to_json(v))
        return len(outputs) < take
    outcome = Interpreter.run(bound, value, options or EvaluationOptions(), output)
    if outcome.kind == OutcomeKind.ERROR:
        outputs.append("error: " + value_text.to_json(outcome.value))
    elif outcome.kind == OutcomeKind.HALT:
        outputs.append(f"halt {outcome.code}" + (" " + value_text.to_json(outcome.value) if outcome.has_value else ""))
    elif outcome.kind == OutcomeKind.STOPPED and len(outputs) < take:
        outputs.append("stopped")
    return outputs


def line(program: str | BoundProgram, input: Any = "null", options: EvaluationOptions | None = None) -> str:
    """One line: the outputs separated by ", "."""
    return ", ".join(run(program, input, options))


def on_stack(size: int, program: str, input: str = "null", options: EvaluationOptions | None = None) -> str:
    """As ``line``, on a thread with a C stack of ``size`` bytes."""
    bound = compile(program)
    return on_small_stack(None, lambda: line(bound, input, options), size)  # type: ignore[arg-type,return-value]


def on_small_stack_line(program: str, input: str = "null", options: EvaluationOptions | None = None) -> str:
    """As ``line``, on a thread with a 1 MB stack (the default of a Windows main thread)."""
    return on_stack(1024 * 1024, program, input, options)


class MemoryModules:
    """A module source over a dictionary of identities and texts; a path is its identity without the extension."""

    def __init__(self, modules: dict[str, str]) -> None:
        self.modules = modules

    def try_resolve(self, path: str, kind: int, metadata: Any, from_: str | None) -> tuple[bool, str]:
        identity = path + (".jq" if kind == 0 else ".json")
        return (True, identity) if identity in self.modules else (False, f"module not found: {path}")

    def try_read(self, identity: str) -> tuple[bool, str]:
        text = self.modules.get(identity)
        return (False, "missing") if text is None else (True, text)
