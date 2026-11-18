using JQLite.Core.Binding;
using JQLite.Core.Values;

namespace JQLite.Core.Runtime;

/// <summary>A filter argument: the argument's node with the caller's environment (§7.7).</summary>
sealed class Closure(Node node, Env? env)
{
    public Node Node { get; } = node;

    public Env? Env { get; } = env;
}

/// <summary>
/// One frame of the environment, linked to the frame outside it; the empty environment is null.
/// A frame holds the variables of one binding (<see cref="Values"/>), the closures of one call of a
/// function with parameters (<see cref="Closures"/>), or one label instance (<see cref="Label"/>);
/// the resolver numbered the frames, so a reference walks a known number of frames outward (Bound.cs).
/// Frames are never changed once built, so they can be shared freely.
/// </summary>
sealed class Env
{
    public Env(Env? parent, JqValue[] values)
    {
        Parent = parent;
        Values = values;
    }

    public Env(Env? parent, Closure[] closures)
    {
        Parent = parent;
        Closures = closures;
    }

    public Env(Env? parent, long label)
    {
        Parent = parent;
        Label = label;
    }

    public Env? Parent { get; }

    public JqValue[]? Values { get; }

    public Closure[]? Closures { get; }

    public long Label { get; }

    /// <summary>The environment <paramref name="hops"/> frames out from <paramref name="env"/>.</summary>
    public static Env? Up(Env? env, int hops)
    {
        for (; hops > 0; hops--)
            env = env!.Parent;
        return env;
    }
}
