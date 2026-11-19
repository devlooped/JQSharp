// The math of §9.8 that is not a host library function: the exactly specified operations,
// written with operations every language has with the same IEEE results (+ − × ÷, sqrt, floor,
// trunc, bit casts), and the special functions defined by formula, evaluated left to right as
// written.
//
// Host-specific: the library functions of §9.8 are the host's `Math.*` (mathNatives.ts), except
// two that JavaScript lacks or defines differently, written here: `fma` (there is no fused
// multiply-add: the exact sum is computed with BigInt and rounded once) and `pow` (`**` gives
// NaN for pow(1; nan) and pow(-1; ±infinite), which C99 defines as 1). The bit casts are typed
// arrays over one buffer.

const Pi = 3.141592653589793;

// The bits of a double, as one 64-bit integer.
const doubleView = new Float64Array(1);
const bitsView = new BigInt64Array(doubleView.buffer);

function doubleToBits(x: number): bigint {
  doubleView[0] = x;
  return bitsView[0]!;
}

function bitsToDouble(bits: bigint): number {
  bitsView[0] = bits;
  return doubleView[0]!;
}

// Whether the sign bit is set: negative numbers and -0.
function isNegative(x: number): boolean {
  return x < 0 || Object.is(x, -0);
}

/** Nearest integer, halves away from zero; the sign of a zero result is the input's. */
export function round(x: number): number {
  if (!Number.isFinite(x)) return x;
  const t = Math.trunc(x);
  // x − t is exact: both have the same sign and |t| <= |x|.
  return Math.abs(x - t) >= 0.5 ? t + (x < 0 ? -1.0 : 1.0) : t;
}

/** Nearest integer, halves to even; the sign of a zero result is the input's. */
export function nearbyInt(x: number): number {
  if (!Number.isFinite(x)) return x;
  const t = Math.floor(x);
  const d = x - t;
  const r = d > 0.5 || (d === 0.5 && t % 2 !== 0) ? t + 1 : t;
  return r === 0 && isNegative(x) ? -0.0 : r;
}

/** IEEE remainder: x − n·y with n the integer nearest x/y, halves to even; exact. */
export function remainder(x: number, y: number): number {
  let r = x % y;
  if (Number.isNaN(r) || y === Infinity || y === -Infinity) return r;
  const ay = Math.abs(y);
  const twice = 2 * Math.abs(r);
  // r has the sign of x and |r| < |y|; moving it by |y| toward zero is exact (Sterbenz).
  // At a half, the quotient of the truncated division decides: odd moves, even stays.
  if (twice > ay || (twice === ay && Math.abs(x % (2 * ay)) >= ay)) r -= r < 0 ? -ay : ay;
  return r;
}

/** The smaller argument; NaN gives way to the other; equal values give the first. */
export function min(x: number, y: number): number {
  return Number.isNaN(x) ? y : Number.isNaN(y) ? x : y < x ? y : x;
}

/** The larger argument; NaN gives way to the other; equal values give the first. */
export function max(x: number, y: number): number {
  return Number.isNaN(x) ? y : Number.isNaN(y) ? x : y > x ? y : x;
}

/** x − y when x > y, else 0; NaN when either is NaN. */
export function fdim(x: number, y: number): number {
  return Number.isNaN(x) || Number.isNaN(y) ? NaN : x > y ? x - y : 0.0;
}

/** |x| with the sign of y (-0 negative); a NaN y counts as positive, since not every language can tell its sign. */
export function copySign(x: number, y: number): number {
  const a = Math.abs(x);
  return !Number.isNaN(y) && isNegative(y) ? -a : a;
}

/** The double next to x toward y, by its bits; y when they are equal. */
export function nextAfter(x: number, y: number): number {
  if (Number.isNaN(x) || Number.isNaN(y)) return NaN;
  if (x === y) return y;
  if (x === 0) return y > 0 ? Number.MIN_VALUE : -Number.MIN_VALUE;
  // Away from zero is one more in the magnitude bits, toward zero one less, whatever the sign.
  const bits = doubleToBits(x);
  return bitsToDouble(bits + (y > x === x > 0 ? 1n : -1n));
}

