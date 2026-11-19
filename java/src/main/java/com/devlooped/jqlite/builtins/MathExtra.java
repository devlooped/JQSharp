package com.devlooped.jqlite.builtins;

/**
 * The math of §9.8 that is not a host library function: the exactly specified operations, written
 * with operations every language has with the same IEEE results (+ − × ÷, sqrt, floor, trunc, bit
 * casts), and the special functions defined by formula, evaluated left to right as written.
 *
 * <p>Host-specific additions (Java): {@link #pow} fixes {@link StrictMath#pow}'s two departures
 * from C99 ({@code pow(1, y)} and {@code pow(-1, ±Infinity)} are 1), and {@link #acosh},
 * {@link #asinh}, {@link #atanh} and {@link #log2}, which {@code java.lang.StrictMath} lacks, follow
 * fdlibm's formulas on {@code StrictMath.log}, {@code log1p} and {@code sqrt}.
 */
final class MathExtra {
    private static final double PI = 3.141592653589793;
    private static final double LN2 = 0.6931471805599453;

    private MathExtra() {
    }

    private static boolean isFinite(double x) {
        return !Double.isNaN(x) && !Double.isInfinite(x);
    }

    private static boolean isNegative(double x) {
        return Double.doubleToRawLongBits(x) < 0;
    }

    /** Truncation toward zero. */
    static double truncate(double x) {
        return x < 0 ? Math.ceil(x) : Math.floor(x);
    }

    /** Nearest integer, halves away from zero; the sign of a zero result is the input's. */
    static double round(double x) {
        if (!isFinite(x)) {
            return x;
        }
        double t = truncate(x);
        // x − t is exact: both have the same sign and |t| <= |x|.
        return Math.abs(x - t) >= 0.5 ? t + (x < 0 ? -1.0 : 1.0) : t;
    }

    /** Nearest integer, halves to even; the sign of a zero result is the input's. */
    static double nearbyInt(double x) {
        if (!isFinite(x)) {
            return x;
        }
        double t = Math.floor(x);
        double d = x - t;
        double r = d > 0.5 || (d == 0.5 && t % 2 != 0) ? t + 1 : t;
        return r == 0 && isNegative(x) ? -0.0 : r;
    }

    /** IEEE remainder: x − n·y with n the integer nearest x/y, halves to even; exact. */
    static double remainder(double x, double y) {
        double r = x % y;
        if (Double.isNaN(r) || Double.isInfinite(y)) {
            return r;
        }
        double ay = Math.abs(y);
        double twice = 2 * Math.abs(r);
        // r has the sign of x and |r| < |y|; moving it by |y| toward zero is exact (Sterbenz).
        // At a half, the quotient of the truncated division decides: odd moves, even stays.
        if (twice > ay || (twice == ay && Math.abs(x % (2 * ay)) >= ay)) {
            r -= r < 0 ? -ay : ay;
        }
        return r;
    }

    /** The smaller argument; NaN gives way to the other; equal values give the first. */
    static double min(double x, double y) {
        return Double.isNaN(x) ? y : Double.isNaN(y) ? x : y < x ? y : x;
    }

    /** The larger argument; NaN gives way to the other; equal values give the first. */
    static double max(double x, double y) {
        return Double.isNaN(x) ? y : Double.isNaN(y) ? x : y > x ? y : x;
    }

    /** x − y when x &gt; y, else 0; NaN when either is NaN. */
    static double fdim(double x, double y) {
        return Double.isNaN(x) || Double.isNaN(y) ? Double.NaN : x > y ? x - y : 0.0;
    }

    /** |x| with the sign of y (-0 negative); a NaN y counts as positive, since not every language can tell its sign. */
    static double copySign(double x, double y) {
        return Double.isNaN(y) ? Math.abs(x) : Math.copySign(x, y);
    }

    /** The double next to x toward y, by its bits; y when they are equal. */
    static double nextAfter(double x, double y) {
        if (Double.isNaN(x) || Double.isNaN(y)) {
            return Double.NaN;
        }
        if (x == y) {
            return y;
        }
        if (x == 0) {
            return y > 0 ? Double.MIN_VALUE : -Double.MIN_VALUE;
        }
        // Away from zero is one more in the magnitude bits, toward zero one less, whatever the sign.
        long bits = Double.doubleToRawLongBits(x);
        return Double.longBitsToDouble(bits + ((y > x) == (x > 0) ? 1 : -1));
    }

