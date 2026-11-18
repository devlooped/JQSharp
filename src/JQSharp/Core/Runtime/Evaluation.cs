using Devlooped.Core.Values;

namespace Devlooped.Core.Runtime;

/// <summary>The host's clock and time zone (§12): <c>now</c>, and the local zone of <c>localtime</c>, <c>strflocaltime</c>, <c>%z</c>, <c>%Z</c>.</summary>
interface IClock
{
    /// <summary>The current time, in seconds since the Unix epoch.</summary>
    double Now();

    /// <summary>The offset of the local zone from UTC at the instant <paramref name="seconds"/>, in seconds east of UTC.</summary>
    int Offset(double seconds);

    /// <summary>The abbreviation of the local zone at the instant <paramref name="seconds"/>, or null.</summary>
    string? Abbreviation(double seconds);
}

/// <summary>What an evaluation takes from its host besides the program and the input (§12).</summary>
sealed class EvaluationOptions
{
    /// <summary>The host variables, by name without '$'; names are validated by the host boundary.</summary>
    public IReadOnlyDictionary<string, JqValue>? Variables { get; init; }

    /// <summary>The object <c>$ENV</c> is: string to string, in the order the host gives.</summary>
    public JqValue Environment { get; init; } = JqValue.FromObject(JqObject.Empty);

    /// <summary>The call depth limit of §11.</summary>
    public int MaxDepth { get; init; } = 1000;

    /// <summary>The clock and zone, for the date builtins.</summary>
    public IClock? Clock { get; init; }
}

enum OutcomeKind
{
    /// <summary>Every output was produced.</summary>
    Completed,

    /// <summary>The host stopped the stream.</summary>
    Stopped,

    /// <summary>An error reached the top: <see cref="Outcome.Value"/>.</summary>
    Error,

    /// <summary><c>halt</c> or <c>halt_error</c>: <see cref="Outcome.Code"/>, and <see cref="Outcome.Value"/> when <see cref="Outcome.HasValue"/>.</summary>
    Halt,
}

/// <summary>How an evaluation ended (§12).</summary>
readonly record struct Outcome(OutcomeKind Kind, JqValue Value = default, bool HasValue = false, int Code = 0);
