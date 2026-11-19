package builtins

// The engine of §9.10: compiles a pattern through RegexSyntax and finds the next match from a
// position. It is portable code, no host regex engine: none reads the flavor with one meaning
// (docs/porting.md §3.3).
//
// The matcher reads the input once, from the start position on, and follows every way the program
// can match at the same time (a Pike machine): a thread is an instruction that waits for a code
// point, with the captures of the way that led to it. The threads are kept in the order a
// backtracking matcher would try them (a greedy quantifier takes all it can first, a lazy one as
// little, the left alternative first), and a state reached a second time at the same position is
// dropped: the first way to it has priority and the same future. So the time is the length of the
// input times the size of the program, and the memory depends on the program alone. The
// translation bounds the program (§11), so nothing is refused here: within those limits a program
// has at most 50,000 instructions, a hundred levels and 2,002 capture slots per thread. A search for
// the leftmost match does not start again at each position: a new thread starts at each position,
// behind the ones already running, until a match is found. Nothing recurses.
//
// A loop whose body can match nothing ends after an iteration that matched nothing (OpMark and
// OpEmptyExit). Whether an iteration has matched something yet belongs to the state of a thread, as
// its level: of the loops with a mark around its instruction, how many have, counted from the
// outermost (if an outer iteration has matched nothing, the inner ones it contains have not either).
//
// The input is read by code point: a match never splits the UTF-8 bytes of one, and the positions
// are byte offsets (the reference's are UTF-16 indices). Compiled programs are immutable and cached
// per process in a concurrent map that is emptied when it reaches cacheLimit entries; the memory of
// a search is a RegexMatcher of its own.

import (
	"math"
	"strings"
	"sync"
	"sync/atomic"
	"unicode/utf8"
)

type regexEngine struct{}

// RegexEngine compiles patterns to programs.
var RegexEngine regexEngine

const cacheLimit = 256

var (
	regexCache sync.Map
	// The entries of regexCache: sync.Map has no count.
	regexCacheSize atomic.Int64
)

// TryCompile gives the program of pattern under options, or false with why the pattern is not of
// the flavor or beyond its limits.
func (regexEngine) TryCompile(pattern string, options RegexOptions) (*RegexProgram, string, bool) {
	key := flag(options.Extended, "x") + flag(options.DotAll, "m") + flag(options.IgnoreCase, "i") + pattern
	if cached, found := regexCache.Load(key); found {
		return cached.(*RegexProgram), "", true
	}
	translation, reason, ok := RegexSyntax.TryTranslate(pattern, options)
	if !ok {
		return nil, reason, false
	}
	program := NewRegexProgram(translation)
	if regexCacheSize.Load() >= cacheLimit {
		regexCache.Clear()
		regexCacheSize.Store(0)
	}
	if actual, loaded := regexCache.LoadOrStore(key, program); loaded {
		return actual.(*RegexProgram), "", true
	}
	regexCacheSize.Add(1)
	return program, "", true
}

func flag(set bool, letter string) string {
	if set {
		return letter
	}
	return "-"
}

// RegexProgram is a translated pattern ready to run: immutable, shared by the searches of every
// goroutine.
type RegexProgram struct {
	// Groups has one name ("" when unnamed) per capturing group.
	Groups []string
	// Slots are the capture slots of a thread: 2 · (groups + 1).
	Slots int
	// States are the states of the program: its instructions, each at every level it can have.
	States int
	// Readers are the instructions that read a code point.
	Readers int

	Ops  []RegexOp
	A    []int
	B    []int
	Sets [][]int

	// Depth is, per instruction, the loops with a mark around it.
	Depth []int
	// Base is, per instruction, its first state (the one of level 0).
	Base []int

	// Literal is the UTF-8 text of the code point a match must start with, or "".
	Literal string
	// Anchored is whether a match can only start at the start of the input.
	Anchored bool
}

// NewRegexProgram makes the program of a translation.
func NewRegexProgram(translation *Translation) *RegexProgram {
	ops := translation.Ops
	p := &RegexProgram{
		Groups: translation.Groups,
		Slots:  2 * (len(translation.Groups) + 1),
		Ops:    ops,
		A:      translation.A,
		B:      translation.B,
		Sets:   translation.Sets,
		Depth:  make([]int, len(ops)),
		Base:   make([]int, len(ops)),
	}
	depth := 0
	states := 0
	readers := 0
	for pc, op := range ops {
		p.Depth[pc] = depth
		p.Base[pc] = states
		states += depth + 1
		if op == OpMark {
			depth++
		} else if op == OpEmptyExit {
			depth--
		} else if op <= OpAnyButLineFeed {
			readers++
		}
	}

	p.States = states
	p.Readers = readers
	// The program starts with the instruction that stores the start of the match: what follows it
	// is the first thing every match does.
	if ops[1] == OpChar {
		p.Literal = string(rune(translation.A[1]))
	}
	p.Anchored = ops[1] == OpBegin
	return p
}

