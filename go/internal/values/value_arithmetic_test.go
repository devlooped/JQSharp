package values

import (
	"math"
	"slices"
	"testing"
)

// apply gives the result as JSON text, or "error: <message>".
func apply(t *testing.T, op, a, b string) string {
	x, y := V(t, a), V(t, b)
	switch op {
	case "+":
		return resultText(ValueOps.TryAdd(x, y))
	case "-":
		return resultText(ValueOps.TrySubtract(x, y))
	case "*":
		return resultText(ValueOps.TryMultiply(x, y))
	case "/":
		return resultText(ValueOps.TryDivide(x, y))
	default:
		return resultText(ValueOps.TryModulo(x, y))
	}
}

func checkOperator(t *testing.T, op string, cases [][3]string) {
	for _, c := range cases {
		equal(t, c[2], apply(t, op, c[0], c[1]), c[0]+" "+op+" "+c[1])
	}
}

func TestAdd(t *testing.T) {
	checkOperator(t, "+", [][3]string{
		{"null", "1", "1"},
		{"1", "null", "1"},
		{"null", "null", "null"},
		{"null", `{"a":1}`, `{"a":1}`},
		{"1", "2", "3"},
		{"0.1", "0.2", "0.30000000000000004"},
		{"1e308", "1e308", "1.7976931348623157e+308"},
		{`"a"`, `"b"`, `"ab"`},
		{"[1]", "[2,[3]]", "[1,2,[3]]"},
		{"[]", "[]", "[]"},
		{`{"a":1,"b":2}`, `{"c":3,"a":4}`, `{"a":4,"b":2,"c":3}`},
		{`{"b":1}`, `{"a":2}`, `{"b":1,"a":2}`},
		{"1", `"a"`, `error: number (1) and string ("a") cannot be added`},
		{"{}", "[]", "error: object ({}) and array ([]) cannot be added"},
		{"true", "true", "error: boolean (true) and boolean (true) cannot be added"},
		{"false", "1", "error: boolean (false) and number (1) cannot be added"},
		{`{"a":"very long string"}`, "1", `error: object ({"a":"very long string"}) and number (1) cannot be added`},
		{"[1,2,3,4,5,6,7,8,9,10,11,12,13]", "1", "error: array ([1,2,3,4,5,6,7,8,9,10,11,...]) and number (1) cannot be added"},
	})
}

func TestSubtract(t *testing.T) {
	checkOperator(t, "-", [][3]string{
		{"5", "3", "2"},
		{"[1,2,3,1,[1]]", "[1,[1]]", "[2,3]"},
		{"[1,2]", "[]", "[1,2]"},
		{`[{"a":1,"b":2}]`, `[{"b":2,"a":1}]`, "[]"},
		{`"a"`, `"a"`, `error: string ("a") and string ("a") cannot be subtracted`},
		{"null", "1", "error: null (null) and number (1) cannot be subtracted"},
		{"{}", "{}", "error: object ({}) and object ({}) cannot be subtracted"},
	})
}

func TestMultiply(t *testing.T) {
	checkOperator(t, "*", [][3]string{
		{"2", "3", "6"},
		{`"ab"`, "3", `"ababab"`},
		{"3", `"ab"`, `"ababab"`},
		{`"ab"`, "1", `"ab"`},
		{`"ab"`, "1.9", `"ab"`},
		{`"ab"`, "1.5", `"ab"`},
		{`"x"`, "0", `""`},
		{`"ab"`, "0.5", `""`},
		{`"ab"`, "-0", `""`},
		{"0", `"ab"`, `""`},
		{`"ab"`, "-0.5", "null"},
		{`"ab"`, "-1", "null"},
		{`"ab"`, "nan", "null"},
		{`""`, "5", `""`},
		{`""`, "1e300", `""`},
		{`"é"`, "1.5e9", "error: Repeat string result too long"},
		// The reference has "ab" * 1e9, past .NET's string length limit; Go strings reach the
		// spec's limit of 2^31 - 1 bytes, so the case goes past that.
		{`"ab"`, "1.1e9", "error: Repeat string result too long"},
		{`{"a":{"b":1},"c":1}`, `{"a":{"d":2},"c":{"x":1}}`, `{"a":{"b":1,"d":2},"c":{"x":1}}`},
		{`{"a":{"b":{"c":1}}}`, `{"a":{"b":{"d":2}},"e":3}`, `{"a":{"b":{"c":1,"d":2}},"e":3}`},
		{`{"a":{"b":1}}`, `{"a":1}`, `{"a":1}`},
		{"[1]", "2", "error: array ([1]) and number (2) cannot be multiplied"},
		{`"a"`, `"b"`, `error: string ("a") and string ("b") cannot be multiplied`},
		{"null", "2", "error: null (null) and number (2) cannot be multiplied"},
	})
}

