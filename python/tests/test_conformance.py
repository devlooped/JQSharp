"""Every case of the jqlite conformance corpus (spec/tests/*.test), by the rules of
spec/tests/README.md (ConformanceTests.cs): through the public API (`jqlite.parse` with
`Options`), and through the core with every prelude builtin bound to its definition, to check that
the natives that override some change nothing. Every case must pass both ways; each is a test."""

from __future__ import annotations

import json
import unittest

import jqlite

from . import corpus

OPTIONS = jqlite.Options(
    environment={},
    clock=corpus.FixedZoneClock(),
    max_depth=1000,
    resolver=jqlite.FileResolver(corpus.MODULES),
)


def run_public(case: corpus.Case) -> str | None:
    try:
        expression = jqlite.parse(case.program, OPTIONS)
    except jqlite.JqParseError as e:
        return corpus.missing_fixed_text(case.expected_error, e.message) if case.should_fail else f"{e.message}"
    if case.should_fail:
        return "the program parses and binds but must not"
    outputs: list[str] = []

    def collect(result: object) -> bool:
        outputs.append(json.dumps(result, ensure_ascii=False, allow_nan=False))
        return True
    try:
        expression.stream(json.loads(case.input), collect)
    except jqlite.JqError as e:
        return f"{type(e).__name__} {e.message} after [{', '.join(outputs)}]"
    return corpus.compare(case.expected, outputs)


class CorpusPublicApi(unittest.TestCase):
    """The corpus through the public API (native overrides on)."""


class CorpusWithoutNativeOverrides(unittest.TestCase):
    """The corpus through the core, every prelude builtin bound to its definition."""


def _add(cls: type, name: str, check: object, case: corpus.Case) -> None:
    def test(self: unittest.TestCase) -> None:
        failure = check(case)  # type: ignore[operator]
        self.assertIsNone(failure, f"{case.name}: {case.program}: {failure}")
    test.__name__ = name
    test.__doc__ = f"{case.name}: {case.program}"
    setattr(cls, name, test)


for _case in corpus.all_cases():
    _name = f"test_{_case.file.removesuffix('.test')}_{_case.line:04d}"
    _add(CorpusPublicApi, _name, run_public, _case)
    _add(CorpusWithoutNativeOverrides, _name, corpus.run_core, _case)
