package binding

import (
	"strings"

	"github.com/devlooped/jqlite/go/internal/syntax"
	"github.com/devlooped/jqlite/go/internal/values"
)

type entryKind int

const (
	entryVariable entryKind = iota
	entryParameter
	entryLabel
	entryFunction
	entryConstant
)

// entry is a name in scope. Variable, Parameter, Label: depth is the frame holding it, slot its
// slot there. Constant: a data module's value (modules.go), constants[slot]. Function: depth is the
// number of frames where it is defined, slot its index in functions, topLevel whether it is a
// top-level definition of the program or module.
type entry struct {
	kind     entryKind
	name     string
	arity    int
	depth    int
	slot     int
	topLevel bool
}

type step int

const (
	// stepVisit resolves node, or schedules its children and its build.
	stepVisit step = iota
	// stepBuild pops the bound children of node and pushes the bound node.
	stepBuild
	// stepBuildCall pops the arguments of the call node and pushes the call of functions[a],
	// defined b frames out.
	stepBuildCall
	// stepBuildNative pops the arguments of the call node and pushes the call of Natives.All[a].
	stepBuildNative
	// stepMakeBinder pops the bound pattern keys of the binding node and pushes its binder.
	stepMakeBinder
	// stepEnterBinder pushes the frame of the binder on top of binders.
	stepEnterBinder
	// stepDefine defines definition at this point of the scope (a = 1 at the top level of the program).
	stepDefine
	// stepEndFunction pops the body of definition, which is functions[a].
	stepEndFunction
	// stepLeave restores the scope to a entries and the depth to b.
	stepLeave
)

type task struct {
	step       step
	node       syntax.Node
	definition *syntax.Definition
	a, b       int
}

// shared is what the main program and its modules (modules.go) share: one function table, one
// list of host variables, one list of data-module constants.
type shared struct {
	functions     []*Function
	hostVariables []string
	constants     []values.Value
}

// defining is a function whose body is being bound. single: whether its calls of itself in that
// body are bound as single (spec §7.7): assumed at first, and checked when the body is bound,
// unless the answer is known already. selfCalls counts the calls bound as single on that assumption.
type defining struct {
	index           int
	valueParameters []int
	single          bool
	known           bool
	selfCalls       int
}

type pendingBinder struct {
	binder *Binder
	names  []string
}

// Resolver binds a parsed program (spec §7): walks the AST once and resolves every variable,
// closure parameter, function call and label to its definition, producing the BoundProgram
// described at the top of bound.go. Unknown names are binding errors with a position; nothing panics.
//
// The scope is a stack of entries searched from the top, so the innermost definition wins. A call
// resolves to the innermost user definition or closure parameter of that name and arity, then to
// the prelude, then to the natives (§7.7). Entries record the frame depth at which they live, so
// the distance to a definition is the current depth minus the entry's.
//
// The walk does not recurse: it is a loop over a stack of tasks: visit resolves what it can at once
// and pushes the node's children, the scope changes between them and the final build; bound
// children wait on a stack of results. Only patterns are walked recursively, one small frame per
// level of pattern nesting, which the parser limits.
type Resolver struct {
	prelude *Prelude
	// Whether builtins with a native override bind to it (Native.OverridesPrelude).
	nativeOverrides bool
	scope           []entry
	shared          *shared
	exports         []int
	tasks           []task
	results         []Node
	// The binders made and not yet built into their node, with the names of their slots.
	binders []pendingBinder
	// The functions being defined, innermost last.
	defining []*defining
	// While the body of a function is bound a second time (endFunction): whether each function
	// nested in it is single, as the first time found, by index from replayStart.
	replay      []bool
	replaying   bool
	replayStart int
	depth       int
	err         *syntax.SyntaxError
	// The modules of this binding (shared by the main program and every module), and the module
	// this resolver binds (nil for the main program and the prelude).
	loader *ModuleLoader
	module *Module
}

var identity = NewIdentity()

func newResolver(prelude *Prelude, nativeOverrides bool, loader *ModuleLoader) *Resolver {
	s := &shared{}
	if prelude != nil {
		s.functions = append(s.functions, prelude.Functions...)
	}
	return &Resolver{prelude: prelude, nativeOverrides: nativeOverrides, shared: s, loader: loader}
}

