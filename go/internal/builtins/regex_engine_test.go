package builtins

import (
	"fmt"
	"slices"
	"strings"
	"testing"
	"time"
	"unicode/utf8"

	"github.com/devlooped/jqlite/go/internal/binding"
	"github.com/devlooped/jqlite/go/internal/runtime"
	"github.com/devlooped/jqlite/go/internal/values"
)

// The regex engine: what it finds is what the simplest matcher of the same programs finds
// (BacktrackingMatcher), in time linear in the input and with memory the size of the pattern. The
// corpus shows what the programs mean (spec §9.10).

var plain = RegexOptions{}

// evaluateLine gives the outputs of a program on an input, separated by ", ", then "error: <value>"
// when the evaluation fails.
func evaluateLine(t testing.TB, program, input string) string {
	t.Helper()
	bound, err := binding.TryCompile(program, true, nil)
	if err != nil {
		t.Fatalf("'%s': %s at %d:%d", program, err.Message, err.Line, err.Column)
	}
	value, _, ok := values.ValueText.TryParse(input)
	if !ok {
		t.Fatalf("bad JSON %s", input)
	}
	var outputs []string
	outcome := runtime.Run(bound, value, runtime.NewEvaluationOptions(), func(v values.Value) bool {
		outputs = append(outputs, values.ValueText.ToJson(v))
		return true
	})
	if outcome.Kind == runtime.OutcomeError {
		outputs = append(outputs, "error: "+values.ValueText.ToJson(outcome.Value))
	}
	return strings.Join(outputs, ", ")
}

func TestRandomPatternsMatchAsTheBacktrackingMatcherDoes(t *testing.T) {
	for _, seed := range []uint32{1, 7, 12345} {
		t.Run(fmt.Sprint(seed), func(t *testing.T) {
			fuzz := newRegexFuzz(seed)
			compared := 0
			for n := 0; n < 20000; n++ {
				source := fuzz.Pattern(3)
				options := plain
				options.DotAll = fuzz.Next(2) == 0
				translation, reason, ok := RegexSyntax.TryTranslate(source, options)
				if !ok {
					t.Fatalf("%s: %s", source, reason)
				}
				matcher := NewRegexMatcher(NewRegexProgram(translation))
				width := 2 * (len(translation.Groups) + 1)
				for k := 0; k < 4; k++ {
					text := fuzz.Input(7)
					for start := 0; start <= len(text); start++ {
						// A search starts between code points.
						if start < len(text) && !utf8.RuneStart(text[start]) {
							continue
						}
						expected := make([]int, width)
						found, decided := BacktrackingMatcher.TryMatch(translation, text, start, expected, 200000)
						if !decided {
							continue
						}
						where := fmt.Sprintf("%s on %s from %d", source, text, start)
						spans := make([]int, width)
						if matcher.TryMatch(text, start, spans) != found {
							t.Fatal(where)
						}
						if found && !slices.Equal(expected, spans) {
							t.Fatalf("%s: %v, expected %v", where, spans, expected)
						}
						// Only the match; only whether there is one.
						match := make([]int, 2)
						if matcher.TryMatch(text, start, match) != found || found && !slices.Equal(expected[:2], match) {
							t.Fatal(where)
						}
						if matcher.TryMatch(text, start, nil) != found {
							t.Fatal(where)
						}
						compared++
					}
				}
			}

			t.Logf("%d searches compared", compared)
			if compared <= 300000 {
				t.Errorf("compared %d", compared)
			}
		})
	}
}

// Patterns a backtracking matcher takes exponential or quadratic time for, and long inputs.
func TestLinearTime(t *testing.T) {
	cases := []struct{ program, expected string }{
		{`"a" * 28 + "c" | test("^(a*)*b$")`, "false"},
		{`"a" * 5000 + "c" | test("^(a*)*b$")`, "false"},
		{`"ab" * 2000000 | test("^(a|b)*$")`, "true"},
		{`"a" * 200000 | test("a*b")`, "false"},
		{`"a" * 200000 | [match("a*b")] | length`, "0"},
		{`"x" * 100000 | test("(x+x+)+y")`, "false"},
		{`"a" * 40 | test("(a?){40}a{40}")`, "true"},
		{`"ab" * 2000000 | match("^(a|b)*$") | .captures[0].offset`, "3999999"},
		{`"a" * 100000 | [match("(a|aa)*?$"; "g") | .length]`, "[100000,0]"},
	}
	for _, c := range cases {
		started := time.Now()
		if actual := evaluateLine(t, c.program, "null"); actual != c.expected {
			t.Errorf("%s: expected %s, got %s", c.program, c.expected, actual)
		}
		elapsed := time.Since(started)
		t.Logf("%s: %v", c.program, elapsed)
		if elapsed >= 2*time.Second {
			t.Errorf("%s: %v", c.program, elapsed)
		}
	}
}

