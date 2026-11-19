package syntax

import (
	"fmt"
	"strings"
	"testing"

	"github.com/devlooped/jqlite/go/internal/values"
)

// The AST printer of the reference's tests: an S-expression so tests can state structure: .,
// literals as JSON, $x, a call without arguments as its name, (f a b), (| a b), (, a b), (+ a b),
// (neg a), (index t k), (slice t from to), (iter t), (str @fmt "a" e "b"), (object (k v) (k _)),
// (as source pattern body), …; a missing child is _. Recursive: for the trees of ordinary tests only.

// parseText parses a program and prints its top-level definitions and body, separated by spaces.
func parseText(t *testing.T, source string) string {
	t.Helper()
	program, err := TryParse(source)
	if err != nil {
		t.Fatalf("'%s' does not parse: %s at %d:%d", source, err.Message, err.Line, err.Column)
	}
	return printProgram(program)
}

// errorText parses a program that must fail and gives "message @line:column".
func errorText(t *testing.T, source string) string {
	t.Helper()
	program, err := TryParse(source)
	if err == nil {
		t.Fatalf("'%s' parses as %s", source, printProgram(program))
	}
	return fmt.Sprintf("%s @%d:%d", err.Message, err.Line, err.Column)
}

func printProgram(program *Program) string {
	var parts []string
	if program.Metadata != nil {
		parts = append(parts, "(module "+values.ValueText.ToJson(*program.Metadata)+")")
	}
	for _, imp := range program.Imports {
		alias := ""
		if imp.HasAlias {
			if imp.IsData {
				alias = " $" + imp.Alias
			} else {
				alias = " " + imp.Alias
			}
		}
		meta := ""
		if imp.Metadata != nil {
			meta = " " + values.ValueText.ToJson(*imp.Metadata)
		}
		keyword := "include"
		if imp.HasAlias {
			keyword = "import"
		}
		parts = append(parts, fmt.Sprintf("(%s \"%s\"%s%s)", keyword, imp.Path, alias, meta))
	}

	for _, definition := range program.Definitions {
		parts = append(parts, "(def "+signature(definition)+" "+printNode(definition.Body)+")")
	}
	if program.Body != nil {
		parts = append(parts, printNode(program.Body))
	}
	return strings.Join(parts, " ")
}

func printNode(node Node) string {
	switch n := node.(type) {
	case nil:
		return "_"
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
			builder.WriteString(" " + values.ValueText.ToJson(values.FromString(n.Texts[i])) + " " + printNode(part))
		}
		builder.WriteString(" " + values.ValueText.ToJson(values.FromString(n.Texts[len(n.Texts)-1])) + ")")
		return builder.String()
	case *Format:
		return "@" + n.Name
	case *Index:
		return "(index " + printNode(n.Target) + " " + printNode(n.Key) + ")"
	case *Slice:
		return "(slice " + printNode(n.Target) + " " + printNode(n.From) + " " + printNode(n.To) + ")"
	case *Iterate:
		return "(iter " + printNode(n.Target) + ")"
	case *Variable:
		return "$" + n.Name
	case *Call:
		if len(n.Arguments) == 0 {
			return n.Name
		}
		return "(" + n.Name + " " + printNodes(n.Arguments) + ")"
	case *Pipe:
		return "(| " + printNode(n.Left) + " " + printNode(n.Right) + ")"
	case *Comma:
		return "(, " + printNode(n.Left) + " " + printNode(n.Right) + ")"
	case *Binary:
		return "(" + symbol(n.Op) + " " + printNode(n.Left) + " " + printNode(n.Right) + ")"
	case *And:
		return "(and " + printNode(n.Left) + " " + printNode(n.Right) + ")"
	case *Or:
		return "(or " + printNode(n.Left) + " " + printNode(n.Right) + ")"
	case *Alternative:
		return "(// " + printNode(n.Left) + " " + printNode(n.Right) + ")"
	case *Negate:
		return "(neg " + printNode(n.Operand) + ")"
	case *ArrayExpr:
		if n.Body == nil {
			return "(array)"
		}
		return "(array " + printNode(n.Body) + ")"
	case *ObjectExpr:
		if len(n.Entries) == 0 {
			return "(object)"
		}
		var entries []string
		for _, entry := range n.Entries {
			entries = append(entries, "("+printNode(entry.Key)+" "+printNode(entry.Value)+")")
		}
		return "(object " + strings.Join(entries, " ") + ")"
	case *If:
		return "(if " + printNode(n.Condition) + " " + printNode(n.Then) + " " + printNode(n.Else) + ")"
	case *Try:
		if n.Handler == nil {
			return "(try " + printNode(n.Body) + ")"
		}
		return "(try " + printNode(n.Body) + " " + printNode(n.Handler) + ")"
	case *Reduce:
		return "(reduce " + printNode(n.Source) + " " + printPatterns(n.Patterns) + " " + printNode(n.Init) + " " + printNode(n.Update) + ")"
	case *Foreach:
		return "(foreach " + printNode(n.Source) + " " + printPatterns(n.Patterns) + " " + printNode(n.Init) + " " + printNode(n.Update) + " " + printNode(n.Extract) + ")"
	case *Bind:
		return "(as " + printNode(n.Source) + " " + printPatterns(n.Patterns) + " " + printNode(n.Body) + ")"
	case *FuncDef:
		return "(def " + signature(n.Definition) + " " + printNode(n.Definition.Body) + " " + printNode(n.Rest) + ")"
	case *Label:
		return "(label $" + n.Name + " " + printNode(n.Body) + ")"
	case *Break:
		return "(break $" + n.Name + ")"
	}
	panic(fmt.Sprintf("Unknown node %v", node.Kind()))
}

func printNodes(nodes []Node) string {
	var parts []string
	for _, node := range nodes {
		parts = append(parts, printNode(node))
	}
	return strings.Join(parts, " ")
}

func signature(definition *Definition) string {
	if len(definition.Params) == 0 {
		return definition.Name
	}
	var params []string
	for _, p := range definition.Params {
		if p.IsVariable {
			params = append(params, "$"+p.Name)
		} else {
			params = append(params, p.Name)
		}
	}
	return definition.Name + "(" + strings.Join(params, ";") + ")"
}

func printPatterns(patterns []Pattern) string {
	if len(patterns) == 1 {
		return printPattern(patterns[0])
	}
	var parts []string
	for _, p := range patterns {
		parts = append(parts, printPattern(p))
	}
	return "(?// " + strings.Join(parts, " ") + ")"
}

func printPattern(pattern Pattern) string {
	switch p := pattern.(type) {
	case nil:
		return "_"
	case *VariablePattern:
		return "$" + p.Name
	case *ArrayPattern:
		var parts []string
		for _, element := range p.Elements {
			parts = append(parts, printPattern(element))
		}
		return "[" + strings.Join(parts, " ") + "]"
	case *ObjectPattern:
		var parts []string
		for _, entry := range p.Entries {
			variable := "_"
			if entry.HasVariable {
				variable = "$" + entry.Variable
			}
			parts = append(parts, "("+printNode(entry.Key)+" "+variable+" "+printPattern(entry.Value)+")")
		}
		return "{" + strings.Join(parts, " ") + "}"
	}
	panic("Unknown pattern")
}

func symbol(op BinaryOp) string {
	return [...]string{"+", "-", "*", "/", "%", "==", "!=", "<", "<=", ">", ">="}[op]
}
