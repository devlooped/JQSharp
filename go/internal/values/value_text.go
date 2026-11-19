package values

// Host-specific (H): the shortest round-trip digits of a double come from strconv.FormatFloat
// (format 'e', precision -1), and the decimal-to-double step from strconv.ParseFloat. A port uses
// its own host's shortest-digits and correctly rounded parsing functions.

import (
	"math"
	"strconv"
	"strings"
	"unicode/utf8"
)

type valueText struct{}

// ValueText is the one JSON writer, number formatter and JSON reader of jqlite.
//
// Canonical text is compact JSON. Strings escape only ", \, the control characters below U+0020
// (\b \f \n \r \t, the others \u00xx) and U+007F, as jq does. Numbers are the shortest digits that
// read back to the same double, laid out as ECMAScript's Number::toString; NaN is null and
// ±Infinity are ±1.7976931348623157e+308.
var ValueText valueText

const maxDouble = "1.7976931348623157e+308"

// MaxWriteDepth is the deepest level the writer writes (§5.1, §11).
const MaxWriteDepth = 10001

const tooDeep = "<skipped: too deep>"

// ToJson gives the canonical text of value.
func (valueText) ToJson(value Value) string {
	var builder strings.Builder
	ValueText.Write(&builder, value)
	return builder.String()
}

type writeFrame struct {
	container Value
	index     int
}

// Write writes the canonical text of value. No recursion: the containers being written are on an
// explicit stack, so nesting depth is not limited by the host stack. A value nested deeper than
// MaxWriteDepth levels (the outermost value is level 1) is written as <skipped: too deep>, as jq
// 1.8.2 does for scalars and containers alike.
func (valueText) Write(builder *strings.Builder, value Value) {
	var stack []writeFrame
	current := value
	for {
		// len(stack) is the number of containers around current: its level minus one.
		if len(stack) >= MaxWriteDepth {
			builder.WriteString(tooDeep)
		} else if writeOpening(builder, current) {
			// A container with elements: write its first one next.
			stack = append(stack, writeFrame{current, 0})
			current = writeElement(builder, current, 0)
			continue
		}

		// current is written: go on with the next element, closing the containers that end.
		for {
			if len(stack) == 0 {
				return
			}

			top := &stack[len(stack)-1]
			top.index++
			if top.index < containerCount(top.container) {
				builder.WriteByte(',')
				current = writeElement(builder, top.container, top.index)
				break
			}

			if top.container.kind == KindArray {
				builder.WriteByte(']')
			} else {
				builder.WriteByte('}')
			}
			stack = stack[:len(stack)-1]
		}
	}
}

// writeOpening writes a scalar or an empty container whole and gives false; writes the opening
// bracket of a container with elements and gives true.
func writeOpening(builder *strings.Builder, value Value) bool {
	switch value.kind {
	case KindNull:
		builder.WriteString("null")
	case KindFalse:
		builder.WriteString("false")
	case KindTrue:
		builder.WriteString("true")
	case KindNumber:
		builder.WriteString(ValueText.FormatNumber(value.num))
	case KindString:
		ValueText.WriteString(builder, value.ref.(string))
	case KindArray:
		builder.WriteByte('[')
		if value.AsArray().Count() > 0 {
			return true
		}
		builder.WriteByte(']')
	default:
		builder.WriteByte('{')
		if value.AsObject().Count() > 0 {
			return true
		}
		builder.WriteByte('}')
	}
	return false
}

func containerCount(container Value) int {
	if container.kind == KindArray {
		return container.AsArray().Count()
	}
	return container.AsObject().Count()
}

// writeElement gives the element at index; for an object, writes its key and colon first.
func writeElement(builder *strings.Builder, container Value, index int) Value {
	if container.kind == KindArray {
		return container.AsArray().At(index)
	}

	obj := container.AsObject()
	ValueText.WriteString(builder, obj.KeyAt(index))
	builder.WriteByte(':')
	return obj.ValueAt(index)
}

const hexDigits = "0123456789abcdef"

