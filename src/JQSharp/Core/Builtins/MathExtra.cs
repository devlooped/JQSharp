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

    /// <summary>log(1 + x), accurate near 0 (Goldberg: the rounding of 1 + x cancels out).</summary>
    public static double Log1p(double x)
    {
        var u = 1 + x;
        if (u == 1 || double.IsPositiveInfinity(u))
            return x;
        return Math.Log(u) * x / (u - 1);
    }

    /// <summary>exp(x) − 1, accurate near 0 (Kahan: the rounding of exp(x) cancels out).</summary>
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
