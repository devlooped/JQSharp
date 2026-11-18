namespace Devlooped.Core.Builtins;

/// <summary>
/// The math of §9.8 that is not a host library function: the exactly specified operations, written
/// with operations every language has with the same IEEE results (+ − × ÷, sqrt, floor, trunc,
/// bit casts), and the special functions defined by formula, evaluated left to right as written.
/// </summary>
static class MathExtra
{
    const double Pi = 3.141592653589793;

    /// <summary>Nearest integer, halves away from zero; the sign of a zero result is the input's.</summary>
    public static double Round(double x)
    {
        if (!double.IsFinite(x))
            return x;
        var t = Math.Truncate(x);
        // x − t is exact: both have the same sign and |t| <= |x|.
        return Math.Abs(x - t) >= 0.5 ? t + (x < 0 ? -1.0 : 1.0) : t;
    }

    /// <summary>Nearest integer, halves to even; the sign of a zero result is the input's.</summary>
    public static double NearbyInt(double x)
    {
        if (!double.IsFinite(x))
            return x;
        var t = Math.Floor(x);
        var d = x - t;
        var r = d > 0.5 || d == 0.5 && t % 2 != 0 ? t + 1 : t;
        return r == 0 && double.IsNegative(x) ? -0.0 : r;
    }

    /// <summary>IEEE remainder: x − n·y with n the integer nearest x/y, halves to even; exact.</summary>
    public static double Remainder(double x, double y)
    {
        var r = x % y;
        if (double.IsNaN(r) || double.IsInfinity(y))
            return r;
        var ay = Math.Abs(y);
        var twice = 2 * Math.Abs(r);
        // r has the sign of x and |r| < |y|; moving it by |y| toward zero is exact (Sterbenz).
        // At a half, the quotient of the truncated division decides: odd moves, even stays.
        if (twice > ay || twice == ay && Math.Abs(x % (2 * ay)) >= ay)
            r -= r < 0 ? -ay : ay;
        return r;
    }

    /// <summary>The smaller argument; NaN gives way to the other; equal values give the first.</summary>
    public static double Min(double x, double y) => double.IsNaN(x) ? y : double.IsNaN(y) ? x : y < x ? y : x;

    /// <summary>The larger argument; NaN gives way to the other; equal values give the first.</summary>
    public static double Max(double x, double y) => double.IsNaN(x) ? y : double.IsNaN(y) ? x : y > x ? y : x;

    /// <summary>x − y when x > y, else 0; NaN when either is NaN.</summary>
    public static double Fdim(double x, double y) => double.IsNaN(x) || double.IsNaN(y) ? double.NaN : x > y ? x - y : 0.0;

    /// <summary>|x| with the sign of y (-0 negative); a NaN y counts as positive, since not every language can tell its sign.</summary>
    public static double CopySign(double x, double y) => double.IsNaN(y) ? Math.Abs(x) : Math.CopySign(x, y);

    /// <summary>The double next to x toward y, by its bits; y when they are equal.</summary>
    public static double NextAfter(double x, double y)
    {
        if (double.IsNaN(x) || double.IsNaN(y))
            return double.NaN;
        if (x == y)
            return y;
        if (x == 0)
            return y > 0 ? double.Epsilon : -double.Epsilon;
        // Away from zero is one more in the magnitude bits, toward zero one less, whatever the sign.
        var bits = BitConverter.DoubleToInt64Bits(x);
        return BitConverter.Int64BitsToDouble(bits + ((y > x) == (x > 0) ? 1 : -1));
    }

    /// <summary>x · 2^e for an integer e (as ldexp); NaN for a finite e that is not an integer; an infinite e multiplies or divides by Infinity.</summary>
    public static double Scalb(double x, double e)
    {
        if (double.IsNaN(x) || double.IsNaN(e))
            return double.NaN;
        if (double.IsPositiveInfinity(e))
            return x * e;
        if (double.IsNegativeInfinity(e))
            return x / double.PositiveInfinity;
        return Math.Truncate(e) == e ? ScaleB(x, e) : double.NaN;
    }

    /// <summary>x · 2^trunc(e), correctly rounded, also to a subnormal (musl's scalbn).</summary>
    public static double ScaleB(double x, double e)
    {
        if (double.IsNaN(e))
            return double.NaN;
        var n = e >= 4000 ? 4000 : e <= -4000 ? -4000 : (int)Math.Truncate(e);
        var y = x;
        for (var i = 0; i < 2 && n > 1023; i++)
        {
            y *= PowerOfTwo(1023);
            n -= 1023;
        }

        // Down in two steps through 2^-1022 · 2^53, so that only the last product rounds.
        for (var i = 0; i < 2 && n < -1022; i++)
        {
            y *= PowerOfTwo(-1022) * PowerOfTwo(53);
            n += 1022 - 53;
        }

        n = Math.Clamp(n, -1022, 1023);
        return y * PowerOfTwo(n);
    }

