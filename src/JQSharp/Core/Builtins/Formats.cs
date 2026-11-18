using System.Text;
using Devlooped.Core.Values;
using static Devlooped.Core.Builtins.NativeTable;

namespace Devlooped.Core.Builtins;

/// <summary>The <c>@name</c> formats of §9.9.</summary>
static class Formats
{
    const string Hex = "0123456789ABCDEF";
    const string Base64Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    /// <summary>Applies the format <paramref name="name"/> (without '@') to <paramref name="value"/>, giving a string.</summary>
    public static bool TryApply(string name, JqValue value, out JqValue result, out JqValue error)
    {
        switch (name)
        {
            case "text":
                return Ok(JqValue.FromString(StringNatives.ToText(value)), out result, out error);
            case "json":
                return Ok(JqValue.FromString(ValueText.ToJson(value)), out result, out error);
            case "html":
                return Ok(JqValue.FromString(Html(StringNatives.ToText(value))), out result, out error);
            case "uri":
                return Ok(JqValue.FromString(Uri(StringNatives.ToText(value))), out result, out error);
            case "urid":
                return UriDecode(StringNatives.ToText(value), out result, out error);
            case "csv":
                return value.Kind == JqKind.Array
                    ? Row(value.AsArray(), tsv: false, out result, out error)
                    : Fail(Errors.CannotBeCsvFormatted(value), out result, out error);
            case "tsv":
                return value.Kind == JqKind.Array
                    ? Row(value.AsArray(), tsv: true, out result, out error)
                    : Fail(Errors.CannotBeTsvFormatted(value), out result, out error);
            case "sh":
                return Shell(value.Kind == JqKind.Array ? value.AsArray() : JqArray.Of(value), out result, out error);
            case "base64":
                return Ok(JqValue.FromString(Base64(StringNatives.ToText(value))), out result, out error);
            case "base64d":
                return Base64Decode(StringNatives.ToText(value), out result, out error);
            default:
                return Fail(Errors.InvalidFormat(name), out result, out error);
        }
    }

