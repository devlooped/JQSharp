"""The math of §9.8 that is not a host library function (MathExtra.cs).

The exactly specified operations, written with operations every language has with the same IEEE
results (+ - × ÷, sqrt, floor, trunc, bit casts), and the special functions defined by formula,
evaluated left to right as written. The library functions it uses (exp, log, sin, cos, pow, sqrt)
are those of math_natives.py, with their IEEE results where Python's `math` raises.
"""

from __future__ import annotations

import math
import struct

PI = 3.141592653589793
_INF = math.inf
_NAN = math.nan
_EPSILON = 5e-324

_pack_double = struct.Struct("<d").pack
_unpack_double = struct.Struct("<d").unpack
_pack_long = struct.Struct("<q").pack
_unpack_long = struct.Struct("<q").unpack


def _bits(x: float) -> int:
    return _unpack_long(_pack_double(x))[0]  # type: ignore[no-any-return]


def _from_bits(bits: int) -> float:
    return _unpack_double(_pack_long(bits))[0]  # type: ignore[no-any-return]


def _is_finite(x: float) -> bool:
    return x - x == 0.0


def trunc(x: float) -> float:
    """IEEE truncation; the sign of a zero result is the input's; ±Infinity and NaN unchanged."""
    if not _is_finite(x):
        return x
    return math.copysign(float(math.trunc(x)), x)


def floor(x: float) -> float:
    if not _is_finite(x):
        return x
    r = float(math.floor(x))
    return math.copysign(r, x) if r == 0 else r


def ceil(x: float) -> float:
    if not _is_finite(x):
        return x
    r = float(math.ceil(x))
    return math.copysign(r, x) if r == 0 else r


def fmod(x: float, y: float) -> float:
    """C fmod: the exact remainder with the sign of x."""
    if x != x or y != y or y == 0 or not _is_finite(x):
        return _NAN
    if not _is_finite(y):
        return x
    return math.fmod(x, y)


def round_(x: float) -> float:
    """Nearest integer, halves away from zero; the sign of a zero result is the input's."""
    if not _is_finite(x):
        return x
    t = trunc(x)
    # x - t is exact: both have the same sign and |t| <= |x|.
    return t + (-1.0 if x < 0 else 1.0) if abs(x - t) >= 0.5 else t


def nearby_int(x: float) -> float:
    """Nearest integer, halves to even; the sign of a zero result is the input's."""
    if not _is_finite(x):
        return x
    t = floor(x)
    d = x - t
    r = t + 1 if d > 0.5 or (d == 0.5 and fmod(t, 2) != 0) else t
    return -0.0 if r == 0 and math.copysign(1.0, x) < 0 else r


def remainder(x: float, y: float) -> float:
    """IEEE remainder: x - n·y with n the integer nearest x/y, halves to even; exact."""
    r = fmod(x, y)
    if r != r or not _is_finite(y):
        return r
    ay = abs(y)
    twice = 2 * abs(r)
    # r has the sign of x and |r| < |y|; moving it by |y| toward zero is exact (Sterbenz).
    # At a half, the quotient of the truncated division decides: odd moves, even stays.
    if twice > ay or (twice == ay and abs(fmod(x, 2 * ay)) >= ay):
        r -= -ay if r < 0 else ay
    return r


def min_(x: float, y: float) -> float:
    """The smaller argument; NaN gives way to the other; equal values give the first."""
    if x != x:
        return y
    if y != y:
        return x
    return y if y < x else x


def max_(x: float, y: float) -> float:
    """The larger argument; NaN gives way to the other; equal values give the first."""
    if x != x:
        return y
    if y != y:
        return x
    return y if y > x else x


def fdim(x: float, y: float) -> float:
    """x - y when x > y, else 0; NaN when either is NaN."""
    if x != x or y != y:
        return _NAN
    return x - y if x > y else 0.0


def copy_sign(x: float, y: float) -> float:
    """|x| with the sign of y (-0 negative); a NaN y counts as positive."""
    return abs(x) if y != y else math.copysign(x, y)


