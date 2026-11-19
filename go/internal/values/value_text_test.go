package values

import (
	"math"
	"math/rand"
	"strings"
	"testing"
)

func TestNumberText(t *testing.T) {
	for _, c := range []struct {
		number   float64
		expected string
	}{
		{0, "0"},
		{1, "1"},
		{-1, "-1"},
		{1.5, "1.5"},
		{-1.5, "-1.5"},
		{0.5, "0.5"},
		{100, "100"},
		{1e15, "1000000000000000"},
		{1e20, "100000000000000000000"},
		{123456789012345680000, "123456789012345680000"},
		{1e21, "1e+21"},
		{-1e21, "-1e+21"},
		{1.5e300, "1.5e+300"},
		{1e-6, "0.000001"},
		{1.5e-6, "0.0000015"},
		{1e-7, "1e-7"},
		{1.23e-18, "1.23e-18"},
		{0.30000000000000004, "0.30000000000000004"},
		{5e-324, "5e-324"},
		{1.7976931348623157e308, "1.7976931348623157e+308"},
		{9007199254740993, "9007199254740992"},
		{123.456, "123.456"},
		{math.NaN(), "null"},
		{math.Inf(1), "1.7976931348623157e+308"},
		{math.Inf(-1), "-1.7976931348623157e+308"},
		{math.Copysign(0, -1), "-0"},
	} {
		equal(t, c.expected, ValueText.FormatNumber(c.number), c.number)
	}
}

func TestNumberTextRoundTrips(t *testing.T) {
	random := rand.New(rand.NewSource(42))
	for i := 0; i < 10000; i++ {
		number := math.Float64frombits(random.Uint64())
		if math.IsNaN(number) || math.IsInf(number, 0) {
			continue
		}
		text := ValueText.FormatNumber(number)
		back, ok := ValueText.TryParseNumber(text)
		equal(t, true, ok, text)
		equal(t, number, back, text)
	}
}

func TestStringEscaping(t *testing.T) {
	for _, c := range [][2]string{
		{"é<", `"é<"`},
		{`a"b\c/d`, `"a\"b\\c/d"`},
		{"\b\f\n\r\t", `"\b\f\n\r\t"`},
		{"\u0000\u0001\u001f", `"\u0000\u0001\u001f"`},
		{"\u007f\u0080", "\"\\u007f\u0080\""},
		{"😀", `"😀"`},
		{"\u2028", "\"\u2028\""},
	} {
		equal(t, c[1], J(FromString(c[0])), c[0])
	}
}

func TestReaderRoundTrips(t *testing.T) {
	for _, json := range []string{
		"null", "true", "false", "[]", "{}", `""`,
		`[1,2.5,"x",null,true,false,{},[[]]]`,
		`{"b":1,"a":[1,{"z":null,"y":"é"}],"c":{"d":[]}}`,
		"-0",
	} {
		equal(t, json, J(V(t, json)))
	}
}

