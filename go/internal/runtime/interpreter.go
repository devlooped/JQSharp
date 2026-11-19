package runtime

import (
	"strings"

	"github.com/devlooped/jqlite/go/internal/binding"
	"github.com/devlooped/jqlite/go/internal/syntax"
	"github.com/devlooped/jqlite/go/internal/values"
)

type value = values.Value

// Emit receives one output of an evaluation; gives nil to go on, or the signal that stops it.
type Emit func(v values.Value, p *Path) *Signal

// Interpreter is the push-based interpreter of a bound program (spec §6 to §8). One instance is
// one evaluation: it holds every piece of per-evaluation state (host variables, $ENV, the call
// depth, label numbers, scratch stacks), while the bound program and the prelude are immutable and
// shared, so one program is evaluated concurrently from many goroutines, each with its own
// interpreter.
//
// Eval evaluates a node with an input and an environment and pushes each output, with its path in
// path mode, to emit. It gives nil when the node is done, or the Signal that stops it: an error, a
// break, a halt, or a signal given by the consumer, which is passed up unchanged. Nothing panics.
// Path mode (§8.1) is path not nil: then every output carries where it came from (see Path).
//
// EvalOne is the same evaluation for nodes that yield at most one output (the resolver marks them,
// Node.AtMostOne): it gives the output instead of pushing it, so arithmetic, indexing, object
// construction and the like run without callbacks. Eval hands such nodes to it. A call it evaluates
// (a single call, spec §7.7) has ended when its output is given, so it leaves the depth of §11
// before that output is used; a call Eval enters stays in the depth while its outputs are pushed.
//
// Both are loops where a node's last step is another node (a pipe whose left side yields at most
// one value, a branch of if, the body of a binding or of a function, a closure, the last item of a
// comma), so tail positions and calls in them do not recurse; left-deep chains of operators and
// indexes are walked with scratch stacks. What still recurses is bounded by the nesting limit and
// MaxDepth, and goroutine stacks grow (stack_guard.go).
type Interpreter struct {
	program *binding.BoundProgram
	// The host variables, by the index of BoundProgram.HostVariables; hostSet false when the host
	// gave none of that name.
	hostValues      []value
	hostSet         []bool
	readEnvironment func() value
	environment     value
	hasEnvironment  bool
	maxDepth        int
	depth           int
	labels          int64
	// The Eval and EvalOne frames active on the current goroutine's stack (StackGuard).
	hostDepth int

	// Scratch stacks of the chain walks of EvalOne; each walk uses the part above its mark.
	spineNodes  []binding.Node
	spineValues []value

	// Clock is the host's clock, for the date builtins.
	Clock Clock
	// Debug is the host's handler of debug.
	Debug func(value)
	// Stderr is the host's handler of stderr.
	Stderr func(value)
}

func newInterpreter(program *binding.BoundProgram, options *EvaluationOptions) *Interpreter {
	in := &Interpreter{
		program:         program,
		hostValues:      make([]value, len(program.HostVariables)),
		hostSet:         make([]bool, len(program.HostVariables)),
		readEnvironment: options.Environment,
		maxDepth:        options.MaxDepth,
		Clock:           options.Clock,
		Debug:           options.Debug,
		Stderr:          options.Stderr,
	}
	for i, name := range program.HostVariables {
		if v, ok := options.Variables[name]; ok {
			in.hostValues[i] = v
			in.hostSet[i] = true
		}
	}
	return in
}

// Program gives the program being evaluated.
func (in *Interpreter) Program() *binding.BoundProgram { return in.program }

// Run evaluates program on input, giving each output to output, which gives false to stop the stream.
func Run(program *binding.BoundProgram, input value, options *EvaluationOptions, output func(value) bool) Outcome {
	if !NativeTable.loaded {
		panic("the natives are not registered: import the builtins package")
	}
	signal := newInterpreter(program, options).Eval(program.Body, nil, input, nil, func(v value, _ *Path) *Signal {
		if output(v) {
			return nil
		}
		return StopSignal
	})
	switch {
	case signal == nil:
		return Outcome{Kind: OutcomeCompleted}
	case signal.Kind == SignalError:
		return Outcome{Kind: OutcomeError, Value: signal.Value, HasValue: true}
	case signal.Kind == SignalHalt:
		return Outcome{Kind: OutcomeHalt, Value: signal.Value, HasValue: signal.HasValue, Code: signal.Code}
	default:
		// A break always meets its label: the resolver binds every break to an enclosing one.
		return Outcome{Kind: OutcomeStopped}
	}
}

// Eval evaluates node and pushes each output to emit.
func (in *Interpreter) Eval(node binding.Node, env *Env, input value, path *Path, emit Emit) *Signal {
	if !StackGuard.HasStack(in.hostDepth) {
		return in.evalOnFreshStack(node, env, input, path, emit)
	}
	in.hostDepth++
	signal := in.eval(node, env, input, path, emit)
	in.hostDepth--
	return signal
}

// eval is Eval on the current stack.
func (in *Interpreter) eval(node binding.Node, env *Env, input value, path *Path, emit Emit) *Signal {
	// Function calls entered by this frame (tail calls): they stay active, counting in the depth of
	// §11, while their outputs are consumed, that is until this frame returns.
	calls := 0
	var signal *Signal
	var has bool
loop:
	for {
		if node.AtMostOne() {
			var v value
			var at *Path
			signal, has, v, at = in.EvalOne(node, env, input, path)
			if signal == nil && has {
				signal = Output(emit, v, at)
			}
			break
		}

		switch n := node.(type) {
		case *binding.Comma:
			if signal = in.evalItems(n, env, input, path, emit); signal != nil {
				break loop
			}
			node = n.Items[len(n.Items)-1]
		case *binding.Pipe:
			if !n.Left.AtMostOne() {
				signal = in.evalPipe(n, env, input, path, emit)
				break loop
			}
			if signal, has, input, path = in.EvalOne(n.Left, env, input, path); signal != nil || !has {
				break loop
			}
			node = n.Right
		case *binding.If:
			if !n.Condition.AtMostOne() {
				signal = in.evalIf(n, env, input, path, emit)
				break loop
			}
			if node, signal, has = in.decide(n, env, input); signal != nil || !has {
				break loop
			}
		case *binding.Alternative:
			if signal, has = in.evalAlternative(n, env, input, path, emit); signal != nil || has {
				break loop
			}
			node = n.Right
		case *binding.Bind:
			if !isSimple(n) {
				signal = in.evalBind(n, env, input, path, emit)
				break loop
			}
			if env, signal, has = in.bindOne(n, env, input); signal != nil || !has {
				break loop
			}
			node = n.Body
		case *binding.CallFunction:
			calls++
			in.depth++
			if in.depth > in.maxDepth {
				signal = Fail(values.Errors.DepthExceeded())
				break loop
			}
			env = callEnv(n.Hops, n.Arguments, env)
			node = in.program.Functions[n.Function].Body
		case *binding.CallParameter:
			closure := Up(env, n.Hops).Closures[n.Slot]
			env = closure.Env
			node = closure.Node
		default:
			signal = in.evalMany(node, env, input, path, emit)
			break loop
		}
	}

	in.depth -= calls
	return signal
}

