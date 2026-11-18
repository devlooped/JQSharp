namespace Devlooped.Core.Values;

/// <summary>
/// The message catalogue of the runtime errors of spec §10.3 and of the builtins of §9: every text
/// an evaluation can raise, in one place. It lives in the value layer, the lowest one, because the
/// value operations raise most of these errors; the interpreter and the natives use it too. A
/// jqlite error is a value, usually the message string. (Syntax and binding errors, §10.2, are
/// SyntaxMessages and BindingMessages: they are reported before evaluation, with a position.)
/// </summary>
static class Errors
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

    // The interpreter (§10.3).
    public static JqValue CannotIterate(JqValue value) => Message($"Cannot iterate over {Describe(value)}");

    public static JqValue CannotUseAsKey(JqValue key) => Message($"Cannot use {Describe(key)} as object key");

    public static JqValue VariableNotDefined(string name) => Message($"${name} is not defined");

    public static JqValue InvalidFormat(string name) => Message($"{name} is not a valid format");

    public static JqValue DepthExceeded => Message("Maximum call depth exceeded");

    /// <summary>A native of the catalogue that this build does not implement yet.</summary>
    public static JqValue NotImplemented(string name, int arity) => Message($"{name}/{arity} is not implemented");

    // Paths (§8).
    public static JqValue InvalidPathResult(JqValue value) => Message($"Invalid path expression with result {Dump(value)}");

    public static JqValue InvalidPathAccess(JqValue key, JqValue target)
        => Message($"Invalid path expression near attempt to access element {Dump(key)} of {Dump(target)}");

    public static JqValue InvalidPathIterate(JqValue target) => Message($"Invalid path expression near attempt to iterate through {Dump(target)}");

    public static JqValue PathNotArray => Message("Path must be specified as an array");

    public static JqValue PathsNotArray => Message("Paths must be specified as an array");

    public static JqValue PathElementNotArray(JqValue path) => Message($"Path must be specified as array, not {path.TypeName}");

    public static JqValue NegativeIndex => Message("Out of bounds negative array index");

    public static JqValue IndexTooLarge => Message("Array index too large");

    public static JqValue NaNIndex => Message("Cannot set array element at NaN index");

    public static JqValue SliceNeedsArray => Message("A slice of an array can only be assigned another array");

    public static JqValue CannotUpdateStringSlices => Message("Cannot update string slices");

    public static JqValue CannotUpdateField(JqValue key, JqValue container) => Message($"Cannot update field at {key.TypeName} index of {container.TypeName}");

    public static JqValue CannotDeleteElement(JqValue key) => Message($"Cannot delete {key.TypeName} element of array");

    public static JqValue CannotDeleteField(JqValue key) => Message($"Cannot delete {key.TypeName} field of object");

    public static JqValue CannotDeleteFrom(JqValue container) => Message($"Cannot delete fields from {container.TypeName}");

    // Builtins (§9), by section.
    public static JqValue HasNoLength(JqValue value) => Message($"{Describe(value)} has no length");

    public static JqValue OnlyStringsHaveUtf8Length(JqValue value) => Message($"{Describe(value)} only strings have UTF-8 byte length");

    public static JqValue HaltErrorNumberRequired(JqValue input) => Message($"{Describe(input)} halt_error/1: number required");

    public static JqValue HasNoKeys(JqValue value) => Message($"{Describe(value)} has no keys");

    public static JqValue CannotCheckHas(JqValue input, JqValue key) => Message($"Cannot check whether {input.TypeName} has a {key.TypeName} key");

    public static JqValue RangeNotNumeric => Message("Range bounds must be numeric");

    public static JqValue NegativeLimit => Message("limit doesn't support negative count");

    public static JqValue NegativeSkip => Message("skip doesn't support negative count");

    public static JqValue CannotBeSorted(JqValue value) => Message($"{Describe(value)} cannot be sorted, as it is not an array");

    public static JqValue CannotBeSortedBy(JqValue a, JqValue b) => Message($"{Describe(a)} and {Describe(b)} cannot be sorted, as they are not both arrays");

    public static JqValue CannotBeIterated(JqValue a, JqValue b) => Message($"{Describe(a)} and {Describe(b)} cannot be iterated over");

    public static JqValue CannotBeSearched(JqValue value) => Message($"{Describe(value)} cannot be searched from");

    public static JqValue OnlyStringsParse(JqValue value) => Message($"{Describe(value)} only strings can be parsed");

    public static JqValue CannotParseNumber(JqValue value) => Message($"{Describe(value)} cannot be parsed as a number");

    public static JqValue StartsWithStrings => Message("startswith() requires string inputs");

    public static JqValue EndsWithStrings => Message("endswith() requires string inputs");

    public static JqValue SplitStrings => Message("split input and separator must be strings");

    public static JqValue CannotSearchNotString(JqValue value) => Message($"{Describe(value)} cannot be searched, as it is not a string");

    public static JqValue NotAString(JqValue value) => Message($"{Describe(value)} is not a string");

    /// <summary><c>T(v) (D(v))</c>, as in <c>object ({"a":1})</c>.</summary>
    public static string Describe(JqValue value) => $"{value.TypeName} ({Dump(value)})";

    /// <summary>
    /// The error dump D(v) (§10.1): the canonical text; if longer than 29 UTF-8 bytes, its longest
    /// prefix of at most 25 bytes ending on a code point boundary, then "...".
    /// </summary>
    public static string Dump(JqValue value) => ValueText.ToJsonTruncated(value, DumpLimit, DumpKeep);

    static JqValue Message(string text) => JqValue.FromString(text);
}