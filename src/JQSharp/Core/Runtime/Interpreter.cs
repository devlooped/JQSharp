using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using Devlooped.Core.Binding;
using Devlooped.Core.Builtins;
using Devlooped.Core.Values;

namespace Devlooped.Core.Runtime;

// Inside the namespace, so they win over the old engine's types of the same names (namespace
// Devlooped, until step 10 removes them).
using ArrayPattern = Devlooped.Core.Binding.ArrayPattern;
using Index = Devlooped.Core.Binding.Index;
using ObjectPattern = Devlooped.Core.Binding.ObjectPattern;
using VariablePattern = Devlooped.Core.Binding.VariablePattern;

/// <summary>Receives one output of an evaluation; returns null to go on, or the signal that stops it.</summary>
delegate Signal? Emit(JqValue value, Path? path);

/// <summary>
/// The push-based interpreter of a bound program (spec §6 to §8). One instance is one evaluation:
/// it holds every piece of per-evaluation state (host variables, <c>$ENV</c>, the call depth, label
/// numbers, scratch stacks), while the bound program and the prelude are immutable and shared, so
/// one program is evaluated concurrently from many threads, each with its own interpreter.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Eval"/> evaluates a node with an input and an environment and pushes each output, with
/// its path in path mode, to <c>emit</c>. It returns null when the node is done, or the
/// <see cref="Signal"/> that stops it: an error, a break, a halt, or a signal returned by the
/// consumer, which is passed up unchanged. Nothing throws. Path mode (§8.1) is <c>path</c> not
/// null: then every output carries where it came from (see <see cref="Path"/>).
/// </para>
/// <para>
/// <see cref="EvalOne"/> is the same evaluation for nodes that yield at most one output (the
/// resolver marks them, <see cref="Node.AtMostOne"/>): it returns the output instead of pushing it,
/// so arithmetic, indexing, object construction and the like run without callbacks or allocations.
/// <see cref="Eval"/> hands such nodes to it.
/// </para>
/// <para>
/// The host stack: both are loops where a node's last step is another node (a pipe whose left side
/// yields at most one value, a branch of <c>if</c>, the body of a binding or of a function, a
/// closure, the last item of a comma), so tail positions and calls in them do not recurse; left-deep
/// chains of operators and indexes are walked with scratch stacks. What still recurses (nesting,
/// non-tail calls, generators feeding generators) checks the host stack where an evaluation starts
/// and where an output is pushed, and continues on a fresh stack when it runs short (StackGuard),
/// so the only recursion limit a program meets is MaxDepth (§11).
/// </para>
/// <para>
/// .NET only: the recursive methods are marked AggressiveOptimization. Tiered compilation runs a
/// new method unoptimized at first, with frames several times larger, and at that size the
/// nesting limits of §11 do not fit in a 1 MB stack. A port ignores the attribute.
/// </para>
/// </remarks>
sealed class Interpreter
{
    readonly BoundProgram program;
    // The host variables, by the index of BoundProgram.HostVariables; null when the host has none.
    readonly JqValue?[] hostValues;
    readonly JqValue environment;
    readonly int maxDepth;
    int depth;
    long labels;

    // Scratch stacks of the chain walks of EvalOne; each walk uses the part above its mark.
    readonly List<Node> spineNodes = [];
    readonly List<JqValue> spineValues = [];

    Interpreter(BoundProgram program, EvaluationOptions options)
    {
        this.program = program;
        hostValues = new JqValue?[program.HostVariables.Length];
        for (var i = 0; i < hostValues.Length; i++)
        {
            if (options.Variables is not null && options.Variables.TryGetValue(program.HostVariables[i], out var value))
                hostValues[i] = value;
        }

        environment = options.Environment;
        maxDepth = options.MaxDepth;
        Clock = options.Clock;
    }

    /// <summary>The host's clock, for the date builtins.</summary>
    public IClock? Clock { get; }

    /// <summary>
    /// Evaluates <paramref name="program"/> on <paramref name="input"/>, giving each output to
    /// <paramref name="output"/>, which returns false to stop the stream.
    /// </summary>
    public static Outcome Run(BoundProgram program, JqValue input, EvaluationOptions options, Func<JqValue, bool> output)
    {
        var signal = new Interpreter(program, options).Eval(program.Body, null, input, null, (value, _) => output(value) ? null : Signal.Stop);
        return signal switch
        {
            null => new Outcome(OutcomeKind.Completed),
            { Kind: SignalKind.Error } => new Outcome(OutcomeKind.Error, signal.Value, true),
            { Kind: SignalKind.Halt } => new Outcome(OutcomeKind.Halt, signal.Value, signal.HasValue, signal.Code),
            // A break always meets its label: the resolver binds every break to an enclosing one.
            _ => new Outcome(OutcomeKind.Stopped),
        };
    }

    /// <summary>Evaluates <paramref name="node"/> and pushes each output to <paramref name="emit"/>.</summary>
    /// <remarks>
    /// Each nesting level of the program costs a frame of this method (or of <see cref="EvalOne"/>)
    /// and one of a helper, so both keep few locals: every case is a call, and the tail cases only
    /// reassign the parameters.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public Signal? Eval(Node node, Env? env, JqValue input, Path? path, Emit emit)
    {
        if (!StackGuard.HasStack())
            return EvalOnFreshStack(node, env, input, path, emit);

        // Function calls entered by this frame (tail calls): they stay active, counting in the depth
        // of §11, while their outputs are consumed, that is until this frame returns.
        var calls = 0;
        Signal? signal;
        bool has;
        while (true)
        {
            if (node.AtMostOne)
            {
                signal = EvalOne(node, env, input, path, out has, out var value, out var at);
                if (signal is null && has)
                    signal = Output(emit, value, at);
                break;
            }

            switch (node.Kind)
            {
                case NodeKind.Comma:
                    signal = EvalItems((Comma)node, env, input, path, emit);
                    if (signal is not null)
                        break;
                    node = ((Comma)node).Items[^1];
                    continue;
                case NodeKind.Pipe when ((Pipe)node).Left.AtMostOne:
                    signal = EvalOne(((Pipe)node).Left, env, input, path, out has, out input, out path);
                    if (signal is not null || !has)
                        break;
                    node = ((Pipe)node).Right;
                    continue;
                case NodeKind.If when ((If)node).Condition.AtMostOne:
                    signal = Decide((If)node, ref node, env, input, out has);
                    if (signal is not null || !has)
                        break;
                    continue;
                case NodeKind.Alternative:
                    signal = EvalAlternative((Alternative)node, env, input, path, emit, out has);
                    if (signal is not null || has)
                        break;
                    node = ((Alternative)node).Right;
                    continue;
                case NodeKind.Bind when IsSimple((Bind)node):
                    signal = BindOne((Bind)node, ref env, input, out has);
                    if (signal is not null || !has)
                        break;
                    node = ((Bind)node).Body;
                    continue;
                case NodeKind.CallFunction:
                    calls++;
                    if (++depth > maxDepth)
                    {
                        signal = Fail(Errors.DepthExceeded);
                        break;
                    }

                    env = CallEnv(((CallFunction)node).Hops, ((CallFunction)node).Arguments, env);
                    node = program.Functions[((CallFunction)node).Function].Body;
                    continue;
                case NodeKind.CallParameter:
                    var closure = Env.Up(env, ((CallParameter)node).Hops)!.Closures![((CallParameter)node).Slot];
                    env = closure.Env;
                    node = closure.Node;
                    continue;
                default:
                    signal = EvalMany(node, env, input, path, emit);
                    break;
            }

            break;
        }

        depth -= calls;
        return signal;
    }