// TryBind binds a program against the prelude and the natives, loading its modules from modules
// (§9.11; nil: no module exists). Without nativeOverrides every prelude builtin binds to its
// definition, even where a native overrides it.
func TryBind(program *syntax.Program, nativeOverrides bool, modules ModuleSource) (*BoundProgram, *syntax.SyntaxError) {
	prelude := PreludeInstance()
	if !nativeOverrides {
		prelude = PreludeWithoutOverrides()
	}
	resolver := newResolver(prelude, nativeOverrides, newModuleLoader(modules))
	body := resolver.bindProgram(program)
	if body == nil {
		return nil, resolver.err
	}
	return &BoundProgram{
		Functions:     resolver.shared.functions,
		HostVariables: resolver.shared.hostVariables,
		Body:          body,
		Modules:       resolver.loader.Table(),
	}, nil
}

// TryCompile parses and binds a program.
func TryCompile(source string, nativeOverrides bool, modules ModuleSource) (*BoundProgram, *syntax.SyntaxError) {
	program, err := syntax.TryParse(source)
	if err != nil {
		return nil, err
	}
	return TryBind(program, nativeOverrides, modules)
}

// tryBindPrelude binds the prelude module against the natives only: all its functions, and its
// top-level definitions in order.
func tryBindPrelude(module *syntax.Program, nativeOverrides bool) (functions, definitions []*Function, err *syntax.SyntaxError) {
	resolver := newResolver(nil, nativeOverrides, nil)
	if resolver.bindProgram(module) == nil {
		return nil, nil, resolver.err
	}
	functions = resolver.shared.functions
	for _, index := range resolver.exports {
		definitions = append(definitions, functions[index])
	}
	return functions, definitions, nil
}

// bindProgram gives the body of a program (Identity for a module, whose result is its
// definitions), or nil.
func (r *Resolver) bindProgram(program *syntax.Program) Node {
	if !r.bindImports(program.Imports) {
		return nil
	}

	// Pushed last first: the definitions in order, then the body.
	if program.Body == nil {
		r.results = append(r.results, identity)
	} else {
		r.tasks = append(r.tasks, task{step: stepVisit, node: program.Body})
	}
	for i := len(program.Definitions) - 1; i >= 0; i-- {
		r.tasks = append(r.tasks, task{step: stepDefine, definition: program.Definitions[i], a: 1})
	}

	for len(r.tasks) > 0 && r.err == nil {
		t := r.tasks[len(r.tasks)-1]
		r.tasks = r.tasks[:len(r.tasks)-1]
		r.run(t)
	}

	if r.err != nil {
		return nil
	}
	return r.results[0]
}

func (r *Resolver) run(t task) {
	switch t.step {
	case stepVisit:
		r.visit(t.node)
	case stepBuild:
		r.results = append(r.results, r.build(t.node, t.a))
	case stepBuildCall:
		arguments := r.popAll(len(t.node.(*syntax.Call).Arguments))
		r.results = append(r.results, r.call(t.a, t.b, arguments))
	case stepBuildNative:
		arguments := r.popAll(len(t.node.(*syntax.Call).Arguments))
		r.results = append(r.results, NewCallNative(Natives.All[t.a], arguments))
	case stepMakeBinder:
		r.makeBinder(t.node)
	case stepEnterBinder:
		r.enterBinder()
	case stepDefine:
		r.define(t.definition, t.a == 1)
	case stepEndFunction:
		r.endFunction(t.definition, t.a)
	default:
		r.scope = r.scope[:t.a]
		r.depth = t.b
	}
}