func TestDivide(t *testing.T) {
	checkOperator(t, "/", [][3]string{
		{"6", "4", "1.5"},
		{"-1", "3", "-0.3333333333333333"},
		{"1", "0", "error: number (1) and number (0) cannot be divided because the divisor is zero"},
		{"0", "-0", "error: number (0) and number (-0) cannot be divided because the divisor is zero"},
		{`"a,b,c"`, `","`, `["a","b","c"]`},
		{`"a,b,"`, `","`, `["a","b",""]`},
		{`"a, b"`, `", "`, `["a","b"]`},
		{`"abc"`, `"x"`, `["abc"]`},
		{`""`, `","`, "[]"},
		{`"aé😀"`, `""`, `["a","é","😀"]`},
		{"[]", "1", "error: array ([]) and number (1) cannot be divided"},
		{`"a"`, "1", `error: string ("a") and number (1) cannot be divided`},
	})
}

func TestModulo(t *testing.T) {
	checkOperator(t, "%", [][3]string{
		{"5", "3", "2"},
		{"-5", "3", "-2"},
		{"5", "-3", "2"},
		{"5.9", "3.9", "2"},
		{"5", "-1", "0"},
		{"-9223372036854775808", "-1", "0"},
		{"1e300", "7", "0"},
		{"nan", "1", "null"},
		{"1", "nan", "null"},
		{"1e300", "-1e300", "9223372036854776000"},
		{"-1e300", "1e300", "-1"},
		{"1", "0", "error: number (1) and number (0) cannot be divided (remainder) because the divisor is zero"},
		{"5", "0.5", "error: number (5) and number (0.5) cannot be divided (remainder) because the divisor is zero"},
		{`"a"`, "1", `error: string ("a") and number (1) cannot be divided (remainder)`},
		{"1", `"a"`, `error: number (1) and string ("a") cannot be divided (remainder)`},
	})
}

func TestRemainderOfInfinities(t *testing.T) {
	// [(infinite, -infinite) % (1, -1, infinite)] is [0,0,0,0,0,-1] (spec §4.4).
	var results []string
	for _, a := range []float64{math.Inf(1), math.Inf(-1)} {
		for _, b := range []float64{1, -1, math.Inf(1)} {
			result, _, ok := ValueOps.TryModulo(N(a), N(b))
			equal(t, true, ok)
			results = append(results, J(result))
		}
	}
	equal(t, true, slices.Equal([]string{"0", "0", "0", "0", "0", "-1"}, results), results)
}

func TestNegation(t *testing.T) {
	for _, c := range [][2]string{
		{"1", "-1"},
		{"0", "-0"},
		{"-0", "0"},
		{"nan", "null"},
		{"null", "error: null (null) cannot be negated"},
		{`"a"`, `error: string ("a") cannot be negated`},
		{`"very-long-long-long-long-string"`, `error: string ("very-long-long-long-long...") cannot be negated`},
	} {
		equal(t, c[1], resultText(ValueOps.TryNegate(V(t, c[0]))), c[0])
	}
}

func TestOperandsNeverChange(t *testing.T) {
	a := V(t, `{"a":1,"b":{"c":2}}`)
	b := V(t, `{"b":{"d":3},"e":4}`)
	_, _, ok := ValueOps.TryAdd(a, b)
	equal(t, true, ok)
	_, _, ok = ValueOps.TryMultiply(a, b)
	equal(t, true, ok)
	equal(t, `{"a":1,"b":{"c":2}}`, J(a))
	equal(t, `{"b":{"d":3},"e":4}`, J(b))
}

func TestDeepMergeOfTenThousandLevelsDoesNotRecurse(t *testing.T) {
	// {"a":{"a":…{"x":1}}} * {"a":{"a":…{"y":2}}}, 10,000 levels.
	nest := func(key string, depth int) Value {
		value := FromObject(EmptyObject.Set(key, N(float64(depth))))
		for i := 0; i < depth; i++ {
			value = FromObject(EmptyObject.Set("a", value))
		}
		return value
	}

	result, _, ok := ValueOps.TryMultiply(nest("x", 10_000), nest("y", 10_000))
	equal(t, true, ok)
	for i := 0; i < 10_000; i++ {
		result = result.AsObject().ValueAt(0)
	}
	equal(t, `{"x":10000,"y":10000}`, J(result))
}