// RegexMatcher is the memory of the searches of one caller over one program: one mark per state, a
// stack of at most two entries per state, and two lists of threads of at most one thread per
// instruction that reads a code point. Made at the first search and used again by the next ones;
// never shared between goroutines.
type RegexMatcher struct {
	program *RegexProgram
	// Per state: the generation that reached it last. A generation is one position of one search.
	seen       []int32
	generation int32
	// Pairs: an instruction and its level, still to follow; or -1 - slot and the value to put back.
	stack []int
	// The captures of the thread being followed.
	work    []int
	current *threads
	next    *threads
}

// NewRegexMatcher gives a matcher of program, with no memory yet.
func NewRegexMatcher(program *RegexProgram) *RegexMatcher { return &RegexMatcher{program: program} }

// Program is the program the matcher runs.
func (m *RegexMatcher) Program() *RegexProgram { return m.program }

// TryMatch finds the first match starting at or after byte offset start (anchors and word
// boundaries see the whole input). spans receives start and end of the match, then of each group
// (-1, -1 for a group that did not participate): 2 · (groups + 1) entries, byte offsets. A shorter
// spans receives the first entries only; an empty one asks only whether there is a match.
func (m *RegexMatcher) TryMatch(input string, start int, spans []int) bool {
	program := m.program
	ops := program.Ops
	a := program.A
	sets := program.Sets
	depth := program.Depth
	length := len(input)
	width := min(len(spans), program.Slots)
	if len(m.seen) == 0 {
		m.seen = make([]int32, program.States)
		m.stack = make([]int, 2*program.States)
		m.work = make([]int, program.Slots)
		capacity := min(8, program.Readers+1)
		m.current = newThreads(capacity, program.Slots)
		m.next = newThreads(capacity, program.Slots)
	}

	m.current.count = 0
	matched := false
	pos := start
	m.advance()
	for {
		// The threads at pos are in current. Until a match is found, one more starts here, behind
		// them: a match that starts earlier has priority.
		if !matched {
			if m.current.count == 0 {
				if program.Anchored && pos > 0 {
					return false
				}
				if program.Literal != "" {
					at := strings.Index(input[pos:], program.Literal)
					if at < 0 {
						return false
					}
					if at != 0 {
						pos += at
						m.advance()
					}
				}
			}

			for slot := 0; slot < width; slot++ {
				m.work[slot] = -1
			}
			if m.follow(m.current, 0, 0, input, pos, width, spans) {
				if width == 0 {
					return true
				}
				matched = true
			}
		}

		if pos >= length {
			return matched
		}
		c, size := rune(input[pos]), 1
		if c >= utf8.RuneSelf {
			c, size = utf8.DecodeRuneInString(input[pos:])
		}

		if m.current.count == 0 {
			if matched {
				return true
			}
			pos += size
			m.advance()
			continue
		}

		// Every thread reads the code point at pos: the ones that accept it go on after it.
		m.advance()
		current, next := m.current, m.next
		next.count = 0
		for i := 0; i < current.count; i++ {
			pc := current.pcs[i]
			op := ops[pc]
			if op == OpChar && rune(a[pc]) != c || op == OpSet && !inSet(sets[a[pc]], c) || op == OpAnyButLineFeed && c == '\n' {
				continue
			}
			copy(m.work[:width], current.captures[i*program.Slots:])
			// It has read a code point: every loop around it has matched something.
			if m.follow(next, pc+1, depth[pc+1], input, pos+size, width, spans) {
				if width == 0 {
					return true
				}
				// The threads behind this one have a lower priority than the match.
				matched = true
				break
			}
		}

		pos += size
		m.current, m.next = next, current
	}
}

// advance moves to the next position of the search: no state has been reached at it yet.
func (m *RegexMatcher) advance() {
	if m.generation == math.MaxInt32 {
		clear(m.seen)
		m.generation = 0
	}

	m.generation++
}