// visit resolves a node. Tasks run last pushed first, so each case pushes its steps in reverse order.
func (r *Resolver) visit(node syntax.Node) {
	switch n := node.(type) {
	case *syntax.Identity:
		r.results = append(r.results, identity)
	case *syntax.Literal:
		r.results = append(r.results, NewLiteral(n.Value))
	case *syntax.Format:
		r.results = append(r.results, NewFormat(n.Name))
	case *syntax.Variable:
		r.add(r.bindVariable(n))
	case *syntax.Break:
		r.add(r.bindBreak(n))
	case *syntax.Call:
		r.visitCall(n)
	case *syntax.Template:
		r.pushStep(stepBuild, node)
		r.pushAll(n.Parts)
	case *syntax.Pipe:
		r.pushStep(stepBuild, node, n.Right, n.Left)
	case *syntax.Comma:
		r.visitComma(n)
	case *syntax.Binary:
		r.pushStep(stepBuild, node, n.Right, n.Left)
	case *syntax.And:
		r.pushStep(stepBuild, node, n.Right, n.Left)
	case *syntax.Or:
		r.pushStep(stepBuild, node, n.Right, n.Left)
	case *syntax.Alternative:
		r.pushStep(stepBuild, node, n.Right, n.Left)
	case *syntax.Negate:
		r.pushStep(stepBuild, node, n.Operand)
	case *syntax.Index:
		r.pushStep(stepBuild, node, n.Key, n.Target)
	case *syntax.Slice:
		r.pushStep(stepBuild, node, n.To, n.From, n.Target)
	case *syntax.Iterate:
		r.pushStep(stepBuild, node, n.Target)
	case *syntax.Try:
		r.pushStep(stepBuild, node, n.Handler, n.Body)
	case *syntax.If:
		r.pushStep(stepBuild, node, n.Else, n.Then, n.Condition)
	case *syntax.ArrayExpr:
		r.pushStep(stepBuild, node, n.Body)
	case *syntax.ObjectExpr:
		r.pushStep(stepBuild, node)
		for i := len(n.Entries) - 1; i >= 0; i-- {
			r.push(n.Entries[i].Value, n.Entries[i].Key)
		}
	case *syntax.Bind:
		// The source and the pattern keys in the current scope, the body with the variables' frame.
		r.leave()
		r.pushStep(stepBuild, node, n.Body)
		r.tasks = append(r.tasks, task{step: stepEnterBinder})
		r.pushBinder(node, n.Patterns)
		r.push(n.Source)
	case *syntax.Reduce:
		r.leave()
		r.pushStep(stepBuild, node, n.Update)
		r.tasks = append(r.tasks, task{step: stepEnterBinder})
		r.push(n.Init)
		r.pushBinder(node, n.Patterns)
		r.push(n.Source)
	case *syntax.Foreach:
		r.leave()
		r.pushStep(stepBuild, node, n.Extract, n.Update)
		r.tasks = append(r.tasks, task{step: stepEnterBinder})
		r.push(n.Init)
		r.pushBinder(node, n.Patterns)
		r.push(n.Source)
	case *syntax.Label:
		r.leave()
		r.pushStep(stepBuild, node, n.Body)
		r.depth++
		r.scope = append(r.scope, entry{kind: entryLabel, name: n.Name, depth: r.depth})
	case *syntax.FuncDef:
		// The definition is in functions; the node's result is the rest's.
		r.leave()
		r.push(n.Rest)
		r.tasks = append(r.tasks, task{step: stepDefine, definition: n.Definition})
	default:
		panic("Unknown node")
	}
}

// visitComma: a left-nested chain of commas becomes one node with the items in order.
func (r *Resolver) visitComma(comma *syntax.Comma) {
	var items []syntax.Node
	for {
		items = append(items, comma.Right)
		left, ok := comma.Left.(*syntax.Comma)
		if !ok {
			break
		}
		comma = left
	}

	items = append(items, comma.Left)
	for i, j := 0, len(items)-1; i < j; i, j = i+1, j-1 {
		items[i], items[j] = items[j], items[i]
	}
	r.tasks = append(r.tasks, task{step: stepBuild, node: comma, a: len(items)})
	r.pushAll(items)
}

