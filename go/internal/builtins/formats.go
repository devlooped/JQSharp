package builtins

import (
	"math"
	"strings"

	"github.com/devlooped/jqlite/go/internal/values"
)

type formats struct{}

// Formats are the @name formats of §9.9.
var Formats formats

const (
	hexUpper       = "0123456789ABCDEF"
	base64Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"
)

// TryApply applies the format name (without '@') to v, giving a string.
func (formats) TryApply(name string, v value) (value, value, bool) {
	switch name {
	case "text":
		return ok(values.FromString(ToText(v)))
	case "json":
		return ok(values.FromString(values.ValueText.ToJson(v)))
	case "html":
		return ok(values.FromString(html(ToText(v))))
	case "uri":
		return ok(values.FromString(uri(ToText(v))))
	case "urid":
		return uriDecode(ToText(v))
	case "csv":
		if v.Kind() == values.KindArray {
			return row(v.AsArray(), false)
		}
		return fail(values.Errors.CannotBeCsvFormatted(v))
	case "tsv":
		if v.Kind() == values.KindArray {
			return row(v.AsArray(), true)
		}
		return fail(values.Errors.CannotBeTsvFormatted(v))
	case "sh":
		if v.Kind() == values.KindArray {
			return shell(v.AsArray())
		}
		return shell(values.ArrayOf(v))
	case "base64":
		return ok(values.FromString(base64(ToText(v))))
	case "base64d":
		return base64Decode(ToText(v))
	default:
		return fail(values.Errors.InvalidFormat(name))
	}
}

// html: @html: & < > ' " as entities.
func html(text string) string {
	var builder strings.Builder
	builder.Grow(len(text))
	for i := 0; i < len(text); i++ {
		switch c := text[i]; c {
		case '&':
			builder.WriteString("&amp;")
		case '<':
			builder.WriteString("&lt;")
		case '>':
			builder.WriteString("&gt;")
		case '\'':
			builder.WriteString("&apos;")
		case '"':
			builder.WriteString("&quot;")
		default:
			builder.WriteByte(c)
		}
	}
	return builder.String()
}

// uri: @uri: the UTF-8 bytes, unreserved ones (A-Z a-z 0-9 - _ . ~) kept, the others as %XX.
func uri(text string) string {
	bytes := values.Utf8.Encode(text, nil)
	var builder strings.Builder
	builder.Grow(len(bytes))
	for _, b := range bytes {
		if (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9') || b == '-' || b == '_' || b == '.' || b == '~' {
			builder.WriteByte(b)
		} else {
			builder.WriteByte('%')
			builder.WriteByte(hexUpper[b>>4])
			builder.WriteByte(hexUpper[b&0xF])
		}
	}
	return builder.String()
}

// uriDecode: @urid: %XX (either case) is that byte, any other code point its UTF-8 bytes; the
// bytes must be well-formed UTF-8.
func uriDecode(text string) (value, value, bool) {
	bytes := make([]byte, 0, len(text))
	for i := 0; i < len(text); i++ {
		c := text[i]
		if c == '%' {
			high, low := -1, -1
			if i+1 < len(text) {
				high = hexDigit(text[i+1])
			}
			if i+2 < len(text) {
				low = hexDigit(text[i+2])
			}
			if high < 0 || low < 0 {
				return fail(values.Errors.NotValidUriEncoding(values.FromString(text)))
			}
			bytes = append(bytes, byte(high<<4|low))
			i += 2
			continue
		}
		bytes = append(bytes, c)
	}

	decoded, valid := values.Utf8.TryDecode(bytes)
	if !valid {
		return fail(values.Errors.NotValidUriEncoding(values.FromString(text)))
	}
	return ok(values.FromString(decoded))
}

func hexDigit(c byte) int {
	switch {
	case c >= '0' && c <= '9':
		return int(c - '0')
	case c >= 'a' && c <= 'f':
		return int(c-'a') + 10
	case c >= 'A' && c <= 'F':
		return int(c-'A') + 10
	}
	return -1
}

