namespace Devlooped;

/// <summary>What an expression takes from its host besides the input and the variables: modules, <c>$ENV</c>, the clock and the depth limit.</summary>
/// <remarks>Options are read when the expression is parsed (<see cref="Jq.Parse"/>) and apply to every evaluation of it.</remarks>
public sealed class JqOptions
{
    /// <summary>Resolves <c>import</c> and <c>include</c> directives and <c>modulemeta</c>; without one, no module exists.</summary>
    public JqResolver? Resolver { get; init; }

    /// <summary>
    /// The variables of <c>$ENV</c> and <c>env</c>, in this order. When <see langword="null"/> (the default),
    /// the process environment, read when an evaluation first uses <c>$ENV</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>The source of <c>now</c> and of the local time zone (<c>localtime</c>, <c>strflocaltime</c>, <c>%z</c>, <c>%Z</c>).</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// The maximum number of calls of jq-defined functions active at once (spec §11); beyond it, the
    /// catchable error <c>Maximum call depth exceeded</c>. Deeper recursion never exhausts the host stack.
    /// </summary>
    public int MaxDepth { get; init; } = 1000;
}
