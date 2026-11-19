// Package runtime is the push-based interpreter of a bound program (spec §6 to §8), its
// environments, paths, signals, the host interface (evaluation.go) and the native table.
package runtime

import "github.com/devlooped/jqlite/go/internal/values"

// SignalKind is why evaluation stops besides reaching its end.
type SignalKind int

// The kinds of signals.
const (
	// SignalError is a jqlite error (§6.2); Value is the error value. Caught by try, ? and ?//.
	SignalError SignalKind = iota
	// SignalBreak is break to the label instance Label; handled by that label only.
	SignalBreak
	// SignalHalt is halt or halt_error: stops the whole evaluation; handled by the host only.
	SignalHalt
	// SignalStop is the host stopping the stream of outputs (not an error); handled by the host only.
	SignalStop
)

// Signal is why evaluation stops besides reaching its end: evaluation gives nil to go on, or one of
// these, which every construct passes up unchanged except the one that handles it (§6.2).
//
// A signal is a pointer, and its identity matters: an error-handling construct recognizes the
// signals that came back from its consumer by identity (see consumer in interpreter.go), so a
// construct that passes a signal up must pass the same pointer.
type Signal struct {
	Kind SignalKind
	// Value is the error value, or the halt value when HasValue.
	Value values.Value
	// HasValue: for SignalHalt, whether a value goes with the exit code (halt_error).
	HasValue bool
	// Label: for SignalBreak, the label instance.
	Label int64
	// Code: for SignalHalt, the exit code.
	Code int
}

// StopSignal is the one SignalStop signal.
var StopSignal = &Signal{Kind: SignalStop}

// ErrorSignal gives an error signal.
func ErrorSignal(value values.Value) *Signal {
	return &Signal{Kind: SignalError, Value: value, HasValue: true}
}

// BreakSignal gives a break to a label instance.
func BreakSignal(label int64) *Signal { return &Signal{Kind: SignalBreak, Label: label} }

// HaltSignal gives a halt, with a value when hasValue.
func HaltSignal(value values.Value, hasValue bool, code int) *Signal {
	return &Signal{Kind: SignalHalt, Value: value, HasValue: hasValue, Code: code}
}
