package runtime

import "github.com/devlooped/jqlite/go/internal/values"

// Path is where a value came from, in path mode (§8.1): the keys from the root input, as an
// immutable list linked from the last key, so appending is O(1) and shares the prefix.
//
// Evaluation takes and emits a *Path: nil in value mode (no path is ever built), and in path mode
// either a tracked path (RootPath and what is appended to it) or UntrackedPath, for an output that
// has no path (a literal, a variable, an arithmetic result).
type Path struct {
	parent *Path
	key    values.Value
	length int
}

var (
	// RootPath is the empty path: the root input in path mode.
	RootPath = &Path{length: 0}
	// UntrackedPath is an output in path mode that has no path.
	UntrackedPath = &Path{length: -1}
)

// IsTracked tells whether the path is tracked.
func (p *Path) IsTracked() bool { return p.length >= 0 }

// PathOf gives the path of an output without one: nil in value mode, else UntrackedPath.
func PathOf(mode *Path) *Path {
	if mode == nil {
		return nil
	}
	return UntrackedPath
}

// Append gives this path followed by next; this path must be tracked.
func (p *Path) Append(next values.Value) *Path { return &Path{p, next, p.length + 1} }

// AppendAll gives this path followed by the elements of keys; this path must be tracked.
func (p *Path) AppendAll(keys *values.Array) *Path {
	path := p
	for i := 0; i < keys.Count(); i++ {
		path = path.Append(keys.At(i))
	}
	return path
}

// ToValue gives the keys as a jqlite array, first key first; this path must be tracked.
func (p *Path) ToValue() values.Value {
	keys := make([]values.Value, p.length)
	path := p
	for i := p.length - 1; i >= 0; i-- {
		keys[i] = path.key
		path = path.parent
	}
	return values.FromArray(values.ArrayFromOwned(keys))
}

// appendKey is at.Append(key) when at is not nil (value mode builds no path).
func appendKey(at *Path, key values.Value) *Path {
	if at == nil {
		return nil
	}
	return at.Append(key)
}
