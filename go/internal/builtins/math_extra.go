package builtins

import "math"

type mathExtra struct{}

// MathExtra is the math of §9.8 that is not a host library function: the exactly specified
// operations, written with operations every language has with the same IEEE results (+ − × ÷, sqrt,
// floor, trunc, bit casts), and the special functions defined by formula, evaluated left to right
// as written.
//
// No fused multiply-add: Go may fuse x*y + z into one operation (it does on arm64), so each product
// that is added is converted with float64(…), which the Go spec says rounds it first.
var MathExtra mathExtra

const pi = 3.141592653589793

// Round is the nearest integer, halves away from zero; the sign of a zero result is the input's.
func (mathExtra) Round(x float64) float64 {
	if math.IsNaN(x) || math.IsInf(x, 0) {
		return x
	}
	t := math.Trunc(x)
	// x − t is exact: both have the same sign and |t| <= |x|.
	if math.Abs(x-t) >= 0.5 {
		if x < 0 {
			return t - 1
		}
		return t + 1
	}
	return t
}

// NearbyInt is the nearest integer, halves to even; the sign of a zero result is the input's.
func (mathExtra) NearbyInt(x float64) float64 {
	if math.IsNaN(x) || math.IsInf(x, 0) {
		return x
	}
	t := math.Floor(x)
	d := x - t
	r := t
	if d > 0.5 || (d == 0.5 && math.Mod(t, 2) != 0) {
		r = t + 1
	}
	if r == 0 && math.Signbit(x) {
		return math.Copysign(0, -1)
	}
	return r
}

// Remainder is the IEEE remainder: x − n·y with n the integer nearest x/y, halves to even; exact.
func (mathExtra) Remainder(x, y float64) float64 {
	r := math.Mod(x, y)
	if math.IsNaN(r) || math.IsInf(y, 0) {
		return r
	}
	ay := math.Abs(y)
	twice := 2 * math.Abs(r)
	// r has the sign of x and |r| < |y|; moving it by |y| toward zero is exact (Sterbenz). At a
	// half, the quotient of the truncated division decides: odd moves, even stays.
	if twice > ay || (twice == ay && math.Abs(math.Mod(x, 2*ay)) >= ay) {
		if r < 0 {
			r += ay
		} else {
			r -= ay
		}
	}
	return r
}

// Min is the smaller argument; NaN gives way to the other; equal values give the first.
func (mathExtra) Min(x, y float64) float64 {
	switch {
	case math.IsNaN(x):
		return y
	case math.IsNaN(y):
		return x
	case y < x:
		return y
	}
	return x
}

// Max is the larger argument; NaN gives way to the other; equal values give the first.
func (mathExtra) Max(x, y float64) float64 {
	switch {
	case math.IsNaN(x):
		return y
	case math.IsNaN(y):
		return x
	case y > x:
		return y
	}
	return x
}

// Fdim is x − y when x > y, else 0; NaN when either is NaN.
func (mathExtra) Fdim(x, y float64) float64 {
	switch {
	case math.IsNaN(x) || math.IsNaN(y):
		return math.NaN()
	case x > y:
		return x - y
	}
	return 0
}

// CopySign is |x| with the sign of y (-0 negative); a NaN y counts as positive, since not every
// language can tell its sign.
func (mathExtra) CopySign(x, y float64) float64 {
	if math.IsNaN(y) {
		return math.Abs(x)
	}
	return math.Copysign(x, y)
}

// NextAfter is the double next to x toward y, by its bits; y when they are equal.
func (mathExtra) NextAfter(x, y float64) float64 {
	if math.IsNaN(x) || math.IsNaN(y) {
		return math.NaN()
	}
	if x == y {
		return y
	}
	if x == 0 {
		if y > 0 {
			return math.SmallestNonzeroFloat64
		}
		return -math.SmallestNonzeroFloat64
	}
	// Away from zero is one more in the magnitude bits, toward zero one less, whatever the sign.
	bits := int64(math.Float64bits(x))
	if (y > x) == (x > 0) {
		bits++
	} else {
		bits--
	}
	return math.Float64frombits(uint64(bits))
}