    /** x · 2^e for an integer e (as ldexp); NaN for a finite e that is not an integer; an infinite e multiplies or divides by Infinity. */
    static double scalb(double x, double e) {
        if (Double.isNaN(x) || Double.isNaN(e)) {
            return Double.NaN;
        }
        if (e == Double.POSITIVE_INFINITY) {
            return x * e;
        }
        if (e == Double.NEGATIVE_INFINITY) {
            return x / Double.POSITIVE_INFINITY;
        }
        return truncate(e) == e ? scaleB(x, e) : Double.NaN;
    }

    /** x · 2^trunc(e), correctly rounded, also to a subnormal (musl's scalbn). */
    static double scaleB(double x, double e) {
        if (Double.isNaN(e)) {
            return Double.NaN;
        }
        int n = e >= 4000 ? 4000 : e <= -4000 ? -4000 : (int) truncate(e);
        double y = x;
        for (int i = 0; i < 2 && n > 1023; i++) {
            y *= powerOfTwo(1023);
            n -= 1023;
        }

        // Down in two steps through 2^-1022 · 2^53, so that only the last product rounds.
        for (int i = 0; i < 2 && n < -1022; i++) {
            y *= powerOfTwo(-1022) * powerOfTwo(53);
            n += 1022 - 53;
        }

        n = Math.clamp(n, -1022, 1023);
        return y * powerOfTwo(n);
    }

    /** The unbiased binary exponent (subnormals included): floor(log2 |x|). */
    static double logb(double x) {
        if (Double.isNaN(x)) {
            return x;
        }
        if (Double.isInfinite(x)) {
            return Double.POSITIVE_INFINITY;
        }
        if (x == 0) {
            return Double.NEGATIVE_INFINITY;
        }
        return exponent(x);
    }

    /** x · 2^−logb(x), between 1 and 2 in magnitude: twice the mantissa of frexp; 0, ±Infinity and NaN unchanged. */
    static double significand(double x) {
        return x == 0 || !isFinite(x) ? x : 2 * frexp(x)[0];
    }

    /** [m, e] with x = m · 2^e and 0.5 ≤ |m| &lt; 1; 0 → [0, 0]; ±Infinity → [±Infinity, 0]; NaN → [NaN, 0]. */
    static double[] frexp(double x) {
        if (x == 0 || !isFinite(x)) {
            return new double[] {x, 0};
        }
        int e = exponent(x) + 1;
        return new double[] {scaleB(x, -e), e};
    }

    /** [frac, int], both with the sign of x; ±Infinity → [±0, ±Infinity]. */
    static double[] modf(double x) {
        if (Double.isNaN(x)) {
            return new double[] {x, x};
        }
        double t = truncate(x);
        double f = Double.isInfinite(x) ? 0.0 : x - t;
        return new double[] {f == 0 && isNegative(x) ? -0.0 : f, t};
    }

    /** The library pow with C99's special cases: pow(1, y) and pow(-1, ±Infinity) are 1. */
    static double pow(double x, double y) {
        if (x == 1 || (x == -1 && Double.isInfinite(y))) {
            return 1;
        }
        return StrictMath.pow(x, y);
    }

    /** 2^x, as the library pow. */
    static double exp2(double x) {
        return pow(2, x);
    }

    /** 10^x, as the library pow. */
    static double exp10(double x) {
        return pow(10, x);
    }

    /** log(1 + x), accurate near 0 (Goldberg: the rounding of 1 + x cancels out). */
    static double log1p(double x) {
        double u = 1 + x;
        if (u == 1 || u == Double.POSITIVE_INFINITY) {
            return x;
        }
        return StrictMath.log(u) * x / (u - 1);
    }

    /** exp(x) − 1, accurate near 0 (Kahan: the rounding of exp(x) cancels out). */
    static double expm1(double x) {
        double u = StrictMath.exp(x);
        if (u == 1) {
            return x;
        }
        double um1 = u - 1;
        if (um1 == -1 || Double.isInfinite(u)) {
            return um1;
        }
        return um1 * x / StrictMath.log(u);
    }