    /// <summary>The unbiased binary exponent (subnormals included): floor(log2 |x|).</summary>
    public static double Logb(double x)
    {
        if (double.IsNaN(x))
            return x;
        if (double.IsInfinity(x))
            return double.PositiveInfinity;
        if (x == 0)
            return double.NegativeInfinity;
        return Exponent(x);
    }

    /// <summary>x · 2^−logb(x), between 1 and 2 in magnitude: twice the mantissa of frexp; 0, ±Infinity and NaN unchanged.</summary>
    public static double Significand(double x) => x == 0 || !double.IsFinite(x) ? x : 2 * Frexp(x).Mantissa;

    /// <summary>[m, e] with x = m · 2^e and 0.5 ≤ |m| &lt; 1; 0 → [0, 0]; ±Infinity → [±Infinity, 0]; NaN → [NaN, 0].</summary>
    public static (double Mantissa, double Exponent) Frexp(double x)
    {
        if (x == 0 || !double.IsFinite(x))
            return (x, 0);
        var e = Exponent(x) + 1;
        return (ScaleB(x, -e), e);
    }

    /// <summary>[frac, int], both with the sign of x; ±Infinity → [±0, ±Infinity].</summary>
    public static (double Fraction, double Integer) Modf(double x)
    {
        if (double.IsNaN(x))
            return (x, x);
        var t = Math.Truncate(x);
        var f = double.IsInfinity(x) ? 0.0 : x - t;
        return (f == 0 && double.IsNegative(x) ? -0.0 : f, t);
    }

    /// <summary>2^x, as the library pow.</summary>
    /// <remarks>
    /// <code>
    /// exp2(x): pow(2, x)
    /// </code>
    /// </remarks>
    public static double Exp2(double x) => Math.Pow(2, x);

    /// <summary>10^x, as the library pow.</summary>
    public static double Exp10(double x) => Math.Pow(10, x);

    /// <summary>log(1 + x), accurate near 0 (Goldberg: the rounding of 1 + x cancels out).</summary>
    /// <remarks>
    /// <code>
    /// log1p(x): u = 1 + x
    ///           if u == 1 or u == +Infinity: return x
    ///           return log(u) * x / (u - 1)
    /// </code>
    /// </remarks>
    public static double Log1p(double x)
    {
        var u = 1 + x;
        if (u == 1 || double.IsPositiveInfinity(u))
            return x;
        return Math.Log(u) * x / (u - 1);
    }

    /// <summary>exp(x) − 1, accurate near 0 (Kahan: the rounding of exp(x) cancels out).</summary>
    /// <remarks>
    /// <code>
    /// expm1(x): u = exp(x)
    ///           if u == 1: return x
    ///           m = u - 1
    ///           if m == -1 or u == +Infinity: return m
    ///           return m * x / log(u)
    /// </code>
    /// </remarks>
    public static double Expm1(double x)
    {
        var u = Math.Exp(x);
        if (u == 1)
            return x;
        var um1 = u - 1;
        if (um1 == -1 || double.IsInfinity(u))
            return um1;
        return um1 * x / Math.Log(u);
    }

    /// <summary>Abramowitz &amp; Stegun 7.1.26.</summary>
    public static double Erf(double x)
    {
        if (double.IsNaN(x))
            return x;
        var s = x > 0 ? 1.0 : x < 0 ? -1.0 : 0.0;
        var a = Math.Abs(x);
        var t = 1.0 / (1.0 + 0.3275911 * a);
        var y = 1.0 - (((((1.061405429 * t + -1.453152027) * t) + 1.421413741) * t + -0.284496736) * t + 0.254829592) * t * Math.Exp(-a * a);
        return s * y;
    }

    public static double Erfc(double x) => 1.0 - Erf(x);

    static readonly double[] Lanczos =
    [
        0.99999999999980993, 676.5203681218851, -1259.1392167224028,
        771.32342877765313, -176.61502916214059, 12.507343278686905,
        -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
    ];

