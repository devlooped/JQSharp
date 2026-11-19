package builtins

import "unicode/utf8"

// backtrackingMatcher is what the programs of RegexSyntax mean, as the simplest machine that runs
// them: a backtracking matcher that tries each start position in turn and each alternative in
// priority order, with the positions of the marks as they are stored. It can take time exponential
// in the input, so it is not the engine; the tests of regex_engine_test.go compare the engine with
// it on small patterns and inputs, and give up on a search that takes it more than a budget of
// steps.
type backtrackingMatcher struct{}

var BacktrackingMatcher backtrackingMatcher

// What the stack holds, three numbers an entry: an alternative to try (its instruction and
// position), or the value of a slot or of a mark to put back when going back past it.
const (
	backtrackAlternative = iota
	backtrackSlot
	backtrackMark
)

// TryMatch finds the first match starting at or after byte offset start: its spans, as
// RegexMatcher.TryMatch gives them. The second result is false when the search is not over after
// budget instructions.
func (backtrackingMatcher) TryMatch(program *Translation, input string, start int, spans []int, budget int) (found, decided bool) {
	steps := budget
	for at := start; ; {
		if backtrackRun(program, input, at, spans, &steps) {
			return true, true
		}
		if steps < 0 {
			return false, false
		}
		if at == len(input) {
			return false, true
		}
		_, size := utf8.DecodeRuneInString(input[at:])
		at += size
	}
}

// backtrackRun tells whether the program matches from at; the spans are then in spans.
func backtrackRun(program *Translation, input string, at int, spans []int, steps *int) bool {
	ops := program.Ops
	marks := make([]int, program.Marks)
	for i := range marks {
		marks[i] = -1
	}
	for i := range spans {
		spans[i] = -1
	}
	var stack []int
	pc := 0
	pos := at
	for {
		*steps--
		if *steps < 0 {
			return false
		}
		ok := true
		a := program.A[pc]
		switch op := ops[pc]; op {
		case OpChar, OpSet, OpAny, OpAnyButLineFeed:
			c, size := rune(-1), 0
			if pos < len(input) {
				c, size = utf8.DecodeRuneInString(input[pos:])
			}
			if c >= 0 && (op == OpChar && int(c) == a || op == OpSet && backtrackInSet(program.Sets[a], c) || op == OpAny || op == OpAnyButLineFeed && c != '\n') {
				pos += size
				pc++
			} else {
				ok = false
			}
		case OpSplit:
			stack = append(stack, backtrackAlternative, pc+program.B[pc], pos)
			pc += a
		case OpJump:
			pc += a
		case OpSave:
			stack = append(stack, backtrackSlot, a, spans[a])
			spans[a] = pos
			pc++
		case OpBegin:
			ok = pos == 0
			pc++
		case OpEnd:
			ok = pos == len(input)
			pc++
		case OpWordBoundary, OpNotWordBoundary:
			boundary := (pos > 0 && isWord(input[pos-1])) != (pos < len(input) && isWord(input[pos]))
			ok = boundary == (op == OpWordBoundary)
			pc++
		case OpMark:
			stack = append(stack, backtrackMark, a, marks[a])
			marks[a] = pos
			pc++
		case OpEmptyExit:
			if pos == marks[a] {
				pc += program.B[pc]
			} else {
				pc++
			}
		default:
			return true
		}

		if ok {
			continue
		}

		// Back to the last alternative, undoing what was stored since.
		for {
			if len(stack) == 0 {
				return false
			}
			n := len(stack)
			kind, index, value := stack[n-3], stack[n-2], stack[n-1]
			stack = stack[:n-3]
			if kind == backtrackAlternative {
				pc = index
				pos = value
				break
			}

			if kind == backtrackSlot {
				spans[index] = value
			} else {
				marks[index] = value
			}
		}
	}
}

func backtrackInSet(ranges []int, c rune) bool {
	for i := 0; i < len(ranges); i += 2 {
		if int(c) >= ranges[i] && int(c) <= ranges[i+1] {
			return true
		}
	}
	return false
}