def next_after(x: float, y: float) -> float:
    """The double next to x toward y, by its bits; y when they are equal."""
    if x != x or y != y:
        return _NAN
    if x == y:
        return y
    if x == 0:
        return _EPSILON if y > 0 else -_EPSILON
    # Away from zero is one more in the magnitude bits, toward zero one less, whatever the sign.
    return _from_bits(_bits(x) + (1 if (y > x) == (x > 0) else -1))


def scalb(x: float, e: float) -> float:
    """x · 2^e for an integer e (as ldexp); NaN for a finite e that is not an integer; an infinite e multiplies or divides by Infinity."""
    if x != x or e != e:
        return _NAN
    if e == _INF:
        return x * e
    if e == -_INF:
        return x / _INF
    return scale_b(x, e) if trunc(e) == e else _NAN


def _power_of_two(n: int) -> float:
    # 2^n for -1022 <= n <= 1023, built from its bits.
    return _from_bits((n + 1023) << 52)


def scale_b(x: float, e: float) -> float:
    """x · 2^trunc(e), correctly rounded, also to a subnormal (musl's scalbn)."""
    if e != e:
        return _NAN
    n = 4000 if e >= 4000 else (-4000 if e <= -4000 else int(e))
    y = x
    i = 0
    while i < 2 and n > 1023:
        y *= _power_of_two(1023)
        n -= 1023
        i += 1
    # Down in two steps through 2^-1022 · 2^53, so that only the last product rounds.
    i = 0
    while i < 2 and n < -1022:
        y *= _power_of_two(-1022) * _power_of_two(53)
        n += 1022 - 53
        i += 1
    n = -1022 if n < -1022 else (1023 if n > 1023 else n)
    return y * _power_of_two(n)


def logb(x: float) -> float:
    """The unbiased binary exponent (subnormals included): floor(log2 |x|)."""
    if x != x:
        return x
    if not _is_finite(x):
        return _INF
    if x == 0:
        return -_INF
    return float(_exponent(x))


def significand(x: float) -> float:
    """x · 2^-logb(x), between 1 and 2 in magnitude; 0, ±Infinity and NaN unchanged."""
    if x == 0 or not _is_finite(x):
        return x
    return 2 * frexp(x)[0]


def frexp(x: float) -> tuple[float, float]:
    """[m, e] with x = m · 2^e and 0.5 <= |m| < 1; 0 -> [0, 0]; ±Infinity -> [±Infinity, 0]; NaN -> [NaN, 0]."""
    if x == 0 or not _is_finite(x):
        return x, 0.0
    e = _exponent(x) + 1
    return scale_b(x, -e), float(e)


def modf(x: float) -> tuple[float, float]:
    """[frac, int], both with the sign of x; ±Infinity -> [±0, ±Infinity]."""
    if x != x:
        return x, x
    t = trunc(x)
    f = 0.0 if not _is_finite(x) else x - t
    return (-0.0 if f == 0 and math.copysign(1.0, x) < 0 else f), t


def exp2(x: float) -> float:
    """2^x, as the library pow."""
    from .math_natives import pow_
    return pow_(2.0, x)


def exp10(x: float) -> float:
    """10^x, as the library pow."""
    from .math_natives import pow_
    return pow_(10.0, x)


def log1p(x: float) -> float:
    """log(1 + x), accurate near 0 (Goldberg: the rounding of 1 + x cancels out)."""
    from .math_natives import log
    u = 1 + x
    if u == 1 or u == _INF:
        return x
    return log(u) * x / (u - 1)


def expm1(x: float) -> float:
    """exp(x) - 1, accurate near 0 (Kahan: the rounding of exp(x) cancels out)."""
    from .math_natives import exp, log
    u = exp(x)
    if u == 1:
        return x
    um1 = u - 1
    if um1 == -1 or not _is_finite(u):
        return um1
    return um1 * x / log(u)


def erf(x: float) -> float:
    """Abramowitz & Stegun 7.1.26."""
    from .math_natives import exp
    if x != x:
        return x
    s = 1.0 if x > 0 else (-1.0 if x < 0 else 0.0)
    a = abs(x)
    t = 1.0 / (1.0 + 0.3275911 * a)
    y = 1.0 - (((((1.061405429 * t + -1.453152027) * t) + 1.421413741) * t + -0.284496736) * t + 0.254829592) * t * exp(-a * a)
    return s * y