    /// <summary>Lanczos approximation, g = 7, with the reflection formula below 0.5.</summary>
    public static double TGamma(double x)
    {
        if (x <= 0 && Math.Floor(x) == x)
            return double.PositiveInfinity;
        if (x < 0.5)
            return Pi / (Math.Sin(Pi * x) * TGamma(1 - x));
        x = x - 1;
        var sum = Lanczos[0];
        for (var i = 1; i <= 8; i++)
            sum = sum + Lanczos[i] / (x + i);
        var t = x + 7 + 0.5;
        return Math.Sqrt(2 * Pi) * Math.Pow(t, x + 0.5) * Math.Exp(-t) * sum;
    }

    public static double LGamma(double x) => Math.Log(Math.Abs(TGamma(x)));

    /// <summary>The sign of tgamma(x): -1 when it is negative, else 1 (lgamma_r's second element).</summary>
    public static double GammaSign(double x) => TGamma(x) < 0 ? -1.0 : 1.0;

    /// <summary>Bessel J0, Numerical Recipes rational approximations.</summary>
    public static double J0(double x)
    {
        var a = Math.Abs(x);
        if (a <= 8.0)
        {
            var y = a * a;
            var n = 57568490574.0 + y * (-13362590354.0 + y * (651619640.7 + y * (-11214424.18 + y * (77392.33017 + y * (-184.9052456)))));
            var d = 57568490411.0 + y * (1029532985.0 + y * (9494680.718 + y * (59272.64853 + y * (267.8532712 + y))));
            return n / d;
        }

        var z = 8.0 / a;
        var yy = z * z;
        var xx = a - 0.785398164;
        var p = 1.0 + yy * (-0.1098628627e-2 + yy * (0.2734510407e-4 + yy * (-0.2073370639e-5 + yy * 0.2093887211e-6)));
        var q = -0.1562499995e-1 + yy * (0.1430488765e-3 + yy * (-0.6911147651e-5 + yy * (0.7621095161e-6 - yy * 0.934935152e-7)));
        return Math.Sqrt(0.636619772 / a) * (p * Math.Cos(xx) - z * q * Math.Sin(xx));
    }

    /// <summary>Bessel J1, Numerical Recipes rational approximations.</summary>
    public static double J1(double x)
    {
        var s = 1.0;
        var a = x;
        if (a < 0.0)
        {
            a = -a;
            s = -1.0;
        }

        if (a <= 8.0)
        {
            var y = a * a;
            var n = a * (72362614232.0 + y * (-7895059235.0 + y * (242396853.1 + y * (-2972611.439 + y * (15704.48260 + y * (-30.16036606))))));
            var d = 144725228442.0 + y * (2300535178.0 + y * (18583304.74 + y * (99447.43394 + y * (376.9991397 + y))));
            return s * n / d;
        }

        var z = 8.0 / a;
        var yy = z * z;
        var xx = a - 2.356194491;
        var p = 1.0 + yy * (0.183105e-2 + yy * (-0.3516396496e-4 + yy * (0.2457520174e-5 + yy * (-0.240337019e-6))));
        var q = 0.04687499995 + yy * (-0.2002690873e-3 + yy * (0.8449199096e-5 + yy * (-0.88228987e-6 + yy * 0.105787412e-6)));
        return s * Math.Sqrt(0.636619772 / a) * (p * Math.Cos(xx) - z * q * Math.Sin(xx));
    }

    /// <summary>Bessel Y0, Numerical Recipes rational approximations.</summary>
    public static double Y0(double x)
    {
        if (x < 0)
            return double.NaN;
        if (x == 0)
            return double.NegativeInfinity;
        if (x < 8.0)
        {
            var y = x * x;
            var n = -2957821389.0 + y * (7062834065.0 + y * (-512359803.6 + y * (10879881.29 + y * (-86327.92757 + y * 228.4622733))));
            var d = 40076544269.0 + y * (745249964.8 + y * (7189466.438 + y * (47447.26470 + y * (226.1030244 + y))));
            return n / d + 0.636619772 * J0(x) * Math.Log(x);
        }

        var z = 8.0 / x;
        var yy = z * z;
        var xx = x - 0.785398164;
        var p = 1.0 + yy * (-0.1098628627e-2 + yy * (0.2734510407e-4 + yy * (-0.2073370639e-5 + yy * 0.2093887211e-6)));
        var q = -0.1562499995e-1 + yy * (0.1430488765e-3 + yy * (-0.6911147651e-5 + yy * (0.7621095161e-6 + yy * (-0.934945152e-7))));
        return Math.Sqrt(0.636619772 / x) * (p * Math.Sin(xx) + z * q * Math.Cos(xx));
    }