// WriteString writes text as a JSON string literal.
func (valueText) WriteString(builder *strings.Builder, text string) {
	builder.WriteByte('"')
	run := 0
	for i := 0; i < len(text); i++ {
		c := text[i]
		if c >= 0x20 && c != '"' && c != '\\' && c != 0x7F {
			continue
		}

		builder.WriteString(text[run:i])
		run = i + 1
		switch c {
		case '"':
			builder.WriteString("\\\"")
		case '\\':
			builder.WriteString("\\\\")
		case '\b':
			builder.WriteString("\\b")
		case '\f':
			builder.WriteString("\\f")
		case '\n':
			builder.WriteString("\\n")
		case '\r':
			builder.WriteString("\\r")
		case '\t':
			builder.WriteString("\\t")
		default:
			builder.WriteString("\\u00")
			builder.WriteByte(hexDigits[c>>4])
			builder.WriteByte(hexDigits[c&0xF])
		}
	}

	builder.WriteString(text[run:])
	builder.WriteByte('"')
}

// ToJsonTruncated gives the JSON text of value; when longer than maxBytes UTF-8 bytes, its
// longest prefix of at most keepBytes bytes that ends on a code point boundary, then "..." and the
// closing delimiter of the value (", ] or }), as jq 1.8.2 writes them: "aaa...", [1,2,...],
// {"a":...}.
func (valueText) ToJsonTruncated(value Value, maxBytes, keepBytes int) string {
	text := ValueText.ToJson(value)
	if len(text) <= maxBytes {
		return text
	}

	end := keepBytes
	for end > 0 && !utf8.RuneStart(text[end]) {
		end--
	}

	switch value.kind {
	case KindString:
		return text[:end] + "...\""
	case KindArray:
		return text[:end] + "...]"
	default:
		return text[:end] + "...}"
	}
}

// FormatNumber gives the canonical text of a number: shortest round-trip digits in ECMAScript
// Number::toString layout.
func (valueText) FormatNumber(number float64) string {
	if math.IsNaN(number) {
		return "null"
	}
	if math.IsInf(number, 0) {
		if number > 0 {
			return maxDouble
		}
		return "-" + maxDouble
	}
	if number == 0 {
		if math.Signbit(number) {
			return "-0"
		}
		return "0"
	}
	// Fast path: integers below 1e15 print as themselves.
	if math.Abs(number) < 1e15 && number == math.Trunc(number) {
		return strconv.FormatInt(int64(number), 10)
	}

	// strconv's 'e' format with precision -1 gives the shortest round-trip digits as
	// d.ddde±XX; only the layout changes below.
	text := strconv.FormatFloat(number, 'e', -1, 64)
	negative := text[0] == '-'
	body := text
	if negative {
		body = text[1:]
	}
	e := strings.IndexByte(body, 'e')
	exponent, _ := strconv.Atoi(body[e+1:])
	body = body[:e]

	dot := strings.IndexByte(body, '.')
	digits := body
	point := len(body)
	if dot >= 0 {
		digits = body[:dot] + body[dot+1:]
		point = dot
	}
	// n: the position of the decimal point relative to the first digit (value = 0.digits * 10^n).
	n := point + exponent
	lead := 0
	for lead < len(digits)-1 && digits[lead] == '0' {
		lead++
	}
	digits = strings.TrimRight(digits[lead:], "0")
	n -= lead
	k := len(digits)

	var builder strings.Builder
	builder.Grow(32)
	if negative {
		builder.WriteByte('-')
	}
	switch {
	case k <= n && n <= 21:
		builder.WriteString(digits)
		builder.WriteString(strings.Repeat("0", n-k))
	case 0 < n && n <= 21:
		builder.WriteString(digits[:n])
		builder.WriteByte('.')
		builder.WriteString(digits[n:])
	case -6 < n && n <= 0:
		builder.WriteString("0.")
		builder.WriteString(strings.Repeat("0", -n))
		builder.WriteString(digits)
	default:
		builder.WriteByte(digits[0])
		if k > 1 {
			builder.WriteByte('.')
			builder.WriteString(digits[1:])
		}
		builder.WriteByte('e')
		if n-1 < 0 {
			builder.WriteByte('-')
		} else {
			builder.WriteByte('+')
		}
		builder.WriteString(strconv.Itoa(abs(n - 1)))
	}
	return builder.String()
}

func abs(x int) int {
	if x < 0 {
		return -x
	}
	return x
}

func isAsciiDigit(c byte) bool { return c >= '0' && c <= '9' }