/** x · 2^e for an integer e (as ldexp); NaN for a finite e that is not an integer; an infinite e multiplies or divides by Infinity. */
export function scalb(x: number, e: number): number {
  if (Number.isNaN(x) || Number.isNaN(e)) return NaN;
  if (e === Infinity) return x * e;
  if (e === -Infinity) return x / Infinity;
  return Math.trunc(e) === e ? scaleB(x, e) : NaN;
}

/** x · 2^trunc(e), correctly rounded, also to a subnormal (musl's scalbn). */
export function scaleB(x: number, e: number): number {
  if (Number.isNaN(e)) return NaN;
  let n = e >= 4000 ? 4000 : e <= -4000 ? -4000 : Math.trunc(e);
  let y = x;
  for (let i = 0; i < 2 && n > 1023; i++) {
    y *= powerOfTwo(1023);
    n -= 1023;
  }

  // Down in two steps through 2^-1022 · 2^53, so that only the last product rounds.
  for (let i = 0; i < 2 && n < -1022; i++) {
    y *= powerOfTwo(-1022) * powerOfTwo(53);
    n += 1022 - 53;
  }

  n = n < -1022 ? -1022 : n > 1023 ? 1023 : n;
  return y * powerOfTwo(n);
}

/** The unbiased binary exponent (subnormals included): floor(log2 |x|). */
export function logb(x: number): number {
  if (Number.isNaN(x)) return x;
  if (x === Infinity || x === -Infinity) return Infinity;
  if (x === 0) return -Infinity;
  return exponent(x);
}

/** x · 2^−logb(x), between 1 and 2 in magnitude: twice the mantissa of frexp; 0, ±Infinity and NaN unchanged. */
export function significand(x: number): number {
  return x === 0 || !Number.isFinite(x) ? x : 2 * frexp(x)[0];
}

/** [m, e] with x = m · 2^e and 0.5 ≤ |m| < 1; 0 → [0, 0]; ±Infinity → [±Infinity, 0]; NaN → [NaN, 0]. */
export function frexp(x: number): [mantissa: number, exponent: number] {
  if (x === 0 || !Number.isFinite(x)) return [x, 0];
  const e = exponent(x) + 1;
  return [scaleB(x, -e), e];
}

/** [frac, int], both with the sign of x; ±Infinity → [±0, ±Infinity]. */
export function modf(x: number): [fraction: number, integer: number] {
  if (Number.isNaN(x)) return [x, x];
  const t = Math.trunc(x);
  const f = x === Infinity || x === -Infinity ? 0.0 : x - t;
  return [f === 0 && isNegative(x) ? -0.0 : f, t];
}

/**
 * x^y as C99 defines it (Annex F): the host's `Math.pow`, except pow(1, y) = 1 for any y, NaN
 * included, and pow(-1, ±Infinity) = 1, where ECMAScript gives NaN.
 */
export function pow(x: number, y: number): number {
  if (x === 1) return 1;
  if (x === -1 && (y === Infinity || y === -Infinity)) return 1;
  return Math.pow(x, y);
}

/**
 * x·y + z with a single rounding (C `fma`). The host has no fused multiply-add: the product and
 * the sum are computed exactly, as integers scaled by a power of two, and rounded once to the
 * nearest double, halves to even.
 */