// build pops the bound children of node, last first, and builds its bound node; count is the
// number of items of a comma.
func (r *Resolver) build(node syntax.Node, count int) Node {
	switch n := node.(type) {
	case *syntax.Template:
		return NewTemplate(n.Format, n.HasFormat, n.Texts, r.popAll(len(n.Parts)))
	case *syntax.Pipe:
		right := r.pop()
		return NewPipe(r.pop(), right)
	case *syntax.Comma:
		return NewComma(r.popAll(count))
	case *syntax.Binary:
		right := r.pop()
		return NewBinary(n.Op, r.pop(), right)
	case *syntax.And:
		right := r.pop()
		return NewAnd(r.pop(), right)
	case *syntax.Or:
		right := r.pop()
		return NewOr(r.pop(), right)
	case *syntax.Alternative:
		right := r.pop()
		return NewAlternative(r.pop(), right)
	case *syntax.Negate:
		return NewNegate(r.pop())
	case *syntax.Index:
		key := r.pop()
		return NewIndex(r.pop(), key)
	case *syntax.Slice:
		var to, from Node
		if n.To != nil {
			to = r.pop()
		}
		if n.From != nil {
			from = r.pop()
		}
		return NewSlice(r.pop(), from, to)
	case *syntax.Iterate:
		return NewIterate(r.pop())
	case *syntax.Try:
		var handler Node
		if n.Handler != nil {
			handler = r.pop()
		}
		return NewTry(r.pop(), handler)
	case *syntax.If:
		otherwise := r.pop()
		then := r.pop()
		return NewIf(r.pop(), then, otherwise)
	case *syntax.ArrayExpr:
		var body Node
		if n.Body != nil {
			body = r.pop()
		}
		return NewArrayExpr(body)
	case *syntax.ObjectExpr:
		entries := make([]ObjectEntry, len(n.Entries))
		for i := len(entries) - 1; i >= 0; i-- {
			var value Node
			if n.Entries[i].Value != nil {
				value = r.pop()
			}
			entries[i] = ObjectEntry{r.pop(), value}
		}
		return NewObjectExpr(entries)
	case *syntax.Bind:
		body := r.pop()
		return NewBind(r.pop(), r.popBinder(), body)
	case *syntax.Reduce:
		update := r.pop()
		init := r.pop()
		return NewReduce(r.pop(), r.popBinder(), init, update)
	case *syntax.Foreach:
		extract := r.pop()
		update := r.pop()
		init := r.pop()
		return NewForeach(r.pop(), r.popBinder(), init, update, extract)
	default:
		return NewLabel(r.pop())
	}
}

// define defines a function at the current point of the scope, visible in its own body and after it.
func (r *Resolver) define(definition *syntax.Definition, topLevel bool) {
	index := len(r.shared.functions)
	r.shared.functions = append(r.shared.functions, nil)
	if topLevel {
		r.exports = append(r.exports, index)
	}
	r.scope = append(r.scope, entry{entryFunction, definition.Name, len(definition.Params), r.depth, index, topLevel})

	// Single until its body says otherwise, or what the first binding of this body found.
	replayed := r.replaying && index-r.replayStart < len(r.replay)
	single := !replayed || r.replay[index-r.replayStart]
	r.defining = append(r.defining, &defining{index: index, valueParameters: valueParameters(definition), single: single, known: replayed})

	r.leave()
	r.tasks = append(r.tasks, task{step: stepEndFunction, definition: definition, a: index})
	r.push(definition.Body)
	if len(definition.Params) == 0 {
		return
	}
	r.depth++
	for slot, parameter := range definition.Params {
		r.scope = append(r.scope, entry{kind: entryParameter, name: parameter.Name, depth: r.depth, slot: slot})
	}
	// Each $name parameter then has a frame of its own, first parameter outermost (bindParameters).
	for _, parameter := range definition.Params {
		if parameter.IsVariable {
			r.depth++
			r.scope = append(r.scope, entry{kind: entryVariable, name: parameter.Name, depth: r.depth})
		}
	}
}

// endFunction: the body of functions[index] is bound: the function is single when its body is
// (spec §7.7).
func (r *Resolver) endFunction(definition *syntax.Definition, index int) {
	body := r.pop()
	own := r.defining[len(r.defining)-1]
	if own.single && !body.AtMostOne() && !own.known {
		// It was assumed single and is not. The calls of itself bound as single are wrong, and so
		// is every node built on them: bind the body again, this time knowing. The functions
		// nested in it do not depend on the assumption (their calls of this one are never single,
		// call), so they keep what this binding found and are not bound a third time.
		own.single = false
		own.known = true
		if own.selfCalls > 0 {
			r.replay = nil
			r.replaying = true
			r.replayStart = index + 1
			for _, function := range r.shared.functions[r.replayStart:] {
				r.replay = append(r.replay, function.Single)
			}
			r.shared.functions = r.shared.functions[:r.replayStart]
			r.tasks = append(r.tasks, task{step: stepEndFunction, definition: definition, a: index})
			r.push(definition.Body)
			return
		}
	}

	r.defining = r.defining[:len(r.defining)-1]
	if r.replaying && r.replayStart == index+1 {
		r.replay = nil
		r.replaying = false
	}
	r.shared.functions[index] = &Function{definition.Name, len(definition.Params), bindParameters(definition, body),
		body.AtMostOne(), own.valueParameters}
}