func TestTheMemoryOfASearchIsTheSizeOfThePattern(t *testing.T) {
	// The threads are kept in slices that grow with their number: after a long input they hold no
	// more than one thread per instruction that reads a code point.
	program, _, ok := RegexEngine.TryCompile("^(?:(a)|(b)|[ab]c?)*$", plain)
	if !ok || program.Readers != 4 || program.Slots != 6 {
		t.Fatalf("compiled %v, readers %d, slots %d", ok, program.Readers, program.Slots)
	}
	matcher := NewRegexMatcher(program)
	spans := make([]int, 6)
	if !matcher.TryMatch(strings.Repeat("ab", 500000), 0, spans) {
		t.Fatal("no match")
	}
	if expected := []int{0, 1000000, 999998, 999999, 999999, 1000000}; !slices.Equal(expected, spans) {
		t.Errorf("spans %v, expected %v", spans, expected)
	}
	if pcs := len(matcher.current.pcs); pcs > 8 || len(matcher.current.captures) != pcs*6 {
		t.Errorf("%d threads, %d captures", pcs, len(matcher.current.captures))
	}
	if len(matcher.seen) != program.States || len(matcher.stack) != 2*program.States {
		t.Errorf("seen %d, stack %d, states %d", len(matcher.seen), len(matcher.stack), program.States)
	}
}

func TestTheLargestPatternsOfSpec11AreProgramsTheMatcherRuns(t *testing.T) {
	// The limits are the translation's, and the corpus has their boundaries. What is the engine's:
	// within them a program has at most five instructions per unit of the pattern's size, and a
	// state is an instruction at a level (nested loops that can match nothing have levels).
	compiled := func(source string) *RegexProgram {
		t.Helper()
		program, reason, ok := RegexEngine.TryCompile(source, plain)
		if !ok {
			t.Fatalf("%s: %s", source, reason)
		}
		return program
	}
	nested := func(levels int, inner string) string {
		return strings.Repeat("(?:", levels) + inner + strings.Repeat(")*", levels)
	}

	if MaxSize != 10000 || MaxNesting != 100 || MaxGroups != 1000 {
		t.Fatalf("limits %d, %d, %d", MaxSize, MaxNesting, MaxGroups)
	}
	// The most instructions for a size: an empty group repeated, each copy optional and with its mark.
	copies, reason, ok := RegexSyntax.TryTranslate(strings.Repeat("(){0,1000}", 9)+"(){0,990}", plain)
	if !ok {
		t.Fatal(reason)
	}
	if len(copies.Ops) != 5*(MaxSize-10)+3 {
		t.Errorf("%d instructions", len(copies.Ops))
	}
	if !NewRegexMatcher(NewRegexProgram(copies)).TryMatch("a", 0, make([]int, 2)) {
		t.Error("copies: no match")
	}
	// The most states: every instruction inside a hundred loops that can match nothing.
	deep := compiled(nested(MaxNesting, strings.Repeat("a?", 4900)))
	if deep.States < 1_000_001 || deep.States > 1_999_999 {
		t.Errorf("%d states", deep.States)
	}
	if !NewRegexMatcher(deep).TryMatch(strings.Repeat("a", 100)+"b", 0, make([]int, 2)) {
		t.Error("deep: no match")
	}
	if actual := evaluateLine(t, `"a" * 300 | [match("`+nested(MaxNesting, "a*")+`"; "g") | .length]`, "null"); actual != "[300,0]" {
		t.Errorf("nested loops: %s", actual)
	}
	// The most capture slots: a thousand groups, each thread with its own.
	groups := compiled(strings.Repeat("(a)", MaxGroups))
	if groups.Slots != 2*(MaxGroups+1) {
		t.Errorf("%d slots", groups.Slots)
	}
	spans := make([]int, groups.Slots)
	if !NewRegexMatcher(groups).TryMatch(strings.Repeat("a", MaxGroups+5), 0, spans) {
		t.Fatal("groups: no match")
	}
	if actual, expected := append(slices.Clone(spans[:4]), spans[len(spans)-2:]...), []int{0, 1000, 0, 1, 999, 1000}; !slices.Equal(actual, expected) {
		t.Errorf("spans %v, expected %v", actual, expected)
	}
	// Past a limit the translation fails, with the fixed reason.
	if _, failure, ok := RegexEngine.TryCompile(strings.Repeat("(a)", MaxGroups+1), plain); ok || failure != "the pattern is too large" {
		t.Errorf("compiled %v, reason %q", ok, failure)
	}
}