    /** log2(x): the binary exponent plus the logarithm of the mantissa, exact for powers of two. */
    static double log2(double x) {
        if (Double.isNaN(x) || x < 0) {
            return Double.NaN;
        }
        if (x == 0) {
            return Double.NEGATIVE_INFINITY;
        }
        if (Double.isInfinite(x)) {
            return x;
        }
        int e = exponent(x);
        double m = scaleB(x, -e);
        return m == 1 ? e : e + StrictMath.log(m) / LN2;
    }

    /** Inverse hyperbolic cosine (fdlibm e_acosh). */
    static double acosh(double x) {
        if (Double.isNaN(x) || x < 1) {
            return Double.NaN;
        }
        if (x >= 268435456.0) {
            return Double.isInfinite(x) ? x : StrictMath.log(x) + LN2;
        }
        if (x == 1) {
            return 0;
        }
        if (x > 2) {
            return StrictMath.log(2 * x - 1 / (x + StrictMath.sqrt(x * x - 1)));
        }
        double t = x - 1;
        return StrictMath.log1p(t + StrictMath.sqrt(2 * t + t * t));
    }

    /** Inverse hyperbolic sine (fdlibm s_asinh). */
    static double asinh(double x) {
        if (Double.isNaN(x) || Double.isInfinite(x)) {
            return x;
        }
        double a = Math.abs(x);
        double w;
        if (a < 3.725290298461914e-9) {
            return x;
        } else if (a > 268435456.0) {
            w = StrictMath.log(a) + LN2;
        } else if (a > 2) {
            w = StrictMath.log(2 * a + 1 / (StrictMath.sqrt(x * x + 1) + a));
        } else {
            double t = x * x;
            w = StrictMath.log1p(a + t / (1 + StrictMath.sqrt(1 + t)));
        }
        return x > 0 ? w : -w;
    }

    /** Inverse hyperbolic tangent (fdlibm e_atanh). */
    static double atanh(double x) {
        double a = Math.abs(x);
        if (Double.isNaN(x) || a > 1) {
            return Double.NaN;
        }
        if (a == 1) {
            return x > 0 ? Double.POSITIVE_INFINITY : Double.NEGATIVE_INFINITY;
        }
        if (a < 3.725290298461914e-9) {
            return x;
        }
        double t;
        if (a < 0.5) {
            t = a + a;
            t = 0.5 * StrictMath.log1p(t + t * a / (1 - a));
        } else {
            t = 0.5 * StrictMath.log1p((a + a) / (1 - a));
        }
        return x >= 0 ? t : -t;
    }

    /** Abramowitz &amp; Stegun 7.1.26. */
    static double erf(double x) {
        if (Double.isNaN(x)) {
            return x;
        }
        double s = x > 0 ? 1.0 : x < 0 ? -1.0 : 0.0;
        double a = Math.abs(x);
        double t = 1.0 / (1.0 + 0.3275911 * a);
        double y = 1.0 - (((((1.061405429 * t + -1.453152027) * t) + 1.421413741) * t + -0.284496736) * t + 0.254829592) * t * StrictMath.exp(-a * a);
        return s * y;
    }

    static double erfc(double x) {
        return 1.0 - erf(x);
    }

    private static final double[] LANCZOS = {
        0.99999999999980993, 676.5203681218851, -1259.1392167224028,
        771.32342877765313, -176.61502916214059, 12.507343278686905,
        -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
    };

    /** Lanczos approximation, g = 7, with the reflection formula below 0.5. */
    static double tGamma(double x) {
        if (x <= 0 && Math.floor(x) == x) {
            return Double.POSITIVE_INFINITY;
        }
        if (x < 0.5) {
            return PI / (StrictMath.sin(PI * x) * tGamma(1 - x));
        }
        x = x - 1;
        double sum = LANCZOS[0];
        for (int i = 1; i <= 8; i++) {
            sum = sum + LANCZOS[i] / (x + i);
        }
        double t = x + 7 + 0.5;
        return StrictMath.sqrt(2 * PI) * pow(t, x + 0.5) * StrictMath.exp(-t) * sum;
    }

    static double lGamma(double x) {
        return StrictMath.log(Math.abs(tGamma(x)));
    }

    /** The sign of tgamma(x): -1 when it is negative, else 1 (lgamma_r's second element). */
    static double gammaSign(double x) {
        return tGamma(x) < 0 ? -1.0 : 1.0;
    }