// follow follows a thread from an instruction at pos, with the captures in work, through the
// instructions that read nothing: each reading instruction it reaches first becomes a thread of
// threads, in priority order. True when it reached the end of the program: the match is then in
// spans, and what it had still to follow has a lower priority.
func (m *RegexMatcher) follow(threads *threads, pc, level int, input string, pos, width int, spans []int) bool {
	program := m.program
	ops := program.Ops
	a := program.A
	b := program.B
	depth := program.Depth
	states := program.Base
	slots := program.Slots
	seen := m.seen
	stack := m.stack
	work := m.work
	generation := m.generation
	top := 0
	for {
		op := ops[pc]
		// What an instruction that reads does next does not depend on the level.
		state := states[pc]
		if op > OpAnyButLineFeed {
			state += level
		}
		alive := seen[state] != generation
		if alive {
			seen[state] = generation
			switch op {
			case OpChar, OpSet, OpAny, OpAnyButLineFeed:
				index := threads.count
				if index == len(threads.pcs) {
					threads.grow()
				}
				threads.pcs[index] = pc
				copy(threads.captures[index*slots:index*slots+width], work[:width])
				threads.count = index + 1
				alive = false
			case OpSplit:
				stack[top] = pc + b[pc]
				stack[top+1] = level
				top += 2
				pc += a[pc]
			case OpJump:
				pc += a[pc]
			case OpSave:
				if slot := a[pc]; slot < width {
					stack[top] = -1 - slot
					stack[top+1] = work[slot]
					top += 2
					work[slot] = pos
				}
				pc++
			case OpBegin:
				if pos == 0 {
					pc++
				} else {
					alive = false
				}
			case OpEnd:
				if pos == len(input) {
					pc++
				} else {
					alive = false
				}
			case OpWordBoundary, OpNotWordBoundary:
				boundary := (pos > 0 && isWord(input[pos-1])) != (pos < len(input) && isWord(input[pos]))
				if boundary == (op == OpWordBoundary) {
					pc++
				} else {
					alive = false
				}
			case OpMark:
				// An iteration starts: it has matched nothing yet, so the level stays.
				pc++
			case OpEmptyExit:
				// The iteration of the innermost loop ends: it matched something if the level counts it.
				inner := depth[pc]
				if level < inner {
					pc += b[pc]
				} else {
					pc++
				}
				if level == inner {
					level = inner - 1
				}
			default:
				copy(spans[:width], work[:width])
				return true
			}
		}

		if alive {
			continue
		}

		// This way ended: back to the last one left aside, putting back the captures stored since.
		for {
			if top == 0 {
				return false
			}
			value := stack[top-1]
			entry := stack[top-2]
			top -= 2
			if entry >= 0 {
				pc = entry
				level = value
				break
			}

			work[-1-entry] = value
		}
	}
}

// isWord tells the ASCII word characters of §9.10: a byte of the UTF-8 of a code point above U+007F
// is not one.
func isWord(unit byte) bool {
	return unit >= '0' && unit <= '9' || unit >= 'A' && unit <= 'Z' || unit >= 'a' && unit <= 'z' || unit == '_'
}

// inSet tells whether a code point is in a set: sorted pairs low, high.
func inSet(ranges []int, c rune) bool {
	code := int(c)
	if len(ranges) <= 8 {
		for i := 0; i < len(ranges); i += 2 {
			if code < ranges[i] {
				return false
			}
			if code <= ranges[i+1] {
				return true
			}
		}

		return false
	}

	low := 0
	high := len(ranges)/2 - 1
	for low <= high {
		middle := (low + high) >> 1
		if code < ranges[2*middle] {
			high = middle - 1
		} else if code > ranges[2*middle+1] {
			low = middle + 1
		} else {
			return true
		}
	}

	return false
}

// threads are the threads at one position, in priority order: the instruction of each and its
// captures.
type threads struct {
	pcs      []int
	captures []int
	count    int
	slots    int
}

func newThreads(capacity, slots int) *threads {
	return &threads{pcs: make([]int, capacity), captures: make([]int, capacity*slots), slots: slots}
}

// grow makes room for one more thread: there are never more than the program has reading
// instructions.
func (t *threads) grow() {
	pcs := make([]int, len(t.pcs)*2)
	copy(pcs, t.pcs)
	t.pcs = pcs
	captures := make([]int, len(pcs)*t.slots)
	copy(captures, t.captures)
	t.captures = captures
}