// evalItems evaluates the items of a comma but the last, which Eval evaluates in tail position.
func (in *Interpreter) evalItems(node *binding.Comma, env *Env, input value, path *Path, emit Emit) *Signal {
	for _, item := range node.Items[:len(node.Items)-1] {
		if signal := in.Eval(item, env, input, path, emit); signal != nil {
			return signal
		}
	}
	return nil
}

// decide: if c then a else b end with a condition that yields at most one value gives the branch
// to take; has is false when the condition has no output.
func (in *Interpreter) decide(branch *binding.If, env *Env, input value) (binding.Node, *Signal, bool) {
	signal, has, condition, _ := in.EvalOne(branch.Condition, env, input, nil)
	if signal != nil || !has {
		return nil, signal, has
	}
	if condition.IsTruthy() {
		return branch.Then, nil, true
	}
	return branch.Else, nil, true
}

// isSimple: a binding Eval and EvalOne take in tail position: one pattern, a source and keys that
// yield at most one output.
func isSimple(node *binding.Bind) bool {
	return node.Source.AtMostOne() && node.Binder.KeysAtMostOne && len(node.Binder.Patterns) == 1
}

// bindOne binds the output of a simple binding's source: gives the env with the new frame; has is
// false when nothing binds.
func (in *Interpreter) bindOne(node *binding.Bind, env *Env, input value) (*Env, *Signal, bool) {
	signal, has, source, _ := in.EvalOne(node.Source, env, input, nil)
	if signal != nil || !has {
		return env, signal, has
	}
	slots := make([]value, node.Binder.Slots)
	if signal, has = in.matchOne(node.Binder.Patterns[0], source, slots, env); signal == nil && has {
		return &Env{Parent: env, Values: slots}, nil, true
	}
	return env, signal, has
}

// evalMany evaluates the constructs of Eval that are not in tail position.
func (in *Interpreter) evalMany(node binding.Node, env *Env, input value, path *Path, emit Emit) *Signal {
	switch n := node.(type) {
	case *binding.Iterate:
		return in.evalIterate(n, env, input, path, emit)
	case *binding.Index:
		return in.evalIndex(n, env, input, path, emit)
	case *binding.Slice:
		return in.evalSlice(n, env, input, path, emit)
	case *binding.Binary:
		return in.evalBinary(n, env, input, path, emit)
	case *binding.And:
		return in.evalLogic(true, n.Left, n.Right, env, input, path, emit)
	case *binding.Or:
		return in.evalLogic(false, n.Left, n.Right, env, input, path, emit)
	case *binding.Negate:
		return in.evalNegate(n, env, input, path, emit)
	case *binding.ObjectExpr:
		return in.evalObject(n, 0, values.EmptyObject, env, input, PathOf(path), emit)
	case *binding.Template:
		return in.evalTemplate(n, len(n.Parts)-1, make([]string, len(n.Parts)), env, input, PathOf(path), emit)
	case *binding.Try:
		return in.evalTry(n, env, input, path, emit)
	case *binding.Reduce:
		return in.evalReduce(n, env, input, path, emit)
	case *binding.Foreach:
		return in.evalForeach(n, env, input, path, emit)
	case *binding.Label:
		return in.evalLabel(n, env, input, path, emit)
	default:
		return in.evalNative(node.(*binding.CallNative), env, input, path, emit)
	}
}

func (in *Interpreter) evalPipe(node *binding.Pipe, env *Env, input value, path *Path, emit Emit) *Signal {
	return in.Eval(node.Left, env, input, path, func(v value, at *Path) *Signal {
		return in.Eval(node.Right, env, v, at, emit)
	})
}

func (in *Interpreter) evalIf(node *binding.If, env *Env, input value, path *Path, emit Emit) *Signal {
	return in.Eval(node.Condition, env, input, nil, func(condition value, _ *Path) *Signal {
		if condition.IsTruthy() {
			return in.Eval(node.Then, env, input, path, emit)
		}
		return in.Eval(node.Else, env, input, path, emit)
	})
}

func (in *Interpreter) evalNegate(node *binding.Negate, env *Env, input value, path *Path, emit Emit) *Signal {
	at := PathOf(path)
	return in.Eval(node.Operand, env, input, nil, func(v value, _ *Path) *Signal {
		return apply(at, emit)(values.ValueOps.TryNegate(v))
	})
}

// EvalOne evaluates a node that yields at most one output (Node.AtMostOne): has tells whether it
// did, and v and at are the output and its path. Any signal given is the node's own: there is no
// consumer.
func (in *Interpreter) EvalOne(node binding.Node, env *Env, input value, path *Path) (*Signal, bool, value, *Path) {
	if !StackGuard.HasStack(in.hostDepth) {
		return in.evalOneOnFreshStack(node, env, input, path)
	}
	in.hostDepth++
	signal, has, v, at := in.evalOne(node, env, input, path)
	in.hostDepth--
	return signal, has, v, at
}

// The continuations of Eval and EvalOne on a fresh stack (StackGuard): the evaluation goes on on a
// new goroutine, its count of frames from zero, while this one waits. Recursion only grows through
// function calls, which the depth counter stops at MaxDepth, so the number of hops is bounded.
func (in *Interpreter) evalOnFreshStack(node binding.Node, env *Env, input value, path *Path, emit Emit) *Signal {
	var signal *Signal
	saved := in.hostDepth
	in.hostDepth = 0
	StackGuard.OnFreshStack(func() { signal = in.Eval(node, env, input, path, emit) })
	in.hostDepth = saved
	return signal
}

func (in *Interpreter) evalOneOnFreshStack(node binding.Node, env *Env, input value, path *Path) (*Signal, bool, value, *Path) {
	var signal *Signal
	var has bool
	var v value
	var at *Path
	saved := in.hostDepth
	in.hostDepth = 0
	StackGuard.OnFreshStack(func() { signal, has, v, at = in.EvalOne(node, env, input, path) })
	in.hostDepth = saved
	return signal, has, v, at
}