// Scalb is x · 2^e for an integer e (as ldexp); NaN for a finite e that is not an integer; an
// infinite e multiplies or divides by Infinity.
func (mathExtra) Scalb(x, e float64) float64 {
	switch {
	case math.IsNaN(x) || math.IsNaN(e):
		return math.NaN()
	case math.IsInf(e, 1):
		return x * e
	case math.IsInf(e, -1):
		return x / math.Inf(1)
	case math.Trunc(e) == e:
		return MathExtra.ScaleB(x, e)
	}
	return math.NaN()
}

// ScaleB is x · 2^trunc(e), correctly rounded, also to a subnormal (musl's scalbn).
func (mathExtra) ScaleB(x, e float64) float64 {
	if math.IsNaN(e) {
		return math.NaN()
	}
	var n int
	switch {
	case e >= 4000:
		n = 4000
	case e <= -4000:
		n = -4000
	default:
		n = int(math.Trunc(e))
	}
	y := x
	for i := 0; i < 2 && n > 1023; i++ {
		y *= powerOfTwo(1023)
		n -= 1023
	}

	// Down in two steps through 2^-1022 · 2^53, so that only the last product rounds.
	for i := 0; i < 2 && n < -1022; i++ {
		y *= powerOfTwo(-1022) * powerOfTwo(53)
		n += 1022 - 53
	}

	n = max(-1022, min(1023, n))
	return y * powerOfTwo(n)
}

// Logb is the unbiased binary exponent (subnormals included): floor(log2 |x|).
func (mathExtra) Logb(x float64) float64 {
	switch {
	case math.IsNaN(x):
		return x
	case math.IsInf(x, 0):
		return math.Inf(1)
	case x == 0:
		return math.Inf(-1)
	}
	return float64(exponent(x))
}

// Significand is x · 2^−logb(x), between 1 and 2 in magnitude: twice the mantissa of frexp; 0,
// ±Infinity and NaN unchanged.
func (mathExtra) Significand(x float64) float64 {
	if x == 0 || math.IsInf(x, 0) || math.IsNaN(x) {
		return x
	}
	mantissa, _ := MathExtra.Frexp(x)
	return 2 * mantissa
}

// Frexp is [m, e] with x = m · 2^e and 0.5 ≤ |m| < 1; 0 → [0, 0]; ±Infinity → [±Infinity, 0]; NaN → [NaN, 0].
func (mathExtra) Frexp(x float64) (mantissa, exp float64) {
	if x == 0 || math.IsInf(x, 0) || math.IsNaN(x) {
		return x, 0
	}
	e := float64(exponent(x) + 1)
	return MathExtra.ScaleB(x, -e), e
}

// Modf is [frac, int], both with the sign of x; ±Infinity → [±0, ±Infinity].
func (mathExtra) Modf(x float64) (fraction, integer float64) {
	if math.IsNaN(x) {
		return x, x
	}
	t := math.Trunc(x)
	f := 0.0
	if !math.IsInf(x, 0) {
		f = x - t
	}
	if f == 0 && math.Signbit(x) {
		f = math.Copysign(0, -1)
	}
	return f, t
}

// Exp2 is 2^x, as the library pow.
//
//	exp2(x): pow(2, x)
func (mathExtra) Exp2(x float64) float64 { return math.Pow(2, x) }

// Exp10 is 10^x, as the library pow.
func (mathExtra) Exp10(x float64) float64 { return math.Pow(10, x) }

// Log1p is log(1 + x), accurate near 0 (Goldberg: the rounding of 1 + x cancels out).
//
//	log1p(x): u = 1 + x
//	          if u == 1 or u == +Infinity: return x
//	          return log(u) * x / (u - 1)
func (mathExtra) Log1p(x float64) float64 {
	u := 1 + x
	if u == 1 || math.IsInf(u, 1) {
		return x
	}
	return math.Log(u) * x / (u - 1)
}

// Expm1 is exp(x) − 1, accurate near 0 (Kahan: the rounding of exp(x) cancels out).
//
//	expm1(x): u = exp(x)
//	          if u == 1: return x
//	          m = u - 1
//	          if m == -1 or u == +Infinity: return m
//	          return m * x / log(u)
func (mathExtra) Expm1(x float64) float64 {
	u := math.Exp(x)
	if u == 1 {
		return x
	}
	um1 := u - 1
	if um1 == -1 || math.IsInf(u, 0) {
		return um1
	}
	return um1 * x / math.Log(u)
}

