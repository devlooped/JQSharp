"""§9.8 Math (MathNatives.cs).

One-input functions take a number input; multi-input ones ignore the input and take number
arguments (the first non-number, in argument order, is the error).

Host-specific (**H**): the library functions of §9.8 are Python's `math`, which raises where
IEEE 754 and C99 Annex F give a value (`math.log(0)`, `math.exp(1000)`, `math.sin(inf)`,
`math.pow(0, -1)`): each function below checks the domain first or catches the overflow and
returns the IEEE result. `math.fma` exists from Python 3.13 and raises on overflow; before
3.13, and on overflow, `fma` is computed exactly with `fractions.Fraction` and rounded once.
"""

from __future__ import annotations

import math
from collections.abc import Callable
from fractions import Fraction
from typing import TYPE_CHECKING, Any

from ..runtime.native_table import add_value
from ..values import errors
from ..values.jq_array import JqArray
from ..values.jq_value import Fail
from . import math_extra as X

if TYPE_CHECKING:
    from ..values.jq_value import JqValue

_INF = math.inf
_NAN = math.nan


def _finite(x: float) -> bool:
    return x - x == 0.0


def log(x: float) -> float:
    if x != x or x < 0:
        return _NAN
    return -_INF if x == 0 else math.log(x)


def log2(x: float) -> float:
    if x != x or x < 0:
        return _NAN
    return -_INF if x == 0 else math.log2(x)


def log10(x: float) -> float:
    if x != x or x < 0:
        return _NAN
    return -_INF if x == 0 else math.log10(x)


def exp(x: float) -> float:
    try:
        return math.exp(x)
    except OverflowError:
        return _INF


def cosh(x: float) -> float:
    try:
        return math.cosh(x)
    except OverflowError:
        return _INF


def sinh(x: float) -> float:
    try:
        return math.sinh(x)
    except OverflowError:
        return math.copysign(_INF, x)


def sqrt(x: float) -> float:
    return _NAN if x < 0 else math.sqrt(x)


def sin(x: float) -> float:
    return math.sin(x) if _finite(x) or x != x else _NAN


def cos(x: float) -> float:
    return math.cos(x) if _finite(x) or x != x else _NAN


def tan(x: float) -> float:
    return math.tan(x) if _finite(x) or x != x else _NAN


def acos(x: float) -> float:
    return _NAN if abs(x) > 1 else math.acos(x)


def asin(x: float) -> float:
    return _NAN if abs(x) > 1 else math.asin(x)


def acosh(x: float) -> float:
    return _NAN if x < 1 else math.acosh(x)


def atanh(x: float) -> float:
    if abs(x) > 1:
        return _NAN
    if abs(x) == 1:
        return math.copysign(_INF, x)
    return math.atanh(x)


def _odd_integer(y: float) -> bool:
    return _finite(y) and y == math.floor(y) and math.fmod(y, 2.0) != 0


def pow_(x: float, y: float) -> float:
    """C99 pow, with the special cases of Annex F (pow(1; nan) = 1, pow(-1; ±Infinity) = 1)."""
    try:
        return math.pow(x, y)
    except OverflowError:
        return -_INF if x < 0 and _odd_integer(y) else _INF
    except ValueError:
        if x == 0:
            # A negative power of zero: ±Infinity, negative only for -0 and an odd integer.
            return -_INF if math.copysign(1.0, x) < 0 and _odd_integer(y) else _INF
        # A negative base and a non-integer power.
        return _NAN


def fma(x: float, y: float, z: float) -> float:
    """x·y + z with a single rounding."""
    if not (_finite(x) and _finite(y) and _finite(z)):
        return x * y + z
    if _host_fma is not None:
        try:
            return _host_fma(x, y, z)
        except OverflowError:
            pass
    exact = Fraction(x) * Fraction(y) + Fraction(z)
    if exact == 0:
        # The sign of an exact zero is IEEE addition's of the two terms.
        return x * y + z
    try:
        return float(exact)
    except OverflowError:
        return _INF if exact > 0 else -_INF


_host_fma: Callable[[float, float, float], float] | None = getattr(math, "fma", None)

