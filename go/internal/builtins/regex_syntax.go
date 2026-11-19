package builtins

import (
	"slices"

	"github.com/devlooped/jqlite/go/internal/values"
)

type regexSyntax struct{}

// RegexSyntax is the regex flavor of §9.10: reads a jqlite pattern, rejects everything outside the
// flavor, and writes an equivalent program for the engine (RegexEngine) together with the names of
// its capturing groups in the order of their '('.
//
// The program is made of the instructions of RegexOp, with the meaning the spec demands spelled
// out: ., classes and \d \w \s as sets of code points, $ as the end of the input, \b \B as ASCII
// word boundaries. The i flag is done here: it folds ASCII letters only (rangeSet.addAsciiCases, the
// one place of the rule). No host regex engine reads the flavor with one meaning, so none is used
// (docs/porting.md §3.3).
//
// The limits of §11 are checked here, while the pattern is read (regexMeasure), so that the engine
// never meets a pattern beyond them and takes every one within.
//
// The pattern is read in one pass, without recursion, and each instruction that jumps says how far
// (not where to), so a piece of the program can be copied or moved as it is: a quantifier wraps or
// repeats the instructions of the atom before it.
var RegexSyntax regexSyntax

const (
	// MaxRepeat is the largest count of a quantifier, and of the counts of nested quantifiers multiplied.
	MaxRepeat = 1000
	// MaxSize is the largest size of a pattern (§9.10): its items, counted repetitions written out.
	MaxSize = 10000
	// MaxNesting is the most groups open at once.
	MaxNesting = 100
	// MaxGroups is the most capturing groups.
	MaxGroups = 1000
	// TooLarge is the reason of a pattern beyond MaxSize, MaxNesting or MaxGroups: a fixed text (§10.3).
	TooLarge = "the pattern is too large"
)

// RegexOptions are the flags of §9.10 that change the pattern itself.
type RegexOptions struct {
	Extended   bool
	DotAll     bool
	IgnoreCase bool
}

// RegexOp is an instruction of a program; a and b are its operands.
type RegexOp int

const (
	// OpChar is the code point a.
	OpChar RegexOp = iota
	// OpSet is a code point of the set a of the program.
	OpSet
	// OpAny is any code point.
	OpAny
	// OpAnyButLineFeed is any code point but U+000A.
	OpAnyButLineFeed
	// OpSplit goes on a instructions further; if that fails, b instructions further.
	OpSplit
	// OpJump goes on a instructions further.
	OpJump
	// OpSave stores the position in slot a (2g: the start of group g; 2g + 1: its end; group 0 is
	// the match).
	OpSave
	// OpBegin is the start of the input.
	OpBegin
	// OpEnd is the end of the input.
	OpEnd
	// OpWordBoundary is an ASCII word boundary.
	OpWordBoundary
	// OpNotWordBoundary is not an ASCII word boundary.
	OpNotWordBoundary
	// OpMark stores the position in mark a: where an iteration of a repetition starts.
	OpMark
	// OpEmptyExit goes on b instructions further when the position is the one of mark a (an
	// iteration that matched nothing ends its repetition); else to the next instruction.
	OpEmptyExit
	// OpMatch: the pattern matched.
	OpMatch
)

// Translation is a translated pattern: the program (an instruction and its operands a, b per
// index), its sets of code points (sorted pairs low, high), the number of its marks, and one name
// ("" when unnamed) per capturing group.
type Translation struct {
	Ops    []RegexOp
	A      []int
	B      []int
	Sets   [][]int
	Marks  int
	Groups []string
}

// previous is what precedes a quantifier: only an atom may be repeated.
type previous int

const (
	previousNone previous = iota
	previousAtom
	previousQuantifier
	previousAnchor
)

// TryTranslate gives the program of a pattern, or false with why it is not of the flavor.
func (regexSyntax) TryTranslate(pattern string, options RegexOptions) (*Translation, string, bool) {
	t := &translator{points: decode(pattern), options: options, measure: newRegexMeasure()}
	if !t.translate() {
		return nil, t.reason, false
	}
	return &Translation{t.ops, t.a, t.b, t.sets, t.marks, t.groups}, "", true
}

