package binding_test

import (
	"path/filepath"
	"strings"
	"testing"

	"github.com/devlooped/jqlite/go/internal/binding"
	"github.com/devlooped/jqlite/go/internal/corpus"
	"github.com/devlooped/jqlite/go/internal/corpus/modules"
	"github.com/devlooped/jqlite/go/internal/syntax"
)

// The conformance corpus as binding tests: every program of spec/tests/*.test binds, and every
// %%FAIL case fails to parse or to bind, with the fixed message of spec §10.2 it names.

func TestEveryProgramOfTheCorpusBinds(t *testing.T) {
	source := &modules.DirectoryModules{Root: filepath.Join(corpus.TestsDirectory(), "modules")}
	for _, file := range corpus.Files() {
		name := filepath.Base(file)
		for _, test := range corpus.ParseFile(file) {
			program, err := syntax.TryParse(test.Program)
			if err != nil {
				if !test.ShouldFail {
					t.Errorf("%s:%d: '%s' does not parse: %s", name, test.LineNumber, test.Program, err.Message)
				}
				continue
			}

			_, err = binding.TryBind(program, true, source)
			switch {
			case test.ShouldFail && err == nil:
				t.Errorf("%s:%d: '%s' binds but is a %%%%FAIL case", name, test.LineNumber, test.Program)
			case !test.ShouldFail && err != nil:
				t.Errorf("%s:%d: '%s': %s at %d:%d", name, test.LineNumber, test.Program, err.Message, err.Line, err.Column)
			case test.ShouldFail && test.HasExpectedError && !strings.Contains(err.Message, test.ExpectedError):
				t.Errorf("%s:%d: '%s': '%s' does not contain '%s'", name, test.LineNumber, test.Program, err.Message, test.ExpectedError)
			}
		}
	}
}

func TestSomeFailureOfTheCorpusIsABindingError(t *testing.T) {
	for _, file := range corpus.Files() {
		for _, test := range corpus.ParseFile(file) {
			if _, err := syntax.TryParse(test.Program); test.ShouldFail && err == nil && test.Program == ". as $foo | break $foo" {
				return
			}
		}
	}
	t.Error("no binding failure in the corpus")
}
