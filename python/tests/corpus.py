"""The conformance corpus (spec/tests/*.test) read and run by the rules of spec/tests/README.md.

Shared by the conformance tests (through the public API and through the core) and by the test
tools; the corpus is read from the shared folder, never copied.
"""

from __future__ import annotations

import os
import re
import time
from dataclasses import dataclass, field
from typing import Any

from jqlite.binding.resolver import Resolver
from jqlite.runtime.evaluation import EvaluationOptions, OutcomeKind
from jqlite.runtime.interpreter import Interpreter
from jqlite.syntax.syntax_error import SyntaxError
from jqlite.values import value_text
from jqlite.values.jq_array import JqArray
from jqlite.values.jq_object import JqObject
from jqlite.values.jq_value import Fail

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SPEC_TESTS = os.path.join(ROOT, "spec", "tests")
MODULES = os.path.join(SPEC_TESTS, "modules")

_FIXED_TEXT = re.compile(
    r"^(\S+/\d+ is not defined|\$\*label-\S+ is not defined|\$\S+::\S+ is not defined|Cannot use .+ as object key"
    r"|May need parentheses around object key expression|Module metadata must be constant"
    r"|Module metadata must be an object|Import path must be constant|Invalid escape)$")


@dataclass
class Case:
    file: str
    line: int
    program: str
    input: str
    expected: list[str] = field(default_factory=list)
    should_fail: bool = False
    expected_error: str | None = None

    @property
    def name(self) -> str:
        return f"{self.file}:{self.line}"


def _is_separator(line: str) -> bool:
    trimmed = line.strip()
    return not trimmed or trimmed.startswith("#")


def _is_fail_marker(line: str) -> bool:
    return line.lstrip().startswith("%%FAIL")


def parse_file(path: str) -> list[Case]:
    """The cases of a .test file: lines end at LF (a CR before it is part of the line ending)."""
    with open(path, encoding="utf-8", newline="") as f:
        text = f.read()
    lines = [line[:-1] if line.endswith("\r") else line for line in text.split("\n")]
    name = os.path.basename(path)
    cases: list[Case] = []
    i = 0
    while i < len(lines):
        if _is_separator(lines[i]):
            i += 1
            continue
        if _is_fail_marker(lines[i]):
            ignore = "IGNORE MSG" in lines[i]
            i += 1
            while i < len(lines) and _is_separator(lines[i]):
                i += 1
            if i >= len(lines):
                break
            program = lines[i]
            line = i + 1
            i += 1
            messages: list[str] = []
            while i < len(lines) and not _is_separator(lines[i]) and not _is_fail_marker(lines[i]):
                messages.append(lines[i])
                i += 1
            cases.append(Case(name, line, program, "", [], True, None if ignore else "\n".join(messages)))
            continue
        program = lines[i]
        line = i + 1
        i += 1
        source = lines[i]
        i += 1
        expected: list[str] = []
        while i < len(lines) and not _is_separator(lines[i]) and not _is_fail_marker(lines[i]):
            expected.append(lines[i])
            i += 1
        cases.append(Case(name, line, program, source, expected))
    return cases


def all_cases() -> list[Case]:
    cases: list[Case] = []
    for name in sorted(os.listdir(SPEC_TESTS)):
        if name.endswith(".test"):
            cases.extend(parse_file(os.path.join(SPEC_TESTS, name)))
    return cases


class DirectoryModules:
    """A module source over a directory (the rule of spec/tests/README.md): the path p imported
    from a file names p.jq (code) or p.json (data) in that file's directory, the main program's
    directory being the root. Identities are paths relative to the root."""

    def __init__(self, root: str) -> None:
        self.root = root
        self.reads = 0

    def try_resolve(self, path: str, kind: int, metadata: object, from_: str | None) -> tuple[bool, str]:
        directory = "" if from_ is None else os.path.dirname(from_)
        identity = os.path.join(directory, path + (".jq" if kind == 0 else ".json")).replace("\\", "/")
        if os.path.isfile(os.path.join(self.root, identity)):
            return True, identity
        return False, f"module not found: {path}"

    def try_read(self, identity: str) -> tuple[bool, str]:
        self.reads += 1
        with open(os.path.join(self.root, identity), encoding="utf-8") as f:
            return True, f.read()


class FixedZoneClock:
    """UTC+02:00 without DST or abbreviation (spec §14.2)."""

    def now(self) -> float:
        return time.time()

    def offset(self, seconds: float) -> int:
        return 7200

    def abbreviation(self, seconds: float) -> str | None:
        return None


def missing_fixed_text(expected_error: str | None, message: str) -> str | None:
    """The message lines of a %%FAIL case that are fixed texts of spec §10.2 must be contained in
    the error; the other lines are jq's own wording and are not compared (spec §14.1)."""
    for line in (expected_error or "").split("\n"):
        if _FIXED_TEXT.match(line) and line not in message:
            return f"expected an error containing '{line}', got '{message}'"
    return None


def same(left: Any, right: Any) -> bool:
    """Numbers as doubles (-0 equals 0), strings by code points, arrays in order, objects with the
    same keys in the same order. Without recursion: values may be 10,000 levels deep."""
    stack: list[tuple[Any, Any]] = [(left, right)]
    while stack:
        a, b = stack.pop()
        if type(a) is not type(b):
            return False
        if type(a) is JqArray:
            if a.count != b.count:  # type: ignore[union-attr]
                return False
            stack.extend(zip(a.to_list(), b.to_list()))  # type: ignore[union-attr]
        elif type(a) is JqObject:
            if a.keys[:a.count] != b.keys[:b.count]:  # type: ignore[union-attr]
                return False
            stack.extend(zip(a.values[:a.count], b.values[:b.count]))  # type: ignore[union-attr]
        elif a != b:
            return False
    return True


def compare(expected: list[str], actual: list[str]) -> str | None:
    """The outputs, as text, against the expected lines: each read back and compared structurally (spec §14.2)."""
    if len(expected) == len(actual):
        for e, a in zip(expected, actual):
            left = value_text.try_parse(e)
            right = value_text.try_parse(a)
            if type(left) is Fail or type(right) is Fail or not same(left, right):
                break
        else:
            return None
    return f"expected [{', '.join(expected)}] but got [{', '.join(actual)}]"


CORPUS_MODULES = DirectoryModules(MODULES)


def run_core(case: Case, native_overrides: bool = False) -> str | None:
    """Runs a case through the core (by default with every prelude builtin bound to its
    definition); None when it passes, else what went wrong."""
    bound = Resolver.try_compile(case.program, native_overrides, CORPUS_MODULES)
    if type(bound) is SyntaxError:
        if case.should_fail:
            return missing_fixed_text(case.expected_error, bound.message)
        return f"{bound.message} at {bound.line}:{bound.column}"
    if case.should_fail:
        return "the program parses and binds but must not"
    value = value_text.try_parse(case.input)
    if type(value) is Fail:
        return f"bad input: {value.error}"
    outputs: list[str] = []
    options = EvaluationOptions(max_depth=1000, clock=FixedZoneClock())

    def output(v: object) -> bool:
        outputs.append(value_text.to_json(v))  # type: ignore[arg-type]
        return True
    outcome = Interpreter.run(bound, value, options, output)  # type: ignore[arg-type]
    if outcome.kind == OutcomeKind.ERROR:
        return f"error {value_text.to_json(outcome.value)} after [{', '.join(outputs)}]"
    if outcome.kind == OutcomeKind.HALT and (outcome.has_value or outcome.code != 0):
        return f"halt_error {outcome.code} after [{', '.join(outputs)}]"
    return compare(case.expected, outputs)