// evalOne is EvalOne on the current stack.
func (in *Interpreter) evalOne(node binding.Node, env *Env, input value, path *Path) (signal *Signal, has bool, v value, at *Path) {
	for {
		switch n := node.(type) {
		case *binding.Identity:
			return nil, true, input, path
		case *binding.Literal:
			return nil, true, n.Value, PathOf(path)
		case *binding.Variable:
			return nil, true, Up(env, n.Hops).Values[n.Slot], PathOf(path)
		case *binding.Pipe:
			if signal, has, input, path = in.EvalOne(n.Left, env, input, path); signal != nil || !has {
				return signal, has, values.Null, nil
			}
			node = n.Right
		case *binding.If:
			if node, signal, has = in.decide(n, env, input); signal != nil || !has {
				return signal, has, values.Null, nil
			}
		case *binding.Alternative:
			if signal, has, v, at = in.EvalOne(n.Left, env, input, path); signal != nil || (has && v.IsTruthy()) {
				return
			}
			node = n.Right
		case *binding.Try:
			if signal, has, v, at = in.EvalOne(n.Body, env, input, path); signal == nil || signal.Kind != SignalError {
				return
			}
			if n.Handler == nil {
				return nil, false, values.Null, nil
			}
			node = n.Handler
			input = signal.Value
			path = PathOf(path)
		case *binding.Bind:
			if len(n.Binder.Patterns) != 1 {
				return in.evalBindOne(n, env, input, path)
			}
			if env, signal, has = in.bindOne(n, env, input); signal != nil || !has {
				return signal, has, values.Null, nil
			}
			node = n.Body
		case *binding.CallFunction:
			return in.evalCallOne(n, env, input, path)
		case *binding.CallParameter:
			// The argument of a $ parameter of a single call (Resolver.call): it is single too.
			closure := Up(env, n.Hops).Closures[n.Slot]
			env = closure.Env
			node = closure.Node
		default:
			return in.evalOther(node, env, input, path)
		}
	}
}

// evalCallOne: a single call (spec §7.7) has ended when its output is here, so, unlike a call Eval
// enters, it does not count in the depth of §11 while that output is consumed.
func (in *Interpreter) evalCallOne(call *binding.CallFunction, env *Env, input value, path *Path) (*Signal, bool, value, *Path) {
	in.depth++
	if in.depth > in.maxDepth {
		in.depth--
		return Fail(values.Errors.DepthExceeded()), false, values.Null, nil
	}
	signal, has, v, at := in.EvalOne(in.program.Functions[call.Function].Body, callEnv(call.Hops, call.Arguments, env), input, path)
	in.depth--
	return signal, has, v, at
}

// evalOther evaluates the nodes of EvalOne that are not in tail position.
func (in *Interpreter) evalOther(node binding.Node, env *Env, input value, path *Path) (*Signal, bool, value, *Path) {
	at := PathOf(path)
	var signal *Signal
	var has bool
	var v value
	switch n := node.(type) {
	case *binding.Index, *binding.Slice:
		return in.evalPostfixOne(node, env, input, path)
	case *binding.Binary:
		signal, has, v = in.evalBinaryOne(n, env, input)
	case *binding.And, *binding.Or:
		signal, has, v = in.evalLogicOne(node, env, input)
	case *binding.Negate:
		signal, has, v = in.evalNegateOne(n, env, input)
	case *binding.ArrayExpr:
		signal, has, v = in.evalArrayOne(n, env, input)
	case *binding.ObjectExpr:
		signal, has, v = in.evalObjectOne(n, env, input)
	case *binding.Template:
		signal, has, v = in.evalTemplateOne(n, env, input)
	case *binding.CallNative:
		if n.Native.TakesClosures {
			return in.evalClosureNativeOne(n, env, input, path)
		}
		signal, has, v = in.evalNativeOne(n, env, input)
	case *binding.Label:
		return in.evalLabelOne(n, env, input, path)
	case *binding.Bind:
		return in.evalBindOne(n, env, input, path)
	case *binding.Reduce:
		return in.evalReduceOne(n, env, input, path)
	default:
		signal, has, v = in.evalLeafOne(node, env, input)
	}
	return signal, has, v, at
}

// evalLeafOne evaluates variables, formats and break: no children.
func (in *Interpreter) evalLeafOne(node binding.Node, env *Env, input value) (*Signal, bool, value) {
	switch n := node.(type) {
	case *binding.HostVariable:
		if !in.hostSet[n.Index] {
			return Fail(values.Errors.VariableNotDefined(n.Name)), false, values.Null
		}
		return nil, true, in.hostValues[n.Index]
	case *binding.EnvVariable:
		if !in.hasEnvironment {
			if in.readEnvironment == nil {
				in.environment = values.FromObject(values.EmptyObject)
			} else {
				in.environment = in.readEnvironment()
			}
			in.hasEnvironment = true
		}
		return nil, true, in.environment
	case *binding.Format:
		result, err, ok := NativeTable.format(n.Name, input)
		if !ok {
			return ErrorSignal(err), false, values.Null
		}
		return nil, true, result
	default:
		return BreakSignal(Up(env, node.(*binding.Break).Hops).Label), false, values.Null
	}
}

func (in *Interpreter) evalLabelOne(node *binding.Label, env *Env, input value, path *Path) (*Signal, bool, value, *Path) {
	in.labels++
	label := in.labels
	signal, has, v, at := in.EvalOne(node.Body, &Env{Parent: env, Label: label}, input, path)
	if signal != nil && signal.Kind == SignalBreak && signal.Label == label {
		return nil, has, v, at
	}
	return signal, has, v, at
}

// evalClosureNativeOne: a native that takes closures and gives at most one output
// (Native.SingleWhen): its output is kept.
func (in *Interpreter) evalClosureNativeOne(node *binding.CallNative, env *Env, input value, path *Path) (*Signal, bool, value, *Path) {
	var capture capture
	signal := NativeTable.Closure(node.Native)(in, env, node.Arguments, input, path, capture.emit)
	return signal, capture.has, capture.value, capture.at
}

// evalReduceOne: reduce with one initial value, through the general evaluation, keeping its output.
func (in *Interpreter) evalReduceOne(node *binding.Reduce, env *Env, input value, path *Path) (*Signal, bool, value, *Path) {
	var capture capture
	signal := in.evalReduce(node, env, input, path, capture.emit)
	return signal, capture.has, capture.value, capture.at
}

