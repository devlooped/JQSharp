package builtins

import (
	"strings"
	"unicode/utf8"

	"github.com/devlooped/jqlite/go/internal/values"
)

type stringNatives struct{}

// StringNatives are §9.6: text conversion and the string natives the prelude builds on.
var StringNatives stringNatives

func (stringNatives) Register() {
	add("tostring", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) {
		return ok(values.FromString(ToText(input)))
	})
	add("tojson", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) {
		return ok(values.FromString(values.ValueText.ToJson(input)))
	})
	add("fromjson", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) {
		if input.Kind() == values.KindString {
			return values.ValueText.TryParse(input.AsString())
		}
		return fail(values.Errors.OnlyStringsParse(input))
	})
	add("tonumber", 0, toNumber)
	add("toboolean", 0, toBoolean)
	add("format", 1, func(_ *interpreter, input value, arguments []value) (value, value, bool) {
		if arguments[0].Kind() == values.KindString {
			return Formats.TryApply(arguments[0].AsString(), input)
		}
		return fail(values.Errors.NotAValidFormat(arguments[0]))
	})
	add("explode", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) {
		return values.ValueOps.TryExplode(input)
	})
	add("implode", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) {
		return values.ValueOps.TryImplode(input)
	})
	add("ascii_downcase", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) { return asciiCase(input, false) })
	add("ascii_upcase", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) { return asciiCase(input, true) })
	add("startswith", 1, func(_ *interpreter, input value, arguments []value) (value, value, bool) {
		if bothStrings(input, arguments[0]) {
			return ok(values.FromBool(strings.HasPrefix(input.AsString(), arguments[0].AsString())))
		}
		return fail(values.Errors.StartsWithStrings())
	})
	add("endswith", 1, func(_ *interpreter, input value, arguments []value) (value, value, bool) {
		if bothStrings(input, arguments[0]) {
			return ok(values.FromBool(strings.HasSuffix(input.AsString(), arguments[0].AsString())))
		}
		return fail(values.Errors.EndsWithStrings())
	})
	add("trim", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) { return trim(input, true, true) })
	add("ltrim", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) { return trim(input, true, false) })
	add("rtrim", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) { return trim(input, false, true) })
	add("split", 1, func(_ *interpreter, input value, arguments []value) (value, value, bool) {
		if bothStrings(input, arguments[0]) {
			return ok(values.FromArray(values.ValueOps.Split(input.AsString(), arguments[0].AsString())))
		}
		return fail(values.Errors.SplitStrings())
	})
	add("_strindices", 1, stringIndices)
}

// ToText is tostring: a string as it is, any other value as its canonical text.
func ToText(v value) string {
	if v.Kind() == values.KindString {
		return v.AsString()
	}
	return values.ValueText.ToJson(v)
}

func toNumber(_ *interpreter, input value, _ []value) (value, value, bool) {
	if input.Kind() == values.KindNumber {
		return ok(input)
	}
	if input.Kind() == values.KindString {
		if number, parsed := values.ValueText.TryParseNumber(input.AsString()); parsed {
			return ok(values.FromNumber(number))
		}
	}
	return fail(values.Errors.CannotParseNumber(input))
}

func toBoolean(_ *interpreter, input value, _ []value) (value, value, bool) {
	if input.Kind() == values.KindTrue || input.Kind() == values.KindFalse {
		return ok(input)
	}
	if input.Kind() == values.KindString && (input.AsString() == "true" || input.AsString() == "false") {
		return ok(values.FromBool(input.AsString() == "true"))
	}
	return fail(values.Errors.CannotParseBoolean(input))
}

// trim, ltrim, rtrim: the Unicode White_Space code points.
func trim(input value, start, end bool) (value, value, bool) {
	if input.Kind() != values.KindString {
		return fail(values.Errors.TrimNotString())
	}
	text := input.AsString()
	from, to := 0, len(text)
	for start && from < to {
		r, size := utf8.DecodeRuneInString(text[from:to])
		if !isWhiteSpace(r) {
			break
		}
		from += size
	}
	for end && to > from {
		r, size := utf8.DecodeLastRuneInString(text[from:to])
		if !isWhiteSpace(r) {
			break
		}
		to -= size
	}
	if from == 0 && to == len(text) {
		return ok(input)
	}
	return ok(values.FromString(text[from:to]))
}

func isWhiteSpace(c rune) bool {
	return (c >= 0x09 && c <= 0x0D) || c == 0x20 || c == 0x85 || c == 0xA0 || c == 0x1680 || (c >= 0x2000 && c <= 0x200A) ||
		c == 0x2028 || c == 0x2029 || c == 0x202F || c == 0x205F || c == 0x3000
}

func asciiCase(input value, upper bool) (value, value, bool) {
	if input.Kind() != values.KindString {
		return fail(values.Errors.ExplodeNotString())
	}
	text := []byte(input.AsString())
	for i, c := range text {
		if upper && c >= 'a' && c <= 'z' {
			text[i] = c - 32
		} else if !upper && c >= 'A' && c <= 'Z' {
			text[i] = c + 32
		}
	}
	return ok(values.FromString(string(text)))
}

// stringIndices: _strindices(needle): the code point offsets of every occurrence, overlapping
// ones included.
func stringIndices(_ *interpreter, input value, arguments []value) (value, value, bool) {
	if input.Kind() != values.KindString {
		return fail(values.Errors.CannotSearchNotString(input))
	}
	if arguments[0].Kind() != values.KindString {
		return fail(values.Errors.NotAString(arguments[0]))
	}
	text := input.AsString()
	needle := arguments[0].AsString()
	var offsets []value
	for i, codePoint := 0, 0; len(needle) > 0 && i+len(needle) <= len(text); codePoint++ {
		if strings.HasPrefix(text[i:], needle) {
			offsets = append(offsets, values.FromNumber(float64(codePoint)))
		}
		i = values.CodePoints.Advance(text, i, 1)
	}
	return ok(values.FromArray(values.ArrayFromOwned(offsets)))
}

func bothStrings(a, b value) bool {
	return a.Kind() == values.KindString && b.Kind() == values.KindString
}
