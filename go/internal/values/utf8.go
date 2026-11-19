package values

import "strings"

type utf8Codec struct{}

// Utf8 is UTF-8 encoding and decoding as plain loops over code points and bytes, for the formats
// that work on bytes (§9.9: @uri, @urid, @base64, @base64d) and for sanitizing host text. Go's
// own decoders give one U+FFFD per byte (utf8.DecodeRune) or per run (strings.ToValidUTF8), not
// one per maximal subpart as §4.1 requires.
var Utf8 utf8Codec

// Encode appends the UTF-8 bytes of a well-formed string.
func (utf8Codec) Encode(text string, bytes []byte) []byte { return append(bytes, text...) }

// EncodeRune appends the UTF-8 bytes of a Unicode scalar value.
func (utf8Codec) EncodeRune(codePoint int, bytes []byte) []byte {
	switch {
	case codePoint < 0x80:
		return append(bytes, byte(codePoint))
	case codePoint < 0x800:
		return append(bytes, byte(0xC0|(codePoint>>6)), byte(0x80|(codePoint&0x3F)))
	case codePoint < 0x10000:
		return append(bytes, byte(0xE0|(codePoint>>12)), byte(0x80|((codePoint>>6)&0x3F)), byte(0x80|(codePoint&0x3F)))
	default:
		return append(bytes, byte(0xF0|(codePoint>>18)), byte(0x80|((codePoint>>12)&0x3F)),
			byte(0x80|((codePoint>>6)&0x3F)), byte(0x80|(codePoint&0x3F)))
	}
}

// TryDecode gives the text of well-formed UTF-8 bytes; false when any sequence is ill-formed.
func (utf8Codec) TryDecode(bytes []byte) (string, bool) {
	for i := 0; i < len(bytes); {
		var codePoint int
		i, codePoint = utf8Next(bytes, i)
		if codePoint < 0 {
			return "", false
		}
	}
	return string(bytes), true
}

// DecodeReplacing gives the text of UTF-8 bytes, each ill-formed sequence (as utf8Next delimits
// it) replaced by U+FFFD.
func (utf8Codec) DecodeReplacing(bytes []byte) string {
	var builder strings.Builder
	builder.Grow(len(bytes))
	for i := 0; i < len(bytes); {
		start := i
		var codePoint int
		i, codePoint = utf8Next(bytes, i)
		if codePoint < 0 {
			builder.WriteRune(Replacement)
		} else {
			builder.Write(bytes[start:i])
		}
	}
	return builder.String()
}

// utf8Next decodes the sequence at index and gives the index after it; codePoint is -1 when the
// sequence is ill-formed, and the index is then the end of its maximal ill-formed subpart (Unicode
// §3.9, §4.1 of the spec): the bytes up to the first one that cannot continue a well-formed
// sequence, at least one byte. The second byte's range depends on the first (E0: A0-BF, ED: 80-9F,
// F0: 90-BF, F4: 80-8F), which rules out overlong forms, surrogates and code points above U+10FFFF.
func utf8Next(bytes []byte, index int) (next, codePoint int) {
	first := int(bytes[index])
	if first < 0x80 {
		return index + 1, first
	}

	length := 0
	switch {
	case first >= 0xC2 && first <= 0xDF:
		length = 2
	case first >= 0xE0 && first <= 0xEF:
		length = 3
	case first >= 0xF0 && first <= 0xF4:
		length = 4
	}
	if length == 0 {
		return index + 1, -1
	}

	low, high := 0x80, 0xBF
	switch first {
	case 0xE0:
		low = 0xA0
	case 0xF0:
		low = 0x90
	case 0xED:
		high = 0x9F
	case 0xF4:
		high = 0x8F
	}
	value := first & (0x7F >> length)
	for i := 1; i < length; i++ {
		if index+i == len(bytes) {
			return index + i, -1
		}
		next := int(bytes[index+i])
		if next < low || next > high {
			return index + i, -1
		}
		value = (value << 6) | (next & 0x3F)
		low, high = 0x80, 0xBF
	}
	return index + length, value
}