// ForEach evaluates node and calls each with each output, without a callback for a node that
// yields at most one.
func (in *Interpreter) ForEach(node binding.Node, env *Env, input value, path *Path, each Emit) *Signal {
	if !node.AtMostOne() {
		return in.Eval(node, env, input, path, each)
	}
	signal, has, v, at := in.EvalOne(node, env, input, path)
	if signal == nil && has {
		return each(v, at)
	}
	return signal
}

// Output pushes an output to a consumer. Every output goes through here; the reference checks the
// host stack here (StackGuard), which Go does not need.
func Output(emit Emit, v value, at *Path) *Signal { return emit(v, at) }

// NewLabel gives the next label instance, unique within this evaluation.
func (in *Interpreter) NewLabel() int64 {
	in.labels++
	return in.labels
}

// Fail gives an error signal.
func Fail(err value) *Signal { return ErrorSignal(err) }

// apply gives the function that outputs a result, or the error of a failed operation.
func apply(at *Path, emit Emit) func(result, err value, ok bool) *Signal {
	return func(result, err value, ok bool) *Signal {
		if ok {
			return Output(emit, result, at)
		}
		return ErrorSignal(err)
	}
}

// callEnv gives the environment of a call: the caller's with the frames between the call and the
// definition removed, plus the closures of the arguments when there are any.
func callEnv(hops int, arguments []binding.Node, env *Env) *Env {
	defined := Up(env, hops)
	if len(arguments) == 0 {
		return defined
	}
	closures := make([]*Closure, len(arguments))
	for i, argument := range arguments {
		closures[i] = &Closure{argument, env}
	}
	return &Env{Parent: defined, Closures: closures}
}

// evalAlternative: a // b: the true outputs of a; found tells whether there was one (then b is
// not evaluated).
func (in *Interpreter) evalAlternative(node *binding.Alternative, env *Env, input value, path *Path, emit Emit) (*Signal, bool) {
	found := false
	signal := in.Eval(node.Left, env, input, path, func(v value, at *Path) *Signal {
		if !v.IsTruthy() {
			return nil
		}
		found = true
		return Output(emit, v, at)
	})
	return signal, found
}

// evalIterate: T[]: in path mode each element's path is the target's plus its index or key.
func (in *Interpreter) evalIterate(node *binding.Iterate, env *Env, input value, path *Path, emit Emit) *Signal {
	return in.ForEach(node.Target, env, input, path, func(target value, at *Path) *Signal {
		if at != nil && !at.IsTracked() {
			return Fail(values.Errors.InvalidPathIterate(target))
		}
		if target.Kind() == values.KindArray {
			array := target.AsArray()
			for i := 0; i < array.Count(); i++ {
				if stop := Output(emit, array.At(i), appendKey(at, values.FromNumber(float64(i)))); stop != nil {
					return stop
				}
			}
			return nil
		}

		if target.Kind() != values.KindObject {
			return Fail(values.Errors.CannotIterate(target))
		}
		obj := target.AsObject()
		for i := 0; i < obj.Count(); i++ {
			if stop := Output(emit, obj.ValueAt(i), appendKey(at, values.FromString(obj.KeyAt(i)))); stop != nil {
				return stop
			}
		}
		return nil
	})
}

// evalIndex: T[e]: for each key (on the input of T, in value mode), for each target (§6.3).
func (in *Interpreter) evalIndex(node *binding.Index, env *Env, input value, path *Path, emit Emit) *Signal {
	return in.ForEach(node.Key, env, input, nil, func(key value, _ *Path) *Signal {
		return in.ForEach(node.Target, env, input, path, func(target value, at *Path) *Signal {
			result, next, signal := indexStep(target, at, key)
			if signal != nil {
				return signal
			}
			return Output(emit, result, next)
		})
	})
}

// evalSlice: T[from:to]: from outermost, then to, then T (§6.3).
func (in *Interpreter) evalSlice(node *binding.Slice, env *Env, input value, path *Path, emit Emit) *Signal {
	return in.bound(node.From, env, input, func(from value, _ *Path) *Signal {
		return in.bound(node.To, env, input, func(to value, _ *Path) *Signal {
			return in.ForEach(node.Target, env, input, path, func(target value, at *Path) *Signal {
				result, next, signal := sliceStep(target, at, from, to)
				if signal != nil {
					return signal
				}
				return Output(emit, result, next)
			})
		})
	})
}

// bound evaluates a slice bound: null when absent.
func (in *Interpreter) bound(bound binding.Node, env *Env, input value, each Emit) *Signal {
	if bound == nil {
		return each(values.Null, nil)
	}
	return in.ForEach(bound, env, input, nil, each)
}

// indexStep is one index of a target with its path: in path mode the target must be tracked.
func indexStep(target value, at *Path, key value) (value, *Path, *Signal) {
	if at != nil && !at.IsTracked() {
		return values.Null, at, Fail(values.Errors.InvalidPathAccess(key, target))
	}
	result, err, ok := values.ValueOps.TryIndex(target, key)
	if !ok {
		return values.Null, at, ErrorSignal(err)
	}
	return result, appendKey(at, key), nil
}

// sliceStep is one slice of a target with its path; in a path the slice is the key
// {"start": from, "end": to} (§8.1).
func sliceStep(target value, at *Path, from, to value) (value, *Path, *Signal) {
	next := at
	if at != nil {
		key := values.FromObject(values.EmptyObject.Set("start", from).Set("end", to))
		if !at.IsTracked() {
			return values.Null, at, Fail(values.Errors.InvalidPathAccess(key, target))
		}
		next = at.Append(key)
	}
	result, err, ok := values.ValueOps.TrySlice(target, from, to)
	if !ok {
		return values.Null, next, ErrorSignal(err)
	}
	return result, next, nil
}

// evalBinary: a op b: for each right output, for each left output (§6.3); operands in value mode. A
// right operand that yields at most one value is evaluated first, so the common case has one callback.
func (in *Interpreter) evalBinary(node *binding.Binary, env *Env, input value, path *Path, emit Emit) *Signal {
	at := PathOf(path)
	if !node.Right.AtMostOne() {
		return in.Eval(node.Right, env, input, nil, func(right value, _ *Path) *Signal {
			return in.evalLeft(node, right, env, input, at, emit)
		})
	}
	signal, has, right, _ := in.EvalOne(node.Right, env, input, nil)
	if signal == nil && has {
		return in.evalLeft(node, right, env, input, at, emit)
	}
	return signal
}

func (in *Interpreter) evalLeft(node *binding.Binary, right value, env *Env, input value, at *Path, emit Emit) *Signal {
	return in.Eval(node.Left, env, input, nil, func(left value, _ *Path) *Signal {
		result, err, ok := TryApply(node.Op, left, right)
		if !ok {
			return ErrorSignal(err)
		}
		return Output(emit, result, at)
	})
}

