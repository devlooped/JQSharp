"""§9.10 `test/2` and `match/2`: the flags, the search loop and the match objects (RegexNatives.cs).

Offsets and lengths are in code points. The pattern goes through regex_syntax.py to the engine
(regex_engine.py). Difference from the reference: Python strings and the engine count code points,
so there is no code point map (CodePointMap) between the engine and the loop.
"""

from __future__ import annotations

from collections.abc import Callable
from typing import TYPE_CHECKING, Any

from ..runtime.native_table import add_closure, add_value
from ..runtime.path import Path
from ..runtime.signal import Signal
from ..values import errors
from ..values.jq_array import JqArray
from ..values.jq_object_builder import JqObjectBuilder
from ..values.jq_string import plain
from ..values.jq_value import Fail
from . import regex_engine, regex_syntax

if TYPE_CHECKING:
    from ..values.jq_value import JqValue


def register() -> None:
    add_value("test", 2, _test)
    add_closure("match", 2, _match)


_NO_SPANS: list[int] = []


class _Search:
    # A search ready to run: the input, the matcher of the pattern and the flags.
    __slots__ = ("input", "matcher", "groups", "global_", "skip_empty")

    def __init__(self, input: str, matcher: regex_engine.RegexMatcher, global_: bool, skip_empty: bool) -> None:
        self.input = input
        self.matcher = matcher
        self.groups = matcher.program.groups
        self.global_ = global_
        self.skip_empty = skip_empty


def _test(interpreter: Any, input: JqValue, arguments: Any) -> JqValue | Fail:
    search = _try_prepare(input, arguments[0], arguments[1])
    if type(search) is Fail:
        return search
    # Whether there is a match, and nothing of it; with the n flag, a match that is not empty.
    if not search.skip_empty:  # type: ignore[union-attr]
        return search.matcher.try_match(search.input, 0, _NO_SPANS)  # type: ignore[union-attr]
    found = [False]

    def report(spans: list[int]) -> bool:
        found[0] = True
        return False
    _run(search, report)  # type: ignore[arg-type]
    return found[0]


def _match(interpreter: Any, env: Any, arguments: Any, input: JqValue, path: Path | None, emit: Any) -> Signal | None:
    # match(re; flags): flags outermost, re innermost (re fastest); one output per reported match.
    def each_flags(flags: JqValue, _: Path | None) -> Signal | None:
        def each_re(re_: JqValue, _: Path | None) -> Signal | None:
            search = _try_prepare(input, plain(re_), plain(flags))
            if type(search) is Fail:
                return Signal.error(search.error)
            signal: list = [None]

            def report(spans: list[int]) -> bool:
                signal[0] = emit(_match_object(search, spans), Path.of(path))  # type: ignore[arg-type]
                return signal[0] is None
            _run(search, report)  # type: ignore[arg-type]
            return signal[0]  # type: ignore[no-any-return]
        return interpreter.for_each(arguments[0], env, input, None, each_re)  # type: ignore[no-any-return]
    return interpreter.for_each(arguments[1], env, input, None, each_flags)  # type: ignore[no-any-return]


def _try_prepare(input: JqValue, re_: JqValue, flags: JqValue) -> _Search | Fail:
    if type(input) is not str:
        return Fail(errors.cannot_be_matched(input))
    if type(re_) is not str:
        return Fail(errors.not_a_string(re_))
    if type(flags) is not str and flags is not None:
        return Fail(errors.not_a_string(flags))
    global_ = ignore_case = extended = skip_empty = dot_all = False
    letters: str = flags if flags is not None else ""  # type: ignore[assignment]
    for letter in letters:
        if letter == "g":
            global_ = True
        elif letter == "i":
            ignore_case = True
        elif letter == "x":
            extended = True
        elif letter == "n":
            skip_empty = True
        elif letter == "m" or letter == "p":
            dot_all = True
        elif letter == "s" or letter == "l":
            pass
        else:
            return Fail(errors.invalid_modifiers(letters))
    program = regex_engine.try_compile(re_, regex_syntax.Options(extended, dot_all, ignore_case))  # type: ignore[arg-type]
    if type(program) is str:
        return Fail(errors.regex_failure(program))
    return _Search(input, regex_engine.RegexMatcher(program), global_, skip_empty)  # type: ignore[arg-type]


def _run(search: _Search, report: Callable[[list[int]], bool]) -> None:
    # The search loop of §9.10, in code points; report returns false to stop.
    spans = [0] * (2 * (len(search.groups) + 1))
    length = len(search.input)
    p = 0
    while p <= length:
        if not search.matcher.try_match(search.input, p, spans):
            return
        start = spans[0]
        end = spans[1]
        if not (search.skip_empty and start == end) and (not report(spans) or not search.global_):
            return
        p = end if end > start else end + 1


def _match_object(search: _Search, spans: list[int]) -> JqValue:
    captures: list = []
    text = search.input
    for g, name in enumerate(search.groups):
        capture = JqObjectBuilder()
        start = spans[2 * g + 2]
        end = spans[2 * g + 3]
        if start == end or start < 0:
            # An empty or absent group has jq's other key order.
            capture.set("offset", -1.0 if start < 0 else float(start))
            capture.set("string", None if start < 0 else "")
            capture.set("length", 0.0)
        else:
            capture.set("offset", float(start))
            capture.set("length", float(end - start))
            capture.set("string", text[start:end])
        capture.set("name", name)
        captures.append(capture.to_object())
    match = JqObjectBuilder()
    match.set("offset", float(spans[0]))
    match.set("length", float(spans[1] - spans[0]))
    match.set("string", text[spans[0]:spans[1]])
    match.set("captures", JqArray.from_owned(captures))
    return match.to_object()