export function fma(x: number, y: number, z: number): number {
  if (Number.isNaN(x) || Number.isNaN(y) || Number.isNaN(z)) return NaN;
  // An infinite factor: the product is infinite, or NaN (Infinity · 0); adding z is IEEE.
  if (!Number.isFinite(x) || !Number.isFinite(y)) return x * y + z;
  if (z === Infinity || z === -Infinity) return z;
  // A zero factor: the product is an exact zero.
  if (x === 0 || y === 0) return x * y + z;
  // A zero addend: the product alone, which the host rounds once (and whose sign stays when it underflows).
  if (z === 0) return x * y;

  const xm = mantissaOf(x);
  const xe = scaleOf(x);
  const ym = mantissaOf(y);
  const ye = scaleOf(y);
  const zm = mantissaOf(z);
  const ze = scaleOf(z);
  const pe = xe + ye;
  const e = Math.min(pe, ze);
  const sum = ((xm * ym) << BigInt(pe - e)) + (zm << BigInt(ze - e));
  // An exact cancellation is +0 when rounding to nearest.
  if (sum === 0n) return 0;
  return roundToDouble(sum, e);
}

// A finite non-zero double is mantissaOf(x) · 2^scaleOf(x), the mantissa an integer with the sign of x.
function mantissaOf(x: number): bigint {
  const bits = doubleToBits(x);
  const fraction = bits & 0xfffffffffffffn;
  const biased = (bits >> 52n) & 0x7ffn;
  const magnitude = biased === 0n ? fraction : fraction | 0x10000000000000n;
  return bits < 0n ? -magnitude : magnitude;
}

function scaleOf(x: number): number {
  const biased = Number((doubleToBits(x) >> 52n) & 0x7ffn);
  return biased === 0 ? -1074 : biased - 1075;
}

// The double nearest m · 2^e (m not zero), halves to even: ±Infinity past the largest double,
// a subnormal or ±0 below the smallest normal one.
function roundToDouble(m: bigint, e: number): number {
  const negative = m < 0n;
  const a = negative ? -m : m;
  const bits = a.toString(2).length;
  // The value is in [2^top, 2^(top + 1)).
  const top = bits - 1 + e;
  if (top > 1023) return negative ? -Infinity : Infinity;
  // The weight of the last bit kept: 53 bits, or fewer for a subnormal.
  const last = Math.max(top - 52, -1074);
  const shift = last - e;
  let kept: bigint;
  if (shift <= 0) {
    kept = a << BigInt(-shift);
  } else {
    const s = BigInt(shift);
    kept = a >> s;
    const rest = a & ((1n << s) - 1n);
    const half = 1n << (s - 1n);
    if (rest > half || (rest === half && (kept & 1n) === 1n)) kept += 1n;
  }

  // kept is at most 2^53, exact as a double, and kept · 2^last is a double (or overflows).
  const result = scaleB(Number(kept), last);
  return negative ? -result : result;
}

/**
 * 2^x, as the library pow.
 *
 *     exp2(x): pow(2, x)
 */
export function exp2(x: number): number {
  return pow(2, x);
}

/** 10^x, as the library pow. */
export function exp10(x: number): number {
  return pow(10, x);
}

/**
 * log(1 + x), accurate near 0 (Goldberg: the rounding of 1 + x cancels out).
 *
 *     log1p(x): u = 1 + x
 *               if u == 1 or u == +Infinity: return x
 *               return log(u) * x / (u - 1)
 */
export function log1p(x: number): number {
  const u = 1 + x;
  if (u === 1 || u === Infinity) return x;
  return (Math.log(u) * x) / (u - 1);
}

/**
 * exp(x) − 1, accurate near 0 (Kahan: the rounding of exp(x) cancels out).
 *
 *     expm1(x): u = exp(x)
 *               if u == 1: return x
 *               m = u - 1
 *               if m == -1 or u == +Infinity: return m
 *               return m * x / log(u)
 */
export function expm1(x: number): number {
  const u = Math.exp(x);
  if (u === 1) return x;
  const um1 = u - 1;
  if (um1 === -1 || u === Infinity || u === -Infinity) return um1;
  return (um1 * x) / Math.log(u);
}

