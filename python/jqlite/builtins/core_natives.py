"""§9.2 Core and §9.3 Types: the natives of the evaluation model itself and of its host (CoreNatives.cs)."""

from __future__ import annotations

import math
import threading
from typing import TYPE_CHECKING, Any

from ..binding.prelude import Prelude
from ..runtime.native_table import add_closure, add_value
from ..runtime.path import Path
from ..runtime.signal import Signal
from ..values import errors
from ..values.jq_array import JqArray
from ..values.jq_object import JqObject
from ..values.jq_value import Fail, type_name
from . import natives

if TYPE_CHECKING:
    from ..values.jq_value import JqValue

_INF = math.inf
_INT_MIN = -2147483648
_INT_MAX = 2147483647

# builtins/0: every non-internal builtin, natives and prelude, sorted by code point; computed once.
_builtins: list[JqArray] = []
_builtins_lock = threading.Lock()


def register() -> None:
    add_closure("empty", 0, lambda interpreter, env, arguments, input, path, emit: None)
    add_value("error", 0, lambda interpreter, input, arguments: Fail(input))
    add_value("type", 0, lambda interpreter, input, arguments: type_name(input))
    add_value("length", 0, _length)
    add_value("utf8bytelength", 0, _utf8_byte_length)
    add_value("builtins", 0, lambda interpreter, input, arguments: _list_builtins())
    add_closure("halt", 0, lambda interpreter, env, arguments, input, path, emit: Signal.halt(None, False, 0))
    add_closure("halt_error", 1, _halt_error)
    add_closure("last", 1, _last)
    add_value("debug", 0, _debug)
    add_value("stderr", 0, _stderr)
    add_value("infinite", 0, lambda interpreter, input, arguments: _INF)
    add_value("nan", 0, lambda interpreter, input, arguments: math.nan)
    add_value("isinfinite", 0, lambda interpreter, input, arguments: type(input) is float and math.isinf(input))
    add_value("isnan", 0, lambda interpreter, input, arguments: type(input) is float and input != input)
    add_value("isnormal", 0, _is_normal)


def _length(interpreter: Any, input: JqValue, arguments: Any) -> JqValue | Fail:
    t = type(input)
    if input is None:
        return 0.0
    if t is float:
        return abs(input)  # type: ignore[arg-type]
    if t is str:
        return float(len(input))  # type: ignore[arg-type]
    if t is JqArray or t is JqObject:
        return float(input.count)  # type: ignore[union-attr]
    return Fail(errors.has_no_length(input))


def _utf8_byte_length(interpreter: Any, input: JqValue, arguments: Any) -> JqValue | Fail:
    if type(input) is not str:
        return Fail(errors.only_strings_have_utf8_length(input))
    return float(len(input) if input.isascii() else len(input.encode("utf-8")))  # type: ignore[union-attr]


def _debug(interpreter: Any, input: JqValue, arguments: Any) -> JqValue:
    if interpreter.debug is not None:
        interpreter.debug(input)
    return input


def _stderr(interpreter: Any, input: JqValue, arguments: Any) -> JqValue:
    if interpreter.stderr is not None:
        interpreter.stderr(input)
    return input


def _is_normal(interpreter: Any, input: JqValue, arguments: Any) -> bool:
    # A finite, non-zero, not subnormal number (|x| >= 2^-1022).
    return type(input) is float and math.isfinite(input) and abs(input) >= 2.2250738585072014e-308  # type: ignore[arg-type]


def _halt_error(interpreter: Any, env: Any, arguments: Any, input: JqValue, path: Path | None, emit: Any) -> Signal | None:
    # halt_error(c): halts with the input and trunc(c) for the first output c.
    first = Signal.break_(interpreter.new_label())
    code: list = [None]

    def each(value: JqValue, _: Path | None) -> Signal:
        code[0] = value
        return first
    signal = interpreter.eval(arguments[0], env, input, None, each)
    if signal is not first:
        return signal  # type: ignore[no-any-return]
    value = code[0]
    if type(value) is not float:
        return Signal.error(errors.halt_error_number_required(input))
    if value != value:
        number = 0
    elif value <= _INT_MIN:
        number = _INT_MIN
    elif value >= _INT_MAX:
        number = _INT_MAX
    else:
        number = int(value)
    return Signal.halt(input, True, number)


def _last(interpreter: Any, env: Any, arguments: Any, input: JqValue, path: Path | None, emit: Any) -> Signal | None:
    # last(f): the last output of f, with its path. *loop*
    last: list = []

    def each(value: JqValue, at: Path | None) -> None:
        if last:
            last[0] = value
            last[1] = at
        else:
            last.append(value)
            last.append(at)
        return None
    signal = interpreter.eval(arguments[0], env, input, path, each)
    if signal is not None:
        return signal  # type: ignore[no-any-return]
    return emit(last[0], last[1]) if last else None  # type: ignore[no-any-return]


def _list_builtins() -> JqArray:
    if _builtins:
        return _builtins[0]
    with _builtins_lock:
        if not _builtins:
            names = [f"{n.name}/{n.arity}" for n in natives.ALL if not n.is_internal and not n.overrides_prelude]
            names += [f"{f.name}/{f.arity}" for f in Prelude.instance().definitions if not f.name.startswith("_")]
            names.sort()
            _builtins.append(JqArray.from_owned(names))  # type: ignore[arg-type]
    return _builtins[0]
