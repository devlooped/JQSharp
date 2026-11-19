using JQLite.Core.Values;

namespace JQLite.Core.Runtime;

/// <summary>
/// Where a value came from, in path mode (§8.1): the keys from the root input, as an immutable list
/// linked from the last key, so appending is O(1) and shares the prefix.
/// </summary>
/// <remarks>
/// Evaluation takes and emits a <c>Path?</c>: null in value mode (no path is ever built), and in
/// path mode either a tracked path (<see cref="Root"/> and what is appended to it) or
/// <see cref="Untracked"/>, for an output that has no path (a literal, a variable, an arithmetic
/// result).
/// </remarks>
sealed class Path
{
    /// <summary>The empty path: the root input in path mode.</summary>
    public static readonly Path Root = new(null, default, 0);

    /// <summary>An output in path mode that has no path.</summary>
    public static readonly Path Untracked = new(null, default, -1);

    readonly Path? parent;
    readonly JqValue key;
    readonly int length;

    Path(Path? parent, JqValue key, int length)
    {
        this.parent = parent;
        this.key = key;
        this.length = length;
    }

    public bool IsTracked => length >= 0;

    /// <summary>The path of an output without one: null in value mode, else <see cref="Untracked"/>.</summary>
    public static Path? Of(Path? mode) => mode is null ? null : Untracked;

    /// <summary>This path followed by <paramref name="next"/>; this path must be tracked.</summary>
    public Path Append(JqValue next) => new(this, next, length + 1);

    /// <summary>This path followed by the elements of <paramref name="keys"/>; this path must be tracked.</summary>
    public Path Append(JqArray keys)
    {
        var path = this;
        for (var i = 0; i < keys.Count; i++)
            path = path.Append(keys[i]);
        return path;
    }

    /// <summary>The keys as a jqlite array, first key first; this path must be tracked.</summary>
    public JqValue ToValue()
    {
        var keys = new JqValue[length];
        var path = this;
        for (var i = length - 1; i >= 0; i--)
        {
            keys[i] = path.key;
            path = path.parent!;
        }

        return JqValue.FromArray(JqArray.FromOwned(keys));
    }
}
