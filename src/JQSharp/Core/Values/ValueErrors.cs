namespace Devlooped.Core.Values;

/// <summary>
/// Every error message of the value layer, in one place (step 6 folds them into the runtime's
/// message catalogue). A jqlite error is a value, usually the message string; the texts are
/// those of spec/jqlite.md §10.
/// </summary>
static class ValueErrors
{
    public const string Added = "added";
    public const string Subtracted = "subtracted";
    public const string Multiplied = "multiplied";
    public const string Divided = "divided";
    public const string Remainder = "divided (remainder)";

    // The error dump D(v): the canonical text, cut when longer than DumpLimit UTF-8 bytes (§10.1).
    const int DumpLimit = 29;
    const int DumpKeep = 25;

    /// <summary><c>number (1) and string ("a") cannot be added</c>; <paramref name="operation"/> is one of the constants above.</summary>
    public static JqValue CannotBe(JqValue a, JqValue b, string operation)
        => Message($"{Describe(a)} and {Describe(b)} cannot be {operation}");

    /// <summary><c>... cannot be divided because the divisor is zero</c> (<paramref name="operation"/> <see cref="Divided"/>) or <c>... cannot be divided (remainder) because the divisor is zero</c> (<see cref="Remainder"/>).</summary>
    public static JqValue DivisorIsZero(JqValue a, JqValue b, string operation)
        => Message($"{Describe(a)} and {Describe(b)} cannot be {operation} because the divisor is zero");

    public static JqValue CannotNegate(JqValue value) => Message($"{Describe(value)} cannot be negated");

    public static JqValue RepeatTooLong => Message("Repeat string result too long");

    public static JqValue ImplodeNotArray => Message("implode input must be an array");

    /// <summary>A code point that is not a number (or is NaN), described by its own dump.</summary>
    public static JqValue CodepointNotNumeric(JqValue codepoint)
        => Message($"{Describe(codepoint)} can't be imploded, unicode codepoint needs to be numeric");

    public static JqValue ExplodeNotString => Message("explode input must be a string");

    /// <summary><c>Cannot index number with object ({"start":1,"end":2})</c>: the dump is the key's.</summary>
    public static JqValue CannotIndex(JqValue input, JqValue key)
        => Message($"Cannot index {input.TypeName} with {Describe(key)}");

    public static JqValue SliceIndicesNotIntegers => Message("Array/string slice indices must be integers");

    // JSON reader reasons (implementation-defined text, §5.3); the reader adds the position and
    // WhileParsing wraps them.
    public const string ExpectedValue = "Expected JSON value";
    public const string ExtraValues = "Unexpected extra JSON values";
    public const string UnfinishedAtEof = "Unfinished JSON term at EOF";
    public const string InvalidLiteral = "Invalid literal";
    public const string InvalidNumericLiteral = "Invalid numeric literal";
    public const string InvalidEscape = "Invalid escape";
    public const string InvalidUnicodeEscape = "Invalid \\uXXXX escape";
    public const string ControlCharacter = "Invalid string: control characters from U+0000 through U+001F must be escaped";
    public const string KeysMustBeStrings = "Object keys must be strings";
    public const string KeyValuePairs = "Objects must consist of key:value pairs";
    public const string ExpectedSeparator = "Expected separator between values";
    public const string ExpectedArrayElement = "Expected another array element";
    public const string DepthLimit = "Exceeds depth limit for parsing";

    public static string ExpectedValueBefore(char c) => $"Expected value before '{c}'";

    public static string Unmatched(char c) => $"Unmatched '{c}'";

    public static string AtPosition(string reason, int line, int column) => $"{reason} at line {line}, column {column}";

    /// <summary>A reader error: <c>&lt;reason&gt; (while parsing '&lt;text&gt;')</c>, <paramref name="text"/> the whole input (§5.3).</summary>
    public static JqValue WhileParsing(string reason, string text) => Message($"{reason} (while parsing '{text}')");

    /// <summary><c>T(v) (D(v))</c>, as in <c>object ({"a":1})</c>.</summary>
    public static string Describe(JqValue value) => $"{value.TypeName} ({Dump(value)})";

    /// <summary>
    /// The error dump D(v) (§10.1): the canonical text; if longer than 29 UTF-8 bytes, its longest
    /// prefix of at most 25 bytes ending on a code point boundary, then "...".
    /// </summary>
    public static string Dump(JqValue value) => ValueText.ToJsonTruncated(value, DumpLimit, DumpKeep);

    static JqValue Message(string text) => JqValue.FromString(text);
}
