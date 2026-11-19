package values

import (
	"strings"
	"unicode/utf8"
)

type codePoints struct{}

// CodePoints works on strings by Unicode code point. jqlite strings are sequences of code points:
// length, slices, ordering, explode and implode count code points, never bytes. A Go string holds
// them as UTF-8, whose byte order is code point order; every string of a Value is well-formed
// UTF-8 (see Sanitize).
var CodePoints codePoints

const (
	// MaxCodePoint is the largest code point.
	MaxCodePoint = 0x10FFFF
	// Replacement is U+FFFD.
	Replacement = 0xFFFD
)

// Sanitize replaces each ill-formed UTF-8 sequence (each maximal subpart, §4.1) with U+FFFD;
// returns text itself when it is well-formed.
func (codePoints) Sanitize(text string) string {
	if utf8.ValidString(text) {
		return text
	}
	return Utf8.DecodeReplacing([]byte(text))
}

// Length gives the number of code points of a well-formed string.
func (codePoints) Length(text string) int { return utf8.RuneCountInString(text) }

// Substring gives the code points start (included) to end (excluded);
// 0 <= start <= end <= length.
func (codePoints) Substring(text string, start, end int) string {
	from := CodePoints.Advance(text, 0, start)
	to := CodePoints.Advance(text, from, end-start)
	return text[from:to]
}

// Advance gives the byte offset count code points after the byte offset offset.
func (codePoints) Advance(text string, offset, count int) int {
	for i := 0; i < count; i++ {
		if text[offset] < utf8.RuneSelf {
			offset++
		} else {
			_, size := utf8.DecodeRuneInString(text[offset:])
			offset += size
		}
	}
	return offset
}

// Compare orders two well-formed strings by code point (the order of their UTF-8 bytes); gives
// -1, 0 or 1.
func (codePoints) Compare(a, b string) int { return strings.Compare(a, b) }

// Explode gives the code points of a well-formed string, as numbers.
func (codePoints) Explode(text string) *Array {
	items := make([]Value, 0, len(text))
	for _, c := range text {
		items = append(items, FromNumber(float64(c)))
	}
	return ArrayFromOwned(items)
}

// IsScalar tells whether codePoint is a Unicode scalar value (a code point that is not a surrogate).
func (codePoints) IsScalar(codePoint int) bool {
	return codePoint >= 0 && codePoint <= MaxCodePoint && (codePoint < 0xD800 || codePoint > 0xDFFF)
}

// Hex gives a non-negative number in upper-case hexadecimal, padded with zeros to width digits.
func (codePoints) Hex(n, width int) string {
	const digits = "0123456789ABCDEF"
	var text [16]byte
	i := len(text)
	for n > 0 || len(text)-i < width {
		i--
		text[i] = digits[n%16]
		n /= 16
	}
	return string(text[i:])
}

// Decimal gives an integer in decimal.
func (codePoints) Decimal(n int) string { return ValueText.FormatNumber(float64(n)) }

// Append appends a Unicode scalar value.
func (codePoints) Append(builder *strings.Builder, codePoint int) {
	builder.WriteRune(rune(codePoint))
}
