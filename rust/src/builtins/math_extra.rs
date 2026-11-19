//! The math of §9.8 that is not a host library function: the exactly specified operations, written
//! with operations every language has with the same IEEE results (+ − × ÷, sqrt, floor, trunc,
//! bit casts), and the special functions defined by formula, evaluated left to right as written
//! (Rust never fuses a multiplication and an addition on its own).

// The double nearest π (3.141592653589793).
const PI: f64 = std::f64::consts::PI;

/// Nearest integer, halves away from zero; the sign of a zero result is the input's.
pub fn round(x: f64) -> f64 {
    if !x.is_finite() {
        return x;
    }
    let t = x.trunc();
    // x − t is exact: both have the same sign and |t| <= |x|.
    if (x - t).abs() >= 0.5 { t + (if x < 0.0 { -1.0 } else { 1.0 }) } else { t }
}

/// Nearest integer, halves to even; the sign of a zero result is the input's.
pub fn nearby_int(x: f64) -> f64 {
    if !x.is_finite() {
        return x;
    }
    let t = x.floor();
    let d = x - t;
    let r = if d > 0.5 || d == 0.5 && t % 2.0 != 0.0 { t + 1.0 } else { t };
    if r == 0.0 && x.is_sign_negative() { -0.0 } else { r }
}

/// IEEE remainder: x − n·y with n the integer nearest x/y, halves to even; exact.
pub fn remainder(x: f64, y: f64) -> f64 {
    let mut r = x % y;
    if r.is_nan() || y.is_infinite() {
        return r;
    }
    let ay = y.abs();
    let twice = 2.0 * r.abs();
    // r has the sign of x and |r| < |y|; moving it by |y| toward zero is exact (Sterbenz).
    // At a half, the quotient of the truncated division decides: odd moves, even stays.
    if twice > ay || twice == ay && (x % (2.0 * ay)).abs() >= ay {
        r -= if r < 0.0 { -ay } else { ay };
    }
    r
}

/// The smaller argument; NaN gives way to the other; equal values give the first.
pub fn min(x: f64, y: f64) -> f64 {
    if x.is_nan() {
        y
    } else if y.is_nan() {
        x
    } else if y < x {
        y
    } else {
        x
    }
}

/// The larger argument; NaN gives way to the other; equal values give the first.
pub fn max(x: f64, y: f64) -> f64 {
    if x.is_nan() {
        y
    } else if y.is_nan() {
        x
    } else if y > x {
        y
    } else {
        x
    }
}

/// x − y when x > y, else 0; NaN when either is NaN.
pub fn fdim(x: f64, y: f64) -> f64 {
    if x.is_nan() || y.is_nan() {
        f64::NAN
    } else if x > y {
        x - y
    } else {
        0.0
    }
}

/// |x| with the sign of y (-0 negative); a NaN y counts as positive, since not every language can
/// tell its sign.
pub fn copy_sign(x: f64, y: f64) -> f64 {
    if y.is_nan() { x.abs() } else { x.copysign(y) }
}

/// The double next to x toward y, by its bits; y when they are equal.
pub fn next_after(x: f64, y: f64) -> f64 {
    if x.is_nan() || y.is_nan() {
        return f64::NAN;
    }
    if x == y {
        return y;
    }
    if x == 0.0 {
        let smallest = f64::from_bits(1);
        return if y > 0.0 { smallest } else { -smallest };
    }
    // Away from zero is one more in the magnitude bits, toward zero one less, whatever the sign.
    let bits = x.to_bits() as i64;
    f64::from_bits((bits + if (y > x) == (x > 0.0) { 1 } else { -1 }) as u64)
}

/// x · 2^e for an integer e (as ldexp); NaN for a finite e that is not an integer; an infinite e
/// multiplies or divides by Infinity.
pub fn scalb(x: f64, e: f64) -> f64 {
    if x.is_nan() || e.is_nan() {
        return f64::NAN;
    }
    if e == f64::INFINITY {
        return x * e;
    }
    if e == f64::NEG_INFINITY {
        return x / f64::INFINITY;
    }
    if e.trunc() == e { scale_b(x, e) } else { f64::NAN }
}

/// x · 2^trunc(e), correctly rounded, also to a subnormal (musl's scalbn).
pub fn scale_b(x: f64, e: f64) -> f64 {
    if e.is_nan() {
        return f64::NAN;
    }
    let mut n: i32 = if e >= 4000.0 {
        4000
    } else if e <= -4000.0 {
        -4000
    } else {
        e.trunc() as i32
    };
    let mut y = x;
    let mut i = 0;
    while i < 2 && n > 1023 {
        y *= power_of_two(1023);
        n -= 1023;
        i += 1;
    }

    // Down in two steps through 2^-1022 · 2^53, so that only the last product rounds.
    i = 0;
    while i < 2 && n < -1022 {
        y *= power_of_two(-1022) * power_of_two(53);
        n += 1022 - 53;
        i += 1;
    }

    n = n.clamp(-1022, 1023);
    y * power_of_two(n)
}

