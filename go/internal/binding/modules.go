package binding

import (
	"github.com/devlooped/jqlite/go/internal/syntax"
	"github.com/devlooped/jqlite/go/internal/values"
)

// The modules group of spec §9.11, which a port may leave out. Everything of it is here and in
// builtins/module_natives.go, behind Resolver.bindImports.

// ModuleKind is what an import asks for (§9.11): a code module or a data module.
type ModuleKind int

// The kinds of modules.
const (
	ModuleCode ModuleKind = iota
	ModuleData
)

// ModuleSource is what the core needs from its host to load modules (§9.11, §12): the host decides
// where a module comes from. Called while binding, and at run time by modulemeta, possibly from
// several goroutines at once.
type ModuleSource interface {
	// TryResolve resolves the module path (as written in the directive) of the given kind,
	// imported with metadata (the directive's metadata object, or nil), from the module from (its
	// identity, or "" for the main program), to the module's identity (never ""): the same
	// identity means the same module. On failure, err is a message naming the path.
	TryResolve(path string, kind ModuleKind, metadata *values.Value, from string) (identity, err string, ok bool)

	// TryRead gives the source text of a module TryResolve found: read once per identity and binding.
	TryRead(identity string) (text, err string, ok bool)
}

// ModuleTable is the modules of a bound program, kept for modulemeta: the host's source and the
// description of every module loaded while binding. Immutable after binding.
type ModuleTable struct {
	source       ModuleSource
	descriptions map[string]values.Value
}

// TryDescribe is modulemeta of a path resolved as from the main program: the loaded module's
// description, or, for a module the program does not import, the description of its parse.
func (t *ModuleTable) TryDescribe(path string) (description values.Value, err string, ok bool) {
	identity, err, ok := t.source.TryResolve(path, ModuleCode, nil, "")
	if !ok {
		return values.Null, err, false
	}
	if description, found := t.descriptions[identity]; found {
		return description, "", true
	}
	text, err, ok := t.source.TryRead(identity)
	if !ok {
		return values.Null, err, false
	}
	module, syntaxError := syntax.TryParseModule(text)
	if syntaxError != nil {
		return values.Null, BindingMessages.InModule(syntaxError.Message, identity, syntaxError.Line, syntaxError.Column), false
	}
	return Describe(module), "", true
}

// Describe is what modulemeta outputs for a module (§9.11): its metadata keys, then deps (per
// directive: its metadata keys, then as unless it is an include, is_data, relpath), then defs,
// each top-level name/arity once, by first definition.
func Describe(module *syntax.Program) values.Value {
	description := values.EmptyObject
	if module.Metadata != nil {
		description = module.Metadata.AsObject()
	}
	deps := make([]values.Value, len(module.Imports))
	for i, imp := range module.Imports {
		dep := values.EmptyObject
		if imp.Metadata != nil {
			dep = imp.Metadata.AsObject()
		}
		if imp.HasAlias {
			dep = dep.Set("as", values.FromString(imp.Alias))
		}
		deps[i] = values.FromObject(dep.
			Set("is_data", values.FromBool(imp.IsData)).
			Set("relpath", values.FromString(imp.Path)))
	}

	var defs []values.Value
	seen := map[string]bool{}
	for _, definition := range module.Definitions {
		signature := definition.Name + "/" + itoa(len(definition.Params))
		if !seen[signature] {
			seen[signature] = true
			defs = append(defs, values.FromString(signature))
		}
	}

	return values.FromObject(description.
		Set("deps", values.FromArray(values.ArrayFromOwned(deps))).
		Set("defs", values.FromArray(values.ArrayFromOwned(defs))))
}

// export is one top-level name/arity of a code module with its function.
type export struct {
	name     string
	arity    int
	function int
}

// Module is a loaded module.
type Module struct {
	Identity string
	Kind     ModuleKind
	// Program is a code module's parse.
	Program *syntax.Program
	// Data is a data module's values, as one array.
	Data values.Value
	// Imports are the identities the module's directives resolved to, in order.
	Imports []string
	// The code module's exports once bound: each top-level name/arity with its function, the last
	// definition winning, in the order of first definition.
	exports     map[string]int
	exportOrder []export
	// On the loading stack: importing it again is a cycle.
	loading bool
}

func (m *Module) addExport(name string, arity, function int) {
	key := name + "/" + itoa(arity)
	if i, ok := m.exports[key]; ok {
		m.exportOrder[i].function = function
		return
	}
	m.exports[key] = len(m.exportOrder)
	m.exportOrder = append(m.exportOrder, export{name, arity, function})
}

// ModuleLoader loads the modules of one binding: resolves and parses each identity once, depth
// first from the main program's directives, with an explicit stack (an import chain of any length
// is no recursion), detecting cycles. Code modules are then bound one after the other, each after
// the modules it imports, each in a scope of its own (Resolver). Loaded modules live as long as
// the binding: a host that wants to share parses between programs caches in its source.
type ModuleLoader struct {
	source  ModuleSource
	modules map[string]*Module
}

func newModuleLoader(source ModuleSource) *ModuleLoader {
	return &ModuleLoader{source: source, modules: map[string]*Module{}}
}

type loadFrame struct {
	module     *Module
	imports    []*syntax.Import
	identities *[]string
	next       int
}