// TryApply applies the arithmetic operators and comparisons of §4.
func TryApply(op syntax.BinaryOp, left, right value) (result, err value, ok bool) {
	switch op {
	case syntax.OpAdd:
		return values.ValueOps.TryAdd(left, right)
	case syntax.OpSubtract:
		return values.ValueOps.TrySubtract(left, right)
	case syntax.OpMultiply:
		return values.ValueOps.TryMultiply(left, right)
	case syntax.OpDivide:
		return values.ValueOps.TryDivide(left, right)
	case syntax.OpModulo:
		return values.ValueOps.TryModulo(left, right)
	case syntax.OpEqual:
		return values.FromBool(values.ValueOps.AreEqual(left, right)), values.Null, true
	case syntax.OpNotEqual:
		return values.FromBool(!values.ValueOps.AreEqual(left, right)), values.Null, true
	case syntax.OpLess:
		return values.FromBool(values.ValueOps.Compare(left, right) < 0), values.Null, true
	case syntax.OpLessEqual:
		return values.FromBool(values.ValueOps.Compare(left, right) <= 0), values.Null, true
	case syntax.OpGreater:
		return values.FromBool(values.ValueOps.Compare(left, right) > 0), values.Null, true
	default:
		return values.FromBool(values.ValueOps.Compare(left, right) >= 0), values.Null, true
	}
}

// evalLogic: a and b, a or b (§7.3): for each left output, the right side only when the left does
// not decide.
func (in *Interpreter) evalLogic(isAnd bool, left, right binding.Node, env *Env, input value, path *Path, emit Emit) *Signal {
	at := PathOf(path)
	return in.ForEach(left, env, input, nil, func(first value, _ *Path) *Signal {
		if first.IsTruthy() != isAnd {
			return Output(emit, values.FromBool(!isAnd), at)
		}
		return in.ForEach(right, env, input, nil, func(second value, _ *Path) *Signal {
			return Output(emit, values.FromBool(second.IsTruthy()), at)
		})
	})
}

// evalObject: {k1: v1, …} from entry i on, with the entries before it in obj: keys and values in
// value mode, k1 outermost (§6.3). Entries that yield at most one output are taken in a loop.
func (in *Interpreter) evalObject(node *binding.ObjectExpr, i int, obj *values.Object, env *Env, input value, at *Path, emit Emit) *Signal {
	for ; i < len(node.Entries); i++ {
		entry := &node.Entries[i]
		if !entry.Key.AtMostOne() || (entry.Value != nil && !entry.Value.AtMostOne()) {
			break
		}
		signal, has, key, member := in.evalEntryOne(entry, env, input)
		if signal != nil || !has {
			return signal
		}
		obj = obj.Set(key, member)
	}

	if i == len(node.Entries) {
		return Output(emit, values.FromObject(obj), at)
	}
	next := &node.Entries[i]
	return in.ForEach(next.Key, env, input, nil, func(key value, _ *Path) *Signal {
		if key.Kind() != values.KindString {
			return Fail(values.Errors.CannotUseAsKey(key))
		}
		if next.Value == nil {
			indexed, err, ok := values.ValueOps.TryIndex(input, key)
			if !ok {
				return ErrorSignal(err)
			}
			return in.evalObject(node, i+1, obj.Set(key.AsString(), indexed), env, input, at, emit)
		}
		return in.ForEach(next.Value, env, input, nil, func(member value, _ *Path) *Signal {
			return in.evalObject(node, i+1, obj.Set(key.AsString(), member), env, input, at, emit)
		})
	})
}

// evalEntryOne evaluates one entry whose key and value yield at most one output.
func (in *Interpreter) evalEntryOne(entry *binding.ObjectEntry, env *Env, input value) (*Signal, bool, string, value) {
	signal, has, name, _ := in.EvalOne(entry.Key, env, input, nil)
	if signal != nil || !has {
		return signal, has, "", values.Null
	}
	if name.Kind() != values.KindString {
		return Fail(values.Errors.CannotUseAsKey(name)), has, "", values.Null
	}
	key := name.AsString()
	if entry.Value == nil {
		member, err, ok := values.ValueOps.TryIndex(input, name)
		if !ok {
			return ErrorSignal(err), true, key, values.Null
		}
		return nil, true, key, member
	}
	signal, has, member, _ := in.EvalOne(entry.Value, env, input, nil)
	return signal, has, key, member
}

// evalTemplate: "a\(x)b\(y)": the parts from part i down to the first, the last part outermost (§6.3).
func (in *Interpreter) evalTemplate(node *binding.Template, i int, texts []string, env *Env, input value, at *Path, emit Emit) *Signal {
	if i < 0 {
		return Output(emit, values.FromString(join(node, texts)), at)
	}
	return in.ForEach(node.Parts[i], env, input, nil, func(part value, _ *Path) *Signal {
		text, err, ok := convert(node, part)
		if !ok {
			return ErrorSignal(err)
		}
		texts[i] = text
		return in.evalTemplate(node, i-1, texts, env, input, at, emit)
	})
}

// convert gives an interpolated value as text: tostring, or the template's format.
func convert(node *binding.Template, part value) (string, value, bool) {
	if !node.HasFormat {
		if part.Kind() == values.KindString {
			return part.AsString(), values.Null, true
		}
		return values.ValueText.ToJson(part), values.Null, true
	}
	formatted, err, ok := NativeTable.format(node.Format, part)
	if !ok {
		return "", err, false
	}
	return formatted.AsString(), values.Null, true
}

func join(node *binding.Template, parts []string) string {
	var builder strings.Builder
	builder.WriteString(node.Texts[0])
	for i, part := range parts {
		builder.WriteString(part)
		builder.WriteString(node.Texts[i+1])
	}
	return builder.String()
}

// evalTry: try B catch H (§7.13): only B's own errors are caught, never one its consumer gave (§6.2).
func (in *Interpreter) evalTry(node *binding.Try, env *Env, input value, path *Path, emit Emit) *Signal {
	consumer := &consumer{next: emit}
	signal := in.Eval(node.Body, env, input, path, consumer.emit)
	if signal == nil || signal.Kind != SignalError || consumer.raised(signal) {
		return signal
	}
	if node.Handler == nil {
		return nil
	}
	return in.Eval(node.Handler, env, signal.Value, PathOf(path), emit)
}

