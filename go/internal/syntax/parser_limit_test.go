package syntax

import (
	"fmt"
	"strings"
	"testing"
)

// The nesting limit of spec §11, and programs that must not exhaust the host stack. The reference
// parses on a thread with a 1 MB stack; a goroutine's stack grows, so these run on the test's own
// goroutine and the check is that the parser keeps to its limit and does not recurse per element.

const limit = MaxNesting

func parses(t *testing.T, source string) *Program {
	t.Helper()
	program, err := TryParse(source)
	if err != nil {
		t.Fatalf("%s at %d:%d", err.Message, err.Line, err.Column)
	}
	return program
}

func fails(t *testing.T, source string) string {
	t.Helper()
	_, err := TryParse(source)
	if err == nil {
		t.Fatal("parses")
	}
	return fmt.Sprintf("%s @%d:%d", err.Message, err.Line, err.Column)
}

// name, opening repeated, closing repeated (the innermost term is "1"), offset in the opening of
// the token that opens the level.
var nestings = []struct {
	name, open, close string
	offset            int
}{
	{"parentheses", "(", ")", 0},
	{"arrays", "[", "]", 0},
	{"objects", "{a:", "}", 0},
	{"indexes", ".[", "]", 1},
	{"calls", "f(", ")", 1},
	{"interpolations", "\"\\(", ")\"", 0},
	{"if", "if . then ", " end", 0},
	{"try", "try ", "", 0},
	{"reduce", "reduce . as $x (", ";.)", 0},
	{"foreach", "foreach . as $x (", ";.)", 0},
	{"definitions", "def f: ", "; f", 0},
	{"object keys", "{(", "):1}", 0},
}

// levels: "{(" opens two levels (the object and the parentheses).
func levels(open string) int {
	if open == "{(" {
		return limit / 2
	}
	return limit
}

// innermost: a parenthesized key must not be a number.
func innermost(open string) string {
	if open == "{(" {
		return "\"a\""
	}
	return "1"
}

func TestNestingUpToTheLimitParses(t *testing.T) {
	for _, n := range nestings {
		count := levels(n.open)
		parses(t, strings.Repeat(n.open, count)+innermost(n.open)+strings.Repeat(n.close, count))
	}
}

func TestNestingBeyondTheLimitFailsAtTheOpeningToken(t *testing.T) {
	for _, n := range nestings {
		count := levels(n.open) + 1
		source := strings.Repeat(n.open, count) + innermost(n.open) + strings.Repeat(n.close, count)
		// The level beyond the limit is opened by the token at offset in the last opening.
		column := (count-1)*len(n.open) + n.offset + 1
		same(t, fmt.Sprintf("Program too deeply nested @1:%d", column), fails(t, source))
	}
}

func TestNestedPatternsCountToo(t *testing.T) {
	parses(t, ". as "+strings.Repeat("[", limit)+"$x"+strings.Repeat("]", limit)+" | $x")
	same(t, fmt.Sprintf("Program too deeply nested @1:%d", limit+6),
		fails(t, ". as "+strings.Repeat("[", limit+1)+"$x"+strings.Repeat("]", limit+1)+" | $x"))
}

func TestNestedMetadataCountsToo(t *testing.T) {
	same(t, fmt.Sprintf("Program too deeply nested @1:%d", limit+11),
		fails(t, "module {a: "+strings.Repeat("[", limit)+strings.Repeat("]", limit)+"}; 1"))
	parses(t, "module {a: "+strings.Repeat("[", limit-1)+strings.Repeat("]", limit-1)+"}; 1")
}

func TestMixedNestingCountsEveryKind(t *testing.T) {
	same(t, fmt.Sprintf("Program too deeply nested @1:%d", limit/2*2+1),
		fails(t, strings.Repeat("([", limit/2+1)+"1"+strings.Repeat("])", limit/2+1)))
}

// Sequences of any length parse without recursing per element: prefix, 10,000 elements, suffix.
func TestLongSequencesDoNotRecurse(t *testing.T) {
	for _, c := range [][3]string{
		{"", "1 + ", "1"},
		{"", "1 * ", "1"},
		{"", "1 // ", "1"},
		{"", "1 and ", "1"},
		{"", "1 == 1 or ", "1"},
		{"", "1 | ", "1"},
		{"", "1, ", "1"},
		{"", "1 as $x | ", "$x"},
		{"", "label $x | ", "1"},
		{"", "def f: 1; ", "f"},
		{"(", "def f: 1; ", "f)"},
		{"[1, ", "def f: 1; ", "f]"},
		{"", "-", "1"},
		{"", "1 + -", "1"},
		{"", ".a", ""},
		{"", ".[0]", ""},
		{"", ".a?", ""},
		{". as $a", " ?// $a", " | 1"},
		{"{a: ", "1 | ", "1}"},
		{"{", "a: 1, ", "}"},
		{"\"", "\\(1)", "\""},
		{"f(", "1; ", "1)"},
	} {
		parses(t, c[0]+strings.Repeat(c[1], 10_000)+c[2])
	}
}

func TestLongElifChainsDoNotRecurse(t *testing.T) {
	parses(t, "if . then 1 "+strings.Repeat("elif . then 1 ", 10_000)+"else 2 end")
}

func TestALongChainKeepsItsAssociativity(t *testing.T) {
	program := parses(t, strings.Repeat("1 - ", 10_000)+"1")
	// Left-associative: the left spine is 10,000 subtractions deep.
	depth := 0
	node := program.Body
	for {
		binary, ok := node.(*Binary)
		if !ok {
			break
		}
		if _, ok := binary.Right.(*Literal); !ok {
			t.Fatal("right is not a literal")
		}
		node = binary.Left
		depth++
	}
	if depth != 10_000 {
		t.Errorf("depth %d", depth)
	}

	program = parses(t, strings.Repeat("1 // ", 10_000)+"1")
	depth = 0
	node = program.Body
	for {
		alternative, ok := node.(*Alternative)
		if !ok {
			break
		}
		if _, ok := alternative.Left.(*Literal); !ok {
			t.Fatal("left is not a literal")
		}
		node = alternative.Right
		depth++
	}
	if depth != 10_000 {
		t.Errorf("depth %d", depth)
	}
}

func TestALongPipeOfBindingsNestsToTheRight(t *testing.T) {
	program := parses(t, strings.Repeat("1 as $x | ", 10_000)+"$x")
	depth := 0
	node := program.Body
	for {
		bind, ok := node.(*Bind)
		if !ok {
			break
		}
		node = bind.Body
		depth++
	}
	if _, ok := node.(*Variable); depth != 10_000 || !ok {
		t.Errorf("depth %d", depth)
	}
}

func TestManyTopLevelDefinitionsAreAList(t *testing.T) {
	program := parses(t, strings.Repeat("def f: 1; ", 10_000)+"f")
	if _, ok := program.Body.(*Call); len(program.Definitions) != 10_000 || !ok {
		t.Errorf("%d definitions", len(program.Definitions))
	}
}
