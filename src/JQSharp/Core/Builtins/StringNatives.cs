using System.Text;
using Devlooped.Core.Runtime;
using Devlooped.Core.Values;
using static Devlooped.Core.Builtins.NativeTable;

namespace Devlooped.Core.Builtins;

/// <summary>§9.6: text conversion and the string natives the prelude builds on.</summary>
static class StringNatives
{
    public static void Register()
    {
        Add("tostring", 0, static (_, input, _, out result, out error) => Ok(JqValue.FromString(ToText(input)), out result, out error));
        Add("tojson", 0, static (_, input, _, out result, out error) => Ok(JqValue.FromString(ValueText.ToJson(input)), out result, out error));
        Add("fromjson", 0, static (_, input, _, out result, out error) => input.Kind == JqKind.String
            ? ValueText.TryParse(input.AsString(), out result, out error)
            : Fail(Errors.OnlyStringsParse(input), out result, out error));
        Add("tonumber", 0, ToNumber);
        Add("toboolean", 0, ToBoolean);
        Add("format", 1, static (_, input, arguments, out result, out error) => arguments[0].Kind == JqKind.String
            ? Formats.TryApply(arguments[0].AsString(), input, out result, out error)
            : Fail(Errors.NotAValidFormat(arguments[0]), out result, out error));
        Add("explode", 0, static (_, input, _, out result, out error) => ValueOps.TryExplode(input, out result, out error));
        Add("implode", 0, static (_, input, _, out result, out error) => ValueOps.TryImplode(input, out result, out error));
        Add("ascii_downcase", 0, static (_, input, _, out result, out error) => AsciiCase(input, upper: false, out result, out error));
        Add("ascii_upcase", 0, static (_, input, _, out result, out error) => AsciiCase(input, upper: true, out result, out error));
        Add("startswith", 1, static (_, input, arguments, out result, out error) => BothStrings(input, arguments[0])
            ? Ok(JqValue.FromBool(input.AsString().StartsWith(arguments[0].AsString(), StringComparison.Ordinal)), out result, out error)
            : Fail(Errors.StartsWithStrings, out result, out error));
        Add("endswith", 1, static (_, input, arguments, out result, out error) => BothStrings(input, arguments[0])
            ? Ok(JqValue.FromBool(input.AsString().EndsWith(arguments[0].AsString(), StringComparison.Ordinal)), out result, out error)
            : Fail(Errors.EndsWithStrings, out result, out error));
        Add("trim", 0, static (_, input, _, out result, out error) => Trim(input, start: true, end: true, out result, out error));
        Add("ltrim", 0, static (_, input, _, out result, out error) => Trim(input, start: true, end: false, out result, out error));
        Add("rtrim", 0, static (_, input, _, out result, out error) => Trim(input, start: false, end: true, out result, out error));
        Add("split", 1, static (_, input, arguments, out result, out error) => BothStrings(input, arguments[0])
            ? Ok(JqValue.FromArray(ValueOps.Split(input.AsString(), arguments[0].AsString())), out result, out error)
            : Fail(Errors.SplitStrings, out result, out error));
        Add("_strindices", 1, StringIndices);
    }

    /// <summary><c>tostring</c>: a string as it is, any other value as its canonical text.</summary>
    public static string ToText(JqValue value) => value.Kind == JqKind.String ? value.AsString() : ValueText.ToJson(value);

    static bool ToNumber(Interpreter interpreter, JqValue input, JqValue[] arguments, out JqValue result, out JqValue error)
    {
        if (input.Kind == JqKind.Number)
            return Ok(input, out result, out error);
        if (input.Kind == JqKind.String && ValueText.TryParseNumber(input.AsString(), out var number))
            return Ok(JqValue.FromNumber(number), out result, out error);
        return Fail(Errors.CannotParseNumber(input), out result, out error);
    }

    static bool ToBoolean(Interpreter interpreter, JqValue input, JqValue[] arguments, out JqValue result, out JqValue error)
    {
        if (input.Kind is JqKind.True or JqKind.False)
            return Ok(input, out result, out error);
        if (input.Kind == JqKind.String && input.AsString() is "true" or "false")
            return Ok(JqValue.FromBool(input.AsString() == "true"), out result, out error);
        return Fail(Errors.CannotParseBoolean(input), out result, out error);
    }

    // trim, ltrim, rtrim: the Unicode White_Space code points, all of them single UTF-16 units.
    static bool Trim(JqValue input, bool start, bool end, out JqValue result, out JqValue error)
    {
        if (input.Kind != JqKind.String)
            return Fail(Errors.TrimNotString, out result, out error);
        var text = input.AsString();
        int from = 0, to = text.Length;
        while (start && from < to && IsWhiteSpace(text[from]))
            from++;
        while (end && to > from && IsWhiteSpace(text[to - 1]))
            to--;
        return Ok(from == 0 && to == text.Length ? input : JqValue.FromString(text[from..to]), out result, out error);
    }

    static bool IsWhiteSpace(char c)
        => c is (>= '\u0009' and <= '\u000D') or '\u0020' or '\u0085' or '\u00A0' or '\u1680' or (>= '\u2000' and <= '\u200A')
            or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000';

    static bool AsciiCase(JqValue input, bool upper, out JqValue result, out JqValue error)
    {
        if (input.Kind != JqKind.String)
            return Fail(Errors.ExplodeNotString, out result, out error);
        var text = input.AsString();
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
            builder.Append(upper ? (c is >= 'a' and <= 'z' ? (char)(c - 32) : c) : (c is >= 'A' and <= 'Z' ? (char)(c + 32) : c));
        return Ok(JqValue.FromString(builder.ToString()), out result, out error);
    }

    // _strindices(needle): the code point offsets of every occurrence, overlapping ones included.
    static bool StringIndices(Interpreter interpreter, JqValue input, JqValue[] arguments, out JqValue result, out JqValue error)
    {
        if (input.Kind != JqKind.String)
            return Fail(Errors.CannotSearchNotString(input), out result, out error);
        if (arguments[0].Kind != JqKind.String)
            return Fail(Errors.NotAString(arguments[0]), out result, out error);
        var text = input.AsString();
        var needle = arguments[0].AsString();
        var offsets = new List<JqValue>();
        for (int i = 0, codePoint = 0; needle.Length > 0 && i + needle.Length <= text.Length; codePoint++)
        {
            if (string.CompareOrdinal(text, i, needle, 0, needle.Length) == 0)
                offsets.Add(JqValue.FromNumber(codePoint));
            i += char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
        }

        return Ok(JqValue.FromArray(JqArray.FromOwned([.. offsets])), out result, out error);
    }

    static bool BothStrings(JqValue a, JqValue b) => a.Kind == JqKind.String && b.Kind == JqKind.String;
}