// consumer tells an operand's own signals from those of its consumer (§6.2). It stands between the
// operand and the consumer and remembers the last signal the consumer gave; every construct passes
// a signal up unchanged and stops at once, so when the operand gives that very pointer, it came
// from the consumer. Comparing identities costs nothing per output.
type consumer struct {
	next Emit
	last *Signal
}

func (c *consumer) emit(v value, p *Path) *Signal {
	c.last = Output(c.next, v, p)
	return c.last
}

func (c *consumer) raised(signal *Signal) bool { return signal == c.last }

// capture keeps the output of a node that yields at most one.
type capture struct {
	has   bool
	value value
	at    *Path
}

func (c *capture) emit(v value, at *Path) *Signal {
	c.has = true
	c.value = v
	c.at = at
	return nil
}

func (in *Interpreter) evalLabel(node *binding.Label, env *Env, input value, path *Path, emit Emit) *Signal {
	in.labels++
	label := in.labels
	signal := in.Eval(node.Body, &Env{Parent: env, Label: label}, input, path, emit)
	if signal != nil && signal.Kind == SignalBreak && signal.Label == label {
		return nil
	}
	return signal
}

// evalBind: E as P1 ?// P2 | B, in general: for each output of E (value mode), each binding, B in
// path mode.
func (in *Interpreter) evalBind(node *binding.Bind, env *Env, input value, path *Path, emit Emit) *Signal {
	var c *consumer
	body := emit
	if len(node.Binder.Patterns) > 1 {
		c = &consumer{next: emit}
		body = c.emit
	}
	return in.ForEach(node.Source, env, input, nil, func(source value, _ *Path) *Signal {
		return in.bindEach(node.Binder, source, env, func(frame *Env) *Signal {
			return in.Eval(node.Body, frame, input, path, body)
		}, c, nil)
	})
}

// evalBindOne: E as P1 ?// P2 | B where E, the keys and B yield at most one output.
func (in *Interpreter) evalBindOne(node *binding.Bind, env *Env, input value, path *Path) (*Signal, bool, value, *Path) {
	signal, has, source, _ := in.EvalOne(node.Source, env, input, nil)
	if signal != nil || !has {
		return signal, has, values.Null, nil
	}
	patterns := node.Binder.Patterns
	for i, pattern := range patterns {
		slots := make([]value, node.Binder.Slots)
		var v value
		var at *Path
		signal, has = in.matchOne(pattern, source, slots, env)
		if signal == nil && has {
			signal, has, v, at = in.EvalOne(node.Body, &Env{Parent: env, Values: slots}, input, path)
		}
		if signal == nil || signal.Kind != SignalError || i == len(patterns)-1 {
			return signal, has, v, at
		}
	}
	return nil, false, values.Null, nil
}

// bindEach binds v by the binder's patterns and runs body in the environment with the new frame,
// once per binding (several when a key has several outputs). With ?// alternatives (§7.5), when
// matching fails or the body raises its own error (not one of c), the next pattern is tried with
// the same value, all variables null again, after restoring what save saved before the first (the
// state of reduce and foreach); errors with the last pattern propagate.
func (in *Interpreter) bindEach(binder *binding.Binder, v value, env *Env, body func(*Env) *Signal, c *consumer, save func() func()) *Signal {
	patterns := binder.Patterns
	var restore func()
	if save != nil {
		restore = save()
	}
	for i := 0; ; i++ {
		if i > 0 && restore != nil {
			restore()
		}
		slots := make([]value, binder.Slots)
		var signal *Signal
		if binder.KeysAtMostOne {
			var matched bool
			if signal, matched = in.matchOne(patterns[i], v, slots, env); signal == nil && matched {
				signal = body(&Env{Parent: env, Values: slots})
			}
		} else {
			signal = in.match(patterns[i], v, slots, env, func() *Signal {
				return body(&Env{Parent: env, Values: append([]value(nil), slots...)})
			})
		}

		if signal == nil || signal.Kind != SignalError || i == len(patterns)-1 || (c != nil && c.raised(signal)) {
			return signal
		}
	}
}

// matchOne destructures v by a pattern whose keys yield at most one output; matched is false when
// a key yields none. Array elements from the last, object entries from the first (§7.5); the keys
// are evaluated in the binding's outer environment, on the value being matched.
func (in *Interpreter) matchOne(pattern binding.Pattern, v value, slots []value, env *Env) (*Signal, bool) {
	switch p := pattern.(type) {
	case *binding.VariablePattern:
		if p.Slot >= 0 {
			slots[p.Slot] = v
		}
		return nil, true
	case *binding.ArrayPattern:
		for i := len(p.Elements) - 1; i >= 0; i-- {
			element, err, ok := values.ValueOps.TryIndex(v, values.FromNumber(float64(i)))
			if !ok {
				return ErrorSignal(err), true
			}
			if signal, matched := in.matchOne(p.Elements[i], element, slots, env); signal != nil || !matched {
				return signal, matched
			}
		}
		return nil, true
	default:
		for _, entry := range pattern.(*binding.ObjectPattern).Entries {
			signal, matched, key, _ := in.EvalOne(entry.Key, env, v, nil)
			if signal != nil || !matched {
				return signal, matched
			}
			member, err, ok := values.ValueOps.TryIndex(v, key)
			if !ok {
				return ErrorSignal(err), true
			}
			if entry.Slot >= 0 {
				slots[entry.Slot] = member
			}
			if entry.Value != nil {
				if signal, matched = in.matchOne(entry.Value, member, slots, env); signal != nil || !matched {
					return signal, matched
				}
			}
		}
		return nil, true
	}
}

// match destructures v when a key may yield several outputs: next runs once per complete binding.
func (in *Interpreter) match(pattern binding.Pattern, v value, slots []value, env *Env, next func() *Signal) *Signal {
	switch p := pattern.(type) {
	case *binding.VariablePattern:
		if p.Slot >= 0 {
			slots[p.Slot] = v
		}
		return next()
	case *binding.ArrayPattern:
		return in.matchElements(p, len(p.Elements)-1, v, slots, env, next)
	default:
		return in.matchEntries(pattern.(*binding.ObjectPattern), 0, v, slots, env, next)
	}
}

func (in *Interpreter) matchElements(array *binding.ArrayPattern, i int, v value, slots []value, env *Env, next func() *Signal) *Signal {
	if i < 0 {
		return next()
	}
	element, err, ok := values.ValueOps.TryIndex(v, values.FromNumber(float64(i)))
	if !ok {
		return ErrorSignal(err)
	}
	return in.match(array.Elements[i], element, slots, env, func() *Signal {
		return in.matchElements(array, i-1, v, slots, env, next)
	})
}

