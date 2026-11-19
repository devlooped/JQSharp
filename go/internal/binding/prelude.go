package binding

// Host-specific (H): reading the prelude embedded in the package with //go:embed. The embed
// directive only sees files of the package's directory, so prelude.jq is a copy of
// spec/prelude.jq made by `go generate` (copy_prelude.go); TestTheEmbeddedPreludeIsTheSpecs fails
// when the copy differs. A port reads its embedded copy of spec/prelude.jq instead.

import (
	_ "embed"
	"sync"

	"github.com/devlooped/jqlite/go/internal/syntax"
)

//go:generate go run copy_prelude.go

//go:embed prelude.jq
var preludeSource string

// PreludeSource gives the text of the embedded prelude.
func PreludeSource() string { return preludeSource }

// Prelude is spec/prelude.jq, parsed and bound once against the natives, then shared by every
// program (immutable, so safe to use from any goroutine). A program's function table starts with
// Functions, so prelude calls keep their indices in every program.
type Prelude struct {
	// Functions is every function of the prelude, nested ones included, in the order of definition.
	Functions []*Function
	// Definitions are the top-level definitions: the builtins the prelude defines.
	Definitions []*Function
	visible     map[string]int
}

var (
	preludeInstance = sync.OnceValue(func() *Prelude { return LoadPrelude(preludeSource, true) })
	preludeExact    = sync.OnceValue(func() *Prelude { return LoadPrelude(preludeSource, false) })
)

// PreludeInstance is the prelude, with the native overrides of its builtins (Native.OverridesPrelude).
func PreludeInstance() *Prelude { return preludeInstance() }

// PreludeWithoutOverrides is the prelude exactly as written, every builtin bound to its
// definition: to check that the overrides change nothing.
func PreludeWithoutOverrides() *Prelude { return preludeExact() }

// Find gives the index in Functions of the builtin name/arity, or -1.
func (p *Prelude) Find(name string, arity int) int {
	if index, ok := p.visible[name+"/"+itoa(arity)]; ok {
		return index
	}
	return -1
}

// LoadPrelude parses and binds a prelude source. The embedded prelude must bind: a failure is a
// defect of the build, not of a program, and panics.
func LoadPrelude(source string, nativeOverrides bool) *Prelude {
	module, err := syntax.TryParseModule(source)
	var functions, definitions []*Function
	if err == nil {
		functions, definitions, err = tryBindPrelude(module, nativeOverrides)
	}
	if err != nil {
		panic("The prelude does not bind: " + err.Message + " at " + itoa(err.Line) + ":" + itoa(err.Column))
	}
	prelude := &Prelude{Functions: functions, Definitions: definitions, visible: map[string]int{}}
	for _, definition := range definitions {
		for index, function := range functions {
			if function == definition {
				prelude.visible[definition.Name+"/"+itoa(definition.Arity)] = index
				break
			}
		}
	}
	return prelude
}
