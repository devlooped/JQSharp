package runtime

import "github.com/devlooped/jqlite/go/internal/values"

// Clock is the host's clock and time zone (§12): now, and the local zone of localtime,
// strflocaltime, %z, %Z.
type Clock interface {
	// Now gives the current time, in seconds since the Unix epoch.
	Now() float64
	// Offset gives the offset of the local zone from UTC at the instant seconds, in seconds east of UTC.
	Offset(seconds float64) int
	// Abbreviation gives the abbreviation of the local zone at the instant seconds, if any.
	Abbreviation(seconds float64) (string, bool)
}

// EvaluationOptions is what an evaluation takes from its host besides the program and the input (§12).
type EvaluationOptions struct {
	// Variables are the host variables, by name without '$'; names are validated by the host boundary.
	Variables map[string]values.Value
	// Environment gives the object $ENV is (string to string, in the order the host gives), asked
	// for once per evaluation, when the program first reads it; nil is the empty object.
	Environment func() values.Value
	// MaxDepth is the call depth limit of §11.
	MaxDepth int
	// Clock is the clock and zone, for the date builtins; nil: now is an error and the zone is UTC.
	Clock Clock
	// Debug receives the values of debug; they are dropped when nil.
	Debug func(values.Value)
	// Stderr receives the values of stderr; they are dropped when nil.
	Stderr func(values.Value)
}

// DefaultMaxDepth is the default call depth limit of §11.
const DefaultMaxDepth = 1000

// NewEvaluationOptions gives the defaults: no host variables, an empty $ENV, MaxDepth 1000, no
// clock, no handlers.
func NewEvaluationOptions() *EvaluationOptions {
	return &EvaluationOptions{MaxDepth: DefaultMaxDepth}
}

// OutcomeKind is how an evaluation ended.
type OutcomeKind int

// The outcomes.
const (
	// OutcomeCompleted: every output was produced.
	OutcomeCompleted OutcomeKind = iota
	// OutcomeStopped: the host stopped the stream.
	OutcomeStopped
	// OutcomeError: an error reached the top: Value.
	OutcomeError
	// OutcomeHalt: halt or halt_error: Code, and Value when HasValue.
	OutcomeHalt
)

// Outcome is how an evaluation ended (§12).
type Outcome struct {
	Kind     OutcomeKind
	Value    values.Value
	HasValue bool
	Code     int
}