/** Abramowitz & Stegun 7.1.26. */
export function erf(x: number): number {
  if (Number.isNaN(x)) return x;
  const s = x > 0 ? 1.0 : x < 0 ? -1.0 : 0.0;
  const a = Math.abs(x);
  const t = 1.0 / (1.0 + 0.3275911 * a);
  const y = 1.0 - ((((1.061405429 * t + -1.453152027) * t + 1.421413741) * t + -0.284496736) * t + 0.254829592) * t * Math.exp(-a * a);
  return s * y;
}

export function erfc(x: number): number {
  return 1.0 - erf(x);
}

/** Γ(x): the Stirling series once x is shifted up to 10 or more, the power split so that it overflows only past 171.62; the reflection formula below 0. */
export function tgamma(x: number): number {
  if (Number.isNaN(x) || x === Infinity) return x;
  if (x === 0) return 1 / x;
  if (x < 0) {
    if (Math.floor(x) === x) return NaN;
    if (x > -170) return Pi / (sinPi(x) * tgamma(1 - x));
    return (Pi / sinPi(x)) * Math.exp(-lgamma(1 - x));
  }
  if (x >= 172) return Infinity;
  let q = 1.0;
  while (x < 10) {
    q = q * x;
    x = x + 1;
  }
  const p = pow(x, (x - 0.5) / 2);
  return (Math.sqrt(2 * Pi) * (p * Math.exp(-x)) * p * Math.exp(stirling(x))) / q;
}

/** log |Γ(x)|: the Stirling series from 10 up, log(Γ(x + 1) / x) below; the reflection formula below 0. */
export function lgamma(x: number): number {
  if (Number.isNaN(x)) return x;
  if (x === Infinity || x === -Infinity) return Infinity;
  if (x <= 0 && Math.floor(x) === x) return Infinity;
  if (x < 0) return Math.log(Pi) - Math.log(Math.abs(sinPi(x))) - lgamma(1 - x);
  if (x < 10) return Math.log(tgamma(x + 1)) - Math.log(x);
  return (x - 0.5) * Math.log(x) - x + 0.5 * Math.log(2 * Pi) + stirling(x);
}

/** The sign of Γ(x) (lgamma_r's second element): -1 for -0 and for a negative non-integer x whose floor is odd, else 1. */
export function gammaSign(x: number): number {
  if (x === 0) return isNegative(x) ? -1.0 : 1.0;
  return x < 0 && Math.floor(x) !== x && isOdd(Math.floor(x)) ? -1.0 : 1.0;
}

// The Stirling series of log Γ(x) − ((x − 0.5) · log(x) − x + log(2π) / 2), for x >= 10:
// B2k / (2k (2k − 1) x^(2k − 1)) for k = 1..8; the next term is below 2e-18 there.
function stirling(x: number): number {
  const z = 1 / (x * x);
  const s = 1.0 / 12 + z * (-1.0 / 360 + z * (1.0 / 1260 + z * (-1.0 / 1680 + z * (1.0 / 1188 + z * (-691.0 / 360360 + z * (1.0 / 156 + z * (-3617.0 / 122400)))))));
  return s / x;
}

// sin(π · x) for a finite x that is not an integer, without the rounding of π · x: the fraction
// f = x − trunc(x) is exact, and so are 1 − f and −1 − f when |f| > 0.5.
function sinPi(x: number): number {
  const n = Math.trunc(x);
  let f = x - n;
  if (f > 0.5) f = 1 - f;
  else if (f < -0.5) f = -1 - f;
  const s = Math.sin(Pi * f);
  return isOdd(n) ? -s : s;
}

// Whether an integer-valued double is odd; n − 2 · floor(n / 2) is exact.
function isOdd(n: number): boolean {
  return n - 2 * Math.floor(n / 2) !== 0;
}

