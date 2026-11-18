using Devlooped.Core.Values;

namespace Devlooped.Core.Syntax;

/// <summary>
/// The one error a lexing, parsing or binding run returns: a message and the 1-based position of the
/// token where it was detected, in the main program or, when <see cref="Module"/> is set, in the
/// module of that identity (§9.11).
/// </summary>
sealed record SyntaxError(string Message, int Line, int Column, string? Module = null);

/// <summary>
/// Every message text of the syntax layer, in one place. The texts of spec §10.2 are fixed; the
/// others are this implementation's.
/// </summary>
static class SyntaxMessages
{
    // Fixed by spec §10.2.
    public const string InvalidEscape = "Invalid escape";
    public const string MayNeedParentheses = "May need parentheses around object key expression";
    public const string MetadataNotConstant = "Module metadata must be constant";
    public const string MetadataNotObject = "Module metadata must be an object";
    public const string ImportPathNotConstant = "Import path must be constant";

    /// <summary><c>Cannot use number (1) as object key</c>.</summary>
    public static string InvalidObjectKey(JqValue key) => Errors.CannotUseAsKey(key).AsString();

    // Fixed by spec §11.
    public const string TooDeeplyNested = "Program too deeply nested";

    // This implementation's.
    public const string UnterminatedString = "Unterminated string";
    public const string VariableNameExpected = "Expected a variable name after '$'";
    public const string FormatNameExpected = "Expected a format name after '@'";
    public const string ModuleHasBody = "A module contains only definitions";

    public static string InvalidCharacter(int codePoint)
        => codePoint is < 0x20 or 0x7F or (>= 0xD800 and <= 0xDFFF)
            ? $"Invalid character U+{codePoint:X4}"
            : $"Invalid character '{char.ConvertFromUtf32(codePoint)}'";

    public static string Unexpected(Token token) => $"Unexpected {token.Describe()}";

    public static string Expected(string what, Token token) => $"Expected {what} but found {token.Describe()}";

    public static string NotAssociative(Token token) => $"Operator {token.Describe()} cannot be chained: add parentheses";

    public static string QuestionAlt(Token token) => $"Unexpected {token.Describe()}: write '? //' for an optional followed by an alternative";
}