// lastItem is whether the last item of an alternative can match nothing, or that it has no item.
type lastItem int8

const (
	lastNone lastItem = iota
	lastNullable
	lastNotNullable
)

func nullability(nullable bool) lastItem {
	if nullable {
		return lastNullable
	}
	return lastNotNullable
}

// frame is an open group, or the whole pattern: where it starts, where its current alternative
// starts, the jumps of its earlier alternatives to its end, and whether it can match nothing (for
// the loops around it): before is whether every item of the current alternative but the last can,
// last whether the last can (lastNone: no item), any whether an earlier alternative can.
type frame struct {
	start       int
	group       int
	alternative int
	jumps       []int
	last        lastItem
	any         bool
	before      bool
}

func newFrame(start, group int) *frame {
	return &frame{start: start, group: group, alternative: start, before: true}
}

func (f *frame) add(nullable bool) {
	f.before = f.before && f.last != lastNotNullable
	f.last = nullability(nullable)
}

// close ends the current alternative.
func (f *frame) close() {
	f.any = f.any || f.before && f.last != lastNotNullable
	f.before = true
	f.last = lastNone
}

type translator struct {
	points  []rune
	options RegexOptions
	ops     []RegexOp
	a       []int
	b       []int
	sets    [][]int
	groups  []string
	frames  []*frame
	measure *regexMeasure
	marks   int
	i       int
	prev    previous
	// Where the instructions of the last atom start.
	atom   int
	reason string
}

func (t *translator) translate() bool {
	points := t.points
	t.emit(OpSave, 0, 0)
	f := newFrame(len(t.ops), 0)
	t.frames = append(t.frames, f)
	for t.i < len(points) {
		c := points[t.i]
		if t.options.Extended && (isExtendedSpace(c) || c == '#') {
			t.i = skipExtended(points, t.i)
			continue
		}

		switch c {
		case '\\':
			if !t.tryEscape() {
				return false
			}
			if !t.measure.add(1) {
				return t.fail(TooLarge)
			}
		case '[':
			items, ok := t.tryClass()
			if !ok {
				return false
			}
			if !t.measure.add(items) {
				return t.fail(TooLarge)
			}
		case '.':
			t.startAtom()
			if t.options.DotAll {
				t.emit(OpAny, 0, 0)
			} else {
				t.emit(OpAnyButLineFeed, 0, 0)
			}
			if !t.measure.add(1) {
				return t.fail(TooLarge)
			}
			t.i++
		case '^':
			t.anchor(OpBegin)
			if !t.measure.add(1) {
				return t.fail(TooLarge)
			}
			t.i++
		case '$':
			t.anchor(OpEnd)
			if !t.measure.add(1) {
				return t.fail(TooLarge)
			}
			t.i++
		case '|':
			if !t.measure.add(1) {
				return t.fail(TooLarge)
			}
			// The alternative so far, or the rest: it ends with a jump to the end of the group.
			t.insert(f.alternative, OpSplit, 1, len(t.ops)-f.alternative+2)
			f.jumps = append(f.jumps, len(t.ops))
			t.emit(OpJump, 0, 0)
			f.alternative = len(t.ops)
			f.close()
			t.prev = previousNone
			t.i++
		case '(':
			if t.measure.depth() == MaxNesting {
				return t.fail(TooLarge)
			}
			start := len(t.ops)
			if !t.tryGroup() {
				return false
			}
			if !t.measure.open() {
				return t.fail(TooLarge)
			}
			group := 0
			if len(t.ops) > start {
				group = len(t.groups)
			}
			f = newFrame(start, group)
			f.alternative = len(t.ops)
			t.frames = append(t.frames, f)
			t.prev = previousNone
		case ')':
			if len(t.frames) == 1 {
				return t.fail("unmatched close parenthesis")
			}
			t.measure.close()
			t.end(f)
			if f.group > 0 {
				t.emit(OpSave, 2*f.group+1, 0)
			}
			t.frames = t.frames[:len(t.frames)-1]
			group := f
			f = t.frames[len(t.frames)-1]
			f.add(group.any)
			t.atom = group.start
			t.prev = previousAtom
			t.i++
		case '*', '+', '?', '{':
			if t.prev != previousAtom {
				if t.prev == previousQuantifier {
					return t.fail("nested quantifier")
				}
				return t.fail("quantifier without an operand")
			}
			if !t.tryQuantifier(f) {
				return false
			}
			t.prev = previousQuantifier
		case ']', '}':
			return t.fail("unescaped '" + string(c) + "'")
		default:
			t.literal(c)
			if !t.measure.add(1) {
				return t.fail(TooLarge)
			}
			t.i++
		}
	}

	if len(t.frames) > 1 {
		return t.fail("missing close parenthesis")
	}
	t.end(f)
	t.emit(OpSave, 1, 0)
	t.emit(OpMatch, 0, 0)
	return true
}

