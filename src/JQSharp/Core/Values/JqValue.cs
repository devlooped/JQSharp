namespace Devlooped.Core.Values;

/// <summary>The kinds of jqlite values, declared in their sort order.</summary>
enum JqKind : byte
{
    Null,
    False,
    True,
    Number,
    String,
    Array,
    Object,
}

/// <summary>
/// An immutable jqlite value: null, false, true, a number (IEEE-754 double, NaN and
/// ±Infinity included), a string, an array or an object.
/// </summary>
/// <remarks>
/// Layout, a local .NET trick to keep the value at 16 bytes: <c>payload</c> is <see langword="null"/>
/// for null, one of three <see cref="Tag"/> singletons for false, true and numbers, or else the
/// <see cref="string"/>, <see cref="JqArray"/> or <see cref="JqObject"/> itself. A port uses its
/// natural tagged union (a Rust enum, a Go struct with a kind, a TypeScript union) instead.
/// </remarks>
readonly struct JqValue
{
    sealed class Tag(JqKind kind)
    {
        public readonly JqKind Kind = kind;
    }

    static readonly Tag FalseTag = new(JqKind.False);
    static readonly Tag TrueTag = new(JqKind.True);
    static readonly Tag NumberTag = new(JqKind.Number);

    public static readonly JqValue Null = default;
    public static readonly JqValue False = new(FalseTag, 0);
    public static readonly JqValue True = new(TrueTag, 0);

    readonly object? payload;
    readonly double number;

    JqValue(object? payload, double number)
    {
        this.payload = payload;
        this.number = number;
    }

    public static JqValue FromBool(bool value) => value ? True : False;

    public static JqValue FromNumber(double value) => new(NumberTag, value);

    /// <summary>Creates a string value; a lone surrogate becomes U+FFFD.</summary>
    public static JqValue FromString(string value) => new(CodePoints.Sanitize(value), 0);

    public static JqValue FromArray(JqArray value) => new(value, 0);

    public static JqValue FromObject(JqObject value) => new(value, 0);

    public JqKind Kind => payload switch
    {
        null => JqKind.Null,
        string => JqKind.String,
        JqArray => JqKind.Array,
        JqObject => JqKind.Object,
        _ => ((Tag)payload).Kind,
    };

    /// <summary>False for null and false, true for every other value.</summary>
    public bool IsTruthy => payload is not null && !ReferenceEquals(payload, FalseTag);

    /// <summary>The name jq's <c>type</c> gives the kind: false and true are both "boolean".</summary>
    public string TypeName => Kind switch
    {
        JqKind.Null => "null",
        JqKind.False or JqKind.True => "boolean",
        JqKind.Number => "number",
        JqKind.String => "string",
        JqKind.Array => "array",
        _ => "object",
    };

    // The As* accessors throw only on a programming error (asking a value for the wrong kind),
    // never as part of jqlite semantics: callers check Kind first.
    public double AsNumber() => ReferenceEquals(payload, NumberTag) ? number : throw WrongKind(JqKind.Number);

    public string AsString() => payload as string ?? throw WrongKind(JqKind.String);

    public JqArray AsArray() => payload as JqArray ?? throw WrongKind(JqKind.Array);

    public JqObject AsObject() => payload as JqObject ?? throw WrongKind(JqKind.Object);

    /// <summary>The canonical JSON text of the value, for diagnostics and tests.</summary>
    public override string ToString() => ValueText.ToJson(this);

    InvalidOperationException WrongKind(JqKind expected) => new($"A {Kind} value is not a {expected}.");
}