    /** Bessel J0, Numerical Recipes rational approximations. */
    static double j0(double x) {
        double a = Math.abs(x);
        if (a <= 8.0) {
            double y = a * a;
            double n = 57568490574.0 + y * (-13362590354.0 + y * (651619640.7 + y * (-11214424.18 + y * (77392.33017 + y * (-184.9052456)))));
            double d = 57568490411.0 + y * (1029532985.0 + y * (9494680.718 + y * (59272.64853 + y * (267.8532712 + y))));
            return n / d;
        }

        double z = 8.0 / a;
        double yy = z * z;
        double xx = a - 0.785398164;
        double p = 1.0 + yy * (-0.1098628627e-2 + yy * (0.2734510407e-4 + yy * (-0.2073370639e-5 + yy * 0.2093887211e-6)));
        double q = -0.1562499995e-1 + yy * (0.1430488765e-3 + yy * (-0.6911147651e-5 + yy * (0.7621095161e-6 - yy * 0.934935152e-7)));
        return StrictMath.sqrt(0.636619772 / a) * (p * StrictMath.cos(xx) - z * q * StrictMath.sin(xx));
    }

    /** Bessel J1, Numerical Recipes rational approximations. */
    static double j1(double x) {
        double s = 1.0;
        double a = x;
        if (a < 0.0) {
            a = -a;
            s = -1.0;
        }

        if (a <= 8.0) {
            double y = a * a;
            double n = a * (72362614232.0 + y * (-7895059235.0 + y * (242396853.1 + y * (-2972611.439 + y * (15704.48260 + y * (-30.16036606))))));
            double d = 144725228442.0 + y * (2300535178.0 + y * (18583304.74 + y * (99447.43394 + y * (376.9991397 + y))));
            return s * n / d;
        }

        double z = 8.0 / a;
        double yy = z * z;
        double xx = a - 2.356194491;
        double p = 1.0 + yy * (0.183105e-2 + yy * (-0.3516396496e-4 + yy * (0.2457520174e-5 + yy * (-0.240337019e-6))));
        double q = 0.04687499995 + yy * (-0.2002690873e-3 + yy * (0.8449199096e-5 + yy * (-0.88228987e-6 + yy * 0.105787412e-6)));
        return s * StrictMath.sqrt(0.636619772 / a) * (p * StrictMath.cos(xx) - z * q * StrictMath.sin(xx));
    }

    /** Bessel Y0, Numerical Recipes rational approximations. */
    static double y0(double x) {
        if (x < 0) {
            return Double.NaN;
        }
        if (x == 0) {
            return Double.NEGATIVE_INFINITY;
        }
        if (x < 8.0) {
            double y = x * x;
            double n = -2957821389.0 + y * (7062834065.0 + y * (-512359803.6 + y * (10879881.29 + y * (-86327.92757 + y * 228.4622733))));
            double d = 40076544269.0 + y * (745249964.8 + y * (7189466.438 + y * (47447.26470 + y * (226.1030244 + y))));
            return n / d + 0.636619772 * j0(x) * StrictMath.log(x);
        }

        double z = 8.0 / x;
        double yy = z * z;
        double xx = x - 0.785398164;
        double p = 1.0 + yy * (-0.1098628627e-2 + yy * (0.2734510407e-4 + yy * (-0.2073370639e-5 + yy * 0.2093887211e-6)));
        double q = -0.1562499995e-1 + yy * (0.1430488765e-3 + yy * (-0.6911147651e-5 + yy * (0.7621095161e-6 + yy * (-0.934945152e-7))));
        return StrictMath.sqrt(0.636619772 / x) * (p * StrictMath.sin(xx) + z * q * StrictMath.cos(xx));
    }