// end is the end of a group or of the pattern: the jumps of its alternatives arrive here.
func (t *translator) end(f *frame) {
	for _, jump := range f.jumps {
		t.a[jump] = len(t.ops) - jump
	}
	f.close()
}

func (t *translator) emit(op RegexOp, first, second int) {
	t.ops = append(t.ops, op)
	t.a = append(t.a, first)
	t.b = append(t.b, second)
}

func (t *translator) insert(at int, op RegexOp, first, second int) {
	t.ops = slices.Insert(t.ops, at, op)
	t.a = slices.Insert(t.a, at, first)
	t.b = slices.Insert(t.b, at, second)
}

// startAtom: an atom starts here, and a quantifier after it repeats the instructions from here on.
func (t *translator) startAtom() {
	t.atom = len(t.ops)
	t.prev = previousAtom
	t.frames[len(t.frames)-1].add(false)
}

func (t *translator) anchor(op RegexOp) {
	t.emit(op, 0, 0)
	t.prev = previousAnchor
	t.frames[len(t.frames)-1].add(true)
}

// tryEscape: \x outside a class: a literal, a shorthand class or an anchor.
func (t *translator) tryEscape() bool {
	points := t.points
	if t.i+1 == len(points) {
		return t.fail("trailing backslash")
	}
	c := points[t.i+1]
	switch c {
	case 'b', 'B':
		if c == 'b' {
			t.anchor(OpWordBoundary)
		} else {
			t.anchor(OpNotWordBoundary)
		}
		t.i += 2
		return true
	case 'd', 'w', 's', 'D', 'W', 'S':
		set := newRangeSet()
		set.addShorthand(c)
		t.startAtom()
		t.class(set, false)
		t.i += 2
		return true
	}

	literal, next, reason, ok := tryEscapedLiteral(points, t.i)
	if !ok {
		return t.fail(reason)
	}
	t.i = next
	t.literal(literal)
	return true
}

// tryGroup reads ( (?: or (?<name>: a capturing group starts with the instruction that stores its
// start.
func (t *translator) tryGroup() bool {
	points := t.points
	i := t.i
	if i+1 < len(points) && points[i+1] == '?' {
		if i+2 < len(points) && points[i+2] == ':' {
			t.i += 3
			return true
		}

		if i+2 < len(points) && points[i+2] == '<' {
			start := i + 3
			end := start
			for end < len(points) && points[end] != '>' {
				end++
			}
			if end == len(points) || !isGroupName(points, start, end) {
				return t.fail("invalid group name")
			}
			name := string(points[start:end])
			if slices.Contains(t.groups, name) {
				return t.fail("duplicate group name " + name)
			}
			if len(t.groups) == MaxGroups {
				return t.fail(TooLarge)
			}
			t.groups = append(t.groups, name)
			t.emit(OpSave, 2*len(t.groups), 0)
			t.i = end + 1
			return true
		}

		return t.fail("unsupported group syntax (?")
	}

	if len(t.groups) == MaxGroups {
		return t.fail(TooLarge)
	}
	t.groups = append(t.groups, "")
	t.emit(OpSave, 2*len(t.groups), 0)
	t.i++
	return true
}

