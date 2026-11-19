package runtime

import (
	"github.com/devlooped/jqlite/go/internal/binding"
	"github.com/devlooped/jqlite/go/internal/values"
)

// Closure is a filter argument: the argument's node with the caller's environment (§7.7).
type Closure struct {
	Node binding.Node
	Env  *Env
}

// Env is one frame of the environment, linked to the frame outside it; the empty environment is
// nil. A frame holds the variables of one binding (Values), the closures of one call of a function
// with parameters (Closures), or one label instance (Label); the resolver numbered the frames, so a
// reference walks a known number of frames outward (bound.go). Frames are never changed once
// built, so they can be shared freely.
type Env struct {
	Parent   *Env
	Values   []values.Value
	Closures []*Closure
	Label    int64
}

// Up gives the environment hops frames out from env.
func Up(env *Env, hops int) *Env {
	for ; hops > 0; hops-- {
		env = env.Parent
	}
	return env
}