    // The items of a comma but the last, which Eval evaluates in tail position.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    Signal? EvalItems(Comma node, Env? env, JqValue input, Path? path, Emit emit)
    {
        var items = node.Items;
        for (var i = 0; i < items.Length - 1; i++)
        {
            if (Eval(items[i], env, input, path, emit) is { } signal)
                return signal;
        }

        return null;
    }

    // if c then a else b end with a condition that yields at most one value: node becomes the branch
    // to take; has is false when the condition has no output.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    Signal? Decide(If branch, ref Node node, Env? env, JqValue input, out bool has)
    {
        var signal = EvalOne(branch.Condition, env, input, null, out has, out var condition, out _);
        if (signal is null && has)
            node = condition.IsTruthy ? branch.Then : branch.Else;
        return signal;
    }

    // A binding Eval and EvalOne take in tail position: one pattern, a source and keys that yield at most one output.
    static bool IsSimple(Bind node) => node.Source.AtMostOne && node.Binder.KeysAtMostOne && node.Binder.Patterns.Length == 1;

    // Binds the output of a simple binding's source: env gets the new frame; has is false when nothing binds.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    Signal? BindOne(Bind node, ref Env? env, JqValue input, out bool has)
    {
        var signal = EvalOne(node.Source, env, input, null, out has, out var source, out _);
        if (signal is not null || !has)
            return signal;
        var slots = new JqValue[node.Binder.Slots];
        signal = MatchOne(node.Binder.Patterns[0], source, slots, env, out has);
        if (signal is null && has)
            env = new Env(env, slots);
        return signal;
    }

    // The constructs of Eval that are not in tail position.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    Signal? EvalMany(Node node, Env? env, JqValue input, Path? path, Emit emit)
    {
        switch (node.Kind)
        {
            case NodeKind.Pipe:
                return EvalPipe((Pipe)node, env, input, path, emit);
            case NodeKind.If:
                return EvalIf((If)node, env, input, path, emit);
            case NodeKind.Bind:
                return EvalBind((Bind)node, env, input, path, emit);
            case NodeKind.Iterate:
                return EvalIterate((Iterate)node, env, input, path, emit);
            case NodeKind.Index:
                return EvalIndex((Index)node, env, input, path, emit);
            case NodeKind.Slice:
                return EvalSlice((Slice)node, env, input, path, emit);
            case NodeKind.Binary:
                return EvalBinary((Binary)node, env, input, path, emit);
            case NodeKind.And or NodeKind.Or:
                return EvalLogic(node, env, input, path, emit);
            case NodeKind.Negate:
                return EvalNegate((Negate)node, env, input, path, emit);
            case NodeKind.ObjectExpr:
                return EvalObject((ObjectExpr)node, 0, JqObject.Empty, env, input, Path.Of(path), emit);
            case NodeKind.Template:
                return EvalTemplate((Template)node, ((Template)node).Parts.Length - 1, new string[((Template)node).Parts.Length], env, input, Path.Of(path), emit);
            case NodeKind.Try:
                return EvalTry((Try)node, env, input, path, emit);
            case NodeKind.Reduce:
                return EvalReduce((Reduce)node, env, input, path, emit);
            case NodeKind.Foreach:
                return EvalForeach((Foreach)node, env, input, path, emit);
            case NodeKind.Label:
                return EvalLabel((Label)node, env, input, path, emit);
            default:
                return EvalNative((CallNative)node, env, input, path, emit);
        }
    }

    // Lambdas are in methods of their own: a method whose lambda captures its parameters allocates the closure on every call.
    Signal? EvalPipe(Pipe node, Env? env, JqValue input, Path? path, Emit emit)
        => Eval(node.Left, env, input, path, (value, at) => Eval(node.Right, env, value, at, emit));

    Signal? EvalIf(If node, Env? env, JqValue input, Path? path, Emit emit)
        => Eval(node.Condition, env, input, null, (condition, _) => Eval(condition.IsTruthy ? node.Then : node.Else, env, input, path, emit));

    Signal? EvalNegate(Negate node, Env? env, JqValue input, Path? path, Emit emit)
    {
        var at = Path.Of(path);
        return Eval(node.Operand, env, input, null, (value, _) => Apply(ValueOps.TryNegate(value, out var result, out var error), result, error, at, emit));
    }