// The constants of fdlibm's s_erf.c: 0.84506291151 rounded to single precision, 2/√π − 1, and 8 times it.
const (
	erx  = 8.45062911510467529297e-01
	efx  = 1.28379167095512586316e-01
	efx8 = 1.02703333676410069053e+00
)

// Erf is the error function, fdlibm's s_erf.c: rational approximations on [0, 0.84375),
// [0.84375, 1.25) and [1.25, 6).
func (mathExtra) Erf(x float64) float64 {
	if math.IsNaN(x) {
		return x
	}
	a := math.Abs(x)
	if a < 0.84375 {
		if a < 2.848094538889218e-306 {
			return 0.125 * (float64(8*x) + float64(efx8*x))
		}
		if a < 3.725290298461914e-09 {
			return x + float64(efx*x)
		}
		return x + float64(x*erfSmall(x*x))
	}
	var r float64
	if a < 1.25 {
		r = erx + erfNearOne(a-1)
	} else if a >= 6 {
		r = 1
	} else {
		r = 1 - erfcTail(a)
	}
	if x < 0 {
		return -r
	}
	return r
}

// Erfc is the complementary error function, fdlibm's s_erf.c: computed directly, so that it keeps
// its relative accuracy where it is tiny.
func (mathExtra) Erfc(x float64) float64 {
	if math.IsNaN(x) {
		return x
	}
	a := math.Abs(x)
	if a < 0.84375 {
		if a < 1.3877787807814457e-17 {
			return 1 - x
		}
		y := erfSmall(x * x)
		if x < 0.25 {
			return 1 - (x + float64(x*y))
		}
		return 0.5 - (float64(x*y) + (x - 0.5))
	}
	if a < 1.25 {
		if x >= 0 {
			return (1 - erx) - erfNearOne(a-1)
		}
		return 1 + (erx + erfNearOne(a-1))
	}
	if x <= -6 {
		return 2
	}
	if a >= 28 {
		if x > 0 {
			return 0
		}
		return 2
	}
	if x > 0 {
		return erfcTail(a)
	}
	return 2 - erfcTail(a)
}

// erfSmall is (erf(x) − x) / x as a function of z = x², for |x| < 0.84375.
func erfSmall(z float64) float64 {
	p := horner(z, 1.28379167095512558561e-01, -3.25042107247001499370e-01, -2.84817495755985104766e-02, -5.77027029648944159157e-03, -2.37630166566501626084e-05)
	q := horner(z, 1, 3.97917223959155352819e-01, 6.50222499887672944485e-02, 5.08130628187576562776e-03, 1.32494738004321644526e-04, -3.96022827877536812320e-06)
	return p / q
}

// erfNearOne is erf(1 + s) − erx, for 0.84375 <= 1 + s < 1.25.
func erfNearOne(s float64) float64 {
	p := horner(s, -2.36211856075265944077e-03, 4.14856118683748331666e-01, -3.72207876035701323847e-01, 3.18346619901161753674e-01, -1.10894694282396677476e-01, 3.54783043256182359371e-02, -2.16637559486879084300e-03)
	q := horner(s, 1, 1.06420880400844228286e-01, 5.40397917702171048937e-01, 7.18286544141962662868e-02, 1.26171219808761642112e-01, 1.36370839120290507362e-02, 1.19844998467991074170e-02)
	return p / q
}

// erfcTail is erfc(a) for 1.25 <= a < 28: exp(−a² − 0.5625 + R/S) / a, with exp(−a²) as
// exp(−z²) · exp((z − a)(z + a)) for z = a with the low 32 bits cleared, so that z² is exact.
func erfcTail(a float64) float64 {
	s := 1 / (a * a)
	var r, t float64
	if a < 1/0.35 {
		r = horner(s, -9.86494403484714822705e-03, -6.93858572707181764372e-01, -1.05586262253232909814e+01, -6.23753324503260060396e+01, -1.62396669462573470355e+02, -1.84605092906711035994e+02, -8.12874355063065934246e+01, -9.81432934416914548592e+00)
		t = horner(s, 1, 1.96512716674392571292e+01, 1.37657754143519042600e+02, 4.34565877475229228821e+02, 6.45387271733267880336e+02, 4.29008140027567833386e+02, 1.08635005541779435134e+02, 6.57024977031928170135e+00, -6.04244152148580987438e-02)
	} else {
		r = horner(s, -9.86494292470009928597e-03, -7.99283237680523006574e-01, -1.77579549177547519889e+01, -1.60636384855821916062e+02, -6.37566443368389627722e+02, -1.02509513161107724954e+03, -4.83519191608651397019e+02)
		t = horner(s, 1, 3.03380607434824582924e+01, 3.25792512996573918826e+02, 1.53672958608443695994e+03, 3.19985821950859553908e+03, 2.55305040643316442583e+03, 4.74528541206955367215e+02, -2.24409524465858183362e+01)
	}
	z := math.Float64frombits(math.Float64bits(a) &^ 0xFFFFFFFF)
	return math.Exp(float64(-z*z)-0.5625) * math.Exp(float64((z-a)*(z+a))+r/t) / a
}

