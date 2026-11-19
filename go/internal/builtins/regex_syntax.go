package builtins

import (
	"slices"
	"strings"

	"github.com/devlooped/jqlite/go/internal/values"
)

type regexSyntax struct{}

// RegexSyntax is the regex dialect of §9.10: reads a jqlite pattern, rejects everything outside the
// dialect, and writes an equivalent pattern for the host engine (RegexEngine) together with the
// names of its capturing groups in the order of their '('.
//
// The output is in the syntax of Go's regexp (RE2), which works on code points, so the classes are
// written directly (the reference, on UTF-16, splits them into a BMP class and surrogate pairs).
// The meaning the spec demands is spelled out so that no engine default leaks in: every code point
// is written as \x{…}; . as a class without U+000A (with m or p, any code point); \d \w \s as ASCII
// classes; $ as \z; \b \B as Go's own ASCII word boundaries; named groups as plain groups. The i flag
// is done here, never by the engine: it folds ASCII letters only (rangeSet.addAsciiCases, the one
// place of the rule).
//
// The limits of §11 are checked here, while the pattern is read (regexMeasure), so that the engine
// meets no pattern beyond them. Two of them are RE2's own: it refuses nested counts that multiply
// above 1000, and a syntax tree of more than 1000 levels (a group that is an alternative, a
// sequence, a repetition and a capture takes four, so 100 groups open at once stay under it).
var RegexSyntax regexSyntax

const (
	// maxRepeat is the largest count of a quantifier, and of the counts of nested quantifiers multiplied.
	maxRepeat = 1000
	// maxSize is the largest size of a pattern (§9.10): its items, counted repetitions written out.
	maxSize = 10000
	// maxNesting is the most groups open at once.
	maxNesting = 100
	// maxGroups is the most capturing groups.
	maxGroups = 1000
	// tooLarge is the reason of a pattern beyond maxSize, maxNesting or maxGroups: a fixed text (§10.3).
	tooLarge = "the pattern is too large"
)

// RegexOptions are the flags of §9.10 that change the pattern itself.
type RegexOptions struct {
	Extended   bool
	DotAll     bool
	IgnoreCase bool
}

// Translation is a translated pattern: the host pattern, one name ("" when unnamed) per capturing
// group, and whether it uses lookaround (never in Go: \b and \B are native).
type Translation struct {
	Pattern    string
	Groups     []string
	Lookaround bool
}

// previous is what precedes a quantifier: only an atom may be repeated.
type previous int

const (
	previousNone previous = iota
	previousAtom
	previousQuantifier
	previousAnchor
)

type regexError string

func (e regexError) Error() string { return string(e) }