/** Bessel J0, Numerical Recipes rational approximations. */
export function j0(x: number): number {
  const a = Math.abs(x);
  if (a <= 8.0) {
    const y = a * a;
    const n = 57568490574.0 + y * (-13362590354.0 + y * (651619640.7 + y * (-11214424.18 + y * (77392.33017 + y * -184.9052456))));
    const d = 57568490411.0 + y * (1029532985.0 + y * (9494680.718 + y * (59272.64853 + y * (267.8532712 + y))));
    return n / d;
  }

  const z = 8.0 / a;
  const yy = z * z;
  const xx = a - 0.785398164;
  const p = 1.0 + yy * (-0.1098628627e-2 + yy * (0.2734510407e-4 + yy * (-0.2073370639e-5 + yy * 0.2093887211e-6)));
  const q = -0.1562499995e-1 + yy * (0.1430488765e-3 + yy * (-0.6911147651e-5 + yy * (0.7621095161e-6 - yy * 0.934935152e-7)));
  return Math.sqrt(0.636619772 / a) * (p * Math.cos(xx) - z * q * Math.sin(xx));
}

/** Bessel J1, Numerical Recipes rational approximations. */
export function j1(x: number): number {
  let s = 1.0;
  let a = x;
  if (a < 0.0) {
    a = -a;
    s = -1.0;
  }

  if (a <= 8.0) {
    const y = a * a;
    const n = a * (72362614232.0 + y * (-7895059235.0 + y * (242396853.1 + y * (-2972611.439 + y * (15704.4826 + y * -30.16036606)))));
    const d = 144725228442.0 + y * (2300535178.0 + y * (18583304.74 + y * (99447.43394 + y * (376.9991397 + y))));
    return (s * n) / d;
  }

  const z = 8.0 / a;
  const yy = z * z;
  const xx = a - 2.356194491;
  const p = 1.0 + yy * (0.183105e-2 + yy * (-0.3516396496e-4 + yy * (0.2457520174e-5 + yy * -0.240337019e-6)));
  const q = 0.04687499995 + yy * (-0.2002690873e-3 + yy * (0.8449199096e-5 + yy * (-0.88228987e-6 + yy * 0.105787412e-6)));
  return s * Math.sqrt(0.636619772 / a) * (p * Math.cos(xx) - z * q * Math.sin(xx));
}

/** Bessel Y0, Numerical Recipes rational approximations. */
export function y0(x: number): number {
  if (x < 0) return NaN;
  if (x === 0) return -Infinity;
  if (x < 8.0) {
    const y = x * x;
    const n = -2957821389.0 + y * (7062834065.0 + y * (-512359803.6 + y * (10879881.29 + y * (-86327.92757 + y * 228.4622733))));
    const d = 40076544269.0 + y * (745249964.8 + y * (7189466.438 + y * (47447.2647 + y * (226.1030244 + y))));
    return n / d + 0.636619772 * j0(x) * Math.log(x);
  }

  const z = 8.0 / x;
  const yy = z * z;
  const xx = x - 0.785398164;
  const p = 1.0 + yy * (-0.1098628627e-2 + yy * (0.2734510407e-4 + yy * (-0.2073370639e-5 + yy * 0.2093887211e-6)));
  const q = -0.1562499995e-1 + yy * (0.1430488765e-3 + yy * (-0.6911147651e-5 + yy * (0.7621095161e-6 + yy * -0.934945152e-7)));
  return Math.sqrt(0.636619772 / x) * (p * Math.sin(xx) + z * q * Math.cos(xx));
}