// TGamma is Γ(x): the Stirling series once x is shifted up to 10 or more, the power split so that
// it overflows only past 171.62; the reflection formula below 0.
func (mathExtra) TGamma(x float64) float64 {
	if math.IsNaN(x) || math.IsInf(x, 1) {
		return x
	}
	if x == 0 {
		return 1 / x
	}
	if x < 0 {
		if math.Floor(x) == x {
			return math.NaN()
		}
		if x > -170 {
			return pi / float64(sinPi(x)*MathExtra.TGamma(1-x))
		}
		return pi / sinPi(x) * math.Exp(-MathExtra.LGamma(1-x))
	}
	if x >= 172 {
		return math.Inf(1)
	}
	q := 1.0
	for x < 10 {
		q = q * x
		x = x + 1
	}
	p := math.Pow(x, (x-0.5)/2)
	return math.Sqrt(2*pi) * float64(p*math.Exp(-x)) * p * math.Exp(stirling(x)) / q
}

// LGamma is log |Γ(x)|: the Stirling series from 10 up, log(Γ(x + 1) / x) below; the reflection formula below 0.
func (mathExtra) LGamma(x float64) float64 {
	if math.IsNaN(x) {
		return x
	}
	if math.IsInf(x, 0) {
		return math.Inf(1)
	}
	if x <= 0 && math.Floor(x) == x {
		return math.Inf(1)
	}
	if x < 0 {
		return math.Log(pi) - math.Log(math.Abs(sinPi(x))) - MathExtra.LGamma(1-x)
	}
	if x < 10 {
		return math.Log(MathExtra.TGamma(x+1)) - math.Log(x)
	}
	return float64((x-0.5)*math.Log(x)) - x + float64(0.5*math.Log(2*pi)) + stirling(x)
}

// GammaSign is the sign of Γ(x) (lgamma_r's second element): -1 for -0 and for a negative
// non-integer x whose floor is odd, else 1.
func (mathExtra) GammaSign(x float64) float64 {
	if x == 0 {
		if math.Signbit(x) {
			return -1
		}
		return 1
	}
	if x < 0 && math.Floor(x) != x && isOdd(math.Floor(x)) {
		return -1
	}
	return 1
}

// stirling is the Stirling series of log Γ(x) − ((x − 0.5) · log(x) − x + log(2π) / 2), for
// x >= 10: B2k / (2k (2k − 1) x^(2k − 1)) for k = 1..8; the next term is below 2e-18 there.
func stirling(x float64) float64 {
	z := 1 / (x * x)
	s := horner(z, 1.0/12, -1.0/360, 1.0/1260, -1.0/1680, 1.0/1188, -691.0/360360, 1.0/156, -3617.0/122400)
	return s / x
}

// sinPi is sin(π · x) for a finite x that is not an integer, without the rounding of π · x: the
// fraction f = x − trunc(x) is exact, and so are 1 − f and −1 − f when |f| > 0.5.
func sinPi(x float64) float64 {
	n := math.Trunc(x)
	f := x - n
	if f > 0.5 {
		f = 1 - f
	} else if f < -0.5 {
		f = -1 - f
	}
	s := math.Sin(pi * f)
	if isOdd(n) {
		return -s
	}
	return s
}

// isOdd is whether an integer-valued double is odd; n − 2 · floor(n / 2) is exact.
func isOdd(n float64) bool { return n-float64(2*math.Floor(n/2)) != 0 }

