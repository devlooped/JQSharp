package builtins

import (
	"github.com/devlooped/jqlite/go/internal/binding"
	"github.com/devlooped/jqlite/go/internal/values"
)

type moduleNatives struct{}

// ModuleNatives is modulemeta/0 (§9.11), the native of the modules group: a port without the group
// (see binding/modules.go) leaves this file out and does not list it.
var ModuleNatives moduleNatives

func (moduleNatives) Register() { add("modulemeta", 0, modulemeta) }

// modulemeta gives the description of the module the input names, resolved as from the main
// program with the source the program was bound with: what the loader recorded when the program
// loaded it.
func modulemeta(in *interpreter, input value, _ []value) (value, value, bool) {
	if input.Kind() != values.KindString {
		return fail(values.Errors.ModulemetaNotString())
	}
	p := input.AsString()
	modules := in.Program().Modules
	if modules == nil {
		return fail(values.FromString(binding.BindingMessages.ModuleNotFound(p)))
	}
	description, failure, found := modules.TryDescribe(p)
	if !found {
		return fail(values.FromString(failure))
	}
	return ok(description)
}