// call builds a call of functions[function]: single when the function is and the argument of each
// of its $ parameters is. A function still being defined is single in its own body as long as its
// defining says so; seen from a function nested in it, it is not.
func (r *Resolver) call(function, hops int, arguments []Node) *CallFunction {
	single := false
	if defined := r.shared.functions[function]; defined != nil {
		single = defined.Single && AllSingle(defined.ValueParameters, arguments)
	} else if own := r.defining[len(r.defining)-1]; own.single && own.index == function && AllSingle(own.valueParameters, arguments) {
		single = true
		if !own.known {
			own.selfCalls++
		}
	}
	return NewCallFunction(function, hops, arguments, single)
}

// valueParameters gives the slots of the $ parameters.
func valueParameters(definition *syntax.Definition) []int {
	slots := []int{}
	for slot, parameter := range definition.Params {
		if parameter.IsVariable {
			slots = append(slots, slot)
		}
	}
	return slots
}

var parameterBinder = NewBinder([]Pattern{&VariablePattern{0}}, 1)

// bindParameters: def f($a; $b): B is def f(a; b): a as $a | b as $b | B, except that each
// binding names its parameter by position: the k-th $ parameter's frame is k frames inside the
// parameters' frame.
func bindParameters(definition *syntax.Definition, body Node) Node {
	variables := 0
	for _, parameter := range definition.Params {
		if parameter.IsVariable {
			variables++
		}
	}
	for slot := len(definition.Params) - 1; slot >= 0; slot-- {
		if definition.Params[slot].IsVariable {
			variables--
			body = NewBind(NewCallParameter(variables, slot), parameterBinder, body)
		}
	}
	return body
}

func (r *Resolver) bindVariable(variable *syntax.Variable) Node {
	// The parser lets nothing bind $__loc__ (§7.5).
	if variable.Name == syntax.LocationVariable {
		return NewLiteral(r.location(variable.Position))
	}

	for i := len(r.scope) - 1; i >= 0; i-- {
		e := r.scope[i]
		if e.kind == entryVariable && e.name == variable.Name {
			return NewVariable(r.depth-e.depth, e.slot)
		}
		if e.kind == entryConstant && e.name == variable.Name {
			return NewLiteral(r.shared.constants[e.slot])
		}
	}

	if variable.Name == "ENV" {
		return NewEnvVariable()
	}
	// A qualified name can only come from a data import (bindImports); the prelude has no host variables.
	if strings.Contains(variable.Name, "::") || r.prelude == nil {
		r.fail(BindingMessages.VariableNotDefined(variable.Name), variable.Position)
		return nil
	}

	index := -1
	for i, name := range r.shared.hostVariables {
		if name == variable.Name {
			index = i
			break
		}
	}
	if index < 0 {
		index = len(r.shared.hostVariables)
		r.shared.hostVariables = append(r.shared.hostVariables, variable.Name)
	}
	return NewHostVariable(variable.Name, index)
}

// location gives $__loc__: {"file": the module's identity or "<top-level>", "line": the line of its '$'}.
func (r *Resolver) location(position syntax.Position) values.Value {
	file := "<top-level>"
	if r.module != nil {
		file = r.module.Identity
	}
	builder := values.NewObjectBuilder(2)
	builder.Set("file", values.FromString(file))
	builder.Set("line", values.FromNumber(float64(position.Line)))
	return values.FromObject(builder.ToObject())
}

