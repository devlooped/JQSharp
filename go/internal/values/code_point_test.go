package values

import (
	"testing"
	"unsafe"
)

func TestLengthCountsCodePoints(t *testing.T) {
	for _, c := range []struct {
		text   string
		length int
	}{{"", 0}, {"abc", 3}, {"aé😀b", 4}, {"😀😀", 2}} {
		equal(t, c.length, CodePoints.Length(c.text), c.text)
	}
}

func TestSlices(t *testing.T) {
	for _, c := range [][4]string{
		{"\"aé😀b\"", "2", "3", "\"😀\""},
		{"\"aé😀b\"", "-2", "null", "\"😀b\""},
		{"\"aé😀b\"", "null", "2", "\"aé\""},
		{"\"aé😀b\"", "1.5", "2.5", "\"é😀\""},
		{"\"aé😀b\"", "3", "1", "\"\""},
		{"\"aé😀b\"", "-100", "100", "\"aé😀b\""},
		{"\"aé😀b\"", "nan", "nan", "\"aé😀b\""},
		{"[1,2,3,4]", "1", "3", "[2,3]"},
		{"[1,2,3,4]", "-1", "null", "[4]"},
		{"[1,2,3,4]", "null", "-1", "[1,2,3]"},
		{"[1,2,3,4]", "0.2", "0.2", "[1]"},
		{"[1,2,3,4]", "5", "10", "[]"},
		{"null", "1", "2", "null"},
		{"[0,1,2,3,4,5,6,7,8,9]", "1.2", "3.5", "[1,2,3]"},
		{"\"abcdefghi\"", "-2", "null", "\"hi\""},
		{"null", "\"a\"", "{}", "null"},
		{"1", "1", "2", "error: Cannot index number with object ({\"start\":1,\"end\":2})"},
		{"1", "\"a\"", "2", "error: Cannot index number with object ({\"start\":\"a\",\"end\":2})"},
		{"{}", "1", "null", "error: Cannot index object with object ({\"start\":1,\"end\":null})"},
		{"[1]", "\"a\"", "null", "error: Array/string slice indices must be integers"},
		{"\"ab\"", "0", "true", "error: Array/string slice indices must be integers"},
	} {
		equal(t, c[3], resultText(ValueOps.TrySlice(V(t, c[0]), V(t, c[1]), V(t, c[2]))), c)
	}
}

// jq 1.8.2: .[{"start":…,"end":…}].
func TestSlicesByObjectKey(t *testing.T) {
	for _, c := range [][3]string{
		{"[1,2,3]", "{\"start\":1,\"end\":null}", "[2,3]"},
		{"[1,2,3]", "{\"start\":1,\"end\":2,\"x\":0}", "[2]"},
		{"\"abc\"", "{\"start\":null,\"end\":null}", "\"abc\""},
		{"\"abc\"", "{\"start\":1}", "error: Array/string slice indices must be integers"},
		{"[1,2,3]", "{\"end\":2}", "error: Array/string slice indices must be integers"},
		{"[1,2,3]", "{\"start\":\"a\",\"end\":2}", "error: Array/string slice indices must be integers"},
		{"null", "{\"start\":1}", "null"},
		{"null", "{\"a\":1}", "null"},
		{"1", "{\"start\":1}", "error: Cannot index number with object ({\"start\":1})"},
		{"{\"a\":1}", "{\"start\":1}", "error: Cannot index object with object ({\"start\":1})"},
	} {
		equal(t, c[2], resultText(ValueOps.TryIndexSlice(V(t, c[0]), V(t, c[1]))), c)
	}
}

func TestExplode(t *testing.T) {
	for _, c := range [][2]string{
		{"\"aé😀b\"", "[97,233,128512,98]"},
		{"\"\"", "[]"},
		{"1", "error: explode input must be a string"},
		{"[\"a\"]", "error: explode input must be a string"},
	} {
		equal(t, c[1], resultText(ValueOps.TryExplode(V(t, c[0]))), c)
	}
}

func TestImplode(t *testing.T) {
	for _, c := range [][2]string{
		{"[97,233,128512,98]", "\"aé😀b\""},
		{"[]", "\"\""},
		{"[65.9]", "\"A\""},
		{"[1114111]", "\"\U0010FFFF\""},
		// jq 1.8.2: [-0.5, 97, 1114112, 55296, -1, 97.9] | implode | explode is [0,97,65533,65533,65533,97].
		{"[-0.5,97,1114112,55296,-1,97.9]", "\"\\u0000a\uFFFD\uFFFD\uFFFDa\""},
		{"[57343,57344,1e300,-1e300]", "\"\uFFFD\uE000\uFFFD\uFFFD\""},
		{"[\"a\"]", "error: string (\"a\") can't be imploded, unicode codepoint needs to be numeric"},
		{"[1,[1]]", "error: array ([1]) can't be imploded, unicode codepoint needs to be numeric"},
		{"[nan]", "error: number (null) can't be imploded, unicode codepoint needs to be numeric"},
		{"[null]", "error: null (null) can't be imploded, unicode codepoint needs to be numeric"},
		{"\"a\"", "error: implode input must be an array"},
		{"{}", "error: implode input must be an array"},
	} {
		equal(t, c[1], resultText(ValueOps.TryImplode(V(t, c[0]))), c)
	}
}

// Go strings are UTF-8: the reference's lone surrogates are ill-formed UTF-8 here, and each
// maximal ill-formed subpart becomes one U+FFFD (§4.1).
func TestIllFormedUtf8BecomesReplacementCharacters(t *testing.T) {
	for _, c := range [][2]string{
		{"a\xED\xA0\xBDb", "a\uFFFD\uFFFD\uFFFDb"}, // an encoded surrogate: ED is valid, A0 is not
		{"\x80", "\uFFFD"},
		{"\xE2\x82", "\uFFFD"},
		{"\xF0\x9F\x98\x80\xF0\x9F", "😀\uFFFD"},
		{"\xC0\xAF", "\uFFFD\uFFFD"},
		{"\xF4\x90\x80\x80", "\uFFFD\uFFFD\uFFFD\uFFFD"},
		{"\xE1\x80\xE2\xF0\x91\x92\xF1\xBF\x41", "\uFFFD\uFFFD\uFFFD\uFFFDA"},
	} {
		equal(t, c[1], FromString(c[0]).AsString(), []byte(c[0]))
	}
}

func TestWellFormedStringsAreNotCopied(t *testing.T) {
	text := "aé😀b"
	equal(t, unsafe.StringData(text), unsafe.StringData(FromString(text).AsString()))
}