    /// <summary>Bessel Y1, Numerical Recipes rational approximations.</summary>
    public static double Y1(double x)
    {
        if (x < 0)
            return double.NaN;
        if (x == 0)
            return double.NegativeInfinity;
        if (x < 8.0)
        {
            var y = x * x;
            var n = x * (-0.4900604943e13 + y * (0.1275274390e13 + y * (-0.5153438139e11 + y * (0.7349264551e9 + y * (-0.4237922726e7 + y * 0.8511937935e4)))));
            var d = 0.2499580570e14 + y * (0.4244419664e12 + y * (0.3733650367e10 + y * (0.2245904002e8 + y * (0.1020426050e6 + y * (0.3549632885e3 + y)))));
            return n / d + 0.636619772 * (J1(x) * Math.Log(x) - 1.0 / x);
        }

        var z = 8.0 / x;
        var yy = z * z;
        var xx = x - 2.356194491;
        var p = 1.0 + yy * (0.183105e-2 + yy * (-0.3516396496e-4 + yy * (0.2457520174e-5 + yy * (-0.240337019e-6))));
        var q = 0.04687499995 + yy * (-0.2002690873e-3 + yy * (0.8449199096e-5 + yy * (-0.88228987e-6 + yy * 0.105787412e-6)));
        return Math.Sqrt(0.636619772 / x) * (p * Math.Sin(xx) + z * q * Math.Cos(xx));
    }

    /// <summary>Bessel J of integer order trunc(n): upward recurrence when |x| > n, else Miller's downward recurrence (Numerical Recipes bessj).</summary>
    public static double Jn(double order, double x)
    {
        if (!Order(order, x, out var n))
            return double.NaN;
        if (n < 0)
            return n % 2 != 0 ? -Jn(-n, x) : Jn(-n, x);
        if (n == 0)
            return J0(x);
        if (n == 1)
            return J1(x);
        if (x == 0)
            return 0;
        var a = Math.Abs(x);
        var t = 2.0 / a;
        double r;
        if (a > n)
        {
            var p = J0(a);
            r = J1(a);
            for (long k = 1; k < n; k++)
            {
                var s = k * t * r - p;
                p = r;
                r = s;
            }
        }
        else
        {
            var m = 2 * ((n + (long)Math.Floor(Math.Sqrt(160.0 * n))) / 2);
            var even = false;
            double p = 0.0, sum = 0.0, b = 1.0;
            r = 0.0;
            for (var k = m; k > 0; k--)
            {
                var s = k * t * b - p;
                p = b;
                b = s;
                if (Math.Abs(b) > 1e10)
                {
                    b = b * 1e-10;
                    p = p * 1e-10;
                    r = r * 1e-10;
                    sum = sum * 1e-10;
                }

                if (even)
                    sum = sum + b;
                even = !even;
                if (k == n)
                    r = p;
            }

            r = r / (2.0 * sum - b);
        }

        return x < 0 && n % 2 != 0 ? -r : r;
    }

    /// <summary>Bessel Y of integer order trunc(n), by upward recurrence from Y0 and Y1 (Numerical Recipes bessy).</summary>
    public static double Yn(double order, double x)
    {
        if (!Order(order, x, out var n))
            return double.NaN;
        if (n < 0)
            return n % 2 != 0 ? -Yn(-n, x) : Yn(-n, x);
        if (n == 0)
            return Y0(x);
        if (n == 1)
            return Y1(x);
        if (x < 0)
            return double.NaN;
        if (x == 0)
            return double.NegativeInfinity;
        var t = 2.0 / x;
        var p = Y0(x);
        var r = Y1(x);
        for (long k = 1; k < n; k++)
        {
            var s = k * t * r - p;
            p = r;
            r = s;
        }

        return r;
    }

    // The order of jn and yn, trunc(order); false (a NaN result) for a NaN argument or |order| >= 2^31.
    static bool Order(double order, double x, out long n)
    {
        n = 0;
        if (double.IsNaN(order) || double.IsNaN(x) || !(Math.Abs(order) < 2147483648.0))
            return false;
        n = (long)Math.Truncate(order);
        return true;
    }

    // The unbiased exponent of a finite non-zero x, from its bits; a subnormal is scaled up first.
    static int Exponent(double x)
    {
        var bits = BitConverter.DoubleToInt64Bits(x);
        var biased = (int)((bits >> 52) & 0x7FF);
        if (biased != 0)
            return biased - 1023;
        bits = BitConverter.DoubleToInt64Bits(x * PowerOfTwo(54));
        return (int)((bits >> 52) & 0x7FF) - 1023 - 54;
    }

    // 2^n for -1022 <= n <= 1023, built from its bits.
    static double PowerOfTwo(int n) => BitConverter.Int64BitsToDouble((long)(n + 1023) << 52);
}