    /** Bessel Y1, Numerical Recipes rational approximations. */
    static double y1(double x) {
        if (x < 0) {
            return Double.NaN;
        }
        if (x == 0) {
            return Double.NEGATIVE_INFINITY;
        }
        if (x < 8.0) {
            double y = x * x;
            double n = x * (-0.4900604943e13 + y * (0.1275274390e13 + y * (-0.5153438139e11 + y * (0.7349264551e9 + y * (-0.4237922726e7 + y * 0.8511937935e4)))));
            double d = 0.2499580570e14 + y * (0.4244419664e12 + y * (0.3733650367e10 + y * (0.2245904002e8 + y * (0.1020426050e6 + y * (0.3549632885e3 + y)))));
            return n / d + 0.636619772 * (j1(x) * StrictMath.log(x) - 1.0 / x);
        }

        double z = 8.0 / x;
        double yy = z * z;
        double xx = x - 2.356194491;
        double p = 1.0 + yy * (0.183105e-2 + yy * (-0.3516396496e-4 + yy * (0.2457520174e-5 + yy * (-0.240337019e-6))));
        double q = 0.04687499995 + yy * (-0.2002690873e-3 + yy * (0.8449199096e-5 + yy * (-0.88228987e-6 + yy * 0.105787412e-6)));
        return StrictMath.sqrt(0.636619772 / x) * (p * StrictMath.sin(xx) + z * q * StrictMath.cos(xx));
    }

    /** Bessel J of integer order trunc(n): upward recurrence when |x| &gt; n, else Miller's downward recurrence (Numerical Recipes bessj). */
    static double jn(double order, double x) {
        if (!validOrder(order, x)) {
            return Double.NaN;
        }
        long n = (long) truncate(order);
        if (n < 0) {
            return n % 2 != 0 ? -jn(-n, x) : jn(-n, x);
        }
        if (n == 0) {
            return j0(x);
        }
        if (n == 1) {
            return j1(x);
        }
        if (x == 0) {
            return 0;
        }
        double a = Math.abs(x);
        double t = 2.0 / a;
        double r;
        if (a > n) {
            double p = j0(a);
            r = j1(a);
            for (long k = 1; k < n; k++) {
                double s = k * t * r - p;
                p = r;
                r = s;
            }
        } else {
            long m = 2 * ((n + (long) Math.floor(StrictMath.sqrt(160.0 * n))) / 2);
            boolean even = false;
            double p = 0.0;
            double sum = 0.0;
            double b = 1.0;
            r = 0.0;
            for (long k = m; k > 0; k--) {
                double s = k * t * b - p;
                p = b;
                b = s;
                if (Math.abs(b) > 1e10) {
                    b = b * 1e-10;
                    p = p * 1e-10;
                    r = r * 1e-10;
                    sum = sum * 1e-10;
                }

                if (even) {
                    sum = sum + b;
                }
                even = !even;
                if (k == n) {
                    r = p;
                }
            }

            r = r / (2.0 * sum - b);
        }

        return x < 0 && n % 2 != 0 ? -r : r;
    }

    /** Bessel Y of integer order trunc(n), by upward recurrence from Y0 and Y1 (Numerical Recipes bessy). */
    static double yn(double order, double x) {
        if (!validOrder(order, x)) {
            return Double.NaN;
        }
        long n = (long) truncate(order);
        if (n < 0) {
            return n % 2 != 0 ? -yn(-n, x) : yn(-n, x);
        }
        if (n == 0) {
            return y0(x);
        }
        if (n == 1) {
            return y1(x);
        }
        if (x < 0) {
            return Double.NaN;
        }
        if (x == 0) {
            return Double.NEGATIVE_INFINITY;
        }
        double t = 2.0 / x;
        double p = y0(x);
        double r = y1(x);
        for (long k = 1; k < n; k++) {
            double s = k * t * r - p;
            p = r;
            r = s;
        }

        return r;
    }

    // The order of jn and yn is trunc(order); false (a NaN result) for a NaN argument or |order| >= 2^31.
    private static boolean validOrder(double order, double x) {
        return !Double.isNaN(order) && !Double.isNaN(x) && Math.abs(order) < 2147483648.0;
    }

    // The unbiased exponent of a finite non-zero x, from its bits; a subnormal is scaled up first.
    private static int exponent(double x) {
        long bits = Double.doubleToRawLongBits(x);
        int biased = (int) ((bits >> 52) & 0x7FF);
        if (biased != 0) {
            return biased - 1023;
        }
        bits = Double.doubleToRawLongBits(x * powerOfTwo(54));
        return (int) ((bits >> 52) & 0x7FF) - 1023 - 54;
    }

    // 2^n for -1022 <= n <= 1023, built from its bits.
    private static double powerOfTwo(int n) {
        return Double.longBitsToDouble((long) (n + 1023) << 52);
    }
}