// TryLoad loads what imports (the main program's directives) need. identities are what they
// resolved to; order the code modules to bind, each after those it imports.
func (l *ModuleLoader) TryLoad(imports []*syntax.Import) (identities []string, order []*Module, err *syntax.SyntaxError) {
	// One frame per module being loaded: its directives, the next one to resolve, where identities go.
	frames := []*loadFrame{{nil, imports, &identities, 0}}
	for len(frames) > 0 {
		frame := frames[len(frames)-1]
		if frame.next == len(frame.imports) {
			frames = frames[:len(frames)-1]
			if frame.module != nil {
				frame.module.loading = false
				order = append(order, frame.module)
			}
			continue
		}

		imp := frame.imports[frame.next]
		frame.next++
		kind := ModuleCode
		if imp.IsData {
			kind = ModuleData
		}
		from := ""
		if frame.module != nil {
			from = frame.module.Identity
		}
		identity, failure, ok := l.tryResolve(imp, kind, from)
		if !ok {
			return nil, nil, moduleError(failure, imp.Position, from)
		}

		*frame.identities = append(*frame.identities, identity)
		if loaded, found := l.modules[identity]; found {
			if loaded.loading {
				return nil, nil, moduleError(BindingMessages.CircularImport(identity), imp.Position, from)
			}
			continue
		}

		text, failure, ok := l.source.TryRead(identity)
		if !ok {
			return nil, nil, moduleError(failure, imp.Position, from)
		}

		if kind == ModuleData {
			data, invalid, ok := values.ValueText.TryParseSequence(text)
			if !ok {
				return nil, nil, moduleError(BindingMessages.InvalidData(identity, invalid.AsString()), imp.Position, from)
			}
			l.modules[identity] = &Module{Identity: identity, Kind: kind, Data: data}
			continue
		}

		program, syntaxError := syntax.TryParseModule(text)
		if syntaxError != nil {
			withModule := *syntaxError
			withModule.Module = identity
			return nil, nil, &withModule
		}

		code := &Module{Identity: identity, Kind: kind, Program: program, exports: map[string]int{}, loading: true}
		l.modules[identity] = code
		frames = append(frames, &loadFrame{code, program.Imports, &code.Imports, 0})
	}
	return identities, order, nil
}

// Table gives the descriptions of the code modules loaded, for modulemeta; nil without a source.
func (l *ModuleLoader) Table() *ModuleTable {
	if l == nil || l.source == nil {
		return nil
	}
	descriptions := map[string]values.Value{}
	for _, module := range l.modules {
		if module.Program != nil {
			descriptions[module.Identity] = Describe(module.Program)
		}
	}
	return &ModuleTable{l.source, descriptions}
}

// tryResolve: without a source no module exists.
func (l *ModuleLoader) tryResolve(imp *syntax.Import, kind ModuleKind, from string) (identity, err string, ok bool) {
	if l.source != nil {
		return l.source.TryResolve(imp.Path, kind, imp.Metadata, from)
	}
	return "", BindingMessages.ModuleNotFound(imp.Path), false
}

func moduleError(message string, position syntax.Position, module string) *syntax.SyntaxError {
	return &syntax.SyntaxError{Message: message, Line: position.Line, Column: position.Column, Module: module}
}

// newModuleResolver gives a resolver for a module: a scope of its own, the functions, host
// variables and constants of the whole program.
func (r *Resolver) newModuleResolver(module *Module) *Resolver {
	return &Resolver{prelude: r.prelude, nativeOverrides: r.nativeOverrides, shared: r.shared, loader: r.loader, module: module}
}

// bindImports is where modules enter a scope (§9.11), before the top-level definitions: the main
// program loads and binds every module it needs first. Each directive then adds its module's
// exports as definitions at that point: alias::f for import, f for include, so that later
// definitions and directives shadow them as usual; a data import adds the constants $alias and
// $alias::alias.
func (r *Resolver) bindImports(imports []*syntax.Import) bool {
	if len(imports) == 0 {
		return true
	}

	var identities []string
	if r.module != nil {
		identities = r.module.Imports
	} else {
		var order []*Module
		var failure *syntax.SyntaxError
		identities, order, failure = r.loader.TryLoad(imports)
		if failure != nil {
			if r.err == nil {
				r.err = failure
			}
			return false
		}
		for _, code := range order {
			if !r.bindModule(code) {
				return false
			}
		}
	}

	for i, imp := range imports {
		loaded := r.loader.modules[identities[i]]
		if imp.IsData {
			r.shared.constants = append(r.shared.constants, loaded.Data)
			slot := len(r.shared.constants) - 1
			r.scope = append(r.scope, entry{kind: entryConstant, name: imp.Alias, slot: slot})
			r.scope = append(r.scope, entry{kind: entryConstant, name: imp.Alias + "::" + imp.Alias, slot: slot})
			continue
		}

		prefix := ""
		if imp.HasAlias {
			prefix = imp.Alias + "::"
		}
		for _, e := range loaded.exportOrder {
			r.scope = append(r.scope, entry{kind: entryFunction, name: prefix + e.name, arity: e.arity, slot: e.function})
		}
	}
	return true
}

// bindModule binds a code module in a scope of its own and records its exports.
func (r *Resolver) bindModule(code *Module) bool {
	resolver := r.newModuleResolver(code)
	if resolver.bindProgram(code.Program) == nil {
		if r.err == nil {
			r.err = resolver.err
		}
		return false
	}

	for _, index := range resolver.exports {
		function := r.shared.functions[index]
		code.addExport(function.Name, function.Arity, index)
	}
	return true
}
