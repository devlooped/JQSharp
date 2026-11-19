package jqlite

import (
	"errors"
	"fmt"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/devlooped/jqlite/go/internal/binding"
	"github.com/devlooped/jqlite/go/internal/corpus"
	"github.com/devlooped/jqlite/go/internal/corpus/modules"
	"github.com/devlooped/jqlite/go/internal/runtime"
	"github.com/devlooped/jqlite/go/internal/values"
)

// Runs every case of the jqlite conformance corpus (spec/tests/*.test), by the rules of
// spec/tests/README.md: through the public API (ParseWithOptions, Expression), and through the core
// with every prelude builtin bound to its definition, to check that the natives that override some
// change nothing. Every case must pass both ways.

// The host of spec §14.2: no environment variables, a zone at UTC+02:00 without DST or
// abbreviation, the default depth, and the module files of modules/.
func corpusOptions() Options {
	return Options{
		Environment: []string{},
		Location:    time.FixedZone("", 2*60*60),
		MaxDepth:    1000,
		Resolver:    NewFileResolver(filepath.Join(corpus.TestsDirectory(), "modules")),
	}
}

// runPublic runs a case through the public API.
func runPublic(c corpus.Case, options Options) (failure string) {
	defer func() {
		if r := recover(); r != nil {
			failure = fmt.Sprintf("panic: %v", r)
		}
	}()
	expression, err := ParseWithOptions(c.Program, options)
	if err != nil {
		var parseError *ParseError
		if !errors.As(err, &parseError) {
			return "not a ParseError: " + err.Error()
		}
		if c.ShouldFail {
			return corpus.MissingFixedText(c, parseError.Message)
		}
		return parseError.Message
	}
	if c.ShouldFail {
		return "the program parses and binds but must not"
	}

	input, err := ParseJSON(c.Input)
	if err != nil {
		return "bad input: " + err.Error()
	}
	var outputs []string
	err = expression.EvaluateFunc(input, nil, func(v Value) bool {
		outputs = append(outputs, ToJSON(v))
		return true
	})
	if err != nil {
		return fmt.Sprintf("%T %s after [%s]", err, err, strings.Join(outputs, ", "))
	}
	return corpus.Compare(c.ExpectedOutputs, outputs)
}

// fixedZoneClock is UTC+02:00 without DST or abbreviation, for the core.
type fixedZoneClock struct{}

func (fixedZoneClock) Now() float64                        { return float64(time.Now().UnixMilli()) / 1000 }
func (fixedZoneClock) Offset(float64) int                  { return 7200 }
func (fixedZoneClock) Abbreviation(float64) (string, bool) { return "", false }

// runCore runs a case through the core, every prelude builtin bound to its definition when
// nativeOverrides is false.
func runCore(c corpus.Case, nativeOverrides bool, source binding.ModuleSource) (failure string) {
	defer func() {
		if r := recover(); r != nil {
			failure = fmt.Sprintf("panic: %v", r)
		}
	}()
	bound, err := binding.TryCompile(c.Program, nativeOverrides, source)
	if err != nil {
		if c.ShouldFail {
			return corpus.MissingFixedText(c, err.Message)
		}
		return fmt.Sprintf("%s at %d:%d", err.Message, err.Line, err.Column)
	}
	if c.ShouldFail {
		return "the program parses and binds but must not"
	}

	input, inputError, ok := values.ValueText.TryParse(c.Input)
	if !ok {
		return "bad input: " + inputError.String()
	}
	var outputs []string
	options := runtime.NewEvaluationOptions()
	options.Clock = fixedZoneClock{}
	outcome := runtime.Run(bound, input, options, func(v values.Value) bool {
		outputs = append(outputs, values.ValueText.ToJson(v))
		return true
	})
	switch {
	case outcome.Kind == runtime.OutcomeError:
		return fmt.Sprintf("error %s after [%s]", values.ValueText.ToJson(outcome.Value), strings.Join(outputs, ", "))
	case outcome.Kind == runtime.OutcomeHalt && (outcome.HasValue || outcome.Code != 0):
		return fmt.Sprintf("halt_error %d after [%s]", outcome.Code, strings.Join(outputs, ", "))
	}
	return corpus.Compare(c.ExpectedOutputs, outputs)
}

func runCorpus(t *testing.T, run func(corpus.Case) string) {
	passed, failed := 0, 0
	for _, file := range corpus.Files() {
		name := filepath.Base(file)
		for _, c := range corpus.ParseFile(file) {
			if failure := run(c); failure != "" {
				failed++
				t.Errorf("%s:%d: %s\n    %s", name, c.LineNumber, c.Program, failure)
			} else {
				passed++
			}
		}
	}
	t.Logf("%d cases passed, %d failed", passed, failed)
}

func TestCorpusCase(t *testing.T) {
	options := corpusOptions()
	runCorpus(t, func(c corpus.Case) string { return runPublic(c, options) })
}

func TestCorpusCaseWithoutNativeOverrides(t *testing.T) {
	source := &modules.DirectoryModules{Root: filepath.Join(corpus.TestsDirectory(), "modules")}
	runCorpus(t, func(c corpus.Case) string { return runCore(c, false, source) })
}
