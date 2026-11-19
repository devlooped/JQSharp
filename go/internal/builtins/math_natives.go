package builtins

// Host-specific (H): the library functions of §9.8 come from Go's math package (floor, ceil,
// trunc, fabs, sqrt, the trigonometric, hyperbolic, exponential and logarithmic functions, cbrt,
// atan2, pow, hypot, fma, fmod as math.Mod). A port uses its host's libm equivalents.

import (
	"math"

	"github.com/devlooped/jqlite/go/internal/values"
)

type mathNatives struct{}

// MathNatives are §9.8 Math. One-input functions take a number input; multi-input ones ignore the
// input and take number arguments (the first non-number, in argument order, is the error).
var MathNatives mathNatives

type unaryFunction struct {
	name     string
	function func(float64) float64
}

type binaryFunction struct {
	name     string
	function func(float64, float64) float64
}

// The functions of one number: the exactly specified ones, the library functions, and those the
// spec defines by formula (MathExtra: exp2, expm1, log1p and the special functions).
var unaryFunctions = []unaryFunction{
	{"floor", math.Floor},
	{"ceil", math.Ceil},
	{"trunc", math.Trunc},
	{"round", MathExtra.Round},
	{"nearbyint", MathExtra.NearbyInt},
	{"rint", MathExtra.NearbyInt},
	{"fabs", math.Abs},
	{"sqrt", math.Sqrt},
	{"logb", MathExtra.Logb},
	{"significand", MathExtra.Significand},
	{"acos", math.Acos},
	{"asin", math.Asin},
	{"atan", math.Atan},
	{"acosh", math.Acosh},
	{"asinh", math.Asinh},
	{"atanh", math.Atanh},
	{"cbrt", math.Cbrt},
	{"cos", math.Cos},
	{"cosh", math.Cosh},
	{"exp", math.Exp},
	{"exp2", MathExtra.Exp2},
	{"exp10", MathExtra.Exp10},
	{"expm1", MathExtra.Expm1},
	{"log", math.Log},
	{"log10", math.Log10},
	{"log1p", MathExtra.Log1p},
	{"log2", math.Log2},
	{"sin", math.Sin},
	{"sinh", math.Sinh},
	{"tan", math.Tan},
	{"tanh", math.Tanh},
	{"erf", MathExtra.Erf},
	{"erfc", MathExtra.Erfc},
	{"tgamma", MathExtra.TGamma},
	{"lgamma", MathExtra.LGamma},
	{"gamma", MathExtra.LGamma},
	{"j0", MathExtra.J0},
	{"j1", MathExtra.J1},
	{"y0", MathExtra.Y0},
	{"y1", MathExtra.Y1},
}

var binaryFunctions = []binaryFunction{
	{"fmod", math.Mod},
	{"remainder", MathExtra.Remainder},
	{"drem", MathExtra.Remainder},
	{"fmin", MathExtra.Min},
	{"fmax", MathExtra.Max},
	{"fdim", MathExtra.Fdim},
	{"copysign", MathExtra.CopySign},
	{"nextafter", MathExtra.NextAfter},
	{"nexttoward", MathExtra.NextAfter},
	{"ldexp", MathExtra.ScaleB},
	{"scalbln", MathExtra.ScaleB},
	{"scalb", MathExtra.Scalb},
	{"atan2", math.Atan2},
	{"pow", math.Pow},
	{"hypot", math.Hypot},
	{"jn", MathExtra.Jn},
	{"yn", MathExtra.Yn},
}

func (mathNatives) Register() {
	for _, u := range unaryFunctions {
		function := u.function
		add(u.name, 0, func(_ *interpreter, input value, _ []value) (value, value, bool) {
			if input.Kind() != values.KindNumber {
				return fail(values.Errors.NumberRequired(input))
			}
			return ok(values.FromNumber(function(input.AsNumber())))
		})
	}

	for _, b := range binaryFunctions {
		function := b.function
		add(b.name, 2, func(_ *interpreter, _ value, arguments []value) (value, value, bool) {
			if err, numbers := numbers(arguments); !numbers {
				return fail(err)
			}
			return ok(values.FromNumber(function(arguments[0].AsNumber(), arguments[1].AsNumber())))
		})
	}

	add("fma", 3, func(_ *interpreter, _ value, arguments []value) (value, value, bool) {
		if err, numbers := numbers(arguments); !numbers {
			return fail(err)
		}
		return ok(values.FromNumber(math.FMA(arguments[0].AsNumber(), arguments[1].AsNumber(), arguments[2].AsNumber())))
	})
	add("frexp", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) {
		if input.Kind() != values.KindNumber {
			return fail(values.Errors.NumberRequired(input))
		}
		mantissa, exponent := MathExtra.Frexp(input.AsNumber())
		return ok(pair(mantissa, exponent))
	})
	add("lgamma_r", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) {
		if input.Kind() != values.KindNumber {
			return fail(values.Errors.NumberRequired(input))
		}
		x := input.AsNumber()
		return ok(pair(MathExtra.LGamma(x), MathExtra.GammaSign(x)))
	})
	add("modf", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) {
		if input.Kind() != values.KindNumber {
			return fail(values.Errors.NumberRequired(input))
		}
		fraction, integer := MathExtra.Modf(input.AsNumber())
		return ok(pair(fraction, integer))
	})
}

func numbers(arguments []value) (value, bool) {
	for _, argument := range arguments {
		if argument.Kind() != values.KindNumber {
			return values.Errors.NumberRequired(argument), false
		}
	}
	return values.Null, true
}

func pair(first, second float64) value {
	return values.FromArray(values.ArrayOf(values.FromNumber(first), values.FromNumber(second)))
}
