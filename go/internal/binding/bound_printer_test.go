package binding

import (
	"fmt"
	"strings"
	"testing"

	"github.com/devlooped/jqlite/go/internal/values"
)

// The bound printer of the reference's tests: a bound program as an S-expression stating what
// every reference resolved to, with $h.s a variable (slot s of the frame h frames out), $$name a
// host variable, $ENV; name/n#i^h a call of the program's function i (0 = its first own function)
// defined h frames out, prelude:name/n^h a call of a prelude builtin, native:name/n a native,
// param^h.s a closure parameter; (break ^h); in patterns $s stores to slot s and $_ discards. The
// program's own functions come first as (def name/n#i body).

func preludeCount() int { return len(PreludeInstance().Functions) }

// bindText parses and binds a program and prints its own functions and its body.
func bindText(t *testing.T, source string) string {
	t.Helper()
	program, err := TryCompile(source, true, nil)
	if err != nil {
		t.Fatalf("'%s' does not bind: %s at %d:%d", source, err.Message, err.Line, err.Column)
	}
	return printBound(program)
}

// bindError binds a program that must fail and gives "message @line:column".
func bindError(t *testing.T, source string) string {
	t.Helper()
	program, err := TryCompile(source, true, nil)
	if err == nil {
		t.Fatalf("'%s' binds as %s", source, printBound(program))
	}
	return fmt.Sprintf("%s @%d:%d", err.Message, err.Line, err.Column)
}

func printBound(program *BoundProgram) string {
	var parts []string
	for i := preludeCount(); i < len(program.Functions); i++ {
		parts = append(parts, "(def "+functionName(program, i)+" "+printBoundNode(program, program.Functions[i].Body)+")")
	}
	parts = append(parts, printBoundNode(program, program.Body))
	return strings.Join(parts, " ")
}

func functionName(program *BoundProgram, index int) string {
	function := program.Functions[index]
	if index < preludeCount() {
		return "prelude:" + function.String()
	}
	return fmt.Sprintf("%s#%d", function, index-preludeCount())
}

func printBoundNode(program *BoundProgram, node Node) string {
	p := func(child Node) string { return printBoundNode(program, child) }
	all := func(children []Node) string {
		var builder strings.Builder
		for _, child := range children {
			builder.WriteString(" " + p(child))
		}
		return builder.String()
	}
	if node == nil {
		return "_"
	}
	switch n := node.(type) {
	case *Identity:
		return "."
	case *Literal:
		return values.ValueText.ToJson(n.Value)
	case *Template:
		var builder strings.Builder
		builder.WriteString("(str")
		if n.HasFormat {
			builder.WriteString(" @" + n.Format)
		}
		for i, part := range n.Parts {
			builder.WriteString(" " + values.ValueText.ToJson(values.FromString(n.Texts[i])) + " " + p(part))
		}
		builder.WriteString(" " + values.ValueText.ToJson(values.FromString(n.Texts[len(n.Texts)-1])) + ")")
		return builder.String()
	case *Format:
		return "@" + n.Name
	case *Index:
		return "(index " + p(n.Target) + " " + p(n.Key) + ")"
	case *Slice:
		return "(slice " + p(n.Target) + " " + p(n.From) + " " + p(n.To) + ")"
	case *Iterate:
		return "(iter " + p(n.Target) + ")"
	case *Variable:
		return fmt.Sprintf("$%d.%d", n.Hops, n.Slot)
	case *HostVariable:
		return "$$" + n.Name
	case *EnvVariable:
		return "$ENV"
	case *CallFunction:
		if len(n.Arguments) == 0 {
			return fmt.Sprintf("%s^%d", functionName(program, n.Function), n.Hops)
		}
		return fmt.Sprintf("(%s^%d%s)", functionName(program, n.Function), n.Hops, all(n.Arguments))
	case *CallParameter:
		return fmt.Sprintf("param^%d.%d", n.Hops, n.Slot)
	case *CallNative:
		if len(n.Arguments) == 0 {
			return "native:" + n.Native.String()
		}
		return "(native:" + n.Native.String() + all(n.Arguments) + ")"
	case *Pipe:
		return "(| " + p(n.Left) + " " + p(n.Right) + ")"
	case *Comma:
		return "(," + all(n.Items) + ")"
	case *Binary:
		return "(" + [...]string{"+", "-", "*", "/", "%", "==", "!=", "<", "<=", ">", ">="}[n.Op] + " " + p(n.Left) + " " + p(n.Right) + ")"
	case *And:
		return "(and " + p(n.Left) + " " + p(n.Right) + ")"
	case *Or:
		return "(or " + p(n.Left) + " " + p(n.Right) + ")"
	case *Alternative:
		return "(// " + p(n.Left) + " " + p(n.Right) + ")"
	case *Negate:
		return "(neg " + p(n.Operand) + ")"
	case *ArrayExpr:
		if n.Body == nil {
			return "(array)"
		}
		return "(array " + p(n.Body) + ")"
	case *ObjectExpr:
		if len(n.Entries) == 0 {
			return "(object)"
		}
		var entries []string
		for _, entry := range n.Entries {
			entries = append(entries, "("+p(entry.Key)+" "+p(entry.Value)+")")
		}
		return "(object " + strings.Join(entries, " ") + ")"
	case *If:
		return "(if " + p(n.Condition) + " " + p(n.Then) + " " + p(n.Else) + ")"
	case *Try:
		if n.Handler == nil {
			return "(try " + p(n.Body) + ")"
		}
		return "(try " + p(n.Body) + " " + p(n.Handler) + ")"
	case *Reduce:
		return "(reduce " + p(n.Source) + " " + printBinder(n.Binder, p) + " " + p(n.Init) + " " + p(n.Update) + ")"
	case *Foreach:
		return "(foreach " + p(n.Source) + " " + printBinder(n.Binder, p) + " " + p(n.Init) + " " + p(n.Update) + " " + p(n.Extract) + ")"
	case *Bind:
		return "(as " + p(n.Source) + " " + printBinder(n.Binder, p) + " " + p(n.Body) + ")"
	case *Label:
		return "(label " + p(n.Body) + ")"
	case *Break:
		return fmt.Sprintf("(break ^%d)", n.Hops)
	}
	panic("Unknown node")
}

func printBinder(binder *Binder, p func(Node) string) string {
	if len(binder.Patterns) == 1 {
		return printBoundPattern(binder.Patterns[0], p)
	}
	var parts []string
	for _, pattern := range binder.Patterns {
		parts = append(parts, printBoundPattern(pattern, p))
	}
	return "(?// " + strings.Join(parts, " ") + ")"
}

func slotText(slot int) string {
	if slot < 0 {
		return "$_"
	}
	return fmt.Sprintf("$%d", slot)
}

func printBoundPattern(pattern Pattern, p func(Node) string) string {
	switch pt := pattern.(type) {
	case nil:
		return "_"
	case *VariablePattern:
		return slotText(pt.Slot)
	case *ArrayPattern:
		var parts []string
		for _, element := range pt.Elements {
			parts = append(parts, printBoundPattern(element, p))
		}
		return "[" + strings.Join(parts, " ") + "]"
	case *ObjectPattern:
		var parts []string
		for _, entry := range pt.Entries {
			slot := "_"
			if entry.Slot >= 0 {
				slot = slotText(entry.Slot)
			}
			parts = append(parts, "("+p(entry.Key)+" "+slot+" "+printBoundPattern(entry.Value, p)+")")
		}
		return "{" + strings.Join(parts, " ") + "}"
	}
	panic("Unknown pattern")
}