/// The unbiased binary exponent (subnormals included): floor(log2 |x|).
pub fn logb(x: f64) -> f64 {
    if x.is_nan() {
        return x;
    }
    if x.is_infinite() {
        return f64::INFINITY;
    }
    if x == 0.0 {
        return f64::NEG_INFINITY;
    }
    exponent(x) as f64
}

/// x · 2^−logb(x), between 1 and 2 in magnitude: twice the mantissa of frexp; 0, ±Infinity and NaN
/// unchanged.
pub fn significand(x: f64) -> f64 {
    if x == 0.0 || !x.is_finite() { x } else { 2.0 * frexp(x).0 }
}

/// [m, e] with x = m · 2^e and 0.5 ≤ |m| < 1; 0 → [0, 0]; ±Infinity → [±Infinity, 0]; NaN → [NaN, 0].
pub fn frexp(x: f64) -> (f64, f64) {
    if x == 0.0 || !x.is_finite() {
        return (x, 0.0);
    }
    let e = exponent(x) + 1;
    (scale_b(x, -(e as f64)), e as f64)
}

/// [frac, int], both with the sign of x; ±Infinity → [±0, ±Infinity].
pub fn modf(x: f64) -> (f64, f64) {
    if x.is_nan() {
        return (x, x);
    }
    let t = x.trunc();
    let f = if x.is_infinite() { 0.0 } else { x - t };
    (if f == 0.0 && x.is_sign_negative() { -0.0 } else { f }, t)
}

/// 2^x, as the library pow.
///
/// ```text
/// exp2(x): pow(2, x)
/// ```
pub fn exp2(x: f64) -> f64 {
    2f64.powf(x)
}

/// 10^x, as the library pow.
pub fn exp10(x: f64) -> f64 {
    10f64.powf(x)
}

/// log(1 + x), accurate near 0 (Goldberg: the rounding of 1 + x cancels out).
///
/// ```text
/// log1p(x): u = 1 + x
///           if u == 1 or u == +Infinity: return x
///           return log(u) * x / (u - 1)
/// ```
pub fn log1p(x: f64) -> f64 {
    let u = 1.0 + x;
    if u == 1.0 || u == f64::INFINITY {
        return x;
    }
    u.ln() * x / (u - 1.0)
}

/// exp(x) − 1, accurate near 0 (Kahan: the rounding of exp(x) cancels out).
///
/// ```text
/// expm1(x): u = exp(x)
///           if u == 1: return x
///           m = u - 1
///           if m == -1 or u == +Infinity: return m
///           return m * x / log(u)
/// ```
pub fn expm1(x: f64) -> f64 {
    let u = x.exp();
    if u == 1.0 {
        return x;
    }
    let um1 = u - 1.0;
    if um1 == -1.0 || u.is_infinite() {
        return um1;
    }
    um1 * x / u.ln()
}

/// Abramowitz & Stegun 7.1.26.
pub fn erf(x: f64) -> f64 {
    if x.is_nan() {
        return x;
    }
    let s = if x > 0.0 {
        1.0
    } else if x < 0.0 {
        -1.0
    } else {
        0.0
    };
    let a = x.abs();
    let t = 1.0 / (1.0 + 0.3275911 * a);
    let y = 1.0 - (((((1.061405429 * t + -1.453152027) * t) + 1.421413741) * t + -0.284496736) * t + 0.254829592) * t * (-a * a).exp();
    s * y
}

pub fn erfc(x: f64) -> f64 {
    1.0 - erf(x)
}

#[allow(clippy::excessive_precision)]
const LANCZOS: [f64; 9] = [
    0.99999999999980993,
    676.5203681218851,
    -1259.1392167224028,
    771.32342877765313,
    -176.61502916214059,
    12.507343278686905,
    -0.13857109526572012,
    9.9843695780195716e-6,
    1.5056327351493116e-7,
];

/// Lanczos approximation, g = 7, with the reflection formula below 0.5.
pub fn tgamma(x: f64) -> f64 {
    if x <= 0.0 && x.floor() == x {
        return f64::INFINITY;
    }
    if x < 0.5 {
        return PI / ((PI * x).sin() * tgamma(1.0 - x));
    }
    let x = x - 1.0;
    let mut sum = LANCZOS[0];
    for (i, coefficient) in LANCZOS.iter().enumerate().skip(1) {
        sum += coefficient / (x + i as f64);
    }
    let t = x + 7.0 + 0.5;
    (2.0 * PI).sqrt() * t.powf(x + 0.5) * (-t).exp() * sum
}