# The functions of one number: the exactly specified ones, the library functions, and those the
# spec defines by formula (math_extra: exp2, expm1, log1p and the special functions).
_UNARY: list[tuple[str, Callable[[float], float]]] = [
    ("floor", X.floor),
    ("ceil", X.ceil),
    ("trunc", X.trunc),
    ("round", X.round_),
    ("nearbyint", X.nearby_int),
    ("rint", X.nearby_int),
    ("fabs", abs),
    ("sqrt", sqrt),
    ("logb", X.logb),
    ("significand", X.significand),
    ("acos", acos),
    ("asin", asin),
    ("atan", math.atan),
    ("acosh", acosh),
    ("asinh", math.asinh),
    ("atanh", atanh),
    ("cbrt", math.cbrt),
    ("cos", cos),
    ("cosh", cosh),
    ("exp", exp),
    ("exp2", X.exp2),
    ("exp10", X.exp10),
    ("expm1", X.expm1),
    ("log", log),
    ("log10", log10),
    ("log1p", X.log1p),
    ("log2", log2),
    ("sin", sin),
    ("sinh", sinh),
    ("tan", tan),
    ("tanh", math.tanh),
    ("erf", X.erf),
    ("erfc", X.erfc),
    ("tgamma", X.tgamma),
    ("lgamma", X.lgamma),
    ("gamma", X.lgamma),
    ("j0", X.j0),
    ("j1", X.j1),
    ("y0", X.y0),
    ("y1", X.y1),
]

_BINARY: list[tuple[str, Callable[[float, float], float]]] = [
    ("fmod", X.fmod),
    ("remainder", X.remainder),
    ("drem", X.remainder),
    ("fmin", X.min_),
    ("fmax", X.max_),
    ("fdim", X.fdim),
    ("copysign", X.copy_sign),
    ("nextafter", X.next_after),
    ("nexttoward", X.next_after),
    ("ldexp", X.scale_b),
    ("scalbln", X.scale_b),
    ("scalb", X.scalb),
    ("atan2", math.atan2),
    ("pow", pow_),
    ("hypot", math.hypot),
    ("jn", X.jn),
    ("yn", X.yn),
]


def _unary(function: Callable[[float], float]) -> Callable[[Any, JqValue, Any], JqValue | Fail]:
    def native(interpreter: Any, input: JqValue, arguments: Any) -> JqValue | Fail:
        if type(input) is not float:
            return Fail(errors.number_required(input))
        return float(function(input))  # type: ignore[arg-type]
    return native


def _numbers(arguments: Any) -> Fail | None:
    for argument in arguments:
        if type(argument) is not float:
            return Fail(errors.number_required(argument))
    return None


def _binary(function: Callable[[float, float], float]) -> Callable[[Any, JqValue, Any], JqValue | Fail]:
    def native(interpreter: Any, input: JqValue, arguments: Any) -> JqValue | Fail:
        failed = _numbers(arguments)
        if failed is not None:
            return failed
        return float(function(arguments[0], arguments[1]))
    return native


def register() -> None:
    for name, function in _UNARY:
        add_value(name, 0, _unary(function))
    for name, function2 in _BINARY:
        add_value(name, 2, _binary(function2))
    add_value("fma", 3, _fma_native)
    add_value("frexp", 0, _frexp)
    add_value("lgamma_r", 0, _lgamma_r)
    add_value("modf", 0, _modf)


def _fma_native(interpreter: Any, input: JqValue, arguments: Any) -> JqValue | Fail:
    failed = _numbers(arguments)
    if failed is not None:
        return failed
    return float(fma(arguments[0], arguments[1], arguments[2]))


def _frexp(interpreter: Any, input: JqValue, arguments: Any) -> JqValue | Fail:
    if type(input) is not float:
        return Fail(errors.number_required(input))
    mantissa, exponent = X.frexp(input)  # type: ignore[arg-type]
    return JqArray.of(mantissa, exponent)


def _lgamma_r(interpreter: Any, input: JqValue, arguments: Any) -> JqValue | Fail:
    if type(input) is not float:
        return Fail(errors.number_required(input))
    return JqArray.of(X.lgamma(input), X.gamma_sign(input))  # type: ignore[arg-type]


def _modf(interpreter: Any, input: JqValue, arguments: Any) -> JqValue | Fail:
    if type(input) is not float:
        return Fail(errors.number_required(input))
    fraction, integer = X.modf(input)  # type: ignore[arg-type]
    return JqArray.of(fraction, integer)
