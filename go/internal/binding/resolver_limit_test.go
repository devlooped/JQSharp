package binding

import (
	"strings"
	"testing"

	"github.com/devlooped/jqlite/go/internal/syntax"
)

// Binding does not recurse on what the parser accepts: nesting up to the limit of spec §11, and
// sequences of any length, which the parser builds as deep spines. The reference binds on a 1 MB
// thread; goroutine stacks grow, so these check that binding works at those sizes.

func binds(t *testing.T, source string) *BoundProgram {
	t.Helper()
	program, err := TryCompile(source, true, nil)
	if err != nil {
		t.Fatalf("%s at %d:%d", err.Message, err.Line, err.Column)
	}
	return program
}

func TestNestingUpToTheLimitBinds(t *testing.T) {
	for _, c := range limitNestingUpToTheLimitBinds {
		levels := syntax.MaxNesting
		if strings.Contains(c[0], "{(") {
			levels /= 2
		}
		binds(t, "def f: 1; def f(g): 1; "+strings.Repeat(c[0], levels)+"1"+strings.Repeat(c[1], levels))
	}
}

func TestBindingLongSequencesDoesNotRecurse(t *testing.T) {
	for _, c := range limitLongSequencesDoNotRecurse {
		binds(t, c[0]+strings.Repeat(c[1], 10_000)+c[2])
	}
}

func TestLongElifChainsBind(t *testing.T) {
	binds(t, "if . then 1 "+strings.Repeat("elif . then 1 ", 10_000)+"else 2 end")
}

func TestAVariableTenThousandFramesOut(t *testing.T) {
	program := binds(t, "0 as $a | "+strings.Repeat("1 as $x | ", 10_000)+"$a")
	node := program.Body
	depth := 0
	for {
		bind, ok := node.(*Bind)
		if !ok {
			break
		}
		node = bind.Body
		depth++
	}
	variable, ok := node.(*Variable)
	if depth != 10_001 || !ok || variable.Hops != 10_000 || variable.Slot != 0 {
		t.Errorf("depth %d, %v", depth, node)
	}
}

func TestManyDefinitionsInARow(t *testing.T) {
	program := binds(t, strings.Repeat("def f: 1; ", 10_000)+"f")
	call := program.Body.(*CallFunction)
	if len(program.Functions) != preludeCount()+10_000 || call.Function != len(program.Functions)-1 || call.Hops != 0 {
		t.Error("wrong functions or call")
	}
}

func TestManyParametersAndArguments(t *testing.T) {
	program := binds(t, "def f("+strings.Repeat("a; ", 10_000)+"b): b; f("+strings.Repeat("1; ", 10_000)+"1)")
	parameter := program.Functions[len(program.Functions)-1].Body.(*CallParameter)
	if len(program.Body.(*CallFunction).Arguments) != 10_001 || parameter.Hops != 0 || parameter.Slot != 10_000 {
		t.Error("wrong call or parameter")
	}
}

// The tables below are the [InlineData] rows of the reference's ResolverLimitTests.

var limitNestingUpToTheLimitBinds = [][]string{
	{`(`, `)`},
	{`[`, `]`},
	{`{a: `, `}`},
	{`{("a" + `, `): 1}`},
	{`.[`, `]`},
	{`f(`, `)`},
	{`"\(`, `)"`},
	{`if . then `, ` end`},
	{`try `, ``},
	{`reduce . as $x (`, `;.)`},
	{`foreach . as $x (`, `;.)`},
	{`def f: `, `; f`},
	{`def f(g): `, `; f(.)`},
	{`[1 + `, `]`},
	{`[1, 2 as $x | `, `]`},
	{`(1 | `, `)`},
	{`.[1, `, `]`},
	{`{a: 1 | 1 + `, `}`},
	{`if 1, 1 as $x | `, ` then . end`},
	{`try -`, ``},
	{`f(label $x | `, `)`},
	{`. as [$x] ?// {("a" + `, `): $y} | 1`},
}

var limitLongSequencesDoNotRecurse = [][]string{
	{``, `1 + `, `1`},
	{``, `1 * `, `1`},
	{``, `1 // `, `1`},
	{``, `1 and `, `1`},
	{``, `1 == 1 or `, `1`},
	{``, `1 | `, `1`},
	{``, `1, `, `1`},
	{``, `1 as $x | `, `$x`},
	{``, `label $x | `, `break $x`},
	{``, `def f: 1; `, `f`},
	{`(`, `def f: 1; `, `f)`},
	{`[1, `, `def f: 1; `, `f]`},
	{``, `-`, `1`},
	{``, `1 + -`, `1`},
	{``, `.a`, ``},
	{``, `.[0]`, ``},
	{``, `.a?`, ``},
	{`. as $a`, ` ?// $a`, ` | 1`},
	{`{a: `, `1 | `, `1}`},
	{`{`, `a: 1, `, `}`},
	{`"`, `\(1)`, `"`},
	{`[`, `1, 1 as $x | `, `$x]`},
	{``, `1, def f: 1; `, `f`},
	{``, `1, label $x | `, `1`},
	{``, `1 // 1 | `, `1`},
	{``, `1 as $x | label $y | def f: $x; `, `f`},
	{``, `1 | 1, 1 | `, `1`},
	{``, `def f(g): g; `, `f(.)`},
	{``, `reduce . as $x (.; .) | `, `1`},
}