func (r *Resolver) visitCall(call *syntax.Call) {
	arity := len(call.Arguments)
	for i := len(r.scope) - 1; i >= 0; i-- {
		e := r.scope[i]
		if e.name != call.Name || e.arity != arity {
			continue
		}
		if e.kind == entryParameter {
			r.results = append(r.results, NewCallParameter(r.depth-e.depth, e.slot))
			return
		}

		if e.kind == entryFunction {
			// Inside the prelude, its own builtins with a native override bind to the native too.
			if native := r.override(call.Name, arity); r.prelude == nil && e.topLevel && native != nil {
				r.tasks = append(r.tasks, task{step: stepBuildNative, node: call, a: native.Index})
			} else {
				r.tasks = append(r.tasks, task{step: stepBuildCall, node: call, a: e.slot, b: r.depth - e.depth})
			}
			r.pushAll(call.Arguments)
			return
		}
	}

	// The prelude is defined in the outermost, empty environment: every frame of the caller is left.
	function := -1
	if r.prelude != nil {
		function = r.prelude.Find(call.Name, arity)
	}
	if function >= 0 {
		if native := r.override(call.Name, arity); native != nil {
			r.tasks = append(r.tasks, task{step: stepBuildNative, node: call, a: native.Index})
		} else {
			r.tasks = append(r.tasks, task{step: stepBuildCall, node: call, a: function, b: r.depth})
		}
	} else if native := Natives.Find(call.Name, arity); native != nil {
		r.tasks = append(r.tasks, task{step: stepBuildNative, node: call, a: native.Index})
	} else {
		r.fail(BindingMessages.NotDefined(call.Name, arity), call.Position)
	}
	r.pushAll(call.Arguments)
}

// override gives the native override of a prelude builtin, when overrides are on.
func (r *Resolver) override(name string, arity int) *Native {
	if !r.nativeOverrides {
		return nil
	}
	return Natives.Override(name, arity)
}

func (r *Resolver) bindBreak(node *syntax.Break) Node {
	for i := len(r.scope) - 1; i >= 0; i-- {
		e := r.scope[i]
		if e.kind == entryLabel && e.name == node.Name {
			return NewBreak(r.depth - e.depth)
		}
	}
	r.fail(BindingMessages.LabelNotDefined(node.Name), node.Position)
	return nil
}

// pushBinder schedules the binder of node: its pattern keys, in destructuring order, then makeBinder.
func (r *Resolver) pushBinder(node syntax.Node, patterns []syntax.Pattern) {
	r.tasks = append(r.tasks, task{step: stepMakeBinder, node: node})
	var keys []syntax.Node
	for _, pattern := range patterns {
		keys = collectKeys(pattern, keys)
	}
	r.pushAll(keys)
}

func collectKeys(pattern syntax.Pattern, keys []syntax.Node) []syntax.Node {
	switch p := pattern.(type) {
	case *syntax.ArrayPattern:
		for i := len(p.Elements) - 1; i >= 0; i-- {
			keys = collectKeys(p.Elements[i], keys)
		}
	case *syntax.ObjectPattern:
		for _, e := range p.Entries {
			keys = append(keys, e.Key)
			if e.Value != nil {
				keys = collectKeys(e.Value, keys)
			}
		}
	}
	return keys
}

// makeBinder builds the ?// alternatives of a binding from its bound keys (the last results, in
// the order of collectKeys). Every distinct variable name of all the alternatives gets a slot. A
// name repeated in one pattern (§7.5): without ?// its first occurrence in matching order gives the
// value, so later ones discard theirs; with ?// every occurrence stores and the last one stays.
func (r *Resolver) makeBinder(node syntax.Node) {
	var patterns []syntax.Pattern
	switch n := node.(type) {
	case *syntax.Bind:
		patterns = n.Patterns
	case *syntax.Reduce:
		patterns = n.Patterns
	default:
		patterns = node.(*syntax.Foreach).Patterns
	}

	keys := 0
	for _, pattern := range patterns {
		keys += len(collectKeys(pattern, nil))
	}
	next := len(r.results) - keys
	var names []string
	var seen map[string]bool
	if len(patterns) == 1 {
		seen = map[string]bool{}
	}
	bound := make([]Pattern, len(patterns))
	for i, pattern := range patterns {
		bound[i] = r.bindPattern(pattern, &names, seen, &next)
	}

	r.results = r.results[:len(r.results)-keys]
	r.binders = append(r.binders, pendingBinder{NewBinder(bound, len(names)), names})
}