// tryQuantifier reads * + ? {n} {n,} {n,m}, each optionally followed by ? (lazy).
func (t *translator) tryQuantifier(f *frame) bool {
	points := t.points
	c := points[t.i]
	var low int
	// -1: no maximum.
	var high int
	// The count of §9.10: how many times the operand is written out.
	count := 1
	if c == '{' {
		at, number, found := tryNumber(points, t.i+1)
		if !found {
			return t.fail("'{' is not a quantifier")
		}
		low, high = number, number
		unbounded := false
		if at < len(points) && points[at] == ',' {
			at++
			if at < len(points) && points[at] == '}' {
				unbounded = true
			} else if at, high, found = tryNumber(points, at); !found {
				return t.fail("'{' is not a quantifier")
			}
		}

		if at == len(points) || points[at] != '}' {
			return t.fail("'{' is not a quantifier")
		}
		if low > MaxRepeat || !unbounded && (high > MaxRepeat || high < low) {
			return t.fail("invalid repetition count")
		}
		if unbounded {
			count = max(low, 1)
		} else {
			count = max(high, 1)
		}
		if !t.measure.multiply(count) {
			return t.fail("invalid repetition count")
		}
		if unbounded {
			high = -1
		}
		t.i = at + 1
	} else {
		low, high = 0, -1
		if c == '+' {
			low = 1
		}
		if c == '?' {
			high = 1
		}
		t.i++
	}

	if !t.measure.repeat(count) {
		return t.fail(TooLarge)
	}

	if t.options.Extended {
		for t.i < len(points) && (isExtendedSpace(points[t.i]) || points[t.i] == '#') {
			t.i = skipExtended(points, t.i)
		}
	}

	lazy := false
	if t.i < len(points) && points[t.i] == '?' {
		lazy = true
		t.i++
	}

	return t.repeat(f, low, high, lazy)
}

// repeat repeats the last atom: its instructions low times, then a loop (high < 0: no maximum) or
// high - low optional copies. Each copy is an iteration. When the atom can match nothing and there can be
// several iterations, each marks where it starts, and one that matched nothing ends the
// repetition, whatever its counts (§9.10): "ca" | match("(c??){2}a") captures "" at 1.
func (t *translator) repeat(f *frame, low, high int, lazy bool) bool {
	start := t.atom
	length := len(t.ops) - start
	nullable := f.last != lastNotNullable
	marked := nullable && (high < 0 || high > 1)
	iteration := length
	if marked {
		iteration += 2
	}
	size := low * iteration
	if high < 0 {
		size += iteration + 2
	} else {
		size += (high - low) * (iteration + 1)
	}

	b := body{ops: slices.Clone(t.ops[start:]), a: slices.Clone(t.a[start:]), b: slices.Clone(t.b[start:]), mark: -1, end: start + size}
	if marked {
		b.mark = t.marks
		t.marks++
	}
	t.ops = t.ops[:start]
	t.a = t.a[:start]
	t.b = t.b[:start]
	for n := 0; n < low; n++ {
		t.copy(&b)
	}
	if high < 0 {
		// again: split (iteration, end); iteration; jump again; end:
		again := len(t.ops)
		if lazy {
			t.emit(OpSplit, b.end-again, 1)
		} else {
			t.emit(OpSplit, 1, b.end-again)
		}
		t.copy(&b)
		t.emit(OpJump, again-len(t.ops), 0)
	} else {
		// split (iteration, end); iteration; split (iteration, end); iteration; …; end:
		for n := high - low; n > 0; n-- {
			exit := b.end - len(t.ops)
			if lazy {
				t.emit(OpSplit, exit, 1)
			} else {
				t.emit(OpSplit, 1, exit)
			}
			t.copy(&b)
		}
	}

	f.last = nullability(low == 0 || nullable)
	return true
}

// copy writes one iteration of a repetition: the atom, between its mark and the exit of an empty
// iteration.
func (t *translator) copy(b *body) {
	if b.mark >= 0 {
		t.emit(OpMark, b.mark, 0)
	}
	for k := range b.ops {
		t.emit(b.ops[k], b.a[k], b.b[k])
	}
	if b.mark >= 0 {
		t.emit(OpEmptyExit, b.mark, b.end-len(t.ops))
	}
}