// row: @csv and @tsv: one row; strings quoted (csv) or escaped (tsv), numbers as canonical text
// (NaN empty), booleans as such, null empty.
func row(items *values.Array, tsv bool) (value, value, bool) {
	var builder strings.Builder
	for i := 0; i < items.Count(); i++ {
		if i > 0 {
			if tsv {
				builder.WriteByte('\t')
			} else {
				builder.WriteByte(',')
			}
		}
		item := items.At(i)
		switch item.Kind() {
		case values.KindNull:
		case values.KindFalse:
			builder.WriteString("false")
		case values.KindTrue:
			builder.WriteString("true")
		case values.KindNumber:
			if n := item.AsNumber(); !math.IsNaN(n) {
				builder.WriteString(values.ValueText.FormatNumber(n))
			}
		case values.KindString:
			if tsv {
				tsvEscape(&builder, item.AsString())
			} else {
				builder.WriteByte('"')
				builder.WriteString(strings.ReplaceAll(item.AsString(), "\"", "\"\""))
				builder.WriteByte('"')
			}
		default:
			return fail(values.Errors.NotValidInCsvRow(item))
		}
	}
	return ok(values.FromString(builder.String()))
}

func tsvEscape(builder *strings.Builder, text string) {
	for i := 0; i < len(text); i++ {
		switch c := text[i]; c {
		case '\\':
			builder.WriteString("\\\\")
		case '\t':
			builder.WriteString("\\t")
		case '\n':
			builder.WriteString("\\n")
		case '\r':
			builder.WriteString("\\r")
		default:
			builder.WriteByte(c)
		}
	}
}

// shell: @sh: space-separated words; strings in single quotes (' as '\”), other scalars as
// canonical text.
func shell(items *values.Array) (value, value, bool) {
	var builder strings.Builder
	for i := 0; i < items.Count(); i++ {
		if i > 0 {
			builder.WriteByte(' ')
		}
		item := items.At(i)
		switch item.Kind() {
		case values.KindArray, values.KindObject:
			return fail(values.Errors.CannotBeEscapedForShell(item))
		case values.KindString:
			builder.WriteByte('\'')
			builder.WriteString(strings.ReplaceAll(item.AsString(), "'", "'\\''"))
			builder.WriteByte('\'')
		default:
			values.ValueText.Write(&builder, item)
		}
	}
	return ok(values.FromString(builder.String()))
}

// base64: @base64: RFC 4648 of the UTF-8 bytes, with '=' padding.
func base64(text string) string {
	bytes := values.Utf8.Encode(text, nil)
	var builder strings.Builder
	builder.Grow((len(bytes) + 2) / 3 * 4)
	for i := 0; i < len(bytes); i += 3 {
		left := len(bytes) - i
		group := int(bytes[i]) << 16
		if left > 1 {
			group |= int(bytes[i+1]) << 8
		}
		if left > 2 {
			group |= int(bytes[i+2])
		}
		builder.WriteByte(base64Alphabet[group>>18])
		builder.WriteByte(base64Alphabet[(group>>12)&0x3F])
		if left > 1 {
			builder.WriteByte(base64Alphabet[(group>>6)&0x3F])
		} else {
			builder.WriteByte('=')
		}
		if left > 2 {
			builder.WriteByte(base64Alphabet[group&0x3F])
		} else {
			builder.WriteByte('=')
		}
	}
	return builder.String()
}

// base64Decode: @base64d: the characters before the first '=' must be in the alphabet; a final
// group of 2 or 3 characters gives 1 or 2 bytes, of 1 is an error; the bytes are decoded as UTF-8
// with replacement.
func base64Decode(text string) (value, value, bool) {
	bytes := make([]byte, 0, len(text)*3/4)
	group, count := 0, 0
	for i := 0; i < len(text); i++ {
		c := text[i]
		if c == '=' {
			break
		}
		digit := base64Digit(c)
		if digit < 0 {
			return fail(values.Errors.NotValidBase64(values.FromString(text)))
		}
		group = group<<6 | digit
		count++
		if count == 4 {
			bytes = append(bytes, byte(group>>16), byte(group>>8), byte(group))
			group, count = 0, 0
		}
	}

	switch count {
	case 1:
		return fail(values.Errors.TrailingBase64Byte(values.FromString(text)))
	case 2:
		bytes = append(bytes, byte(group>>4))
	case 3:
		bytes = append(bytes, byte(group>>10), byte(group>>2))
	}
	return ok(values.FromString(values.Utf8.DecodeReplacing(bytes)))
}

func base64Digit(c byte) int {
	switch {
	case c >= 'A' && c <= 'Z':
		return int(c - 'A')
	case c >= 'a' && c <= 'z':
		return int(c-'a') + 26
	case c >= '0' && c <= '9':
		return int(c-'0') + 52
	case c == '+':
		return 62
	case c == '/':
		return 63
	}
	return -1
}
