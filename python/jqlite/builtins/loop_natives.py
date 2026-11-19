"""The natives marked *loop* in §9.4 (LoopNatives.cs).

Generators written as loops, so their iterations do not consume the call depth (§11). Each follows
the jqlite definition the spec gives it, including how it compares a non-numeric count, and passes
the paths of its argument's outputs.
"""

from __future__ import annotations

from typing import TYPE_CHECKING, Any

from ..runtime.native_table import add_closure
from ..runtime.path import Path
from ..runtime.signal import NOTHING, Signal
from ..values import errors, value_ops
from ..values.jq_value import Fail

if TYPE_CHECKING:
    from ..values.jq_value import JqValue

_UNTRACKED = Path.UNTRACKED


def register() -> None:
    add_closure("range", 2, _range)
    add_closure("limit", 2, _limit)
    add_closure("skip", 2, _skip)
    add_closure("while", 2, lambda i, env, args, input, path, emit: _iterate(i, env, args[0], args[1], input, path, emit, True))
    add_closure("until", 2, lambda i, env, args, input, path, emit: _iterate(i, env, args[0], args[1], input, path, emit, False))
    add_closure("repeat", 1, _repeat)


def _range(interpreter: Any, env: Any, arguments: Any, input: JqValue, path: Path | None, emit: Any) -> Signal | None:
    # range($from; $upto): $from outermost (the exception of §9.4).
    at = path and _UNTRACKED

    def each_from(frm: JqValue, _: Path | None) -> Signal | None:
        def each_upto(upto: JqValue, _: Path | None) -> Signal | None:
            if type(frm) is not float or type(upto) is not float:
                return Signal.error(errors.RANGE_NOT_NUMERIC)
            value = frm
            while value < upto:  # type: ignore[operator]
                stop = emit(value, at)
                if stop is not None:
                    return stop  # type: ignore[no-any-return]
                value += 1.0  # type: ignore[operator]
            return None
        return interpreter.for_each(arguments[1], env, input, None, each_upto)  # type: ignore[no-any-return]
    return interpreter.for_each(arguments[0], env, input, None, each_from)  # type: ignore[no-any-return]


def _limit(interpreter: Any, env: Any, arguments: Any, input: JqValue, path: Path | None, emit: Any) -> Signal | None:
    # def limit($n; f): if $n > 0 then label $out | foreach f as $item (0; . + 1; $item, if . >= $n
    # then break $out else empty end) elif $n == 0 then empty else error(...) end;
    def each_n(n: JqValue, _: Path | None) -> Signal | None:
        if value_ops.compare(n, 0.0) <= 0:
            return None if value_ops.are_equal(n, 0.0) else Signal.error(errors.NEGATIVE_LIMIT)
        enough = Signal.break_(interpreter.new_label())
        count = [0.0]
        simple = type(n) is float

        def each(value: JqValue, at: Path | None) -> Signal | None:
            stop = emit(value, at)
            if stop is not None:
                return stop  # type: ignore[no-any-return]
            count[0] += 1.0
            if simple:
                return enough if count[0] >= n else None  # type: ignore[operator]
            return enough if value_ops.compare(count[0], n) >= 0 else None
        signal = interpreter.eval(arguments[1], env, input, path, each)
        return None if signal is enough else signal  # type: ignore[no-any-return]
    return interpreter.for_each(arguments[0], env, input, None, each_n)  # type: ignore[no-any-return]


def _skip(interpreter: Any, env: Any, arguments: Any, input: JqValue, path: Path | None, emit: Any) -> Signal | None:
    # def skip($n; f): if $n > 0 then foreach f as $item ($n; . - 1; if . < 0 then $item else empty
    # end) elif $n == 0 then f else error(...) end;
    def each_n(n: JqValue, _: Path | None) -> Signal | None:
        if value_ops.compare(n, 0.0) <= 0:
            if value_ops.are_equal(n, 0.0):
                return interpreter.eval(arguments[1], env, input, path, emit)  # type: ignore[no-any-return]
            return Signal.error(errors.NEGATIVE_SKIP)
        count = [n]

        def each(value: JqValue, at: Path | None) -> Signal | None:
            result = value_ops.try_subtract(count[0], 1.0)
            if type(result) is Fail:
                return Signal.error(result.error)
            count[0] = result
            return emit(value, at) if value_ops.compare(result, 0.0) < 0 else None  # type: ignore[no-any-return]
        return interpreter.eval(arguments[1], env, input, path, each)  # type: ignore[no-any-return]
    return interpreter.for_each(arguments[0], env, input, None, each_n)  # type: ignore[no-any-return]


def _iterate(interpreter: Any, env: Any, cond: Any, update: Any, input: JqValue, path: Path | None, emit: Any, emit_when_true: bool) -> Signal | None:
    # while (emit_when_true): for each output of cond, if true: the value, then the same for each
    # output of update. until: for each output of cond, if true: the value; else the same for each
    # output of update. When cond and update yield at most one output, a plain loop; otherwise a
    # stack of pending work replaces the recursion, the outputs of cond and update collected at each step.
    if cond.at_most_one and update.at_most_one:
        value = input
        at = path
        while True:
            test = interpreter.eval_one(cond, env, value, None)
            if type(test) is Signal:
                return None if test is NOTHING else test
            if test is not None and test is not False:
                # while goes on after the value, until stops at it.
                stop = emit(value, at)
                if stop is not None or not emit_when_true:
                    return stop  # type: ignore[no-any-return]
            elif emit_when_true:
                return None
            value = interpreter.eval_one(update, env, value, at)
            if type(value) is Signal:
                return None if value is NOTHING else value
            at = interpreter.at

    # Work items: (emit, value, at); the top of the stack is next.
    work: list[tuple[bool, Any, Any]] = [(False, input, path)]
    while work:
        emit_it, value, at = work.pop()
        if emit_it:
            stop = emit(value, at)
            if stop is not None:
                return stop  # type: ignore[no-any-return]
            continue
        tests: list[bool] = []

        def each_test(test: JqValue, _: Path | None) -> None:
            tests.append(test is not None and test is not False)
            return None
        signal = interpreter.eval(cond, env, value, None, each_test)
        # The outputs of update, computed once: each test that continues goes on with them.
        updates: list[tuple[Any, Any]] = []
        if signal is None and emit_when_true in tests:
            def each_update(nxt: JqValue, nxt_at: Path | None) -> None:
                updates.append((nxt, nxt_at))
                return None
            signal = interpreter.eval(update, env, value, at, each_update)
        if signal is not None:
            return signal  # type: ignore[no-any-return]
        # For each test in order: while: if true, the value then each update; until: if true, the
        # value, else each update. Pushed last first, so that they run in order.
        for t in range(len(tests) - 1, -1, -1):
            if tests[t] == emit_when_true:
                for u in range(len(updates) - 1, -1, -1):
                    work.append((False, updates[u][0], updates[u][1]))
            if tests[t]:
                work.append((True, value, at))
    return None


def _repeat(interpreter: Any, env: Any, arguments: Any, input: JqValue, path: Path | None, emit: Any) -> Signal | None:
    # def repeat(f): def _repeat: f, _repeat; _repeat; - the outputs of f on the same input, again and again.
    while True:
        signal = interpreter.eval(arguments[0], env, input, path, emit)
        if signal is not None:
            return signal  # type: ignore[no-any-return]
