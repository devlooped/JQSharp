"""§9.4 `contains/1`: whether the input contains the argument (Containment.cs)."""

from __future__ import annotations

from typing import TYPE_CHECKING, Any

from ..runtime.native_table import add_value
from ..values import errors, value_ops
from ..values.jq_array import JqArray
from ..values.jq_object import JqObject
from ..values.jq_value import Fail, kind

if TYPE_CHECKING:
    from ..values.jq_value import JqValue


def register() -> None:
    add_value("contains", 1, _contains)


def _contains(interpreter: Any, input: JqValue, arguments: Any) -> JqValue | Fail:
    # At the top level the kinds must be equal; below it a kind mismatch is false.
    if kind(input) != kind(arguments[0]):
        return Fail(errors.cannot_check_containment(input, arguments[0]))
    return _check(input, arguments[0])


_PENDING = object()


def _scalar(a: JqValue, b: JqValue) -> Any:
    # The result of a check that needs no frame (kinds differ, strings, other scalars), or _PENDING.
    if kind(a) != kind(b):
        return False
    t = type(a)
    if t is str:
        return b in a  # type: ignore[operator]
    if t is JqArray or t is JqObject:
        return _PENDING
    return value_ops.are_equal(a, b)


def _check(a: JqValue, b: JqValue) -> bool:
    # Containers are checked with an explicit stack, so that deep values cannot exhaust the host
    # stack (§11): each frame asks for one child result at a time, which comes back in answer. A
    # frame is [a, b, item, candidate]: for arrays, every element b[item] must be contained by some
    # a[candidate]; for objects, every key of b (its entry item) must be in a with a value that
    # contains b's.
    scalar = _scalar(a, b)
    if scalar is not _PENDING:
        return scalar  # type: ignore[no-any-return]
    stack: list[list] = [[a, b, 0, 0]]
    answer = False
    answered = False
    while stack:
        frame = stack[-1]
        fa = frame[0]
        fb = frame[1]
        done: bool | None = None
        child_a: Any = None
        child_b: Any = None
        if type(fb) is JqArray:
            if answered:
                if answer:
                    frame[2] += 1
                    frame[3] = 0
                else:
                    frame[3] += 1
            if frame[2] == fb.count:
                done = True
            elif frame[3] == fa.count:
                done = False
            else:
                child_a = fa.buffer[fa.start + frame[3]]
                child_b = fb.buffer[fb.start + frame[2]]
        else:
            if answered and not answer:
                done = False
            else:
                if answered:
                    frame[2] += 1
                if frame[2] == fb.count:
                    done = True
                elif not fa.contains_key(fb.keys[frame[2]]):
                    done = False
                else:
                    child_a = fa.get(fb.keys[frame[2]])
                    child_b = fb.values[frame[2]]
        answered = False
        if done is not None:
            stack.pop()
            answer = done
            answered = True
            continue
        direct = _scalar(child_a, child_b)
        if direct is not _PENDING:
            answer = direct
            answered = True
        else:
            stack.append([child_a, child_b, 0, 0])
    return answer