// TryParseNumber reads the numeric text of §5.3 (tonumber, and the numbers of JSON text): an
// optional sign, then digits with an optional fraction (1. and .5 included) and exponent, or, in
// any ASCII case, nan or snan (NaN), inf or infinity. No white space.
func (valueText) TryParseNumber(text string) (float64, bool) {
	i := 0
	negative := false
	if i < len(text) && (text[i] == '+' || text[i] == '-') {
		negative = text[i] == '-'
		i++
	}

	word := text[i:]
	if strings.EqualFold(word, "nan") || strings.EqualFold(word, "snan") {
		return math.NaN(), true
	}
	if strings.EqualFold(word, "inf") || strings.EqualFold(word, "infinity") {
		if negative {
			return math.Inf(-1), true
		}
		return math.Inf(1), true
	}

	digits := 0
	for ; i < len(text) && isAsciiDigit(text[i]); i++ {
		digits++
	}
	if i < len(text) && text[i] == '.' {
		for i++; i < len(text) && isAsciiDigit(text[i]); i++ {
			digits++
		}
	}
	if digits == 0 {
		return 0, false
	}

	if i < len(text) && (text[i] == 'e' || text[i] == 'E') {
		i++
		if i < len(text) && (text[i] == '+' || text[i] == '-') {
			i++
		}
		exponentDigits := 0
		for ; i < len(text) && isAsciiDigit(text[i]); i++ {
			exponentDigits++
		}
		if exponentDigits == 0 {
			return 0, false
		}
	}

	if i != len(text) {
		return 0, false
	}
	// The grammar is checked above; the host parser only converts. Its range error is ignored:
	// the value is already ±Infinity or 0.
	number, err := strconv.ParseFloat(text, 64)
	if err != nil {
		if ne, ok := err.(*strconv.NumError); !ok || ne.Err != strconv.ErrRange {
			return 0, false
		}
	}
	return number, true
}

// TryParse reads exactly one JSON value from text (§5.3; jq's fromjson). On failure err is the
// whole message, "<reason> (while parsing '<text>')", the reason with the line and column where
// reading stopped (except for "Expected JSON value" and "Unexpected extra JSON values", as in jq).
func (valueText) TryParse(text string) (value, err Value, ok bool) {
	reader := jsonReader{text: text}
	return reader.read()
}

// TryParseSequence reads a sequence of JSON values separated by white space (a data module,
// §9.11) as one array; no value at all is the empty array. Errors as TryParse.
func (valueText) TryParseSequence(text string) (values, err Value, ok bool) {
	reader := jsonReader{text: text, sequence: true}
	return reader.read()
}

// readFrame is a container being read.
type readFrame struct {
	isObject bool
	array    *Array
	object   *ObjectBuilder
	key      string
}

func newReadFrame(isObject bool) *readFrame {
	frame := &readFrame{isObject: isObject, array: EmptyArray}
	if isObject {
		frame.object = NewObjectBuilder(4)
	}
	return frame
}

func (f *readFrame) add(value Value) {
	if f.object != nil {
		f.object.Set(f.key, value)
	} else {
		f.array = f.array.Append(value)
	}
}

func (f *readFrame) toValue() Value {
	if f.object != nil {
		return FromObject(f.object.ToObject())
	}
	return FromArray(f.array)
}

// jsonReaderMaxDepth is the deepest nesting the reader reads (§11).
const jsonReaderMaxDepth = 10000

// jsonReader is a JSON reader without recursion (an explicit stack), so deep input cannot exhaust
// the host stack. Like jq, a literal is any run of characters up to white space, a quote or one of
// []{},:; a raw control character inside a string is an error, and every lone surrogate escape and
// every ill-formed UTF-8 sequence becomes U+FFFD.
type jsonReader struct {
	text     string
	sequence bool
	pos      int
	failure  Value
	// The values read so far, when reading a sequence.
	values []Value
}

func (r *jsonReader) read() (value, err Value, ok bool) {
	result, ok := r.readDocument()
	if !ok {
		return Null, r.failure, false
	}
	return result, Null, true
}