// horner is c[0] + y*(c[1] + y*(c[2] + …)), each product rounded before it is added.
func horner(y float64, c ...float64) float64 {
	r := c[len(c)-1]
	for i := len(c) - 2; i >= 0; i-- {
		r = c[i] + float64(y*r)
	}
	return r
}

// J0 is Bessel J0, Numerical Recipes rational approximations.
func (mathExtra) J0(x float64) float64 {
	a := math.Abs(x)
	if a <= 8.0 {
		y := a * a
		n := horner(y, 57568490574.0, -13362590354.0, 651619640.7, -11214424.18, 77392.33017, -184.9052456)
		d := horner(y, 57568490411.0, 1029532985.0, 9494680.718, 59272.64853, 267.8532712, 1)
		return n / d
	}

	z := 8.0 / a
	yy := z * z
	xx := a - 0.785398164
	p := horner(yy, 1.0, -0.1098628627e-2, 0.2734510407e-4, -0.2073370639e-5, 0.2093887211e-6)
	q := -0.1562499995e-1 + float64(yy*(0.1430488765e-3+float64(yy*(-0.6911147651e-5+float64(yy*(0.7621095161e-6-float64(yy*0.934935152e-7)))))))
	return math.Sqrt(0.636619772/a) * (float64(p*math.Cos(xx)) - float64(z*q*math.Sin(xx)))
}

// J1 is Bessel J1, Numerical Recipes rational approximations.
func (mathExtra) J1(x float64) float64 {
	s := 1.0
	a := x
	if a < 0.0 {
		a = -a
		s = -1.0
	}

	if a <= 8.0 {
		y := a * a
		n := a * horner(y, 72362614232.0, -7895059235.0, 242396853.1, -2972611.439, 15704.48260, -30.16036606)
		d := horner(y, 144725228442.0, 2300535178.0, 18583304.74, 99447.43394, 376.9991397, 1)
		return s * n / d
	}

	z := 8.0 / a
	yy := z * z
	xx := a - 2.356194491
	p := horner(yy, 1.0, 0.183105e-2, -0.3516396496e-4, 0.2457520174e-5, -0.240337019e-6)
	q := horner(yy, 0.04687499995, -0.2002690873e-3, 0.8449199096e-5, -0.88228987e-6, 0.105787412e-6)
	return s * math.Sqrt(0.636619772/a) * (float64(p*math.Cos(xx)) - float64(z*q*math.Sin(xx)))
}

// Y0 is Bessel Y0, Numerical Recipes rational approximations.
func (mathExtra) Y0(x float64) float64 {
	if x < 0 {
		return math.NaN()
	}
	if x == 0 {
		return math.Inf(-1)
	}
	if x < 8.0 {
		y := x * x
		n := horner(y, -2957821389.0, 7062834065.0, -512359803.6, 10879881.29, -86327.92757, 228.4622733)
		d := horner(y, 40076544269.0, 745249964.8, 7189466.438, 47447.26470, 226.1030244, 1)
		return n/d + float64(0.636619772*MathExtra.J0(x)*math.Log(x))
	}

	z := 8.0 / x
	yy := z * z
	xx := x - 0.785398164
	p := horner(yy, 1.0, -0.1098628627e-2, 0.2734510407e-4, -0.2073370639e-5, 0.2093887211e-6)
	q := horner(yy, -0.1562499995e-1, 0.1430488765e-3, -0.6911147651e-5, 0.7621095161e-6, -0.934945152e-7)
	return math.Sqrt(0.636619772/x) * (float64(p*math.Sin(xx)) + float64(z*q*math.Cos(xx)))
}