    // @html: & < > ' " as entities.
    static string Html(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '\'': builder.Append("&apos;"); break;
                case '"': builder.Append("&quot;"); break;
                default: builder.Append(c); break;
            }
        }

        return builder.ToString();
    }

    // @uri: the UTF-8 bytes, unreserved ones (A-Z a-z 0-9 - _ . ~) kept, the others as %XX.
    static string Uri(string text)
    {
        var bytes = new List<byte>(text.Length);
        Utf8.Encode(text, bytes);
        var builder = new StringBuilder(bytes.Count);
        foreach (var b in bytes)
        {
            if (b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~')
                builder.Append((char)b);
            else
                builder.Append('%').Append(Hex[b >> 4]).Append(Hex[b & 0xF]);
        }

        return builder.ToString();
    }

    // @urid: %XX (either case) is that byte, any other code point its UTF-8 bytes; the bytes must be well-formed UTF-8.
    static bool UriDecode(string text, out JqValue result, out JqValue error)
    {
        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            int c = text[i];
            if (c == '%')
            {
                var high = i + 1 < text.Length ? HexValue(text[i + 1]) : -1;
                var low = i + 2 < text.Length ? HexValue(text[i + 2]) : -1;
                if (high < 0 || low < 0)
                    return Fail(Errors.NotValidUriEncoding(JqValue.FromString(text)), out result, out error);
                bytes.Add((byte)((high << 4) | low));
                i += 2;
                continue;
            }

            if (char.IsHighSurrogate(text[i]))
            {
                c = 0x10000 + ((c - 0xD800) << 10) + (text[i + 1] - 0xDC00);
                i++;
            }

            Utf8.Encode(c, bytes);
        }

        return Utf8.TryDecode(bytes, out var decoded)
            ? Ok(JqValue.FromString(decoded), out result, out error)
            : Fail(Errors.NotValidUriEncoding(JqValue.FromString(text)), out result, out error);
    }

    static int HexValue(char c) => c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;

    // @csv and @tsv: one row; strings quoted (csv) or escaped (tsv), numbers as canonical text
    // (NaN empty), booleans as such, null empty.
    static bool Row(JqArray items, bool tsv, out JqValue result, out JqValue error)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
                builder.Append(tsv ? '\t' : ',');
            var item = items[i];
            switch (item.Kind)
            {
                case JqKind.Null:
                    break;
                case JqKind.False or JqKind.True:
                    builder.Append(item.Kind == JqKind.True ? "true" : "false");
                    break;
                case JqKind.Number:
                    if (!double.IsNaN(item.AsNumber()))
                        builder.Append(ValueText.FormatNumber(item.AsNumber()));
                    break;
                case JqKind.String when tsv:
                    TsvEscape(builder, item.AsString());
                    break;
                case JqKind.String:
                    builder.Append('"').Append(item.AsString().Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
                    break;
                default:
                    return Fail(Errors.NotValidInCsvRow(item), out result, out error);
            }
        }

        return Ok(JqValue.FromString(builder.ToString()), out result, out error);
    }

    static void TsvEscape(StringBuilder builder, string text)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '\t': builder.Append("\\t"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                default: builder.Append(c); break;
            }
        }
    }

    // @sh: space-separated words; strings in single quotes (' as '\''), other scalars as canonical text.
    static bool Shell(JqArray items, out JqValue result, out JqValue error)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
                builder.Append(' ');
            var item = items[i];
            switch (item.Kind)
            {
                case JqKind.Array or JqKind.Object:
                    return Fail(Errors.CannotBeEscapedForShell(item), out result, out error);
                case JqKind.String:
                    builder.Append('\'').Append(item.AsString().Replace("'", "'\\''", StringComparison.Ordinal)).Append('\'');
                    break;
                default:
                    ValueText.Write(builder, item);
                    break;
            }
        }

        return Ok(JqValue.FromString(builder.ToString()), out result, out error);
    }

    // @base64: RFC 4648 of the UTF-8 bytes, with '=' padding.
    static string Base64(string text)
    {
        var bytes = new List<byte>(text.Length);
        Utf8.Encode(text, bytes);
        var builder = new StringBuilder((bytes.Count + 2) / 3 * 4);
        for (var i = 0; i < bytes.Count; i += 3)
        {
            var left = bytes.Count - i;
            var group = bytes[i] << 16 | (left > 1 ? bytes[i + 1] << 8 : 0) | (left > 2 ? bytes[i + 2] : 0);
            builder.Append(Base64Alphabet[group >> 18]);
            builder.Append(Base64Alphabet[(group >> 12) & 0x3F]);
            builder.Append(left > 1 ? Base64Alphabet[(group >> 6) & 0x3F] : '=');
            builder.Append(left > 2 ? Base64Alphabet[group & 0x3F] : '=');
        }

        return builder.ToString();
    }

    // @base64d: the characters before the first '=' must be in the alphabet; a final group of 2
    // or 3 characters gives 1 or 2 bytes, of 1 is an error; the bytes are decoded as UTF-8 with
    // replacement.
    static bool Base64Decode(string text, out JqValue result, out JqValue error)
    {
        var bytes = new List<byte>(text.Length * 3 / 4);
        int group = 0, count = 0;
        foreach (var c in text)
        {
            if (c == '=')
                break;
            var digit = Base64Digit(c);
            if (digit < 0)
                return Fail(Errors.NotValidBase64(JqValue.FromString(text)), out result, out error);
            group = group << 6 | digit;
            if (++count == 4)
            {
                bytes.Add((byte)(group >> 16));
                bytes.Add((byte)(group >> 8));
                bytes.Add((byte)group);
                group = count = 0;
            }
        }

        if (count == 1)
            return Fail(Errors.TrailingBase64Byte(JqValue.FromString(text)), out result, out error);
        if (count == 2)
        {
            bytes.Add((byte)(group >> 4));
        }
        else if (count == 3)
        {
            bytes.Add((byte)(group >> 10));
            bytes.Add((byte)(group >> 2));
        }

        return Ok(JqValue.FromString(Utf8.DecodeReplacing(bytes)), out result, out error);
    }

    static int Base64Digit(char c)
        => c is >= 'A' and <= 'Z' ? c - 'A' : c is >= 'a' and <= 'z' ? c - 'a' + 26 : c is >= '0' and <= '9' ? c - '0' + 52 : c == '+' ? 62 : c == '/' ? 63 : -1;
}