/** Bessel Y1, Numerical Recipes rational approximations. */
export function y1(x: number): number {
  if (x < 0) return NaN;
  if (x === 0) return -Infinity;
  if (x < 8.0) {
    const y = x * x;
    const n = x * (-0.4900604943e13 + y * (0.127527439e13 + y * (-0.5153438139e11 + y * (0.7349264551e9 + y * (-0.4237922726e7 + y * 0.8511937935e4)))));
    const d = 0.249958057e14 + y * (0.4244419664e12 + y * (0.3733650367e10 + y * (0.2245904002e8 + y * (0.102042605e6 + y * (0.3549632885e3 + y)))));
    return n / d + 0.636619772 * (j1(x) * Math.log(x) - 1.0 / x);
  }

  const z = 8.0 / x;
  const yy = z * z;
  const xx = x - 2.356194491;
  const p = 1.0 + yy * (0.183105e-2 + yy * (-0.3516396496e-4 + yy * (0.2457520174e-5 + yy * -0.240337019e-6)));
  const q = 0.04687499995 + yy * (-0.2002690873e-3 + yy * (0.8449199096e-5 + yy * (-0.88228987e-6 + yy * 0.105787412e-6)));
  return Math.sqrt(0.636619772 / x) * (p * Math.sin(xx) + z * q * Math.cos(xx));
}

/** Bessel J of integer order trunc(n): upward recurrence when |x| > n, else Miller's downward recurrence (Numerical Recipes bessj). */
export function jn(order: number, x: number): number {
  if (!isOrder(order, x)) return NaN;
  const n = Math.trunc(order) + 0;
  if (n < 0) return n % 2 !== 0 ? -jn(-n, x) : jn(-n, x);
  if (n === 0) return j0(x);
  if (n === 1) return j1(x);
  if (x === 0) return 0;
  const a = Math.abs(x);
  const t = 2.0 / a;
  let r: number;
  if (a > n) {
    let p = j0(a);
    r = j1(a);
    for (let k = 1; k < n; k++) {
      const s = k * t * r - p;
      p = r;
      r = s;
    }
  } else {
    const m = 2 * Math.floor((n + Math.floor(Math.sqrt(160.0 * n))) / 2);
    let even = false;
    let p = 0.0;
    let sum = 0.0;
    let b = 1.0;
    r = 0.0;
    for (let k = m; k > 0; k--) {
      const s = k * t * b - p;
      p = b;
      b = s;
      if (Math.abs(b) > 1e10) {
        b = b * 1e-10;
        p = p * 1e-10;
        r = r * 1e-10;
        sum = sum * 1e-10;
      }

      if (even) sum = sum + b;
      even = !even;
      if (k === n) r = p;
    }

    r = r / (2.0 * sum - b);
  }

  return x < 0 && n % 2 !== 0 ? -r : r;
}

/** Bessel Y of integer order trunc(n), by upward recurrence from Y0 and Y1 (Numerical Recipes bessy). */
export function yn(order: number, x: number): number {
  if (!isOrder(order, x)) return NaN;
  const n = Math.trunc(order) + 0;
  if (n < 0) return n % 2 !== 0 ? -yn(-n, x) : yn(-n, x);
  if (n === 0) return y0(x);
  if (n === 1) return y1(x);
  if (x < 0) return NaN;
  if (x === 0) return -Infinity;
  const t = 2.0 / x;
  let p = y0(x);
  let r = y1(x);
  for (let k = 1; k < n; k++) {
    const s = k * t * r - p;
    p = r;
    r = s;
  }

  return r;
}

// Whether jn and yn have an order, trunc(order): not for a NaN argument or |order| >= 2^31 (a NaN result).
function isOrder(order: number, x: number): boolean {
  return !(Number.isNaN(order) || Number.isNaN(x) || !(Math.abs(order) < 2147483648.0));
}

// The unbiased exponent of a finite non-zero x, from its bits; a subnormal is scaled up first.
function exponent(x: number): number {
  const biased = Number((doubleToBits(x) >> 52n) & 0x7ffn);
  if (biased !== 0) return biased - 1023;
  return Number((doubleToBits(x * powerOfTwo(54)) >> 52n) & 0x7ffn) - 1023 - 54;
}

// 2^n for -1022 <= n <= 1023, built from its bits.
function powerOfTwo(n: number): number {
  return bitsToDouble(BigInt(n + 1023) << 52n);
}