// TryTranslate validates and translates a pattern; the error is the reason.
func (regexSyntax) TryTranslate(pattern string, options RegexOptions) (Translation, string, bool) {
	points := []rune(pattern)
	var output strings.Builder
	var groups []string
	measure := newRegexMeasure()
	prev := previousNone
	for i := 0; i < len(points); {
		c := points[i]
		if options.Extended && (isExtendedSpace(c) || c == '#') {
			i = skipExtended(points, i)
			continue
		}

		var err error
		// Whether the pattern read so far is within maxSize.
		fits := true
		switch c {
		case '\\':
			i, prev, err = tryEscape(points, i, &output, options, prev)
			fits = err != nil || measure.add(1)
		case '[':
			var items int
			i, items, err = tryClass(points, i, &output, options)
			fits = err != nil || measure.add(items)
			prev = previousAtom
		case '.':
			if options.DotAll {
				output.WriteString(`(?s:.)`)
			} else {
				output.WriteString(`[^\n]`)
			}
			fits = measure.add(1)
			prev = previousAtom
			i++
		case '^':
			output.WriteByte('^')
			fits = measure.add(1)
			prev = previousAnchor
			i++
		case '$':
			output.WriteString(`\z`)
			fits = measure.add(1)
			prev = previousAnchor
			i++
		case '|':
			output.WriteByte('|')
			fits = measure.add(1)
			prev = previousNone
			i++
		case '(':
			if measure.depth() == maxNesting {
				return Translation{}, tooLarge, false
			}
			i, groups, err = tryGroup(points, i, &output, groups)
			fits = err != nil || measure.open()
			prev = previousNone
		case ')':
			if measure.depth() == 0 {
				return Translation{}, "unmatched close parenthesis", false
			}
			measure.close()
			output.WriteByte(')')
			prev = previousAtom
			i++
		case '*', '+', '?', '{':
			if prev != previousAtom {
				if prev == previousQuantifier {
					return Translation{}, "nested quantifier", false
				}
				return Translation{}, "quantifier without an operand", false
			}
			i, err = tryQuantifier(points, i, &output, options, measure)
			prev = previousQuantifier
		case ']', '}':
			return Translation{}, "unescaped '" + string(c) + "'", false
		default:
			literal(&output, c, options)
			fits = measure.add(1)
			prev = previousAtom
			i++
		}
		if err != nil {
			return Translation{}, err.Error(), false
		}
		if !fits {
			return Translation{}, tooLarge, false
		}
	}

	if measure.depth() > 0 {
		return Translation{}, "missing close parenthesis", false
	}
	return Translation{output.String(), groups, false}, "", true
}

// tryEscape: \x outside a class: a literal, a shorthand class or an anchor.
func tryEscape(points []rune, i int, output *strings.Builder, options RegexOptions, prev previous) (int, previous, error) {
	if i+1 == len(points) {
		return i, prev, regexError("trailing backslash")
	}
	c := points[i+1]
	switch c {
	case 'b':
		output.WriteString(`\b`)
		return i + 2, previousAnchor, nil
	case 'B':
		output.WriteString(`\B`)
		return i + 2, previousAnchor, nil
	case 'd', 'w', 's', 'D', 'W', 'S':
		set := &rangeSet{}
		set.addShorthand(c)
		writeClass(output, set, false)
		return i + 2, previousAtom, nil
	}

	value, next, err := tryEscapedLiteral(points, i)
	if err != nil {
		return i, prev, err
	}
	literal(output, value, options)
	return next, previousAtom, nil
}