pub fn lgamma(x: f64) -> f64 {
    tgamma(x).abs().ln()
}

/// The sign of tgamma(x): -1 when it is negative, else 1 (lgamma_r's second element).
pub fn gamma_sign(x: f64) -> f64 {
    if tgamma(x) < 0.0 { -1.0 } else { 1.0 }
}

/// Bessel J0, Numerical Recipes rational approximations.
// 0.636619772 is the constant of the formula (§9.8), not the double nearest 2/π.
#[allow(clippy::excessive_precision, clippy::approx_constant)]
pub fn j0(x: f64) -> f64 {
    let a = x.abs();
    if a <= 8.0 {
        let y = a * a;
        let n = 57568490574.0 + y * (-13362590354.0 + y * (651619640.7 + y * (-11214424.18 + y * (77392.33017 + y * (-184.9052456)))));
        let d = 57568490411.0 + y * (1029532985.0 + y * (9494680.718 + y * (59272.64853 + y * (267.8532712 + y))));
        return n / d;
    }

    let z = 8.0 / a;
    let yy = z * z;
    let xx = a - 0.785398164;
    let p = 1.0 + yy * (-0.1098628627e-2 + yy * (0.2734510407e-4 + yy * (-0.2073370639e-5 + yy * 0.2093887211e-6)));
    let q = -0.1562499995e-1 + yy * (0.1430488765e-3 + yy * (-0.6911147651e-5 + yy * (0.7621095161e-6 - yy * 0.934935152e-7)));
    (0.636619772 / a).sqrt() * (p * xx.cos() - z * q * xx.sin())
}

/// Bessel J1, Numerical Recipes rational approximations.
#[allow(clippy::excessive_precision, clippy::approx_constant)]
pub fn j1(x: f64) -> f64 {
    let mut s = 1.0;
    let mut a = x;
    if a < 0.0 {
        a = -a;
        s = -1.0;
    }

    if a <= 8.0 {
        let y = a * a;
        let n = a * (72362614232.0 + y * (-7895059235.0 + y * (242396853.1 + y * (-2972611.439 + y * (15704.48260 + y * (-30.16036606))))));
        let d = 144725228442.0 + y * (2300535178.0 + y * (18583304.74 + y * (99447.43394 + y * (376.9991397 + y))));
        return s * n / d;
    }

    let z = 8.0 / a;
    let yy = z * z;
    let xx = a - 2.356194491;
    let p = 1.0 + yy * (0.183105e-2 + yy * (-0.3516396496e-4 + yy * (0.2457520174e-5 + yy * (-0.240337019e-6))));
    let q = 0.04687499995 + yy * (-0.2002690873e-3 + yy * (0.8449199096e-5 + yy * (-0.88228987e-6 + yy * 0.105787412e-6)));
    s * (0.636619772 / a).sqrt() * (p * xx.cos() - z * q * xx.sin())
}

/// Bessel Y0, Numerical Recipes rational approximations.
#[allow(clippy::excessive_precision, clippy::approx_constant)]
pub fn y0(x: f64) -> f64 {
    if x < 0.0 {
        return f64::NAN;
    }
    if x == 0.0 {
        return f64::NEG_INFINITY;
    }
    if x < 8.0 {
        let y = x * x;
        let n = -2957821389.0 + y * (7062834065.0 + y * (-512359803.6 + y * (10879881.29 + y * (-86327.92757 + y * 228.4622733))));
        let d = 40076544269.0 + y * (745249964.8 + y * (7189466.438 + y * (47447.26470 + y * (226.1030244 + y))));
        return n / d + 0.636619772 * j0(x) * x.ln();
    }

    let z = 8.0 / x;
    let yy = z * z;
    let xx = x - 0.785398164;
    let p = 1.0 + yy * (-0.1098628627e-2 + yy * (0.2734510407e-4 + yy * (-0.2073370639e-5 + yy * 0.2093887211e-6)));
    let q = -0.1562499995e-1 + yy * (0.1430488765e-3 + yy * (-0.6911147651e-5 + yy * (0.7621095161e-6 + yy * (-0.934945152e-7))));
    (0.636619772 / x).sqrt() * (p * xx.sin() + z * q * xx.cos())
}