// bindPattern visits elements and entries in the order of collectKeys, which is matching order
// (spec §7.5).
func (r *Resolver) bindPattern(pattern syntax.Pattern, names *[]string, seen map[string]bool, next *int) Pattern {
	switch p := pattern.(type) {
	case *syntax.VariablePattern:
		return &VariablePattern{slotOf(p.Name, names, seen)}
	case *syntax.ArrayPattern:
		elements := make([]Pattern, len(p.Elements))
		for i := len(elements) - 1; i >= 0; i-- {
			elements[i] = r.bindPattern(p.Elements[i], names, seen, next)
		}
		return &ArrayPattern{elements}
	default:
		entries := pattern.(*syntax.ObjectPattern).Entries
		bound := make([]PatternEntry, len(entries))
		for i, e := range entries {
			key := r.results[*next]
			*next++
			slot := -1
			if e.HasVariable {
				slot = slotOf(e.Variable, names, seen)
			}
			var value Pattern
			if e.Value != nil {
				value = r.bindPattern(e.Value, names, seen, next)
			}
			bound[i] = PatternEntry{key, slot, value}
		}
		return &ObjectPattern{bound}
	}
}

// slotOf gives the slot of a variable in names, or -1 when seen (first occurrence wins) already
// has the name.
func slotOf(name string, names *[]string, seen map[string]bool) int {
	if seen != nil {
		if seen[name] {
			return -1
		}
		seen[name] = true
	}
	for slot, n := range *names {
		if n == name {
			return slot
		}
	}
	*names = append(*names, name)
	return len(*names) - 1
}

// enterBinder pushes the frame of the last binder made: one variable per name, in slot order.
func (r *Resolver) enterBinder() {
	names := r.binders[len(r.binders)-1].names
	r.depth++
	for slot, name := range names {
		r.scope = append(r.scope, entry{kind: entryVariable, name: name, depth: r.depth, slot: slot})
	}
}

func (r *Resolver) popBinder() *Binder {
	binder := r.binders[len(r.binders)-1].binder
	r.binders = r.binders[:len(r.binders)-1]
	return binder
}

// leave schedules the restoring of the scope as it is now, after the tasks pushed later.
func (r *Resolver) leave() {
	r.tasks = append(r.tasks, task{step: stepLeave, a: len(r.scope), b: r.depth})
}

func (r *Resolver) pushStep(s step, node syntax.Node, children ...syntax.Node) {
	r.tasks = append(r.tasks, task{step: s, node: node})
	r.push(children...)
}

// push schedules the visit of the given nodes, missing ones skipped, the first argument first (it
// runs last).
func (r *Resolver) push(nodes ...syntax.Node) {
	for _, node := range nodes {
		if node != nil {
			r.tasks = append(r.tasks, task{step: stepVisit, node: node})
		}
	}
}

func (r *Resolver) pushAll(nodes []syntax.Node) {
	for i := len(nodes) - 1; i >= 0; i-- {
		r.tasks = append(r.tasks, task{step: stepVisit, node: nodes[i]})
	}
}

func (r *Resolver) add(node Node) {
	if node != nil {
		r.results = append(r.results, node)
	}
}

func (r *Resolver) pop() Node {
	node := r.results[len(r.results)-1]
	r.results = r.results[:len(r.results)-1]
	return node
}

func (r *Resolver) popAll(count int) []Node {
	nodes := make([]Node, count)
	copy(nodes, r.results[len(r.results)-count:])
	r.results = r.results[:len(r.results)-count]
	return nodes
}

func (r *Resolver) fail(message string, position syntax.Position) {
	if r.err == nil {
		module := ""
		if r.module != nil {
			module = r.module.Identity
		}
		r.err = &syntax.SyntaxError{Message: message, Line: position.Line, Column: position.Column, Module: module}
	}
}

type bindingMessages struct{}

// BindingMessages holds the messages of the binding layer; the first three are fixed by spec §10.2.
var BindingMessages bindingMessages

func (bindingMessages) NotDefined(name string, arity int) string {
	return name + "/" + itoa(arity) + " is not defined"
}

func (bindingMessages) LabelNotDefined(name string) string {
	return "$*label-" + name + " is not defined"
}

func (bindingMessages) VariableNotDefined(name string) string { return "$" + name + " is not defined" }

// The modules group (modules.go); the texts are jq's.

func (bindingMessages) ModuleNotFound(path string) string { return "module not found: " + path }

func (bindingMessages) CircularImport(identity string) string {
	return "circular import of " + identity
}

func (bindingMessages) InvalidData(identity, reason string) string {
	return "error loading data file " + identity + ": " + reason
}

// InModule is an error of a module, with where it is: … at <identity>, line L, column C.
func (bindingMessages) InModule(message, identity string, line, column int) string {
	return message + " at " + identity + ", line " + itoa(line) + ", column " + itoa(column)
}
