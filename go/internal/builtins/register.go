// Package builtins implements the natives of spec §9 and registers them into runtime.NativeTable
// when it is initialized. A program that evaluates imports it (the host boundary does).
//
// This file is the static constructor of the reference's NativeTable: the table itself is
// runtime/native_table.go, since Go forbids the import cycle between the interpreter and the
// natives.
package builtins

import (
	"github.com/devlooped/jqlite/go/internal/binding"
	"github.com/devlooped/jqlite/go/internal/runtime"
	"github.com/devlooped/jqlite/go/internal/values"
)

func init() {
	CoreNatives.Register()
	LoopNatives.Register()
	PathNatives.Register()
	CollectionNatives.Register()
	StringNatives.Register()
	AssignNatives.Register()
	ModuleNatives.Register()
	Containment.Register()
	MathNatives.Register()
	DateNatives.Register()
	RegexNatives.Register()
	runtime.NativeTable.SetFormat(Formats.TryApply)
	runtime.NativeTable.Seal()
}

// Shorthands for the registrations and results of the natives.
var (
	table = runtime.NativeTable
	add   = runtime.NativeTable.Add
	addC  = runtime.NativeTable.AddClosure
	ok    = runtime.NativeTable.Ok
	fail  = runtime.NativeTable.Fail
)

type (
	value       = values.Value
	interpreter = runtime.Interpreter
	env         = runtime.Env
	path        = runtime.Path
	emit        = runtime.Emit
	signal      = runtime.Signal
	node        = binding.Node
)
