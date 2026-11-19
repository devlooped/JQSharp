package jqlite

import "time"

// Options is what an expression takes from its host besides the input and the variables: modules,
// $ENV, the clock and zone, the depth limit and the handlers of debug and stderr. Options are read
// when the expression is parsed and apply to every evaluation of it; the zero Options are the
// defaults.
type Options struct {
	// Resolver resolves import and include directives and modulemeta; without one, no module exists.
	Resolver Resolver

	// Environment is the variables of $ENV and env, as "NAME=value" entries in this order (the form
	// of os.Environ). When nil (the default), the process environment, read when an evaluation first
	// uses $ENV; an empty, non-nil slice is an empty $ENV.
	Environment []string

	// Now is the source of now; nil is time.Now.
	Now func() time.Time

	// Location is the local time zone of localtime, strflocaltime, %z and %Z; nil is time.Local. A
	// zone's name (time.Time.Zone) is the abbreviation of %Z; an empty name prints the offset.
	Location *time.Location

	// MaxDepth is the maximum number of calls of jq-defined functions active at once (spec §11);
	// beyond it, the catchable error "Maximum call depth exceeded". Deeper recursion never exhausts
	// the host stack. 0 is the default, 1000.
	MaxDepth int

	// Debug receives the value of each debug (its input) and each message of debug(msgs), as is.
	// Without one, the values are dropped.
	Debug func(Value)

	// Stderr receives the input of each stderr, as is. Without one, the values are dropped.
	Stderr func(Value)
}