// Y1 is Bessel Y1, Numerical Recipes rational approximations.
func (mathExtra) Y1(x float64) float64 {
	if x < 0 {
		return math.NaN()
	}
	if x == 0 {
		return math.Inf(-1)
	}
	if x < 8.0 {
		y := x * x
		n := x * horner(y, -0.4900604943e13, 0.1275274390e13, -0.5153438139e11, 0.7349264551e9, -0.4237922726e7, 0.8511937935e4)
		d := horner(y, 0.2499580570e14, 0.4244419664e12, 0.3733650367e10, 0.2245904002e8, 0.1020426050e6, 0.3549632885e3, 1)
		return n/d + float64(0.636619772*(float64(MathExtra.J1(x)*math.Log(x))-1.0/x))
	}

	z := 8.0 / x
	yy := z * z
	xx := x - 2.356194491
	p := horner(yy, 1.0, 0.183105e-2, -0.3516396496e-4, 0.2457520174e-5, -0.240337019e-6)
	q := horner(yy, 0.04687499995, -0.2002690873e-3, 0.8449199096e-5, -0.88228987e-6, 0.105787412e-6)
	return math.Sqrt(0.636619772/x) * (float64(p*math.Sin(xx)) + float64(z*q*math.Cos(xx)))
}

// Jn is Bessel J of integer order trunc(n): upward recurrence when |x| > n, else Miller's downward
// recurrence (Numerical Recipes bessj).
func (mathExtra) Jn(order, x float64) float64 {
	n, valid := besselOrder(order, x)
	if !valid {
		return math.NaN()
	}
	if n < 0 {
		if n%2 != 0 {
			return -MathExtra.Jn(float64(-n), x)
		}
		return MathExtra.Jn(float64(-n), x)
	}
	if n == 0 {
		return MathExtra.J0(x)
	}
	if n == 1 {
		return MathExtra.J1(x)
	}
	if x == 0 {
		return 0
	}
	a := math.Abs(x)
	t := 2.0 / a
	var r float64
	if a > float64(n) {
		p := MathExtra.J0(a)
		r = MathExtra.J1(a)
		for k := int64(1); k < n; k++ {
			s := float64(float64(k)*t*r) - p
			p = r
			r = s
		}
	} else {
		m := 2 * ((n + int64(math.Floor(math.Sqrt(160.0*float64(n))))) / 2)
		even := false
		p, sum, b := 0.0, 0.0, 1.0
		r = 0.0
		for k := m; k > 0; k-- {
			s := float64(float64(k)*t*b) - p
			p = b
			b = s
			if math.Abs(b) > 1e10 {
				b = b * 1e-10
				p = p * 1e-10
				r = r * 1e-10
				sum = sum * 1e-10
			}
			if even {
				sum = sum + b
			}
			even = !even
			if k == n {
				r = p
			}
		}
		r = r / (float64(2.0*sum) - b)
	}

	if x < 0 && n%2 != 0 {
		return -r
	}
	return r
}

// Yn is Bessel Y of integer order trunc(n), by upward recurrence from Y0 and Y1 (Numerical Recipes bessy).
func (mathExtra) Yn(order, x float64) float64 {
	n, valid := besselOrder(order, x)
	if !valid {
		return math.NaN()
	}
	if n < 0 {
		if n%2 != 0 {
			return -MathExtra.Yn(float64(-n), x)
		}
		return MathExtra.Yn(float64(-n), x)
	}
	if n == 0 {
		return MathExtra.Y0(x)
	}
	if n == 1 {
		return MathExtra.Y1(x)
	}
	if x < 0 {
		return math.NaN()
	}
	if x == 0 {
		return math.Inf(-1)
	}
	t := 2.0 / x
	p := MathExtra.Y0(x)
	r := MathExtra.Y1(x)
	for k := int64(1); k < n; k++ {
		s := float64(float64(k)*t*r) - p
		p = r
		r = s
	}
	return r
}

// besselOrder gives the order of jn and yn, trunc(order); false (a NaN result) for a NaN argument
// or |order| >= 2^31.
func besselOrder(order, x float64) (int64, bool) {
	if math.IsNaN(order) || math.IsNaN(x) || !(math.Abs(order) < 2147483648.0) {
		return 0, false
	}
	return int64(math.Trunc(order)), true
}

// exponent gives the unbiased exponent of a finite non-zero x, from its bits; a subnormal is
// scaled up first.
func exponent(x float64) int {
	bits := math.Float64bits(x)
	biased := int((bits >> 52) & 0x7FF)
	if biased != 0 {
		return biased - 1023
	}
	bits = math.Float64bits(x * powerOfTwo(54))
	return int((bits>>52)&0x7FF) - 1023 - 54
}

// powerOfTwo gives 2^n for -1022 <= n <= 1023, built from its bits.
func powerOfTwo(n int) float64 { return math.Float64frombits(uint64(n+1023) << 52) }