def erfc(x: float) -> float:
    return 1.0 - erf(x)


_LANCZOS = (
    0.99999999999980993, 676.5203681218851, -1259.1392167224028,
    771.32342877765313, -176.61502916214059, 12.507343278686905,
    -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
)


def tgamma(x: float) -> float:
    """Lanczos approximation, g = 7, with the reflection formula below 0.5."""
    from .math_natives import exp, pow_, sin, sqrt
    if x <= 0 and floor(x) == x:
        return _INF
    if x < 0.5:
        return PI / (sin(PI * x) * tgamma(1 - x))
    x = x - 1
    total = _LANCZOS[0]
    for i in range(1, 9):
        total = total + _LANCZOS[i] / (x + i)
    t = x + 7 + 0.5
    return sqrt(2 * PI) * pow_(t, x + 0.5) * exp(-t) * total


def lgamma(x: float) -> float:
    from .math_natives import log
    return log(abs(tgamma(x)))


def gamma_sign(x: float) -> float:
    """The sign of tgamma(x): -1 when it is negative, else 1 (lgamma_r's second element)."""
    return -1.0 if tgamma(x) < 0 else 1.0


def j0(x: float) -> float:
    """Bessel J0, Numerical Recipes rational approximations."""
    from .math_natives import cos, sin, sqrt
    a = abs(x)
    if a <= 8.0:
        y = a * a
        n = 57568490574.0 + y * (-13362590354.0 + y * (651619640.7 + y * (-11214424.18 + y * (77392.33017 + y * (-184.9052456)))))
        d = 57568490411.0 + y * (1029532985.0 + y * (9494680.718 + y * (59272.64853 + y * (267.8532712 + y))))
        return n / d
    z = 8.0 / a
    yy = z * z
    xx = a - 0.785398164
    p = 1.0 + yy * (-0.1098628627e-2 + yy * (0.2734510407e-4 + yy * (-0.2073370639e-5 + yy * 0.2093887211e-6)))
    q = -0.1562499995e-1 + yy * (0.1430488765e-3 + yy * (-0.6911147651e-5 + yy * (0.7621095161e-6 - yy * 0.934935152e-7)))
    return sqrt(0.636619772 / a) * (p * cos(xx) - z * q * sin(xx))


def j1(x: float) -> float:
    """Bessel J1, Numerical Recipes rational approximations."""
    from .math_natives import cos, sin, sqrt
    s = 1.0
    a = x
    if a < 0.0:
        a = -a
        s = -1.0
    if a <= 8.0:
        y = a * a
        n = a * (72362614232.0 + y * (-7895059235.0 + y * (242396853.1 + y * (-2972611.439 + y * (15704.48260 + y * (-30.16036606))))))
        d = 144725228442.0 + y * (2300535178.0 + y * (18583304.74 + y * (99447.43394 + y * (376.9991397 + y))))
        return s * n / d
    z = 8.0 / a
    yy = z * z
    xx = a - 2.356194491
    p = 1.0 + yy * (0.183105e-2 + yy * (-0.3516396496e-4 + yy * (0.2457520174e-5 + yy * (-0.240337019e-6))))
    q = 0.04687499995 + yy * (-0.2002690873e-3 + yy * (0.8449199096e-5 + yy * (-0.88228987e-6 + yy * 0.105787412e-6)))
    return s * sqrt(0.636619772 / a) * (p * cos(xx) - z * q * sin(xx))


def y0(x: float) -> float:
    """Bessel Y0, Numerical Recipes rational approximations."""
    from .math_natives import cos, log, sin, sqrt
    if x < 0:
        return _NAN
    if x == 0:
        return -_INF
    if x < 8.0:
        y = x * x
        n = -2957821389.0 + y * (7062834065.0 + y * (-512359803.6 + y * (10879881.29 + y * (-86327.92757 + y * 228.4622733))))
        d = 40076544269.0 + y * (745249964.8 + y * (7189466.438 + y * (47447.26470 + y * (226.1030244 + y))))
        return n / d + 0.636619772 * j0(x) * log(x)
    z = 8.0 / x
    yy = z * z
    xx = x - 0.785398164
    p = 1.0 + yy * (-0.1098628627e-2 + yy * (0.2734510407e-4 + yy * (-0.2073370639e-5 + yy * 0.2093887211e-6)))
    q = -0.1562499995e-1 + yy * (0.1430488765e-3 + yy * (-0.6911147651e-5 + yy * (0.7621095161e-6 + yy * (-0.934945152e-7))))
    return sqrt(0.636619772 / x) * (p * sin(xx) + z * q * cos(xx))