func (in *Interpreter) matchEntries(obj *binding.ObjectPattern, i int, v value, slots []value, env *Env, next func() *Signal) *Signal {
	if i == len(obj.Entries) {
		return next()
	}
	entry := &obj.Entries[i]
	return in.ForEach(entry.Key, env, v, nil, func(key value, _ *Path) *Signal {
		member, err, ok := values.ValueOps.TryIndex(v, key)
		if !ok {
			return ErrorSignal(err)
		}
		if entry.Slot >= 0 {
			slots[entry.Slot] = member
		}
		rest := func() *Signal { return in.matchEntries(obj, i+1, v, slots, env, next) }
		if entry.Value == nil {
			return rest()
		}
		return in.match(entry.Value, member, slots, env, rest)
	})
}

// state is the state of a reduce or foreach, with its path.
type state struct {
	value value
	at    *Path
}

func (s *state) set(v value, at *Path) { s.value, s.at = v, at }

// saved takes the state back to what it is now.
func (s *state) saved() func() {
	v, at := s.value, s.at
	return func() { s.set(v, at) }
}

// restart: with ?// alternatives, what restores the state before a source value, for a retried
// alternative (§7.8).
func restart(binder *binding.Binder, s *state) func() func() {
	if len(binder.Patterns) > 1 {
		return s.saved
	}
	return nil
}

// evalReduce: reduce SRC as P (INIT; UPDATE) (§7.8): one reduction per initial value; the state is
// the last output of UPDATE, or null. In path mode the state carries its path. With ?//
// alternatives, a retried alternative starts from the state before the source value.
func (in *Interpreter) evalReduce(node *binding.Reduce, env *Env, input value, path *Path, emit Emit) *Signal {
	return in.ForEach(node.Init, env, input, path, func(initial value, initialAt *Path) *Signal {
		s := &state{initial, initialAt}
		signal := in.Eval(node.Source, env, input, nil, func(source value, _ *Path) *Signal {
			return in.bindEach(node.Binder, source, env, func(frame *Env) *Signal {
				if node.Update.AtMostOne() {
					failed, has, next, nextAt := in.EvalOne(node.Update, frame, s.value, s.at)
					if failed == nil {
						if has {
							s.set(next, nextAt)
						} else {
							s.set(values.Null, PathOf(path))
						}
					}
					return failed
				}

				var last capture
				stop := in.Eval(node.Update, frame, s.value, s.at, last.emit)
				if stop == nil {
					if last.has {
						s.set(last.value, last.at)
					} else {
						s.set(values.Null, PathOf(path))
					}
				}
				return stop
			}, nil, restart(node.Binder, s))
		})
		if signal != nil {
			return signal
		}
		return Output(emit, s.value, s.at)
	})
}

// evalForeach: foreach SRC as P (INIT; UPDATE; EXTRACT) (§7.9).
func (in *Interpreter) evalForeach(node *binding.Foreach, env *Env, input value, path *Path, emit Emit) *Signal {
	var c *consumer
	output := emit
	if len(node.Binder.Patterns) > 1 {
		c = &consumer{next: emit}
		output = c.emit
	}
	return in.ForEach(node.Init, env, input, path, func(initial value, initialAt *Path) *Signal {
		s := &state{initial, initialAt}
		return in.Eval(node.Source, env, input, nil, func(source value, _ *Path) *Signal {
			return in.bindEach(node.Binder, source, env, func(frame *Env) *Signal {
				updated := false
				signal := in.Eval(node.Update, frame, s.value, s.at, func(next value, nextAt *Path) *Signal {
					updated = true
					s.set(next, nextAt)
					return in.Eval(node.Extract, frame, next, nextAt, output)
				})
				if signal == nil && !updated {
					s.set(values.Null, PathOf(path))
				}
				return signal
			}, c, restart(node.Binder, s))
		})
	})
}

// evalNative: a closure native decides everything; a value native is called once per combination
// of its arguments' outputs, the last argument outermost (§6.3, §9.1).
func (in *Interpreter) evalNative(node *binding.CallNative, env *Env, input value, path *Path, emit Emit) *Signal {
	native := node.Native
	if native.TakesClosures {
		return NativeTable.Closure(native)(in, env, node.Arguments, input, path, emit)
	}
	arguments := make([]value, native.Arity)
	return in.combine(NativeTable.Value(native), node.Arguments, len(arguments)-1, arguments, env, input, PathOf(path), emit)
}

func (in *Interpreter) combine(function ValueNative, nodes []binding.Node, i int, arguments []value, env *Env, input value, at *Path, emit Emit) *Signal {
	if i < 0 {
		return apply(at, emit)(function(in, input, arguments))
	}
	return in.ForEach(nodes[i], env, input, nil, func(argument value, _ *Path) *Signal {
		arguments[i] = argument
		return in.combine(function, nodes, i-1, arguments, env, input, at, emit)
	})
}

// evalNativeOne evaluates a value native whose arguments yield at most one output each.
func (in *Interpreter) evalNativeOne(node *binding.CallNative, env *Env, input value) (*Signal, bool, value) {
	native := node.Native
	function := NativeTable.Value(native)
	var arguments []value
	if native.Arity > 0 {
		arguments = make([]value, native.Arity)
	}
	for i := len(arguments) - 1; i >= 0; i-- {
		signal, has, argument, _ := in.EvalOne(node.Arguments[i], env, input, nil)
		if signal != nil || !has {
			return signal, has, values.Null
		}
		arguments[i] = argument
	}

	result, err, ok := function(in, input, arguments)
	if !ok {
		return ErrorSignal(err), true, values.Null
	}
	return nil, true, result
}

// evalPostfixOne: a chain of indexes and slices, T[k1][k2]…: the keys from the outermost in, each
// on the input (§6.3: the key before its target), then the innermost target, then each index outward.
func (in *Interpreter) evalPostfixOne(node binding.Node, env *Env, input value, path *Path) (*Signal, bool, value, *Path) {
	nodes := len(in.spineNodes)
	keys := len(in.spineValues)
	var signal *Signal
	has := true
	var v value
	var at *Path
	for has && signal == nil {
		if index, ok := node.(*binding.Index); ok {
			in.spineNodes = append(in.spineNodes, node)
			var key value
			signal, has, key, _ = in.EvalOne(index.Key, env, input, nil)
			in.spineValues = append(in.spineValues, key)
			node = index.Target
		} else if slice, ok := node.(*binding.Slice); ok {
			in.spineNodes = append(in.spineNodes, node)
			var from, to value
			signal, has, from = in.evalBound(slice.From, env, input)
			if signal == nil && has {
				signal, has, to = in.evalBound(slice.To, env, input)
			}
			in.spineValues = append(in.spineValues, from, to)
			node = slice.Target
		} else {
			break
		}
	}

	if signal == nil && has {
		signal, has, v, at = in.EvalOne(node, env, input, path)
	}
	k := len(in.spineValues)
	for n := len(in.spineNodes) - 1; n >= nodes && signal == nil && has; n-- {
		if _, ok := in.spineNodes[n].(*binding.Index); ok {
			k--
			v, at, signal = indexStep(v, at, in.spineValues[k])
		} else {
			k -= 2
			v, at, signal = sliceStep(v, at, in.spineValues[k], in.spineValues[k+1])
		}
	}

	clear(in.spineNodes[nodes:])
	in.spineNodes = in.spineNodes[:nodes]
	in.spineValues = in.spineValues[:keys]
	return signal, has, v, at
}

