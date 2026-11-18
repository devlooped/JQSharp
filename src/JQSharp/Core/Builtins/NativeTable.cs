using System.Collections.Immutable;
using Devlooped.Core.Binding;
using Devlooped.Core.Runtime;
using Devlooped.Core.Values;
using Path = Devlooped.Core.Runtime.Path;

namespace Devlooped.Core.Builtins;

/// <summary>
/// A native with value arguments (§9.1): called once per combination of its arguments' outputs,
/// it gives one output (true, <paramref name="result"/>) or an error (false, <paramref name="error"/>).
/// </summary>
delegate bool ValueNative(Interpreter interpreter, JqValue input, JqValue[] arguments, out JqValue result, out JqValue error);

/// <summary>
/// A native that takes its arguments as closures (<see cref="Native.TakesClosures"/>): it evaluates
/// them itself with <paramref name="env"/>, pushes its outputs to <paramref name="emit"/> (with their
/// paths in path mode, when it passes paths) and returns a signal, as Interpreter.Eval does.
/// </summary>
delegate Signal? ClosureNative(Interpreter interpreter, Env? env, ImmutableArray<Node> arguments, JqValue input, Path? path, Emit emit);

/// <summary>
/// The implementations of the native catalogue, by <see cref="Native.Index"/>. Each family of
/// natives registers its entries; every entry of the catalogue has one, which the table checks
/// once, when it is built (a missing one is a defect of the build, not a run-time error).
/// </summary>
static class NativeTable
{
    static readonly ValueNative?[] values = new ValueNative?[Natives.All.Length];
    static readonly ClosureNative?[] closures = new ClosureNative?[Natives.All.Length];

    static NativeTable()
    {
        CoreNatives.Register();
        LoopNatives.Register();
        PathNatives.Register();
        CollectionNatives.Register();
        StringNatives.Register();
        AssignNatives.Register();
        ModuleNatives.Register();
        Containment.Register();
        MathNatives.Register();
        DateNatives.Register();
        RegexNatives.Register();

        foreach (var native in Natives.All)
        {
            if (!IsImplemented(native))
                throw new InvalidOperationException($"{native} has no implementation");
        }
    }

    /// <summary>The implementation of a value native (one that does not take closures).</summary>
    public static ValueNative Value(Native native) => values[native.Index]!;

    /// <summary>The implementation of a native that takes closures.</summary>
    public static ClosureNative Closure(Native native) => closures[native.Index]!;

    /// <summary>Whether <paramref name="native"/> has an implementation.</summary>
    public static bool IsImplemented(Native native) => values[native.Index] is not null || closures[native.Index] is not null;

    // Registration checks the kind against the catalogue: a mismatch is a defect of the build.
    internal static void Add(string name, int arity, ValueNative function)
        => values[Find(name, arity, closures: false).Index] = function;

    internal static void Add(string name, int arity, ClosureNative function)
        => closures[Find(name, arity, closures: true).Index] = function;

    static Native Find(string name, int arity, bool closures)
    {
        var native = Natives.Find(name, arity) ?? throw new InvalidOperationException($"{name}/{arity} is not in the native catalogue");
        return native.TakesClosures == closures ? native : throw new InvalidOperationException($"{name}/{arity} is registered with the wrong kind");
    }

    /// <summary>A value native's success.</summary>
    internal static bool Ok(JqValue value, out JqValue result, out JqValue error)
    {
        result = value;
        error = default;
        return true;
    }

    /// <summary>A value native's error.</summary>
    internal static bool Fail(JqValue message, out JqValue result, out JqValue error)
    {
        result = default;
        error = message;
        return false;
    }
}