// tryClass reads [...] or [^...]: literals, escapes, ranges of literals and shorthands; items is
// their number.
func (t *translator) tryClass() (items int, ok bool) {
	points := t.points
	at := t.i + 1
	negated := at < len(points) && points[at] == '^'
	if negated {
		at++
	}
	first := at
	set := newRangeSet()
	previousLiteral := rune(-1)
	for {
		if at == len(points) {
			return 0, t.fail("missing ']'")
		}
		c := points[at]
		if c == ']' {
			break
		}
		if c == '[' {
			return 0, t.fail("unescaped '[' in a class")
		}

		if c == '-' && at != first && at+1 < len(points) && points[at+1] != ']' {
			// A range: the item before must be a single literal, the one after a literal too.
			if previousLiteral < 0 {
				return 0, t.fail("'-' must be first, last, escaped or between two literals")
			}
			at++
			high, next, reason, ok := tryClassLiteral(points, at)
			if !ok {
				return 0, t.fail(reason)
			}
			at = next
			if high < previousLiteral {
				return 0, t.fail("range out of order")
			}
			set.add(previousLiteral, high)
			previousLiteral = -1
			continue
		}

		if c == '\\' && at+1 < len(points) && isShorthand(points[at+1]) {
			set.addShorthand(points[at+1])
			previousLiteral = -1
			items++
			at += 2
			continue
		}

		literal, next, reason, ok := tryClassLiteral(points, at)
		if !ok {
			return 0, t.fail(reason)
		}
		at = next
		set.add(literal, literal)
		previousLiteral = literal
		items++
	}

	if at == first {
		return 0, t.fail("empty class")
	}
	// The i flag folds before a negation applies: [^a] matches neither a nor A.
	if t.options.IgnoreCase {
		set.addAsciiCases()
	}
	t.startAtom()
	t.class(set, negated)
	t.i = at + 1
	return items, true
}

// class writes a class as a set of the program: a negated class is its complement.
func (t *translator) class(set *rangeSet, negated bool) {
	if negated {
		set = set.complement()
	}
	ranges := set.normalize()
	pairs := make([]int, 2*len(ranges))
	for k, r := range ranges {
		pairs[2*k] = int(r.low)
		pairs[2*k+1] = int(r.high)
	}

	t.sets = append(t.sets, pairs)
	t.emit(OpSet, len(t.sets)-1, 0)
}

// literal writes a code point; under the i flag an ASCII letter becomes the class of its two cases.
func (t *translator) literal(c rune) {
	t.startAtom()
	if t.options.IgnoreCase && (c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z') {
		set := newRangeSet()
		set.add(c, c)
		set.addAsciiCases()
		t.class(set, false)
		return
	}

	t.emit(OpChar, int(c), 0)
}

func (t *translator) fail(text string) bool {
	t.reason = text
	return false
}