// evalBound evaluates a bound of a slice: null when absent.
func (in *Interpreter) evalBound(bound binding.Node, env *Env, input value) (*Signal, bool, value) {
	if bound == nil {
		return nil, true, values.Null
	}
	signal, has, v, _ := in.EvalOne(bound, env, input, nil)
	return signal, has, v
}

// evalBinaryOne: a left-deep chain of operators, ((a op1 b) op2 c)…: the right operands from the
// outermost in (§6.3: the right operand first), then the innermost left one, then each operation
// outward.
func (in *Interpreter) evalBinaryOne(node *binding.Binary, env *Env, input value) (*Signal, bool, value) {
	nodes := len(in.spineNodes)
	operands := len(in.spineValues)
	var signal *Signal
	has := true
	var v value
	var left binding.Node = node
	for has && signal == nil {
		binary, ok := left.(*binding.Binary)
		if !ok {
			break
		}
		var right value
		signal, has, right, _ = in.EvalOne(binary.Right, env, input, nil)
		in.spineNodes = append(in.spineNodes, binary)
		in.spineValues = append(in.spineValues, right)
		left = binary.Left
	}

	if signal == nil && has {
		signal, has, v, _ = in.EvalOne(left, env, input, nil)
	}
	for n := len(in.spineNodes) - 1; n >= nodes && signal == nil && has; n-- {
		result, err, ok := TryApply(in.spineNodes[n].(*binding.Binary).Op, v, in.spineValues[n-nodes+operands])
		if !ok {
			signal = ErrorSignal(err)
		}
		v = result
	}

	clear(in.spineNodes[nodes:])
	in.spineNodes = in.spineNodes[:nodes]
	in.spineValues = in.spineValues[:operands]
	return signal, has, v
}

// evalLogicOne: a left-deep chain of and/or: the innermost left operand first, then each operator
// outward, evaluating its right operand only when the left value does not decide.
func (in *Interpreter) evalLogicOne(node binding.Node, env *Env, input value) (*Signal, bool, value) {
	nodes := len(in.spineNodes)
	for {
		if and, ok := node.(*binding.And); ok {
			in.spineNodes = append(in.spineNodes, node)
			node = and.Left
		} else if or, ok := node.(*binding.Or); ok {
			in.spineNodes = append(in.spineNodes, node)
			node = or.Left
		} else {
			break
		}
	}

	signal, has, v, _ := in.EvalOne(node, env, input, nil)
	for n := len(in.spineNodes) - 1; n >= nodes && signal == nil && has; n-- {
		and, isAnd := in.spineNodes[n].(*binding.And)
		if v.IsTruthy() == isAnd {
			var right binding.Node
			if isAnd {
				right = and.Right
			} else {
				right = in.spineNodes[n].(*binding.Or).Right
			}
			signal, has, v, _ = in.EvalOne(right, env, input, nil)
		}
		v = values.FromBool(v.IsTruthy())
	}

	clear(in.spineNodes[nodes:])
	in.spineNodes = in.spineNodes[:nodes]
	return signal, has, v
}

// evalNegateOne: -…-x: the operand, then each minus.
func (in *Interpreter) evalNegateOne(node *binding.Negate, env *Env, input value) (*Signal, bool, value) {
	count := 0
	var operand binding.Node = node
	for {
		negate, ok := operand.(*binding.Negate)
		if !ok {
			break
		}
		operand = negate.Operand
		count++
	}
	signal, has, v, _ := in.EvalOne(operand, env, input, nil)
	for ; signal == nil && has && count > 0; count-- {
		result, err, ok := values.ValueOps.TryNegate(v)
		if !ok {
			signal = ErrorSignal(err)
		}
		v = result
	}
	return signal, has, v
}

func (in *Interpreter) evalArrayOne(node *binding.ArrayExpr, env *Env, input value) (*Signal, bool, value) {
	if node.Body == nil {
		return nil, true, values.FromArray(values.EmptyArray)
	}
	if node.Body.AtMostOne() {
		signal, any, item, _ := in.EvalOne(node.Body, env, input, nil)
		if signal != nil {
			return signal, false, values.Null
		}
		if any {
			return nil, true, values.FromArray(values.ArrayOf(item))
		}
		return nil, true, values.FromArray(values.EmptyArray)
	}
	return in.collect(node.Body, env, input)
}

// collect evaluates [f] for an f with several outputs.
func (in *Interpreter) collect(body binding.Node, env *Env, input value) (*Signal, bool, value) {
	var items []value
	stop := in.Eval(body, env, input, nil, func(item value, _ *Path) *Signal {
		items = append(items, item)
		return nil
	})
	return stop, stop == nil, values.FromArray(values.ArrayFromOwned(items))
}

func (in *Interpreter) evalObjectOne(node *binding.ObjectExpr, env *Env, input value) (*Signal, bool, value) {
	obj := values.EmptyObject
	for i := range node.Entries {
		signal, has, key, member := in.evalEntryOne(&node.Entries[i], env, input)
		if signal != nil || !has {
			return signal, has, values.Null
		}
		obj = obj.Set(key, member)
	}
	return nil, true, values.FromObject(obj)
}

func (in *Interpreter) evalTemplateOne(node *binding.Template, env *Env, input value) (*Signal, bool, value) {
	texts := make([]string, len(node.Parts))
	for i := len(texts) - 1; i >= 0; i-- {
		signal, has, part, _ := in.EvalOne(node.Parts[i], env, input, nil)
		if signal != nil || !has {
			return signal, has, values.Null
		}
		text, err, ok := convert(node, part)
		if !ok {
			return ErrorSignal(err), true, values.Null
		}
		texts[i] = text
	}
	return nil, true, values.FromString(join(node, texts))
}
