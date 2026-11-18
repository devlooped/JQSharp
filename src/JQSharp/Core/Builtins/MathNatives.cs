using Devlooped.Core.Values;
using static Devlooped.Core.Builtins.NativeTable;

namespace Devlooped.Core.Builtins;

/// <summary>
/// §9.8 Math. One-input functions take a number input; multi-input ones ignore the input and take
/// number arguments (the first non-number, in argument order, is the error).
/// </summary>
static class MathNatives
{
    // The functions of one number: the exactly specified ones, the library functions (the host's
    // libm, which every target language has, cbrt and hypot included), and those the spec defines by
    // formula (MathExtra: exp2, expm1, log1p and the special functions).
    static readonly (string Name, Func<double, double> Function)[] unary =
    [
        ("floor", Math.Floor),
        ("ceil", Math.Ceiling),
        ("trunc", Math.Truncate),
        ("round", MathExtra.Round),
        ("nearbyint", MathExtra.NearbyInt),
        ("rint", MathExtra.NearbyInt),
        ("fabs", Math.Abs),
        ("sqrt", Math.Sqrt),
        ("logb", MathExtra.Logb),
        ("significand", MathExtra.Significand),
        ("acos", Math.Acos),
        ("asin", Math.Asin),
        ("atan", Math.Atan),
        ("acosh", Math.Acosh),
        ("asinh", Math.Asinh),
        ("atanh", Math.Atanh),
        ("cbrt", Math.Cbrt),
        ("cos", Math.Cos),
        ("cosh", Math.Cosh),
        ("exp", Math.Exp),
        ("exp2", MathExtra.Exp2),
        ("exp10", MathExtra.Exp10),
        ("expm1", MathExtra.Expm1),
        ("log", Math.Log),
        ("log10", Math.Log10),
        ("log1p", MathExtra.Log1p),
        ("log2", Math.Log2),
        ("sin", Math.Sin),
        ("sinh", Math.Sinh),
        ("tan", Math.Tan),
        ("tanh", Math.Tanh),
        ("erf", MathExtra.Erf),
        ("erfc", MathExtra.Erfc),
        ("tgamma", MathExtra.TGamma),
        ("lgamma", MathExtra.LGamma),
        ("gamma", MathExtra.LGamma),
        ("j0", MathExtra.J0),
        ("j1", MathExtra.J1),
        ("y0", MathExtra.Y0),
        ("y1", MathExtra.Y1),
    ];

    static readonly (string Name, Func<double, double, double> Function)[] binary =
    [
        ("fmod", static (x, y) => x % y),
        ("remainder", MathExtra.Remainder),
        ("drem", MathExtra.Remainder),
        ("fmin", MathExtra.Min),
        ("fmax", MathExtra.Max),
        ("fdim", MathExtra.Fdim),
        ("copysign", MathExtra.CopySign),
        ("nextafter", MathExtra.NextAfter),
        ("nexttoward", MathExtra.NextAfter),
        ("ldexp", MathExtra.ScaleB),
        ("scalbln", MathExtra.ScaleB),
        ("scalb", MathExtra.Scalb),
        ("atan2", Math.Atan2),
        ("pow", Math.Pow),
        ("hypot", double.Hypot),
        ("jn", MathExtra.Jn),
        ("yn", MathExtra.Yn),
    ];

    public static void Register()
    {
        foreach (var (name, function) in unary)
        {
            Add(name, 0, (_, input, _, out result, out error) => input.Kind == JqKind.Number
                ? Ok(JqValue.FromNumber(function(input.AsNumber())), out result, out error)
                : Fail(Errors.NumberRequired(input), out result, out error));
        }

        foreach (var (name, function) in binary)
        {
            Add(name, 2, (_, _, arguments, out result, out error) => Numbers(arguments, out error)
                ? Ok(JqValue.FromNumber(function(arguments[0].AsNumber(), arguments[1].AsNumber())), out result, out error)
                : Fail(error, out result, out error));
        }

        Add("fma", 3, static (_, _, arguments, out result, out error) => Numbers(arguments, out error)
            ? Ok(JqValue.FromNumber(Math.FusedMultiplyAdd(arguments[0].AsNumber(), arguments[1].AsNumber(), arguments[2].AsNumber())), out result, out error)
            : Fail(error, out result, out error));
        Add("frexp", 0, static (_, input, _, out result, out error) =>
        {
            if (input.Kind != JqKind.Number)
                return Fail(Errors.NumberRequired(input), out result, out error);
            var (mantissa, exponent) = MathExtra.Frexp(input.AsNumber());
            return Ok(Pair(mantissa, exponent), out result, out error);
        });
        Add("lgamma_r", 0, static (_, input, _, out result, out error) =>
        {
            if (input.Kind != JqKind.Number)
                return Fail(Errors.NumberRequired(input), out result, out error);
            var x = input.AsNumber();
            return Ok(Pair(MathExtra.LGamma(x), MathExtra.GammaSign(x)), out result, out error);
        });
        Add("modf", 0, static (_, input, _, out result, out error) =>
        {
            if (input.Kind != JqKind.Number)
                return Fail(Errors.NumberRequired(input), out result, out error);
            var (fraction, integer) = MathExtra.Modf(input.AsNumber());
            return Ok(Pair(fraction, integer), out result, out error);
        });
    }

    static bool Numbers(JqValue[] arguments, out JqValue error)
    {
        foreach (var argument in arguments)
        {
            if (argument.Kind != JqKind.Number)
            {
                error = Errors.NumberRequired(argument);
                return false;
            }
        }

        error = default;
        return true;
    }

    static JqValue Pair(double first, double second)
        => JqValue.FromArray(JqArray.Of(JqValue.FromNumber(first), JqValue.FromNumber(second)));
}