def y1(x: float) -> float:
    """Bessel Y1, Numerical Recipes rational approximations."""
    from .math_natives import cos, log, sin, sqrt
    if x < 0:
        return _NAN
    if x == 0:
        return -_INF
    if x < 8.0:
        y = x * x
        n = x * (-0.4900604943e13 + y * (0.1275274390e13 + y * (-0.5153438139e11 + y * (0.7349264551e9 + y * (-0.4237922726e7 + y * 0.8511937935e4)))))
        d = 0.2499580570e14 + y * (0.4244419664e12 + y * (0.3733650367e10 + y * (0.2245904002e8 + y * (0.1020426050e6 + y * (0.3549632885e3 + y)))))
        return n / d + 0.636619772 * (j1(x) * log(x) - 1.0 / x)
    z = 8.0 / x
    yy = z * z
    xx = x - 2.356194491
    p = 1.0 + yy * (0.183105e-2 + yy * (-0.3516396496e-4 + yy * (0.2457520174e-5 + yy * (-0.240337019e-6))))
    q = 0.04687499995 + yy * (-0.2002690873e-3 + yy * (0.8449199096e-5 + yy * (-0.88228987e-6 + yy * 0.105787412e-6)))
    return sqrt(0.636619772 / x) * (p * sin(xx) + z * q * cos(xx))


def _order(order: float, x: float) -> int | None:
    # The order of jn and yn, trunc(order); None (a NaN result) for a NaN argument or |order| >= 2^31.
    if order != order or x != x or not abs(order) < 2147483648.0:
        return None
    return int(order)


def jn(order: float, x: float) -> float:
    """Bessel J of integer order trunc(n): upward recurrence when |x| > n, else Miller's downward recurrence (Numerical Recipes bessj)."""
    from .math_natives import sqrt
    n = _order(order, x)
    if n is None:
        return _NAN
    if n < 0:
        return -jn(-n, x) if n % 2 != 0 else jn(-n, x)
    if n == 0:
        return j0(x)
    if n == 1:
        return j1(x)
    if x == 0:
        return 0.0
    a = abs(x)
    t = 2.0 / a
    if a > n:
        p = j0(a)
        r = j1(a)
        for k in range(1, n):
            s = k * t * r - p
            p = r
            r = s
    else:
        m = 2 * ((n + int(math.floor(sqrt(160.0 * n)))) // 2)
        even = False
        p = 0.0
        total = 0.0
        b = 1.0
        r = 0.0
        for k in range(m, 0, -1):
            s = k * t * b - p
            p = b
            b = s
            if abs(b) > 1e10:
                b = b * 1e-10
                p = p * 1e-10
                r = r * 1e-10
                total = total * 1e-10
            if even:
                total = total + b
            even = not even
            if k == n:
                r = p
        r = r / (2.0 * total - b)
    return -r if x < 0 and n % 2 != 0 else r


def yn(order: float, x: float) -> float:
    """Bessel Y of integer order trunc(n), by upward recurrence from Y0 and Y1 (Numerical Recipes bessy)."""
    n = _order(order, x)
    if n is None:
        return _NAN
    if n < 0:
        return -yn(-n, x) if n % 2 != 0 else yn(-n, x)
    if n == 0:
        return y0(x)
    if n == 1:
        return y1(x)
    if x < 0:
        return _NAN
    if x == 0:
        return -_INF
    t = 2.0 / x
    p = y0(x)
    r = y1(x)
    for k in range(1, n):
        s = k * t * r - p
        p = r
        r = s
    return r


def _exponent(x: float) -> int:
    # The unbiased exponent of a finite non-zero x, from its bits; a subnormal is scaled up first.
    biased = (_bits(x) >> 52) & 0x7FF
    if biased != 0:
        return biased - 1023
    return ((_bits(x * _power_of_two(54)) >> 52) & 0x7FF) - 1023 - 54
