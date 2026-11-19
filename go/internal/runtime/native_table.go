package runtime

import (
	"github.com/devlooped/jqlite/go/internal/binding"
	"github.com/devlooped/jqlite/go/internal/values"
)

// native_table.go is the reference's Builtins/NativeTable.cs. It lives in the runtime package
// because Go forbids import cycles: the interpreter calls natives through the table, and the
// natives (package builtins) call back into the interpreter. The builtins package fills the table
// when it is initialized (builtins.init calls each family's register, as NativeTable's static
// constructor does), so a program that evaluates imports it.

// ValueNative is a native with value arguments (§9.1): called once per combination of its
// arguments' outputs, it gives one output (ok, result) or an error (not ok, err).
type ValueNative func(in *Interpreter, input values.Value, arguments []values.Value) (result, err values.Value, ok bool)

// ClosureNative is a native that takes its arguments as closures (Native.TakesClosures): it
// evaluates them itself with env, pushes its outputs to emit (with their paths in path mode, when it
// passes paths) and gives a signal, as Interpreter.Eval does.
type ClosureNative func(in *Interpreter, env *Env, arguments []binding.Node, input values.Value, path *Path, emit Emit) *Signal

// FormatFunction applies a format (§9.9) to a value: the formats are builtins, which the
// interpreter calls for @name and "\(…)" templates.
type FormatFunction func(name string, input values.Value) (result, err values.Value, ok bool)

type nativeTable struct {
	values   []ValueNative
	closures []ClosureNative
	format   FormatFunction
	loaded   bool
}

// NativeTable holds the implementations of the native catalogue, by Native.Index. Each family of
// natives registers its entries; every entry of the catalogue has one, which the table checks once,
// when it is loaded (a missing one is a defect of the build, not a run-time error).
var NativeTable = &nativeTable{
	values:   make([]ValueNative, len(binding.Natives.All)),
	closures: make([]ClosureNative, len(binding.Natives.All)),
}

// Value gives the implementation of a value native (one that does not take closures).
func (t *nativeTable) Value(native *binding.Native) ValueNative { return t.values[native.Index] }

// Closure gives the implementation of a native that takes closures.
func (t *nativeTable) Closure(native *binding.Native) ClosureNative { return t.closures[native.Index] }

// IsImplemented tells whether native has an implementation.
func (t *nativeTable) IsImplemented(native *binding.Native) bool {
	return t.values[native.Index] != nil || t.closures[native.Index] != nil
}

// Add registers a value native; registration checks the kind against the catalogue: a mismatch is
// a defect of the build.
func (t *nativeTable) Add(name string, arity int, function ValueNative) {
	t.values[t.find(name, arity, false).Index] = function
}

// AddClosure registers a native that takes closures.
func (t *nativeTable) AddClosure(name string, arity int, function ClosureNative) {
	t.closures[t.find(name, arity, true).Index] = function
}

// SetFormat registers the function of the formats.
func (t *nativeTable) SetFormat(function FormatFunction) { t.format = function }

// Seal checks that every native of the catalogue is implemented; called once every family is registered.
func (t *nativeTable) Seal() {
	for _, native := range binding.Natives.All {
		if !t.IsImplemented(native) {
			panic(native.String() + " has no implementation")
		}
	}
	if t.format == nil {
		panic("the formats are not registered")
	}
	t.loaded = true
}

func (t *nativeTable) find(name string, arity int, closures bool) *binding.Native {
	native := binding.Natives.Find(name, arity)
	if native == nil {
		panic(name + "/" + values.CodePoints.Decimal(arity) + " is not in the native catalogue")
	}
	if native.TakesClosures != closures {
		panic(native.String() + " is registered with the wrong kind")
	}
	return native
}

// Ok is a value native's success.
func (t *nativeTable) Ok(value values.Value) (result, err values.Value, ok bool) {
	return value, values.Null, true
}

// Fail is a value native's error.
func (t *nativeTable) Fail(message values.Value) (result, err values.Value, ok bool) {
	return values.Null, message, false
}