func TestReaderValues(t *testing.T) {
	for _, c := range [][2]string{
		{" \t\r\n[ 1 , { \"a\" : 2 } ] \n", `[1,{"a":2}]`},
		{"\uFEFF1", "1"},
		{"1.0", "1"},
		{"1E2", "100"},
		{"1e-2", "0.01"},
		{"1.", "1"},
		{".5", "0.5"},
		{"+1", "1"},
		{"007", "7"},
		{"1e1000", "1.7976931348623157e+308"},
		{"-1e1000", "-1.7976931348623157e+308"},
		{"1e-1000", "0"},
		{"nan", "null"},
		{"NaN", "null"},
		{"[-nan]", "[null]"},
		{"Infinity", "1.7976931348623157e+308"},
		{"-Infinity", "-1.7976931348623157e+308"},
		{`"\u00e9\/\""`, `"é/\""`},
		{`"\ud83d\ude00"`, `"😀"`},
		{`"\ud800"`, "\"\uFFFD\""},
		{`"\udc00x"`, "\"\uFFFDx\""},
		{`"\ud800\ud800"`, "\"\uFFFD\uFFFD\""},
		{`"\ud800\u0041"`, "\"\uFFFDA\""},
		{"[nAn,SNaN,-snan,+inf,-INF,iNfInItY]", "[null,null,null,1.7976931348623157e+308,-1.7976931348623157e+308,1.7976931348623157e+308]"},
		{"[1,nan]", "[1,null]"},
		{`{"a":1,"b":2,"a":3}`, `{"a":3,"b":2}`},
		{"\"a\u007Fb\"", `"a\u007fb"`},
		{`{"a":1,"a":2}`, `{"a":2}`},
		{`{"\ud800":1}`, "{\"\uFFFD\":1}"},
		// Raw ill-formed UTF-8 in the text (the reference reads UTF-16 text and has none).
		{"\"a\xFFb\"", "\"a\uFFFDb\""},
		{"{\"\xC0\":1}", "{\"\uFFFD\":1}"},
	} {
		equal(t, c[1], J(V(t, c[0])), c[0])
	}
}

// The reason (implementation-defined, §5.3); every message ends with " (while parsing '<input>')".
func TestReaderFailures(t *testing.T) {
	for _, c := range [][2]string{
		{"", "Expected JSON value"},
		{"   ", "Expected JSON value"},
		{"1 2", "Unexpected extra JSON values"},
		{"{", "Unfinished JSON term at EOF at line 1, column 1"},
		{"[1,2", "Unfinished JSON term at EOF at line 1, column 4"},
		{`"abc`, "Unfinished JSON term at EOF at line 1, column 4"},
		{"[1 2]", "Expected separator between values at line 1, column 4"},
		{"[1,\n2 3]", "Expected separator between values at line 2, column 3"},
		{"tru", "Invalid literal at line 1, column 3"},
		{"nul", "Invalid literal at line 1, column 3"},
		{"Null", "Invalid literal at line 1, column 4"},
		{"NaN1", "Invalid literal at line 1, column 4"},
		{"nanx", "Invalid literal at line 1, column 4"},
		{"[1x]", "Invalid numeric literal at line 1, column 3"},
		{"1e", "Invalid numeric literal at line 1, column 2"},
		{"'a'", "Invalid numeric literal at line 1, column 3"},
		{`"\q"`, "Invalid escape at line 1, column 3"},
		{`"\u12G4"`, "Invalid \\uXXXX escape at line 1, column 6"},
		{"\"a\nb\"", "Invalid string: control characters from U+0000 through U+001F must be escaped at line 2, column 0"},
		{"\"a\x01\"", "Invalid string: control characters from U+0000 through U+001F must be escaped at line 1, column 3"},
		{"[\"\t\"]", "Invalid string: control characters from U+0000 through U+001F must be escaped at line 1, column 3"},
		{"{1:2}", "Object keys must be strings at line 1, column 2"},
		{`{"a" 1}`, "Objects must consist of key:value pairs at line 1, column 6"},
		{"[1,]", "Expected another array element at line 1, column 4"},
		{"]", "Unmatched ']' at line 1, column 1"},
		{"[1}", "Unmatched '}' at line 1, column 3"},
		{",", "Expected value before ',' at line 1, column 1"},
	} {
		_, err, ok := ValueText.TryParse(c[0])
		equal(t, false, ok, c[0])
		if !ok {
			equal(t, c[1]+" (while parsing '"+c[0]+"')", err.AsString())
		}
	}
}

func TestReaderDepthLimit(t *testing.T) {
	ok := strings.Repeat("[", 10000) + strings.Repeat("]", 10000)
	deep, _, success := ValueText.TryParse(ok)
	equal(t, true, success)
	equal(t, ok, J(deep))

	_, err, success := ValueText.TryParse(strings.Repeat("[", 10001))
	equal(t, false, success)
	equal(t, true, strings.HasPrefix(err.AsString(), "Exceeds depth limit for parsing"))
}

