package jqlite

import (
	"iter"
	"math"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/devlooped/jqlite/go/internal/binding"
	_ "github.com/devlooped/jqlite/go/internal/builtins" // registers the natives
	"github.com/devlooped/jqlite/go/internal/runtime"
	"github.com/devlooped/jqlite/go/internal/values"
)

// Expression is a jq expression parsed and bound once by Parse, to evaluate on any number of
// inputs. It is immutable and safe to evaluate concurrently from many goroutines, each evaluation
// with its own input and variables.
type Expression struct {
	program     *binding.BoundProgram
	environment func() values.Value
	clock       runtime.Clock
	maxDepth    int
	debug       func(values.Value)
	stderr      func(values.Value)
}

// Parse parses and binds expression with the default options (no modules). An empty expression is
// `.`. The error is a *ParseError.
func Parse(expression string) (*Expression, error) { return ParseWithOptions(expression, Options{}) }

// ParseWithResolver parses and binds expression, loading its modules through resolver, with the
// default options otherwise.
func ParseWithResolver(expression string, resolver Resolver) (*Expression, error) {
	return ParseWithOptions(expression, Options{Resolver: resolver})
}

// ParseWithOptions parses and binds expression, loading its modules through the options' resolver,
// into an expression to evaluate any number of times, concurrently if need be. The error is a
// *ParseError.
func ParseWithOptions(expression string, options Options) (*Expression, error) {
	var modules binding.ModuleSource
	if options.Resolver != nil {
		modules = resolverSource{options.Resolver}
	}
	program, err := binding.TryCompile(expression, true, modules)
	if err != nil {
		where := ""
		if err.Module != "" {
			where = err.Module + ", "
		}
		return nil, &ParseError{
			Message: err.Message + " at " + where + "line " + itoa(err.Line) + ", column " + itoa(err.Column),
			Line:    err.Line, Column: err.Column, Module: err.Module,
		}
	}

	e := &Expression{program: program, maxDepth: options.MaxDepth, debug: options.Debug, stderr: options.Stderr}
	if e.maxDepth <= 0 {
		e.maxDepth = runtime.DefaultMaxDepth
	}
	e.clock = timeClock{options.Now, options.Location}
	if options.Environment != nil {
		snapshot := environmentObject(options.Environment)
		e.environment = func() values.Value { return snapshot }
	} else {
		e.environment = func() values.Value { return environmentObject(os.Environ()) }
	}
	return e, nil
}

// MustParse is Parse for expressions known to be valid: it panics on an error.
func MustParse(expression string) *Expression {
	e, err := Parse(expression)
	if err != nil {
		panic(err)
	}
	return e
}

// Evaluate parses expression with the default options and evaluates it on input, giving all its
// results.
func Evaluate(expression string, input Value, variables map[string]Value) ([]Value, error) {
	e, err := Parse(expression)
	if err != nil {
		return nil, err
	}
	return e.Evaluate(input, variables)
}

// Evaluate evaluates the expression on input and gives all its results. variables are the host
// variables, by name without '$' ("root" is $root): each name a jq identifier, ENV excluded (a
// *VariableError). A variable the expression uses but the host does not give is the runtime error
// "$name is not defined"; a binding in the expression shadows a host variable. When an error
// reaches the top of the expression (an *Error, or a *HaltError for halt_error), the results are
// nil.
func (e *Expression) Evaluate(input Value, variables map[string]Value) ([]Value, error) {
	var results []Value
	err := e.EvaluateFunc(input, variables, func(v Value) bool {
		results = append(results, v)
		return true
	})
	if err != nil {
		return nil, err
	}
	return results, nil
}

// EvaluateFunc evaluates the expression on input, giving each result to onResult as soon as it is
// produced; onResult gives false to stop the evaluation (stopping is not an error). An error that
// reaches the top of the expression comes after the results produced before it.
func (e *Expression) EvaluateFunc(input Value, variables map[string]Value, onResult func(Value) bool) error {
	for name := range variables {
		if !isIdentifier(name) || name == "ENV" {
			return &VariableError{name}
		}
	}
	options := &runtime.EvaluationOptions{
		Variables:   variables,
		Environment: e.environment,
		MaxDepth:    e.maxDepth,
		Clock:       e.clock,
		Debug:       e.debug,
		Stderr:      e.stderr,
	}
	outcome := runtime.Run(e.program, input, options, onResult)
	switch {
	case outcome.Kind == runtime.OutcomeError:
		return &Error{message(outcome.Value), outcome.Value}
	case outcome.Kind == runtime.OutcomeHalt && outcome.HasValue:
		return &HaltError{message(outcome.Value), outcome.Value, outcome.Code}
	}
	return nil
}

// Results evaluates the expression on input as a sequence for range: each result with a nil
// error, then, if an error reaches the top of the expression, Null with that error. Breaking out
// of the loop stops the evaluation.
func (e *Expression) Results(input Value, variables map[string]Value) iter.Seq2[Value, error] {
	return func(yield func(Value, error) bool) {
		stopped := false
		err := e.EvaluateFunc(input, variables, func(v Value) bool {
			stopped = !yield(v, nil)
			return !stopped
		})
		if err != nil && !stopped {
			yield(Null, err)
		}
	}
}

func isIdentifier(name string) bool {
	if name == "" || !(isLetter(name[0]) || name[0] == '_') {
		return false
	}
	for i := 0; i < len(name); i++ {
		if c := name[i]; !(isLetter(c) || (c >= '0' && c <= '9') || c == '_') {
			return false
		}
	}
	return true
}

func isLetter(c byte) bool { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') }

// environmentObject gives the $ENV object of "NAME=value" entries, in their order.
func environmentObject(entries []string) values.Value {
	builder := values.NewObjectBuilder(len(entries) + 1)
	for _, entry := range entries {
		name, v, _ := strings.Cut(entry, "=")
		builder.Set(values.CodePoints.Sanitize(name), values.FromString(v))
	}
	return values.FromObject(builder.ToObject())
}

// timeClock is Options.Now and Options.Location as the clock and zone of the core (spec §12, §9.12).
type timeClock struct {
	now      func() time.Time
	location *time.Location
}

func (c timeClock) Now() float64 {
	now := time.Now
	if c.now != nil {
		now = c.now
	}
	t := now()
	return float64(t.Unix()) + float64(t.Nanosecond())/1e9
}

func (c timeClock) at(seconds float64) time.Time {
	location := c.location
	if location == nil {
		location = time.Local
	}
	if math.IsNaN(seconds) || math.IsInf(seconds, 0) {
		seconds = 0
	}
	return time.Unix(int64(math.Floor(seconds)), 0).In(location)
}

// Offset gives the zone's offset at that instant, so daylight saving time follows the date.
func (c timeClock) Offset(seconds float64) int {
	_, offset := c.at(seconds).Zone()
	return offset
}

// Abbreviation gives the zone's name at that instant, if it has one.
func (c timeClock) Abbreviation(seconds float64) (string, bool) {
	name, _ := c.at(seconds).Zone()
	return name, name != ""
}

func itoa(n int) string { return strconv.Itoa(n) }