func (r *jsonReader) readDocument() (Value, bool) {
	text := r.text
	if strings.HasPrefix(text, "\xEF\xBB\xBF") { // a byte order mark
		r.pos = 3
	}
	r.skipSpace()
	if r.pos == len(text) && r.sequence {
		return FromArray(EmptyArray), true
	}
	if r.pos == len(text) {
		return Null, r.failWithoutPosition(ExpectedValue)
	}

	var stack []*readFrame
	for {
		// A value starts at pos (white space skipped).
		if r.pos == len(text) {
			return Null, r.fail(UnfinishedAtEof)
		}

		var current Value
		c := text[r.pos]
		switch {
		case c == '[' || c == '{':
			if len(stack) == jsonReaderMaxDepth {
				return Null, r.failAt(DepthLimit, r.pos+1)
			}
			r.pos++
			r.skipSpace()
			frame := newReadFrame(c == '{')
			closing := byte(']')
			if c == '{' {
				closing = '}'
			}
			if r.pos < len(text) && text[r.pos] == closing {
				r.pos++
				current = frame.toValue()
			} else {
				stack = append(stack, frame)
				if frame.isObject && !r.readKey(frame) {
					return Null, false
				}
				continue
			}
		case c == '"':
			r.pos++
			s, ok := r.readString()
			if !ok {
				return Null, false
			}
			current = Value{kind: KindString, ref: s}
		case c == ']' || c == '}':
			if len(stack) == 0 {
				return Null, r.failAt(Errors.Unmatched(c), r.pos+1)
			}
			if stack[len(stack)-1].isObject {
				return Null, r.failAt(KeyValuePairs, r.pos+1)
			}
			return Null, r.failAt(ExpectedArrayElement, r.pos+1)
		case c == ',' || c == ':':
			return Null, r.failAt(Errors.ExpectedValueBefore(c), r.pos+1)
		default:
			var ok bool
			if current, ok = r.readLiteral(); !ok {
				return Null, false
			}
		}

		// Attach the value to its container; close containers as they end.
		for {
			r.skipSpace()
			if len(stack) == 0 && r.sequence {
				// A sequence goes on with the next value, if any.
				r.values = append(r.values, current)
				if r.pos < len(text) {
					break
				}
				return FromArray(ArrayFromOwned(r.values)), true
			}

			if len(stack) == 0 {
				if r.pos != len(text) {
					return Null, r.failWithoutPosition(ExtraValues)
				}
				return current, true
			}

			top := stack[len(stack)-1]
			top.add(current)
			if r.pos == len(text) {
				return Null, r.fail(UnfinishedAtEof)
			}

			d := text[r.pos]
			r.pos++
			if (top.isObject && d == '}') || (!top.isObject && d == ']') {
				stack = stack[:len(stack)-1]
				current = top.toValue()
				continue
			}

			if d != ',' {
				if d == ']' || d == '}' {
					return Null, r.failAt(Errors.Unmatched(d), r.pos)
				}
				return Null, r.failAt(ExpectedSeparator, r.pos)
			}

			r.skipSpace()
			if top.isObject && !r.readKey(top) {
				return Null, false
			}
			break
		}
	}
}

// readKey reads `"key" :` and the white space after it.
func (r *jsonReader) readKey(frame *readFrame) bool {
	text := r.text
	if r.pos == len(text) {
		return r.fail(UnfinishedAtEof)
	}
	if text[r.pos] != '"' {
		return r.failAt(KeysMustBeStrings, r.pos+1)
	}
	r.pos++
	key, ok := r.readString()
	if !ok {
		return false
	}
	r.skipSpace()
	if r.pos == len(text) {
		return r.fail(UnfinishedAtEof)
	}
	if text[r.pos] != ':' {
		return r.failAt(KeyValuePairs, r.pos+1)
	}
	r.pos++
	r.skipSpace()
	frame.key = key
	return true
}