    /// <summary>
    /// Evaluates a node that yields at most one output (<see cref="Node.AtMostOne"/>): <paramref name="has"/>
    /// tells whether it did, and <paramref name="value"/> and <paramref name="at"/> are the output and its path.
    /// Any signal returned is the node's own: there is no consumer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public Signal? EvalOne(Node node, Env? env, JqValue input, Path? path, out bool has, out JqValue value, out Path? at)
    {
        if (!StackGuard.HasStack())
            return EvalOneOnFreshStack(node, env, input, path, out has, out value, out at);
        has = false;
        value = default;
        at = null;

        Signal? signal;
        while (true)
        {
            switch (node.Kind)
            {
                case NodeKind.Identity:
                    return One(input, path, out has, out value, out at);
                case NodeKind.Literal:
                    return One(((Literal)node).Value, Path.Of(path), out has, out value, out at);
                case NodeKind.Variable:
                    return One(Env.Up(env, ((Variable)node).Hops)!.Values![((Variable)node).Slot], Path.Of(path), out has, out value, out at);
                case NodeKind.Pipe:
                    signal = EvalOne(((Pipe)node).Left, env, input, path, out has, out input, out path);
                    if (signal is not null || !has)
                        return signal;
                    node = ((Pipe)node).Right;
                    continue;
                case NodeKind.If:
                    signal = Decide((If)node, ref node, env, input, out has);
                    if (signal is not null || !has)
                        return signal;
                    continue;
                case NodeKind.Alternative:
                    signal = EvalOne(((Alternative)node).Left, env, input, path, out has, out value, out at);
                    if (signal is not null || (has && value.IsTruthy))
                        return signal;
                    node = ((Alternative)node).Right;
                    continue;
                case NodeKind.Try:
                    signal = EvalOne(((Try)node).Body, env, input, path, out has, out value, out at);
                    if (signal is not { Kind: SignalKind.Error })
                        return signal;
                    has = false;
                    if (((Try)node).Handler is null)
                        return null;
                    node = ((Try)node).Handler!;
                    input = signal.Value;
                    path = Path.Of(path);
                    continue;
                case NodeKind.Bind when ((Bind)node).Binder.Patterns.Length == 1:
                    signal = BindOne((Bind)node, ref env, input, out has);
                    if (signal is not null || !has)
                        return signal;
                    node = ((Bind)node).Body;
                    continue;
                default:
                    return EvalOther(node, env, input, path, out has, out value, out at);
            }
        }
    }

    // The nodes of EvalOne that are not in tail position.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    Signal? EvalOther(Node node, Env? env, JqValue input, Path? path, out bool has, out JqValue value, out Path? at)
    {
        at = Path.Of(path);
        switch (node.Kind)
        {
            case NodeKind.Index or NodeKind.Slice:
                return EvalPostfixOne(node, env, input, path, out has, out value, out at);
            case NodeKind.Binary:
                return EvalBinaryOne((Binary)node, env, input, out has, out value);
            case NodeKind.And or NodeKind.Or:
                return EvalLogicOne(node, env, input, out has, out value);
            case NodeKind.Negate:
                return EvalNegateOne((Negate)node, env, input, out has, out value);
            case NodeKind.ArrayExpr:
                return EvalArrayOne((ArrayExpr)node, env, input, out has, out value);
            case NodeKind.ObjectExpr:
                return EvalObjectOne((ObjectExpr)node, env, input, out has, out value);
            case NodeKind.Template:
                return EvalTemplateOne((Template)node, env, input, out has, out value);
            case NodeKind.CallNative:
                return EvalNativeOne((CallNative)node, env, input, out has, out value);
            case NodeKind.Label:
                return EvalLabelOne((Label)node, env, input, path, out has, out value, out at);
            case NodeKind.Bind:
                return EvalBindOne((Bind)node, env, input, path, out has, out value, out at);
            case NodeKind.Reduce:
                return EvalReduceOne((Reduce)node, env, input, path, out has, out value, out at);
            default:
                return EvalLeafOne(node, env, input, out has, out value);
        }
    }