// body is the instructions of a repeated atom; its mark (-1 when its iterations are not marked)
// and the index where the repetition ends.
type body struct {
	ops  []RegexOp
	a    []int
	b    []int
	mark int
	end  int
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
	return m.size <= MaxSize
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
// MaxRepeat.
func (m *regexMeasure) multiply(count int) bool {
	product := count * m.atomProduct
	last := len(m.products) - 1
	m.products[last] = max(m.products[last], product)
	return product <= MaxRepeat
}

// repeat: a quantifier on the last item, which counts 1, and the item once more for each count
// above 1; false when the pattern is too large.
func (m *regexMeasure) repeat(count int) bool {
	m.size += m.atom*(count-1) + 1
	return m.size <= MaxSize
}

// tryEscapedLiteral reads the escapes that stand for one code point: the metacharacters,
// \t \n \r \f \v and \xHH; it gives the code point and the index after the escape.
func tryEscapedLiteral(points []rune, i int) (rune, int, string, bool) {
	if i+1 == len(points) {
		return 0, i, "trailing backslash", false
	}
	c := points[i+1]
	switch c {
	case '\\', '.', '*', '+', '?', '(', ')', '[', ']', '{', '}', '|', '^', '$', '/', '-':
		return c, i + 2, "", true
	case 't':
		return '\t', i + 2, "", true
	case 'n':
		return '\n', i + 2, "", true
	case 'r':
		return '\r', i + 2, "", true
	case 'f':
		return '\f', i + 2, "", true
	case 'v':
		return '\v', i + 2, "", true
	case 'x':
		high, low := -1, -1
		if i+2 < len(points) {
			high = hexValue(points[i+2])
		}
		if i+3 < len(points) {
			low = hexValue(points[i+3])
		}
		if high < 0 || low < 0 {
			return 0, i, `\x must be followed by two hex digits`, false
		}
		return rune(high*16 + low), i + 4, "", true
	}
	return 0, i, `invalid escape \` + string(c), false
}

// tryNumber reads a repetition count from at (at most MaxRepeat + 1); it gives the index after it.
func tryNumber(points []rune, at int) (int, int, bool) {
	value := 0
	start := at
	for at < len(points) && points[at] >= '0' && points[at] <= '9' {
		value = min(value*10+int(points[at]-'0'), MaxRepeat+1)
		at++
	}
	return at, value, at > start
}

func tryClassLiteral(points []rune, at int) (rune, int, string, bool) {
	literal := points[at]
	if literal == '\\' {
		return tryEscapedLiteral(points, at)
	}
	if literal == '[' || literal == ']' {
		return 0, at, "unescaped '" + string(literal) + "' in a class", false
	}
	return literal, at + 1, "", true
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

func isExtendedSpace(c rune) bool { return c >= 0x09 && c <= 0x0D || c == 0x20 }

func isShorthand(c rune) bool {
	return c == 'd' || c == 'w' || c == 's' || c == 'D' || c == 'W' || c == 'S'
}

func isGroupName(points []rune, start, end int) bool {
	if end == start || points[start] >= '0' && points[start] <= '9' {
		return false
	}
	for _, c := range points[start:end] {
		if !(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_') {
			return false
		}
	}
	return true
}

func hexValue(c rune) int {
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

// decode gives the code points of a pattern: Go strings are UTF-8, well-formed in the engine (the
// reference combines surrogate pairs here).
func decode(text string) []rune { return []rune(text) }

type codeRange struct{ low, high rune }

// rangeSet is a set of code points as sorted, disjoint, non-adjacent ranges.
type rangeSet struct {
	ranges     []codeRange
	normalized bool
}

func newRangeSet() *rangeSet { return &rangeSet{normalized: true} }

func (s *rangeSet) add(low, high rune) {
	s.ranges = append(s.ranges, codeRange{low, high})
	s.normalized = false
}

// addShorthand adds the ASCII classes of §9.10 and their complements.
func (s *rangeSet) addShorthand(c rune) {
	set := newRangeSet()
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
	for _, r := range set.normalize() {
		s.add(r.low, r.high)
	}
}

// addAsciiCases is the i flag of §9.10: adds the other case of every ASCII letter in the set (A-Z
// and a-z, 32 apart); no other code point folds.
func (s *rangeSet) addAsciiCases() {
	var added []codeRange
	for _, r := range s.normalize() {
		if upperLow, upperHigh := max(r.low, 'A'), min(r.high, 'Z'); upperLow <= upperHigh {
			added = append(added, codeRange{upperLow + 32, upperHigh + 32})
		}
		if lowerLow, lowerHigh := max(r.low, 'a'), min(r.high, 'z'); lowerLow <= lowerHigh {
			added = append(added, codeRange{lowerLow - 32, lowerHigh - 32})
		}
	}

	for _, r := range added {
		s.add(r.low, r.high)
	}
}

func (s *rangeSet) complement() *rangeSet {
	complement := newRangeSet()
	next := rune(0)
	for _, r := range s.normalize() {
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

// normalize sorts and merges the ranges, and gives them.
func (s *rangeSet) normalize() []codeRange {
	if s.normalized {
		return s.ranges
	}
	slices.SortFunc(s.ranges, func(a, b codeRange) int { return int(a.low - b.low) })
	merged := make([]codeRange, 0, len(s.ranges))
	for _, r := range s.ranges {
		if n := len(merged); n > 0 && r.low <= merged[n-1].high+1 {
			merged[n-1].high = max(merged[n-1].high, r.high)
		} else {
			merged = append(merged, r)
		}
	}

	s.ranges = merged
	s.normalized = true
	return merged
}
