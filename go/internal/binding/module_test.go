package binding_test

import (
	"fmt"
	"path/filepath"
	"slices"
	"strings"
	"testing"

	"github.com/devlooped/jqlite/go/internal/binding"
	_ "github.com/devlooped/jqlite/go/internal/builtins"
	"github.com/devlooped/jqlite/go/internal/corpus"
	"github.com/devlooped/jqlite/go/internal/corpus/modules"
	"github.com/devlooped/jqlite/go/internal/runtime"
	"github.com/devlooped/jqlite/go/internal/syntax"
	"github.com/devlooped/jqlite/go/internal/values"
)

// The modules group (spec §9.11) beyond the corpus: errors and where they are, loading once,
// limits, modulemeta.

func corpusModules() *modules.DirectoryModules {
	return &modules.DirectoryModules{Root: filepath.Join(corpus.TestsDirectory(), "modules")}
}

func moduleError(t *testing.T, program string, source binding.ModuleSource) string {
	t.Helper()
	_, err := binding.TryCompile(program, true, source)
	if err == nil {
		t.Fatalf("'%s' binds", program)
	}
	module := err.Module
	if module == "" {
		module = "main"
	}
	return fmt.Sprintf("%s @%s:%d:%d", err.Message, module, err.Line, err.Column)
}

func evaluate(program *binding.BoundProgram, options *runtime.EvaluationOptions) []string {
	if options == nil {
		options = runtime.NewEvaluationOptions()
	}
	var outputs []string
	outcome := runtime.Run(program, values.Null, options, func(v values.Value) bool {
		outputs = append(outputs, values.ValueText.ToJson(v))
		return true
	})
	if outcome.Kind == runtime.OutcomeError {
		outputs = append(outputs, "error: "+values.ValueText.ToJson(outcome.Value))
	}
	return outputs
}

func runModules(t *testing.T, program string, source binding.ModuleSource) string {
	t.Helper()
	bound, err := binding.TryCompile(program, true, source)
	if err != nil {
		t.Fatalf("'%s': %s", program, err.Message)
	}
	return strings.Join(evaluate(bound, nil), ", ")
}

func expect(t *testing.T, expected, actual string) {
	t.Helper()
	if expected != actual {
		t.Errorf("\nexpected %s\ngot      %s", expected, actual)
	}
}

func TestBindingErrorsSayWhereTheyAre(t *testing.T) {
	source := corpusModules()
	for _, c := range moduleBindingErrorsSayWhereTheyAre {
		expect(t, c[1], moduleError(t, c[0], source))
	}
}

func TestBadDataIsABindingError(t *testing.T) {
	message := moduleError(t, `import "bad" as $b; $b`, corpusModules())
	if !strings.HasPrefix(message, "error loading data file bad.json: Unfinished JSON term at EOF") {
		t.Error(message)
	}
}

func TestWithoutASourceNoModuleExists(t *testing.T) {
	expect(t, "module not found: a @main:1:1", moduleError(t, `import "a" as a; 1`, nil))
	expect(t, `"module not found: a"`, runModules(t, `try ("a" | modulemeta) catch .`, nil))
}

func TestEachModuleIsReadOnce(t *testing.T) {
	source := corpusModules()
	expect(t, "2", runModules(t, `import "one" as o; import "one" as p; include "one"; o::k + p::k`, source))
	if source.Reads() != 1 {
		t.Errorf("%d reads", source.Reads())
	}
}

func TestALongImportChainIsNoRecursion(t *testing.T) {
	// m0 imports m1, …, m999 imports nothing.
	sources := map[string]string{}
	for i := 0; i < 1000; i++ {
		if i == 999 {
			sources[fmt.Sprintf("m%d.jq", i)] = "def f: 999;"
		} else {
			sources[fmt.Sprintf("m%d.jq", i)] = fmt.Sprintf(`import "m%d" as n; def f: n::f; def g: %d;`, i+1, i)
		}
	}
	expect(t, "999, 0", runModules(t, `import "m0" as m; m::f, m::g`, &modules.MemoryModules{Modules: sources}))
}

func TestModulemetaUsesWhatTheProgramLoaded(t *testing.T) {
	source := &modules.MemoryModules{Modules: map[string]string{"x.jq": "def a: 1;"}}
	bound, err := binding.TryCompile(`import "x" as x; ["x", "y"][] | modulemeta | .defs`, true, source)
	if err != nil {
		t.Fatal(err.Message)
	}
	source.Set("x.jq", "def changed: 1;")
	source.Set("y.jq", "def b: 1; def c: 2;")
	// x as the program loaded it; y, which it does not import, from its source now.
	if outputs := evaluate(bound, nil); !slices.Equal([]string{`["a/0"]`, `["b/0","c/0"]`}, outputs) {
		t.Errorf("%v", outputs)
	}
}

func TestModulesShareTheHostVariables(t *testing.T) {
	source := &modules.MemoryModules{Modules: map[string]string{"v.jq": "def v: $x;"}}
	bound, err := binding.TryCompile(`import "v" as v; v::v, $x`, true, source)
	if err != nil {
		t.Fatal(err.Message)
	}
	options := runtime.NewEvaluationOptions()
	options.Variables = map[string]values.Value{"x": values.FromNumber(7)}
	if outputs := evaluate(bound, options); !slices.Equal([]string{"7", "7"}, outputs) {
		t.Errorf("%v", outputs)
	}
}

func TestQualifiedAliasesNest(t *testing.T) {
	expect(t, "1", runModules(t, `import "a" as x::y; x::y::f`, &modules.MemoryModules{Modules: map[string]string{"a.jq": "def f: 1;"}}))
}

func TestAModuleObeysTheNestingLimitOfItsFile(t *testing.T) {
	deep := strings.Repeat("[", syntax.MaxNesting+1) + strings.Repeat("]", syntax.MaxNesting+1)
	source := &modules.MemoryModules{Modules: map[string]string{"deep.jq": "def f: " + deep + ";"}}
	// def opens the first level, so the 1000th bracket is one too many.
	expect(t, "Program too deeply nested @deep.jq:1:1007", moduleError(t, `import "deep" as d; 1`, source))
}

var moduleBindingErrorsSayWhereTheyAre = [][]string{
	{`import "double" as d; import "nope" as n; 1`, `module not found: nope @main:1:23`},
	{`import "broken" as b; 1`, `Expected ')' but found ';' @broken.jq:1:15`},
	{`import "usesmain" as u; 1`, `mainf/0 is not defined @usesmain.jq:1:15`},
	{`import "cycle1" as c; 1`, `circular import of cycle1.jq @cycle2.jq:1:1`},
	{`import "self" as s; 1`, `circular import of self.jq @self.jq:1:1`},
	{`include "config"; 1`, `module not found: config @main:1:1`},
	{`import "a" as a; a::zz`, `a::zz/0 is not defined @main:1:18`},
	{`import "values" as $d; $d::x`, `$d::x is not defined @main:1:24`},
}
