namespace Devlooped;

internal static class MathExtra
{
    // Abramowitz & Stegun approximation 7.1.26 (max error ~1.5e-7)
    // Use a higher-precision rational approximation for better accuracy
    public static double Erf(double x)
    {
        // Save the sign
        var sign = Math.Sign(x);
        x = Math.Abs(x);

        // A&S formula 7.1.26 with constants
        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        const double p = 0.3275911;

        var t = 1.0 / (1.0 + p * x);
        var y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);
        return sign * y;
    }

    public static double Erfc(double x) => 1.0 - Erf(x);

    // Lanczos approximation for Gamma function
    public static double TGamma(double x)
    {
        if (x <= 0 && Math.Floor(x) == x)
            return double.PositiveInfinity; // poles at non-positive integers

        // Reflection formula for negative values
        if (x < 0.5)
            return Math.PI / (Math.Sin(Math.PI * x) * TGamma(1 - x));

        x -= 1;
        const double g = 7;
        double[] c =
        [
            0.99999999999980993,
            676.5203681218851,
            -1259.1392167224028,
            771.32342877765313,
            -176.61502916214059,
            12.507343278686905,
            -0.13857109526572012,
            9.9843695780195716e-6,
            1.5056327351493116e-7
        ];

        var sum = c[0];
        for (var i = 1; i < c.Length; i++)
            sum += c[i] / (x + i);

        var t = x + g + 0.5;
        return Math.Sqrt(2 * Math.PI) * Math.Pow(t, x + 0.5) * Math.Exp(-t) * sum;
    }

    public static double LGamma(double x)
    {
        return Math.Log(Math.Abs(TGamma(x)));
    }

    // Bessel function J0 - polynomial approximation
    public static double BesselJ0(double x)
    {
        x = Math.Abs(x);
        if (x <= 8.0)
        {
            var y = x * x;
            var ans1 = 57568490574.0 + y * (-13362590354.0 + y * (651619640.7
                + y * (-11214424.18 + y * (77392.33017 + y * (-184.9052456)))));
            var ans2 = 57568490411.0 + y * (1029532985.0 + y * (9494680.718
                + y * (59272.64853 + y * (267.8532712 + y))));
            return ans1 / ans2;
        }

        var z = 8.0 / x;
        var y2 = z * z;
        var xx = x - 0.785398164;
        var p0 = 1.0 + y2 * (-0.1098628627e-2 + y2 * (0.2734510407e-4
            + y2 * (-0.2073370639e-5 + y2 * 0.2093887211e-6)));
        var q0 = -0.1562499995e-1 + y2 * (0.1430488765e-3
            + y2 * (-0.6911147651e-5 + y2 * (0.7621095161e-6 - y2 * 0.934935152e-7)));
        return Math.Sqrt(0.636619772 / x) * (p0 * Math.Cos(xx) - z * q0 * Math.Sin(xx));
    }

    // Bessel function J1 - polynomial approximation
    public static double BesselJ1(double x)
    {
        var sign = 1.0;
        if (x < 0.0)
        {
            x = -x;
            sign = -1.0;
        }

        if (x <= 8.0)
        {
            var y = x * x;
            var ans1 = x * (72362614232.0 + y * (-7895059235.0 + y * (242396853.1
                + y * (-2972611.439 + y * (15704.48260 + y * (-30.16036606))))));
            var ans2 = 144725228442.0 + y * (2300535178.0 + y * (18583304.74
                + y * (99447.43394 + y * (376.9991397 + y))));
            return sign * ans1 / ans2;
        }

        var z = 8.0 / x;
        var y2 = z * z;
        var xx = x - 2.356194491;
        var p1 = 1.0 + y2 * (0.183105e-2 + y2 * (-0.3516396496e-4
            + y2 * (0.2457520174e-5 + y2 * (-0.240337019e-6))));
        var q1 = 0.04687499995 + y2 * (-0.2002690873e-3
            + y2 * (0.8449199096e-5 + y2 * (-0.88228987e-6 + y2 * 0.105787412e-6)));
        return sign * Math.Sqrt(0.636619772 / x) * (p1 * Math.Cos(xx) - z * q1 * Math.Sin(xx));
    }
}
