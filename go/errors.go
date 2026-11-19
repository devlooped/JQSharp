package jqlite

import "fmt"

// ParseError is a syntax or binding error, given by Parse before any evaluation: an unknown
// function, label or module, a cycle of imports.
type ParseError struct {
	// Message is the error with where it is: "<message> at [<module>, ]line L, column C".
	Message string
	// Line and Column are 1-based, in the expression or in the module Module (when not empty);
	// columns count code points.
	Line   int
	Column int
	Module string
}

func (e *ParseError) Error() string { return e.Message }

// Error is an error that reached the top of an expression: Value is the error value (what
// error(v) raised, or the message string of a builtin's error). The Message is the value itself
// when it is a string, else its JSON text followed by " (not a string)" (spec §10.1).
type Error struct {
	Message string
	Value   Value
}

func (e *Error) Error() string { return e.Message }

// HaltError is halt_error ending an evaluation: Value is the value it was given and ExitCode its
// code. (halt ends an evaluation normally, without an error.)
type HaltError struct {
	Message  string
	Value    Value
	ExitCode int
}

func (e *HaltError) Error() string { return e.Message }

// VariableError is a host variable name that is not valid (spec §12): not a jq identifier, or ENV.
type VariableError struct {
	Name string
}

func (e *VariableError) Error() string {
	if e.Name == "ENV" {
		return "jqlite: $ENV cannot be a host variable: use Options.Environment"
	}
	return fmt.Sprintf("jqlite: '%s' is not a valid variable name: give a jq identifier without '$'", e.Name)
}

// message is the message of an error that reaches the host (spec §10.1).
func message(v Value) string {
	if v.Kind() == KindString {
		return v.AsString()
	}
	return ToJSON(v) + " (not a string)"
}
