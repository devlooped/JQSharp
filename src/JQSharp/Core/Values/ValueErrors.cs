namespace Devlooped.Core.Values;

/// <summary>
/// Every error message of the value layer, in one place (step 6 folds them into the runtime's
/// message catalogue). A jqlite error is a value, usually the message string; the texts follow
/// jq 1.8.
/// </summary>
static class ValueErrors
{
    public const string Added = "added";
    public const string Subtracted = "subtracted";
    public const string Multiplied = "multiplied";
    public const string Divided = "divided";

    /// <summary><c>number (1) and string ("a") cannot be added</c>.</summary>
    public static JqValue CannotBe(JqValue a, JqValue b, string operation)
        => Message($"{Describe(a)} and {Describe(b)} cannot be {operation}");

    /// <summary><c>number (1) and number (0) cannot be divided because the divisor is zero</c> (for <c>/</c> and <c>%</c>).</summary>
    public static JqValue DivisorIsZero(JqValue a, JqValue b)
        => Message($"{Describe(a)} and {Describe(b)} cannot be divided because the divisor is zero");

    public static JqValue RepeatTooLong => Message("Repeat string result too long");

    public static JqValue CannotImplode(JqValue input)
        => Message($"{Describe(input)} cannot be imploded, must be an array of codepoints");

    public static JqValue CodepointNotNumeric(JqValue input)
        => Message($"{Describe(input)} can't be imploded, unicode codepoint needs to be numeric");

    public static JqValue InvalidCodepoint(JqValue input)
        => Message($"{Describe(input)} can't be imploded, unicode codepoint needs to be a valid code point");

    public static JqValue CannotIndex(JqValue input, string with)
        => Message($"Cannot index {input.TypeName} with {with}");

    public static JqValue SliceIndicesNotNumbers => Message("Start and end indices of an array slice must be numbers");

    // JSON reader (fromjson). The reader appends " at line L, column C".
    public const string ExpectedValue = "Expected JSON value";
    public const string UnfinishedAtEof = "Unfinished JSON term at EOF";
    public const string InvalidLiteral = "Invalid literal";
    public const string InvalidNumericLiteral = "Invalid numeric literal";
    public const string InvalidEscape = "Invalid escape";
    public const string InvalidUnicodeEscape = "Invalid \\uXXXX escape";
    public const string KeysMustBeStrings = "Object keys must be strings";
    public const string KeyValuePairs = "Objects must consist of key:value pairs";
    public const string ExpectedSeparator = "Expected separator between values";
    public const string ExpectedArrayElement = "Expected another array element";
    public const string ExtraValues = "Unexpected extra JSON values";
    public const string DepthLimit = "Exceeds depth limit for parsing";

    public static string ExpectedValueBefore(char c) => $"Expected value before '{c}'";

    public static string Unmatched(char c) => $"Unmatched '{c}'";

    public static JqValue AtPosition(string message, int line, int column)
        => Message($"{message} at line {line}, column {column}");

    /// <summary>jq's <c>kind (dump)</c>, the dump cut at 11 bytes of UTF-8 plus "..." when longer than 14.</summary>
    public static string Describe(JqValue value) => $"{value.TypeName} ({ValueText.ToJsonTruncated(value, 14)})";

    static JqValue Message(string text) => JqValue.FromString(text);
}