// tryEscapedLiteral reads the escapes that stand for one code point: the metacharacters,
// \t \n \r \f \v and \xHH.
func tryEscapedLiteral(points []rune, i int) (rune, int, error) {
	if i+1 == len(points) {
		return 0, i, regexError("trailing backslash")
	}
	c := points[i+1]
	switch c {
	case '\\', '.', '*', '+', '?', '(', ')', '[', ']', '{', '}', '|', '^', '$', '/', '-':
		return c, i + 2, nil
	case 't':
		return '\t', i + 2, nil
	case 'n':
		return '\n', i + 2, nil
	case 'r':
		return '\r', i + 2, nil
	case 'f':
		return '\f', i + 2, nil
	case 'v':
		return '\v', i + 2, nil
	case 'x':
		high, low := -1, -1
		if i+2 < len(points) {
			high = hexRune(points[i+2])
		}
		if i+3 < len(points) {
			low = hexRune(points[i+3])
		}
		if high < 0 || low < 0 {
			return 0, i, regexError(`\x must be followed by two hex digits`)
		}
		return rune(high*16 + low), i + 4, nil
	}
	return 0, i, regexError(`invalid escape \` + string(c))
}

// tryGroup reads ( (?: or (?<name>.
func tryGroup(points []rune, i int, output *strings.Builder, groups []string) (int, []string, error) {
	if i+1 < len(points) && points[i+1] == '?' {
		if i+2 < len(points) && points[i+2] == ':' {
			output.WriteString("(?:")
			return i + 3, groups, nil
		}

		if i+2 < len(points) && points[i+2] == '<' {
			start := i + 3
			end := start
			for end < len(points) && points[end] != '>' {
				end++
			}
			if end == len(points) || !isGroupName(points, start, end) {
				return i, groups, regexError("invalid group name")
			}
			name := string(points[start:end])
			if slices.Contains(groups, name) {
				return i, groups, regexError("duplicate group name " + name)
			}
			if len(groups) == maxGroups {
				return i, groups, regexError(tooLarge)
			}
			output.WriteByte('(')
			return end + 1, append(groups, name), nil
		}

		return i, groups, regexError("unsupported group syntax (?")
	}

	if len(groups) == maxGroups {
		return i, groups, regexError(tooLarge)
	}
	output.WriteByte('(')
	return i + 1, append(groups, ""), nil
}

// tryQuantifier reads * + ? {n} {n,} {n,m}, each optionally followed by ? (lazy).
func tryQuantifier(points []rune, i int, output *strings.Builder, options RegexOptions, measure *regexMeasure) (int, error) {
	c := points[i]
	// The count of §9.10: how many times the operand is written out.
	count := 1
	if c == '{' {
		at := i + 1
		var low, high int
		var found bool
		if at, low, found = tryRepeatNumber(points, at); !found {
			return i, regexError("'{' is not a quantifier")
		}
		high = low
		unbounded := false
		if at < len(points) && points[at] == ',' {
			at++
			if at < len(points) && points[at] == '}' {
				unbounded = true
			} else if at, high, found = tryRepeatNumber(points, at); !found {
				return i, regexError("'{' is not a quantifier")
			}
		}

		if at == len(points) || points[at] != '}' {
			return i, regexError("'{' is not a quantifier")
		}
		if low > maxRepeat || (!unbounded && (high > maxRepeat || high < low)) {
			return i, regexError("invalid repetition count")
		}
		count = high
		if unbounded {
			count = low
		}
		count = max(count, 1)
		if !measure.multiply(count) {
			return i, regexError("invalid repetition count")
		}
		output.WriteString("{" + values.CodePoints.Decimal(low))
		if unbounded {
			output.WriteByte(',')
		} else if high != low {
			output.WriteString("," + values.CodePoints.Decimal(high))
		}
		output.WriteByte('}')
		i = at + 1
	} else {
		output.WriteRune(c)
		i++
	}

	if !measure.repeat(count) {
		return i, regexError(tooLarge)
	}
	if options.Extended {
		for i < len(points) && (isExtendedSpace(points[i]) || points[i] == '#') {
			i = skipExtended(points, i)
		}
	}

	if i < len(points) && points[i] == '?' {
		output.WriteByte('?')
		i++
	}
	return i, nil
}

func tryRepeatNumber(points []rune, at int) (int, int, bool) {
	v := 0
	start := at
	for at < len(points) && points[at] >= '0' && points[at] <= '9' {
		v = min(v*10+int(points[at]-'0'), maxRepeat+1)
		at++
	}
	return at, v, at > start
}

// tryClass reads [...] or [^...]: literals, escapes, ranges of literals and shorthands; the second
// result is the number of its items.
func tryClass(points []rune, i int, output *strings.Builder, options RegexOptions) (int, int, error) {
	items := 0
	at := i + 1
	negated := at < len(points) && points[at] == '^'
	if negated {
		at++
	}
	first := at
	set := &rangeSet{}
	previousLiteral := rune(-1)
	for {
		if at == len(points) {
			return i, 0, regexError("missing ']'")
		}
		c := points[at]
		if c == ']' {
			break
		}
		if c == '[' {
			return i, 0, regexError("unescaped '[' in a class")
		}

		if c == '-' && at != first && at+1 < len(points) && points[at+1] != ']' {
			// A range: the item before must be a single literal, the one after a literal too.
			if previousLiteral < 0 {
				return i, 0, regexError("'-' must be first, last, escaped or between two literals")
			}
			at++
			high, next, err := tryClassLiteral(points, at)
			if err != nil {
				return i, 0, err
			}
			at = next
			if high < previousLiteral {
				return i, 0, regexError("range out of order")
			}
			set.add(previousLiteral, high)
			previousLiteral = -1
			continue
		}

		if c == '\\' && at+1 < len(points) && strings.ContainsRune("dwsDWS", points[at+1]) {
			set.addShorthand(points[at+1])
			previousLiteral = -1
			items++
			at += 2
			continue
		}

		value, next, err := tryClassLiteral(points, at)
		if err != nil {
			return i, 0, err
		}
		at = next
		set.add(value, value)
		previousLiteral = value
		items++
	}

	if at == first {
		return i, 0, regexError("empty class")
	}
	// The i flag folds before a negation applies: [^a] matches neither a nor A.
	if options.IgnoreCase {
		set.addAsciiCases()
	}
	writeClass(output, set, negated)
	return at + 1, items, nil
}

// regexMeasure holds the limits of §11 on the part of a pattern read so far: its size, the groups
// open, and the counts of the counted quantifiers nested in one another.
type regexMeasure struct {
	// Per open group: the size when it opened.
	starts []int
	// For the pattern and per open group: the largest product of the counts nested in it so far.
	products []int
	size     int
	// The last item, the operand of a quantifier: its size and the largest product of the counts nested in it.
	atom        int
	atomProduct int
}

func newRegexMeasure() *regexMeasure {
	return &regexMeasure{products: []int{1}}
}

// depth is the number of groups open.
func (m *regexMeasure) depth() int { return len(m.starts) }

// add counts an item of this size (a literal, a class, an anchor, a '|'); false when the pattern is
// too large.
func (m *regexMeasure) add(itemSize int) bool {
	m.atom = itemSize
	m.atomProduct = 1
	m.size += itemSize
	return m.size <= maxSize
}

// open: a group opens, it counts 1; false when the pattern is too large.
func (m *regexMeasure) open() bool {
	m.starts = append(m.starts, m.size)
	m.products = append(m.products, 1)
	return m.add(1)
}

// close: a group closes, it is the last item, with all it holds.
func (m *regexMeasure) close() {
	m.atom = m.size - m.starts[len(m.starts)-1]
	m.atomProduct = m.products[len(m.products)-1]
	m.starts = m.starts[:len(m.starts)-1]
	m.products = m.products[:len(m.products)-1]
	last := len(m.products) - 1
	m.products[last] = max(m.products[last], m.atomProduct)
}

// multiply: a counted quantifier on the last item; false when the nested counts multiply above
// maxRepeat.
func (m *regexMeasure) multiply(count int) bool {
	product := count * m.atomProduct
	last := len(m.products) - 1
	m.products[last] = max(m.products[last], product)
	return product <= maxRepeat
}

// repeat: a quantifier on the last item, which counts 1, and the item once more for each count
// above 1; false when the pattern is too large.
func (m *regexMeasure) repeat(count int) bool {
	m.size += m.atom*(count-1) + 1
	return m.size <= maxSize
}

func tryClassLiteral(points []rune, at int) (rune, int, error) {
	value := points[at]
	if value == '\\' {
		return tryEscapedLiteral(points, at)
	}
	if value == '[' || value == ']' {
		return 0, at, regexError("unescaped '" + string(value) + "' in a class")
	}
	return value, at + 1, nil
}

// writeClass writes a class of code points; an empty set (a negated class of everything, or no
// code point) is a class no code point is in.
func writeClass(output *strings.Builder, set *rangeSet, negated bool) {
	ranges := set.normalized()
	if negated {
		ranges = set.complement().normalized()
	}
	if len(ranges) == 0 {
		output.WriteString(`[^\x{0}-\x{10FFFF}]`)
		return
	}
	output.WriteByte('[')
	for _, r := range ranges {
		writeCodePoint(output, r.low)
		if r.high > r.low {
			output.WriteByte('-')
			writeCodePoint(output, r.high)
		}
	}
	output.WriteByte(']')
}

// literal writes a code point; under the i flag an ASCII letter becomes the class of its two cases.
func literal(output *strings.Builder, c rune, options RegexOptions) {
	if options.IgnoreCase && ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) {
		set := &rangeSet{}
		set.add(c, c)
		set.addAsciiCases()
		writeClass(output, set, false)
		return
	}
	writeCodePoint(output, c)
}

func writeCodePoint(output *strings.Builder, c rune) {
	output.WriteString(`\x{`)
	output.WriteString(values.CodePoints.Hex(int(c), 1))
	output.WriteByte('}')
}

// skipExtended: with the x flag, whitespace and a # comment up to the next line feed.
func skipExtended(points []rune, i int) int {
	if points[i] != '#' {
		return i + 1
	}
	for i < len(points) && points[i] != '\n' {
		i++
	}
	return i
}

func isExtendedSpace(c rune) bool { return (c >= 0x09 && c <= 0x0D) || c == 0x20 }

func isGroupName(points []rune, start, end int) bool {
	if end == start || (points[start] >= '0' && points[start] <= '9') {
		return false
	}
	for _, c := range points[start:end] {
		if !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_') {
			return false
		}
	}
	return true
}

func hexRune(c rune) int {
	if c < 0x80 {
		return hexDigit(byte(c))
	}
	return -1
}

type codeRange struct{ low, high rune }

// rangeSet is a set of code points as ranges, normalized to sorted, disjoint, non-adjacent ones
// when read.
type rangeSet struct{ ranges []codeRange }

func (s *rangeSet) add(low, high rune) { s.ranges = append(s.ranges, codeRange{low, high}) }

// addShorthand adds the ASCII classes of §9.10 and their complements.
func (s *rangeSet) addShorthand(c rune) {
	set := &rangeSet{}
	switch c {
	case 'd', 'D':
		set.add('0', '9')
	case 'w', 'W':
		set.add('0', '9')
		set.add('A', 'Z')
		set.add('a', 'z')
		set.add('_', '_')
	default:
		set.add('\t', '\r')
		set.add(' ', ' ')
	}
	if c == 'D' || c == 'W' || c == 'S' {
		set = set.complement()
	}
	for _, r := range set.normalized() {
		s.add(r.low, r.high)
	}
}

// addAsciiCases is the i flag of §9.10: adds the other case of every ASCII letter in the set (A-Z
// and a-z, 32 apart); no other code point folds.
func (s *rangeSet) addAsciiCases() {
	var added []codeRange
	for _, r := range s.normalized() {
		if upperLow, upperHigh := max(r.low, 'A'), min(r.high, 'Z'); upperLow <= upperHigh {
			added = append(added, codeRange{upperLow + 32, upperHigh + 32})
		}
		if lowerLow, lowerHigh := max(r.low, 'a'), min(r.high, 'z'); lowerLow <= lowerHigh {
			added = append(added, codeRange{lowerLow - 32, lowerHigh - 32})
		}
	}
	s.ranges = append(s.ranges, added...)
}

func (s *rangeSet) complement() *rangeSet {
	complement := &rangeSet{}
	next := rune(0)
	for _, r := range s.normalized() {
		if r.low > next {
			complement.add(next, r.low-1)
		}
		next = r.high + 1
	}
	if next <= values.MaxCodePoint {
		complement.add(next, values.MaxCodePoint)
	}
	return complement
}

func (s *rangeSet) normalized() []codeRange {
	slices.SortFunc(s.ranges, func(a, b codeRange) int { return int(a.low - b.low) })
	var merged []codeRange
	for _, r := range s.ranges {
		if n := len(merged); n > 0 && r.low <= merged[n-1].high+1 {
			merged[n-1].high = max(merged[n-1].high, r.high)
		} else {
			merged = append(merged, r)
		}
	}
	s.ranges = merged
	return merged
}