// nest gives arrays nested levels deep around inner.
func nest(levels int, inner Value) Value {
	value := inner
	for i := 0; i < levels; i++ {
		value = FromArray(ArrayOf(value))
	}
	return value
}

func TestWriterSkipsValuesDeeperThan10001Levels(t *testing.T) {
	const skipped = "<skipped: too deep>"
	open := func(n int) string { return strings.Repeat("[", n) }
	shut := func(n int) string { return strings.Repeat("]", n) }
	// jq 1.8.2: reduce range(n) as $i (x; [.]) | tojson.
	equal(t, open(10000)+"1"+shut(10000), J(nest(10000, N(1))))
	equal(t, open(10001)+skipped+shut(10001), J(nest(10001, N(1))))
	equal(t, open(10001)+shut(10001), J(nest(10000, FromArray(EmptyArray))))
	equal(t, open(10001)+skipped+shut(10001), J(nest(10001, FromArray(EmptyArray))))
	equal(t, open(10001)+skipped+","+skipped+shut(10001), J(nest(10000, V(t, "[1,[2]]"))))
}

func TestNumberReading(t *testing.T) {
	for _, c := range []struct {
		text     string
		expected float64
	}{
		{"12", 12}, {"-1.5e3", -1500}, {"0.1", 0.1}, {".5", 0.5}, {"5.", 5}, {"+3", 3},
		{"inf", math.Inf(1)}, {"-INFINITY", math.Inf(-1)},
	} {
		number, ok := ValueText.TryParseNumber(c.text)
		equal(t, true, ok, c.text)
		equal(t, c.expected, number, c.text)
	}
}

func TestNumberReadingFailures(t *testing.T) {
	for _, text := range []string{"", " 1", "1 ", "0x10", ".", "-", "1e+", "1,5", "abc", "1_0", "0x1p3"} {
		_, ok := ValueText.TryParseNumber(text)
		equal(t, false, ok, text)
	}
}

func TestNanWordsReadAsNan(t *testing.T) {
	for _, text := range []string{"nan", "NaN", "-nan", "+NAN", "snan", "-sNaN"} {
		number, ok := ValueText.TryParseNumber(text)
		equal(t, true, ok && math.IsNaN(number), text)
	}
}

func TestErrorDump(t *testing.T) {
	for _, c := range [][2]string{
		{"[1,2]", "[1,2]"},
		{"[1,2,3,4,5,6,7,8,9,10,11,12,13]", "[1,2,3,4,5,6,7,8,9,10,11,...]"},
		{`{"aaaaaaaaaaaaaaaaaaaaaaaaaa":1}`, `{"aaaaaaaaaaaaaaaaaaaaaaa...}`},
		// A cut value ends with "..." and its own closing delimiter, not those of what it contains.
		{`"very-long-long-long-long-string"`, `"very-long-long-long-long..."`},
		{`["very-long-long-long-long-string"]`, `["very-long-long-long-lon...]`},
		// 27 characters: 29 bytes with the quotes, not cut; one more is cut.
		{`"abcdefghijklmnopqrstuvwxyz0"`, `"abcdefghijklmnopqrstuvwxyz0"`},
		{`"abcdefghijklmnopqrstuvwxyz01"`, `"abcdefghijklmnopqrstuvwx..."`},
		// Two-byte é: the quote and 12 of them make 25 bytes.
		{`"ééééééééééééééééééé"`, `"éééééééééééé..."`},
		{`"a😀😀😀😀😀😀😀"`, `"a😀😀😀😀😀..."`},
	} {
		dump := Errors.Dump(V(t, c[0]))
		equal(t, c[1], dump)
		equal(t, true, len(dump) <= 29, dump)
	}
}
