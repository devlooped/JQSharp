using JQLite.Core.Values;

namespace JQLite.Core.Runtime;

enum SignalKind
{
    /// <summary>A jqlite error (§6.2); <see cref="Signal.Value"/> is the error value. Caught by <c>try</c>, <c>?</c> and <c>?//</c>.</summary>
    Error,

    /// <summary><c>break</c> to the label instance <see cref="Signal.Label"/>; handled by that label only.</summary>
    Break,

    /// <summary><c>halt</c> or <c>halt_error</c>: stops the whole evaluation; handled by the host only.</summary>
    Halt,

    /// <summary>The host stopped the stream of outputs (not an error); handled by the host only.</summary>
    Stop,
}

/// <summary>
/// Why evaluation stops besides reaching its end: evaluation returns null to go on, or one of
/// these, which every construct passes up unchanged except the one that handles it (§6.2).
/// </summary>
/// <remarks>
/// A signal is an object, and its identity matters: an error-handling construct recognizes the
/// signals that came back from its consumer by identity (see Interpreter.Consumer), so a construct
/// that passes a signal up must pass the same object. A port without object identity gives each
/// signal a serial number.
/// </remarks>
sealed class Signal
{
    /// <summary>The one <see cref="SignalKind.Stop"/> signal.</summary>
    public static readonly Signal Stop = new(SignalKind.Stop, default, false, 0, 0);

    Signal(SignalKind kind, JqValue value, bool hasValue, long label, int code)
    {
        Kind = kind;
        Value = value;
        HasValue = hasValue;
        Label = label;
        Code = code;
    }

    public SignalKind Kind { get; }

    /// <summary>The error value, or the halt value when <see cref="HasValue"/>.</summary>
    public JqValue Value { get; }

    /// <summary>For <see cref="SignalKind.Halt"/>: whether a value goes with the exit code (<c>halt_error</c>).</summary>
    public bool HasValue { get; }

    /// <summary>For <see cref="SignalKind.Break"/>: the label instance.</summary>
    public long Label { get; }

    /// <summary>For <see cref="SignalKind.Halt"/>: the exit code.</summary>
    public int Code { get; }

    public static Signal Error(JqValue value) => new(SignalKind.Error, value, true, 0, 0);

    public static Signal Break(long label) => new(SignalKind.Break, default, false, label, 0);

    public static Signal Halt(JqValue? value, int code) => new(SignalKind.Halt, value ?? default, value is not null, 0, code);
}