// readString reads the rest of a string after its opening quote. A \u escape of a high surrogate
// followed by one of a low surrogate is one code point; every other surrogate escape, and every
// ill-formed UTF-8 sequence of the text, becomes U+FFFD.
func (r *jsonReader) readString() (string, bool) {
	text := r.text
	var builder strings.Builder
	run := r.pos
	escaped := false
	for {
		if r.pos == len(text) {
			return "", r.fail(UnfinishedAtEof)
		}
		c := text[r.pos]
		if c == '"' {
			var value string
			if escaped {
				builder.WriteString(text[run:r.pos])
				value = builder.String()
			} else {
				value = text[run:r.pos]
			}
			r.pos++
			return CodePoints.Sanitize(value), true
		}

		if c < 0x20 {
			return "", r.failAt(ControlCharacter, r.pos+1)
		}

		if c != '\\' {
			r.pos++
			continue
		}

		escaped = true
		builder.WriteString(text[run:r.pos])
		r.pos++
		if r.pos == len(text) {
			return "", r.fail(UnfinishedAtEof)
		}
		e := text[r.pos]
		r.pos++
		switch e {
		case '"', '\\', '/':
			builder.WriteByte(e)
		case 'b':
			builder.WriteByte('\b')
		case 'f':
			builder.WriteByte('\f')
		case 'n':
			builder.WriteByte('\n')
		case 'r':
			builder.WriteByte('\r')
		case 't':
			builder.WriteByte('\t')
		case 'u':
			unit, ok := r.readHex4()
			if !ok {
				return "", false
			}
			if unit >= 0xD800 && unit <= 0xDBFF && r.pos+1 < len(text) && text[r.pos] == '\\' && text[r.pos+1] == 'u' {
				// A high surrogate: combine with a low surrogate escape that follows.
				save := r.pos
				r.pos += 2
				low, ok := r.readHex4()
				if !ok {
					return "", false
				}
				if low >= 0xDC00 && low <= 0xDFFF {
					builder.WriteRune(rune(0x10000 + ((unit - 0xD800) << 10) + (low - 0xDC00)))
					break
				}
				r.pos = save
			}
			if unit >= 0xD800 && unit <= 0xDFFF {
				builder.WriteRune(Replacement)
			} else {
				builder.WriteRune(rune(unit))
			}
		default:
			return "", r.failAt(InvalidEscape, r.pos)
		}

		run = r.pos
	}
}

// readHex4 reads the four hexadecimal digits of a \u escape.
func (r *jsonReader) readHex4() (int, bool) {
	unit := 0
	for i := 0; i < 4; i++ {
		if r.pos == len(r.text) {
			return 0, r.fail(UnfinishedAtEof)
		}
		digit := hexValue(r.text[r.pos])
		r.pos++
		if digit < 0 {
			return 0, r.failAt(InvalidUnicodeEscape, r.pos)
		}
		unit = unit*16 + digit
	}
	return unit, true
}

func (r *jsonReader) readLiteral() (Value, bool) {
	text := r.text
	start := r.pos
	for r.pos < len(text) && !isDelimiter(text[r.pos]) {
		r.pos++
	}
	token := text[start:r.pos]
	switch token {
	case "null":
		return Null, true
	case "true":
		return True, true
	case "false":
		return False, true
	}

	number, ok := ValueText.TryParseNumber(token)
	if !ok {
		first := token[0]
		if (first >= 'a' && first <= 'z') || (first >= 'A' && first <= 'Z') {
			return Null, r.failAt(InvalidLiteral, r.pos)
		}
		return Null, r.failAt(InvalidNumericLiteral, r.pos)
	}
	return FromNumber(number), true
}

func isDelimiter(c byte) bool {
	switch c {
	case ' ', '\t', '\n', '\r', '[', ']', '{', '}', ',', ':', '"':
		return true
	}
	return false
}

func hexValue(c byte) int {
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

func (r *jsonReader) skipSpace() {
	for r.pos < len(r.text) {
		switch r.text[r.pos] {
		case ' ', '\t', '\n', '\r':
			r.pos++
		default:
			return
		}
	}
}

func (r *jsonReader) fail(reason string) bool { return r.failAt(reason, len(r.text)) }

// failAt reports reason with the position after consumed bytes; the column counts the code points
// read on their line, as jq does ("{" fails at line 1, column 1).
func (r *jsonReader) failAt(reason string, consumed int) bool {
	line := 1
	lineStart := 0
	for i := 0; i < consumed && i < len(r.text); i++ {
		if r.text[i] == '\n' {
			line++
			lineStart = i + 1
		}
	}
	column := utf8.RuneCountInString(r.text[lineStart:min(consumed, len(r.text))])
	return r.failWithoutPosition(Errors.AtPosition(reason, line, column))
}

func (r *jsonReader) failWithoutPosition(reason string) bool {
	r.failure = Errors.WhileParsing(reason, r.text)
	return false
}
