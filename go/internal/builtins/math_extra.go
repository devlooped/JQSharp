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

// Erf is Abramowitz & Stegun 7.1.26.
func (mathExtra) Erf(x float64) float64 {
	if math.IsNaN(x) {
		return x
	}
	s := 0.0
	if x > 0 {
		s = 1
	} else if x < 0 {
		s = -1
	}
	a := math.Abs(x)
	t := 1.0 / (1.0 + float64(0.3275911*a))
	poly := float64(float64(float64(float64(float64(1.061405429*t)+-1.453152027)*t)+1.421413741)*t) + -0.284496736
	poly = float64(poly*t) + 0.254829592
	y := 1.0 - float64(float64(poly*t)*math.Exp(-a*a))
	return s * y
}

// Erfc is 1 − erf(x).
func (mathExtra) Erfc(x float64) float64 { return 1.0 - MathExtra.Erf(x) }

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
