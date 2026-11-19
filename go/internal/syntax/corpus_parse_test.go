package syntax

import (
	"os"
	"path/filepath"
	"testing"

	"github.com/devlooped/jqlite/go/internal/corpus"
)

// The conformance corpus and the prelude as parser tests: every program of spec/tests/*.test
// parses, except the %%FAIL cases that fail for a syntax reason; spec/prelude.jq parses as a module.

// %%FAIL cases whose program is syntactically valid: they fail when names are bound.
var bindingFailures = map[string]bool{
	". as $foo | break $foo": true,
	"input":                  true,
	"[inputs]":               true,
}

func TestEveryProgramOfTheCorpusParses(t *testing.T) {
	for _, file := range corpus.Files() {
		name := filepath.Base(file)
		for _, test := range corpus.ParseFile(file) {
			_, err := TryParse(test.Program)
			// The failures of modules.test are all binding errors: missing modules, cycles, names.
			if test.ShouldFail && !bindingFailures[test.Program] && name != "modules.test" {
				if err == nil {
					t.Errorf("%s:%d: '%s' parses but is a %%%%FAIL case", name, test.LineNumber, test.Program)
				}
			} else if err != nil {
				t.Errorf("%s:%d: '%s': %s at %d:%d", name, test.LineNumber, test.Program, err.Message, err.Line, err.Column)
			}
		}
	}
}

func TestTheBindingFailuresAreInTheCorpus(t *testing.T) {
	found := map[string]bool{}
	for _, file := range corpus.Files() {
		for _, test := range corpus.ParseFile(file) {
			if test.ShouldFail {
				found[test.Program] = true
			}
		}
	}
	for program := range bindingFailures {
		if !found[program] {
			t.Errorf("%s is not a %%%%FAIL case of the corpus", program)
		}
	}
}

func TestThePreludeParsesAsAModule(t *testing.T) {
	data, err := os.ReadFile(filepath.Join(corpus.SpecDirectory(), "prelude.jq"))
	if err != nil {
		t.Fatal(err)
	}
	source := string(data)
	program, syntaxError := TryParseModule(source)
	if syntaxError != nil {
		t.Fatalf("%s at %d:%d", syntaxError.Message, syntaxError.Line, syntaxError.Column)
	}
	if program.Body != nil || len(program.Definitions) != 102 {
		t.Errorf("body %v, %d definitions", program.Body, len(program.Definitions))
	}
	found := false
	for _, definition := range program.Definitions {
		found = found || (definition.Name == "_modify" && len(definition.Params) == 2)
	}
	if !found {
		t.Error("no _modify/2")
	}

	// And as a program of definitions only, whose body is '.'.
	program, _ = TryParse(source)
	if _, ok := program.Body.(*Identity); !ok {
		t.Error("the body is not '.'")
	}
}