    // Variables, formats and break: no children.
    Signal? EvalLeafOne(Node node, Env? env, JqValue input, out bool has, out JqValue value)
    {
        has = false;
        value = default;
        switch (node)
        {
            case HostVariable host:
                if (hostValues[host.Index] is not { } hostValue)
                    return Fail(Errors.VariableNotDefined(host.Name));
                (has, value) = (true, hostValue);
                return null;
            case EnvVariable:
                (has, value) = (true, environment);
                return null;
            case Format format:
                if (!Formats.TryApply(format.Name, input, out value, out var error))
                    return Signal.Error(error);
                has = true;
                return null;
            default:
                return Signal.Break(Env.Up(env, ((Break)node).Hops)!.Label);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    Signal? EvalLabelOne(Label node, Env? env, JqValue input, Path? path, out bool has, out JqValue value, out Path? at)
    {
        var label = ++labels;
        var signal = EvalOne(node.Body, new Env(env, label), input, path, out has, out value, out at);
        return signal is { Kind: SignalKind.Break } && signal.Label == label ? null : signal;
    }

    // Reduce with one initial value: through the general evaluation, keeping its output.
    Signal? EvalReduceOne(Reduce node, Env? env, JqValue input, Path? path, out bool has, out JqValue value, out Path? at)
    {
        var capture = new Capture();
        var signal = EvalReduce(node, env, input, path, capture.Emit);
        (has, value, at) = (capture.Has, capture.Value, capture.At);
        return signal;
    }

    /// <summary>Evaluates <paramref name="node"/> and calls <paramref name="each"/> with each output, without a callback for a node that yields at most one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public Signal? ForEach(Node node, Env? env, JqValue input, Path? path, Emit each)
    {
        if (!node.AtMostOne)
            return Eval(node, env, input, path, each);
        var signal = EvalOne(node, env, input, path, out var has, out var value, out var at);
        return signal is null && has ? each(value, at) : signal;
    }

    /// <summary>
    /// Pushes an output to a consumer. Every output goes through here, because consumers chain:
    /// the outputs of a deep recursion flow back through one callback per level, which adds to the
    /// stack the evaluation used on the way down, so the guard of <see cref="HasStack"/> is needed
    /// here as much as at the start of an evaluation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static Signal? Output(Emit emit, JqValue value, Path? at) => StackGuard.HasStack() ? emit(value, at) : OutputOnFreshStack(emit, value, at);

    // The continuations of Eval and EvalOne on a fresh stack (StackGuard), in methods of their own so
    // that the closure is allocated only when the stack is short. Recursion only grows through
    // function calls, which the depth counter stops at MaxDepth, so the number of hops is bounded.
    Signal? EvalOnFreshStack(Node node, Env? env, JqValue input, Path? path, Emit emit)
        => StackGuard.OnFreshStack(() => Eval(node, env, input, path, emit));

    static Signal? OutputOnFreshStack(Emit emit, JqValue value, Path? at) => StackGuard.OnFreshStack(() => emit(value, at));

    Signal? EvalOneOnFreshStack(Node node, Env? env, JqValue input, Path? path, out bool has, out JqValue value, out Path? at)
    {
        var capture = new Capture();
        var signal = StackGuard.OnFreshStack(() => EvalOne(node, env, input, path, out capture.Has, out capture.Value, out capture.At));
        (has, value, at) = (capture.Has, capture.Value, capture.At);
        return signal;
    }

    /// <summary>The next label instance, unique within this evaluation.</summary>
    public long NewLabel() => ++labels;

    /// <summary>An error signal.</summary>
    public static Signal Fail(JqValue error) => Signal.Error(error);

    static Signal? One(JqValue output, Path? path, out bool has, out JqValue value, out Path? at)
    {
        has = true;
        value = output;
        at = path;
        return null;
    }

    static Signal? Apply(bool ok, JqValue result, JqValue error, Path? at, Emit emit) => ok ? Output(emit, result, at) : Signal.Error(error);

    // The environment of a call: the caller's with the frames between the call and the definition
    // removed, plus the closures of the arguments when there are any.
    static Env? CallEnv(int hops, ImmutableArray<Node> arguments, Env? env)
    {
        var defined = Env.Up(env, hops);
        if (arguments.Length == 0)
            return defined;
        var closures = new Closure[arguments.Length];
        for (var i = 0; i < closures.Length; i++)
            closures[i] = new Closure(arguments[i], env);
        return new Env(defined, closures);
    }

    // a // b: the true outputs of a; found tells whether there was one (then b is not evaluated).
    Signal? EvalAlternative(Alternative node, Env? env, JqValue input, Path? path, Emit emit, out bool found)
    {
        var any = false;
        var signal = Eval(node.Left, env, input, path, (value, at) =>
        {
            if (!value.IsTruthy)
                return null;
            any = true;
            return Output(emit, value, at);
        });
        found = any;
        return signal;
    }

    // T[]: in path mode each element's path is the target's plus its index or key.
    Signal? EvalIterate(Iterate node, Env? env, JqValue input, Path? path, Emit emit)
        => ForEach(node.Target, env, input, path, (target, at) =>
        {
            if (at is { IsTracked: false })
                return Fail(Errors.InvalidPathIterate(target));
            if (target.Kind == JqKind.Array)
            {
                var array = target.AsArray();
                for (var i = 0; i < array.Count; i++)
                {
                    if (Output(emit, array[i], at?.Append(JqValue.FromNumber(i))) is { } stop)
                        return stop;
                }

                return null;
            }

            if (target.Kind != JqKind.Object)
                return Fail(Errors.CannotIterate(target));
            var obj = target.AsObject();
            for (var i = 0; i < obj.Count; i++)
            {
                if (Output(emit, obj.ValueAt(i), at?.Append(JqValue.FromString(obj.KeyAt(i)))) is { } stop)
                    return stop;
            }

            return null;
        });

    // T[e]: for each key (on the input of T, in value mode), for each target (§6.3).
    Signal? EvalIndex(Index node, Env? env, JqValue input, Path? path, Emit emit)
        => ForEach(node.Key, env, input, null, (key, _) =>
            ForEach(node.Target, env, input, path, (target, at) => IndexStep(target, at, key, out var result, out at) ?? Output(emit, result, at)));

    // T[from:to]: from outermost, then to, then T (§6.3).
    Signal? EvalSlice(Slice node, Env? env, JqValue input, Path? path, Emit emit)
        => Bound(node.From, env, input, (from, _) =>
            Bound(node.To, env, input, (to, _) =>
                ForEach(node.Target, env, input, path, (target, at) => SliceStep(target, at, from, to, out var result, out at) ?? Output(emit, result, at))));

    // A slice bound: null when absent.
    Signal? Bound(Node? bound, Env? env, JqValue input, Emit each)
        => bound is null ? each(JqValue.Null, null) : ForEach(bound, env, input, null, each);

    // One index of a target with its path: in path mode the target must be tracked.
    static Signal? IndexStep(JqValue target, Path? at, JqValue key, out JqValue result, out Path? next)
    {
        next = at;
        if (at is { IsTracked: false })
        {
            result = default;
            return Fail(Errors.InvalidPathAccess(key, target));
        }

        if (!ValueOps.TryIndex(target, key, out result, out var error))
            return Signal.Error(error);
        next = at?.Append(key);
        return null;
    }

    // One slice of a target with its path; in a path the slice is the key {"start": from, "end": to} (§8.1).
    static Signal? SliceStep(JqValue target, Path? at, JqValue from, JqValue to, out JqValue result, out Path? next)
    {
        result = default;
        next = at;
        if (at is not null)
        {
            var key = JqValue.FromObject(JqObject.Empty.Set("start", from).Set("end", to));
            if (!at.IsTracked)
                return Fail(Errors.InvalidPathAccess(key, target));
            next = at.Append(key);
        }

        return ValueOps.TrySlice(target, from, to, out result, out var error) ? null : Signal.Error(error);
    }

    // a op b: for each right output, for each left output (§6.3); operands in value mode. A right
    // operand that yields at most one value is evaluated first, so the common case has one callback.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    Signal? EvalBinary(Binary node, Env? env, JqValue input, Path? path, Emit emit)
    {
        var at = Path.Of(path);
        if (!node.Right.AtMostOne)
            return EvalRights(node, env, input, at, emit);
        var signal = EvalOne(node.Right, env, input, null, out var has, out var value, out _);
        return signal is null && has ? EvalLeft(node, value, env, input, at, emit) : signal;
    }

    Signal? EvalRights(Binary node, Env? env, JqValue input, Path? at, Emit emit)
        => Eval(node.Right, env, input, null, (right, _) => EvalLeft(node, right, env, input, at, emit));

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    Signal? EvalLeft(Binary node, JqValue right, Env? env, JqValue input, Path? at, Emit emit)
        => Eval(node.Left, env, input, null, (left, _) => !TryApply(node.Op, left, right, out var result, out var error)
            ? Signal.Error(error)
            : Output(emit, result, at));

    /// <summary>The arithmetic operators and comparisons of §4.</summary>
    public static bool TryApply(Syntax.BinaryOp op, JqValue left, JqValue right, out JqValue result, out JqValue error)
    {
        error = default;
        switch (op)
        {
            case Syntax.BinaryOp.Add:
                return ValueOps.TryAdd(left, right, out result, out error);
            case Syntax.BinaryOp.Subtract:
                return ValueOps.TrySubtract(left, right, out result, out error);
            case Syntax.BinaryOp.Multiply:
                return ValueOps.TryMultiply(left, right, out result, out error);
            case Syntax.BinaryOp.Divide:
                return ValueOps.TryDivide(left, right, out result, out error);
            case Syntax.BinaryOp.Modulo:
                return ValueOps.TryModulo(left, right, out result, out error);
            case Syntax.BinaryOp.Equal:
                result = JqValue.FromBool(ValueOps.AreEqual(left, right));
                return true;
            case Syntax.BinaryOp.NotEqual:
                result = JqValue.FromBool(!ValueOps.AreEqual(left, right));
                return true;
            case Syntax.BinaryOp.Less:
                result = JqValue.FromBool(ValueOps.Compare(left, right) < 0);
                return true;
            case Syntax.BinaryOp.LessEqual:
                result = JqValue.FromBool(ValueOps.Compare(left, right) <= 0);
                return true;
            case Syntax.BinaryOp.Greater:
                result = JqValue.FromBool(ValueOps.Compare(left, right) > 0);
                return true;
            default:
                result = JqValue.FromBool(ValueOps.Compare(left, right) >= 0);
                return true;
        }
    }

    // a and b, a or b (§7.3): for each left output, the right side only when the left does not decide.
    Signal? EvalLogic(Node node, Env? env, JqValue input, Path? path, Emit emit)
    {
        var isAnd = node.Kind == NodeKind.And;
        var (left, right) = isAnd ? (((And)node).Left, ((And)node).Right) : (((Or)node).Left, ((Or)node).Right);
        var at = Path.Of(path);
        return ForEach(left, env, input, null, (first, _) =>
            first.IsTruthy != isAnd
                ? Output(emit, JqValue.FromBool(!isAnd), at)
                : ForEach(right, env, input, null, (second, _) => Output(emit, JqValue.FromBool(second.IsTruthy), at)));
    }

    // {k1: v1, …} from entry i on, with the entries before it in obj: keys and values in value
    // mode, k1 outermost (§6.3). Entries that yield at most one output are taken in a loop.
    Signal? EvalObject(ObjectExpr node, int i, JqObject obj, Env? env, JqValue input, Path? at, Emit emit)
    {
        for (; i < node.Entries.Length; i++)
        {
            var entry = node.Entries[i];
            if (!entry.Key.AtMostOne || entry.Value is { AtMostOne: false })
                break;
            var signal = EvalEntryOne(entry, env, input, out var has, out var key, out var value);
            if (signal is not null || !has)
                return signal;
            obj = obj.Set(key, value);
        }

        if (i == node.Entries.Length)
            return Output(emit, JqValue.FromObject(obj), at);
        var next = node.Entries[i];
        return ForEach(next.Key, env, input, null, (key, _) =>
        {
            if (key.Kind != JqKind.String)
                return Fail(Errors.CannotUseAsKey(key));
            if (next.Value is null)
            {
                return ValueOps.TryIndex(input, key, out var indexed, out var error)
                    ? EvalObject(node, i + 1, obj.Set(key.AsString(), indexed), env, input, at, emit)
                    : Signal.Error(error);
            }

            return ForEach(next.Value, env, input, null, (value, _) => EvalObject(node, i + 1, obj.Set(key.AsString(), value), env, input, at, emit));
        });
    }

    // One entry whose key and value yield at most one output.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    Signal? EvalEntryOne(ObjectEntry entry, Env? env, JqValue input, out bool has, out string key, out JqValue value)
    {
        key = "";
        value = default;
        var signal = EvalOne(entry.Key, env, input, null, out has, out var name, out _);
        if (signal is not null || !has)
            return signal;
        if (name.Kind != JqKind.String)
            return Fail(Errors.CannotUseAsKey(name));
        key = name.AsString();
        if (entry.Value is null)
            return ValueOps.TryIndex(input, name, out value, out var error) ? null : Signal.Error(error);
        return EvalOne(entry.Value, env, input, null, out has, out value, out _);
    }

    // "a\(x)b\(y)": the parts from part i down to the first, the last part outermost (§6.3).
    Signal? EvalTemplate(Template node, int i, string[] texts, Env? env, JqValue input, Path? at, Emit emit)
    {
        if (i < 0)
            return Output(emit, JqValue.FromString(Join(node, texts)), at);
        return ForEach(node.Parts[i], env, input, null, (part, _) =>
        {
            if (!Convert(node.Format, part, out texts[i], out var error))
                return Signal.Error(error);
            return EvalTemplate(node, i - 1, texts, env, input, at, emit);
        });
    }

    // An interpolated value as text: tostring, or the template's format.
    static bool Convert(string? format, JqValue part, out string text, out JqValue error)
    {
        error = default;
        if (format is null)
        {
            text = part.Kind == JqKind.String ? part.AsString() : ValueText.ToJson(part);
            return true;
        }

        text = "";
        if (!Formats.TryApply(format, part, out var formatted, out error))
            return false;
        text = formatted.AsString();
        return true;
    }

    static string Join(Template node, string[] parts)
    {
        var builder = new StringBuilder(node.Texts[0]);
        for (var i = 0; i < parts.Length; i++)
            builder.Append(parts[i]).Append(node.Texts[i + 1]);
        return builder.ToString();
    }

    // try B catch H (§7.13): only B's own errors are caught, never one its consumer returned (§6.2).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    Signal? EvalTry(Try node, Env? env, JqValue input, Path? path, Emit emit)
    {
        var consumer = new Consumer(emit);
        var signal = Eval(node.Body, env, input, path, consumer.Emit);
        if (signal is not { Kind: SignalKind.Error } || consumer.Raised(signal))
            return signal;
        return node.Handler is null ? null : Eval(node.Handler, env, signal.Value, Path.Of(path), emit);
    }

    /// <summary>
    /// Tells an operand's own signals from those of its consumer (§6.2). It stands between the
    /// operand and the consumer and remembers the last signal the consumer returned; every construct
    /// passes a signal up unchanged and stops at once, so when the operand returns that very object,
    /// it came from the consumer. Comparing identities costs nothing per output.
    /// </summary>
    sealed class Consumer(Emit emit)
    {
        Signal? last;

        public Signal? Emit(JqValue value, Path? path) => last = Output(emit, value, path);

        public bool Raised(Signal signal) => ReferenceEquals(signal, last);
    }

    // Keeps the output of a node that yields at most one.
    sealed class Capture
    {
        public bool Has;
        public JqValue Value;
        public Path? At;

        public Signal? Emit(JqValue value, Path? at)
        {
            Has = true;
            Value = value;
            At = at;
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    Signal? EvalLabel(Label node, Env? env, JqValue input, Path? path, Emit emit)
    {
        var label = ++labels;
        var signal = Eval(node.Body, new Env(env, label), input, path, emit);
        return signal is { Kind: SignalKind.Break } && signal.Label == label ? null : signal;
    }

    // E as P1 ?// P2 | B, in general: for each output of E (value mode), each binding, B in path mode.
    Signal? EvalBind(Bind node, Env? env, JqValue input, Path? path, Emit emit)
    {
        var consumer = node.Binder.Patterns.Length > 1 ? new Consumer(emit) : null;
        var body = consumer is null ? emit : consumer.Emit;
        return ForEach(node.Source, env, input, null, (source, _) =>
            BindEach(node.Binder, source, env, frame => Eval(node.Body, frame, input, path, body), consumer));
    }

    // E as P1 ?// P2 | B where E, the keys and B yield at most one output.
    Signal? EvalBindOne(Bind node, Env? env, JqValue input, Path? path, out bool has, out JqValue value, out Path? at)
    {
        value = default;
        at = null;
        var signal = EvalOne(node.Source, env, input, null, out has, out var source, out _);
        if (signal is not null || !has)
            return signal;
        var patterns = node.Binder.Patterns;
        for (var i = 0; i < patterns.Length; i++)
        {
            var slots = new JqValue[node.Binder.Slots];
            signal = MatchOne(patterns[i], source, slots, env, out has);
            if (signal is null && has)
                signal = EvalOne(node.Body, new Env(env, slots), input, path, out has, out value, out at);
            if (signal is not { Kind: SignalKind.Error } || i == patterns.Length - 1)
                return signal;
            has = false;
        }

        return null;
    }

    /// <summary>
    /// Binds <paramref name="value"/> by the binder's patterns and runs <paramref name="body"/> in
    /// the environment with the new frame, once per binding (several when a key has several
    /// outputs). With <c>?//</c> alternatives (§7.5), when matching fails or the body raises its own
    /// error (not one of <paramref name="consumer"/>), the next pattern is tried with the same value,
    /// all variables null again, after restoring what <paramref name="save"/> saved before the first
    /// (the state of reduce and foreach); errors with the last pattern propagate.
    /// </summary>
    Signal? BindEach(Binder binder, JqValue value, Env? env, Func<Env, Signal?> body, Consumer? consumer, Func<Action>? save = null)
    {
        var patterns = binder.Patterns;
        var restore = save?.Invoke();
        for (var i = 0; ; i++)
        {
            if (i > 0)
                restore?.Invoke();
            var slots = new JqValue[binder.Slots];
            Signal? signal;
            if (binder.KeysAtMostOne)
            {
                signal = MatchOne(patterns[i], value, slots, env, out var matched);
                if (signal is null && matched)
                    signal = body(new Env(env, slots));
            }
            else
            {
                signal = Match(patterns[i], value, slots, env, () => body(new Env(env, (JqValue[])slots.Clone())));
            }

            if (signal is not { Kind: SignalKind.Error } || i == patterns.Length - 1 || (consumer is not null && consumer.Raised(signal)))
                return signal;
        }
    }

    // Destructures value by a pattern whose keys yield at most one output; matched is false when a
    // key yields none. Array elements from the last, object entries from the first (§7.5); the
    // keys are evaluated in the binding's outer environment, on the value being matched.
    Signal? MatchOne(Pattern pattern, JqValue value, JqValue[] slots, Env? env, out bool matched)
    {
        matched = true;
        switch (pattern)
        {
            case VariablePattern variable:
                if (variable.Slot >= 0)
                    slots[variable.Slot] = value;
                return null;
            case ArrayPattern array:
                for (var i = array.Elements.Length - 1; i >= 0; i--)
                {
                    if (!ValueOps.TryIndex(value, JqValue.FromNumber(i), out var element, out var error))
                        return Signal.Error(error);
                    var signal = MatchOne(array.Elements[i], element, slots, env, out matched);
                    if (signal is not null || !matched)
                        return signal;
                }

                return null;
            default:
                foreach (var entry in ((ObjectPattern)pattern).Entries)
                {
                    var signal = EvalOne(entry.Key, env, value, null, out matched, out var key, out _);
                    if (signal is not null || !matched)
                        return signal;
                    if (MatchKey(value, key, out var member) is { } failed)
                        return failed;
                    if (entry.Slot >= 0)
                        slots[entry.Slot] = member;
                    if (entry.Value is not null && ((signal = MatchOne(entry.Value, member, slots, env, out matched)) is not null || !matched))
                        return signal;
                }

                return null;
        }
    }

    // The value of a key of an object pattern: v[k] with the indexing rules of §7.6, whatever the
    // kind of the key (§7.5; only literal keys are checked, statically).
    static Signal? MatchKey(JqValue value, JqValue key, out JqValue member)
        => ValueOps.TryIndex(value, key, out member, out var error) ? null : Signal.Error(error);

    // Destructures value when a key may yield several outputs: next runs once per complete binding.
    Signal? Match(Pattern pattern, JqValue value, JqValue[] slots, Env? env, Func<Signal?> next)
    {
        switch (pattern)
        {
            case VariablePattern variable:
                if (variable.Slot >= 0)
                    slots[variable.Slot] = value;
                return next();
            case ArrayPattern array:
                return MatchElements(array, array.Elements.Length - 1, value, slots, env, next);
            default:
                return MatchEntries((ObjectPattern)pattern, 0, value, slots, env, next);
        }
    }

    Signal? MatchElements(ArrayPattern array, int i, JqValue value, JqValue[] slots, Env? env, Func<Signal?> next)
    {
        if (i < 0)
            return next();
        if (!ValueOps.TryIndex(value, JqValue.FromNumber(i), out var element, out var error))
            return Signal.Error(error);
        return Match(array.Elements[i], element, slots, env, () => MatchElements(array, i - 1, value, slots, env, next));
    }

    Signal? MatchEntries(ObjectPattern obj, int i, JqValue value, JqValue[] slots, Env? env, Func<Signal?> next)
    {
        if (i == obj.Entries.Length)
            return next();
        var entry = obj.Entries[i];
        return ForEach(entry.Key, env, value, null, (key, _) =>
        {
            if (MatchKey(value, key, out var member) is { } failed)
                return failed;
            if (entry.Slot >= 0)
                slots[entry.Slot] = member;
            Func<Signal?> rest = () => MatchEntries(obj, i + 1, value, slots, env, next);
            return entry.Value is null ? rest() : Match(entry.Value, member, slots, env, rest);
        });
    }

    // reduce SRC as P (INIT; UPDATE) (§7.8): one reduction per initial value; the state is the last
    // output of UPDATE, or null. In path mode the state carries its path. With ?// alternatives, a
    // retried alternative starts from the state before the source value.
    Signal? EvalReduce(Reduce node, Env? env, JqValue input, Path? path, Emit emit)
        => ForEach(node.Init, env, input, path, (initial, initialAt) =>
        {
            var state = new State(initial, initialAt);
            var signal = Eval(node.Source, env, input, null, (source, _) => BindEach(node.Binder, source, env, frame =>
            {
                if (node.Update.AtMostOne)
                {
                    var failed = EvalOne(node.Update, frame, state.Value, state.At, out var has, out var next, out var nextAt);
                    if (failed is null)
                        state.Set(has ? next : JqValue.Null, has ? nextAt : Path.Of(path));
                    return failed;
                }

                var last = new Capture();
                var stop = Eval(node.Update, frame, state.Value, state.At, last.Emit);
                if (stop is null)
                    state.Set(last.Has ? last.Value : JqValue.Null, last.Has ? last.At : Path.Of(path));
                return stop;
            }, null, Restart(node.Binder, state)));
            return signal ?? Output(emit, state.Value, state.At);
        });

    // foreach SRC as P (INIT; UPDATE; EXTRACT) (§7.9).
    Signal? EvalForeach(Foreach node, Env? env, JqValue input, Path? path, Emit emit)
    {
        var consumer = node.Binder.Patterns.Length > 1 ? new Consumer(emit) : null;
        var output = consumer is null ? emit : consumer.Emit;
        return ForEach(node.Init, env, input, path, (initial, initialAt) =>
        {
            var state = new State(initial, initialAt);
            return Eval(node.Source, env, input, null, (source, _) => BindEach(node.Binder, source, env, frame =>
            {
                var updated = false;
                var signal = Eval(node.Update, frame, state.Value, state.At, (next, nextAt) =>
                {
                    updated = true;
                    state.Set(next, nextAt);
                    return Eval(node.Extract, frame, next, nextAt, output);
                });
                if (signal is null && !updated)
                    state.Set(JqValue.Null, Path.Of(path));
                return signal;
            }, consumer, Restart(node.Binder, state)));
        });
    }

    // The state of a reduce or foreach, with its path.
    sealed class State(JqValue value, Path? at)
    {
        public JqValue Value { get; private set; } = value;

        public Path? At { get; private set; } = at;

        public void Set(JqValue value, Path? at) => (Value, At) = (value, at);

        // Takes the state back to what it is now.
        public Action Saved()
        {
            var (value, at) = (Value, At);
            return () => Set(value, at);
        }
    }

    // With ?// alternatives: what restores the state before a source value, for a retried alternative (§7.8).
    static Func<Action>? Restart(Binder binder, State state) => binder.Patterns.Length > 1 ? state.Saved : null;

    // A native: a closure native decides everything; a value native is called once per combination
    // of its arguments' outputs, the last argument outermost (§6.3, §9.1).
    Signal? EvalNative(CallNative node, Env? env, JqValue input, Path? path, Emit emit)
    {
        var native = node.Native;
        if (native.TakesClosures)
        {
            return NativeTable.Closure(native) is { } function
                ? function(this, env, node.Arguments, input, path, emit)
                : Fail(Errors.NotImplemented(native.Name, native.Arity));
        }

        if (NativeTable.Value(native) is not { } valueFunction)
            return Fail(Errors.NotImplemented(native.Name, native.Arity));
        var arguments = new JqValue[native.Arity];
        return Combine(valueFunction, node.Arguments, arguments.Length - 1, arguments, env, input, Path.Of(path), emit);
    }

    Signal? Combine(ValueNative function, ImmutableArray<Node> nodes, int i, JqValue[] arguments, Env? env, JqValue input, Path? at, Emit emit)
    {
        if (i < 0)
            return Apply(function(this, input, arguments, out var result, out var error), result, error, at, emit);
        return ForEach(nodes[i], env, input, null, (argument, _) =>
        {
            arguments[i] = argument;
            return Combine(function, nodes, i - 1, arguments, env, input, at, emit);
        });
    }

    // A value native whose arguments yield at most one output each.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    Signal? EvalNativeOne(CallNative node, Env? env, JqValue input, out bool has, out JqValue value)
    {
        has = false;
        value = default;
        var native = node.Native;
        if (NativeTable.Value(native) is not { } function)
            return Fail(Errors.NotImplemented(native.Name, native.Arity));
        var arguments = native.Arity == 0 ? [] : new JqValue[native.Arity];
        for (var i = arguments.Length - 1; i >= 0; i--)
        {
            var signal = EvalOne(node.Arguments[i], env, input, null, out has, out arguments[i], out _);
            if (signal is not null || !has)
                return signal;
        }

        has = true;
        return function(this, input, arguments, out value, out var error) ? null : Signal.Error(error);
    }

    // A chain of indexes and slices, T[k1][k2]…: the keys from the outermost in, each on the input
    // (§6.3: the key before its target), then the innermost target, then each index outward.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    Signal? EvalPostfixOne(Node node, Env? env, JqValue input, Path? path, out bool has, out JqValue value, out Path? at)
    {
        value = default;
        at = null;
        var nodes = spineNodes.Count;
        var keys = spineValues.Count;
        Signal? signal = null;
        has = true;
        while (has && signal is null && node.Kind is NodeKind.Index or NodeKind.Slice)
        {
            spineNodes.Add(node);
            if (node is Index index)
            {
                signal = EvalOne(index.Key, env, input, null, out has, out var key, out _);
                spineValues.Add(key);
                node = index.Target;
            }
            else
            {
                var slice = (Slice)node;
                var to = JqValue.Null;
                signal = EvalBound(slice.From, env, input, out has, out var from);
                if (signal is null && has)
                    signal = EvalBound(slice.To, env, input, out has, out to);
                spineValues.Add(from);
                spineValues.Add(to);
                node = slice.Target;
            }
        }

        if (signal is null && has)
            signal = EvalOne(node, env, input, path, out has, out value, out at);
        for (int n = spineNodes.Count - 1, k = spineValues.Count; n >= nodes && signal is null && has; n--)
        {
            signal = spineNodes[n].Kind == NodeKind.Index
                ? IndexStep(value, at, spineValues[--k], out value, out at)
                : SliceStep(value, at, spineValues[(k -= 2)], spineValues[k + 1], out value, out at);
        }

        spineNodes.RemoveRange(nodes, spineNodes.Count - nodes);
        spineValues.RemoveRange(keys, spineValues.Count - keys);
        return signal;
    }

    // A bound of a slice: null when absent.
    Signal? EvalBound(Node? bound, Env? env, JqValue input, out bool has, out JqValue value)
    {
        if (bound is not null)
            return EvalOne(bound, env, input, null, out has, out value, out _);
        has = true;
        value = JqValue.Null;
        return null;
    }

    // A left-deep chain of operators, ((a op1 b) op2 c)…: the right operands from the outermost in
    // (§6.3: the right operand first), then the innermost left one, then each operation outward.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    Signal? EvalBinaryOne(Binary node, Env? env, JqValue input, out bool has, out JqValue value)
    {
        value = default;
        var nodes = spineNodes.Count;
        var operands = spineValues.Count;
        Signal? signal = null;
        has = true;
        Node left = node;
        while (has && signal is null && left is Binary binary)
        {
            signal = EvalOne(binary.Right, env, input, null, out has, out var right, out _);
            spineNodes.Add(binary);
            spineValues.Add(right);
            left = binary.Left;
        }

        if (signal is null && has)
            signal = EvalOne(left, env, input, null, out has, out value, out _);
        for (var n = spineNodes.Count - 1; n >= nodes && signal is null && has; n--)
        {
            if (!TryApply(((Binary)spineNodes[n]).Op, value, spineValues[n - nodes + operands], out value, out var error))
                signal = Signal.Error(error);
        }

        spineNodes.RemoveRange(nodes, spineNodes.Count - nodes);
        spineValues.RemoveRange(operands, spineValues.Count - operands);
        return signal;
    }

    // A left-deep chain of and/or: the innermost left operand first, then each operator outward,
    // evaluating its right operand only when the left value does not decide.
    Signal? EvalLogicOne(Node node, Env? env, JqValue input, out bool has, out JqValue value)
    {
        var nodes = spineNodes.Count;
        while (node.Kind is NodeKind.And or NodeKind.Or)
        {
            spineNodes.Add(node);
            node = node is And conjunction ? conjunction.Left : ((Or)node).Left;
        }

        var signal = EvalOne(node, env, input, null, out has, out value, out _);
        for (var n = spineNodes.Count - 1; n >= nodes && signal is null && has; n--)
        {
            var isAnd = spineNodes[n].Kind == NodeKind.And;
            if (value.IsTruthy == isAnd)
            {
                var right = isAnd ? ((And)spineNodes[n]).Right : ((Or)spineNodes[n]).Right;
                signal = EvalOne(right, env, input, null, out has, out value, out _);
            }

            value = JqValue.FromBool(value.IsTruthy);
        }

        spineNodes.RemoveRange(nodes, spineNodes.Count - nodes);
        return signal;
    }

    // -…-x: the operand, then each minus.
    Signal? EvalNegateOne(Negate node, Env? env, JqValue input, out bool has, out JqValue value)
    {
        var count = 0;
        Node operand = node;
        for (; operand is Negate negate; operand = negate.Operand)
            count++;
        var signal = EvalOne(operand, env, input, null, out has, out value, out _);
        for (; signal is null && has && count > 0; count--)
        {
            if (!ValueOps.TryNegate(value, out value, out var error))
                signal = Signal.Error(error);
        }

        return signal;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    Signal? EvalArrayOne(ArrayExpr node, Env? env, JqValue input, out bool has, out JqValue value)
    {
        has = true;
        value = JqValue.FromArray(JqArray.Empty);
        if (node.Body is null)
            return null;
        if (node.Body.AtMostOne)
        {
            var signal = EvalOne(node.Body, env, input, null, out var any, out var item, out _);
            if (signal is null && any)
                value = JqValue.FromArray(JqArray.Of(item));
            has = signal is null;
            return signal;
        }

        return Collect(node.Body, env, input, out has, out value);
    }

    // [f] for an f with several outputs.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    Signal? Collect(Node body, Env? env, JqValue input, out bool has, out JqValue value)
    {
        var items = new List<JqValue>();
        var stop = Eval(body, env, input, null, (item, _) =>
        {
            items.Add(item);
            return null;
        });
        has = stop is null;
        value = JqValue.FromArray(JqArray.FromOwned([.. items]));
        return stop;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    Signal? EvalObjectOne(ObjectExpr node, Env? env, JqValue input, out bool has, out JqValue value)
    {
        has = true;
        value = default;
        var obj = JqObject.Empty;
        foreach (var entry in node.Entries)
        {
            var signal = EvalEntryOne(entry, env, input, out has, out var key, out var member);
            if (signal is not null || !has)
                return signal;
            obj = obj.Set(key, member);
        }

        value = JqValue.FromObject(obj);
        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    Signal? EvalTemplateOne(Template node, Env? env, JqValue input, out bool has, out JqValue value)
    {
        value = default;
        has = true;
        var texts = new string[node.Parts.Length];
        for (var i = texts.Length - 1; i >= 0; i--)
        {
            var signal = EvalOne(node.Parts[i], env, input, null, out has, out var part, out _);
            if (signal is not null || !has)
                return signal;
            if (!Convert(node.Format, part, out texts[i], out var error))
                return Signal.Error(error);
        }

        value = JqValue.FromString(Join(node, texts));
        return null;
    }
}