/// Bessel Y1, Numerical Recipes rational approximations.
#[allow(clippy::excessive_precision, clippy::approx_constant)]
pub fn y1(x: f64) -> f64 {
    if x < 0.0 {
        return f64::NAN;
    }
    if x == 0.0 {
        return f64::NEG_INFINITY;
    }
    if x < 8.0 {
        let y = x * x;
        let n = x * (-0.4900604943e13 + y * (0.1275274390e13 + y * (-0.5153438139e11 + y * (0.7349264551e9 + y * (-0.4237922726e7 + y * 0.8511937935e4)))));
        let d = 0.2499580570e14 + y * (0.4244419664e12 + y * (0.3733650367e10 + y * (0.2245904002e8 + y * (0.1020426050e6 + y * (0.3549632885e3 + y)))));
        return n / d + 0.636619772 * (j1(x) * x.ln() - 1.0 / x);
    }

    let z = 8.0 / x;
    let yy = z * z;
    let xx = x - 2.356194491;
    let p = 1.0 + yy * (0.183105e-2 + yy * (-0.3516396496e-4 + yy * (0.2457520174e-5 + yy * (-0.240337019e-6))));
    let q = 0.04687499995 + yy * (-0.2002690873e-3 + yy * (0.8449199096e-5 + yy * (-0.88228987e-6 + yy * 0.105787412e-6)));
    (0.636619772 / x).sqrt() * (p * xx.sin() + z * q * xx.cos())
}

/// Bessel J of integer order trunc(n): upward recurrence when |x| > n, else Miller's downward
/// recurrence (Numerical Recipes bessj).
pub fn jn(order: f64, x: f64) -> f64 {
    let Some(n) = order_of(order, x) else {
        return f64::NAN;
    };
    if n < 0 {
        return if n % 2 != 0 { -jn(-n as f64, x) } else { jn(-n as f64, x) };
    }
    if n == 0 {
        return j0(x);
    }
    if n == 1 {
        return j1(x);
    }
    if x == 0.0 {
        return 0.0;
    }
    let a = x.abs();
    let t = 2.0 / a;
    let mut r;
    if a > n as f64 {
        let mut p = j0(a);
        r = j1(a);
        for k in 1..n {
            let s = k as f64 * t * r - p;
            p = r;
            r = s;
        }
    } else {
        let m = 2 * ((n + (160.0 * n as f64).sqrt().floor() as i64) / 2);
        let mut even = false;
        let (mut p, mut sum, mut b) = (0.0, 0.0, 1.0);
        r = 0.0;
        let mut k = m;
        while k > 0 {
            let s = k as f64 * t * b - p;
            p = b;
            b = s;
            if b.abs() > 1e10 {
                b *= 1e-10;
                p *= 1e-10;
                r *= 1e-10;
                sum *= 1e-10;
            }

            if even {
                sum += b;
            }
            even = !even;
            if k == n {
                r = p;
            }
            k -= 1;
        }

        r /= 2.0 * sum - b;
    }

    if x < 0.0 && n % 2 != 0 { -r } else { r }
}

/// Bessel Y of integer order trunc(n), by upward recurrence from Y0 and Y1 (Numerical Recipes bessy).
pub fn yn(order: f64, x: f64) -> f64 {
    let Some(n) = order_of(order, x) else {
        return f64::NAN;
    };
    if n < 0 {
        return if n % 2 != 0 { -yn(-n as f64, x) } else { yn(-n as f64, x) };
    }
    if n == 0 {
        return y0(x);
    }
    if n == 1 {
        return y1(x);
    }
    if x < 0.0 {
        return f64::NAN;
    }
    if x == 0.0 {
        return f64::NEG_INFINITY;
    }
    let t = 2.0 / x;
    let mut p = y0(x);
    let mut r = y1(x);
    for k in 1..n {
        let s = k as f64 * t * r - p;
        p = r;
        r = s;
    }

    r
}

// The order of jn and yn, trunc(order); None (a NaN result) for a NaN argument or |order| >= 2^31.
fn order_of(order: f64, x: f64) -> Option<i64> {
    if order.is_nan() || x.is_nan() || order.abs() >= 2147483648.0 {
        return None;
    }
    Some(order.trunc() as i64)
}

// The unbiased exponent of a finite non-zero x, from its bits; a subnormal is scaled up first.
fn exponent(x: f64) -> i32 {
    let biased = ((x.to_bits() >> 52) & 0x7FF) as i32;
    if biased != 0 {
        return biased - 1023;
    }
    let bits = (x * power_of_two(54)).to_bits();
    ((bits >> 52) & 0x7FF) as i32 - 1023 - 54
}

// 2^n for -1022 <= n <= 1023, built from its bits.
fn power_of_two(n: i32) -> f64 {
    f64::from_bits(((n + 1023) as u64) << 52)
}
