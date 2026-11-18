using System.Text;

namespace JQLite.Core.Values;

/// <summary>
/// Strings by Unicode code point. jqlite strings are sequences of code points: length, slices,
/// ordering, explode and implode count code points, never UTF-16 units. A .NET string holds them
/// as UTF-16, so a code point above U+FFFF is a surrogate pair here; every string of a
/// <see cref="JqValue"/> is well-formed (no lone surrogate, see <see cref="Sanitize"/>).
/// </summary>
static class CodePoints
{
    public const int Max = 0x10FFFF;
    public const char Replacement = (char)0xFFFD;

    /// <summary>Replaces each lone surrogate with U+FFFD; returns <paramref name="text"/> itself when it has none.</summary>
    public static string Sanitize(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsSurrogate(c))
                continue;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }

            return Replace(text, i);
        }

        return text;
    }

    static string Replace(string text, int from)
    {
        var builder = new StringBuilder(text.Length);
        builder.Append(text, 0, from);
        for (var i = from; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(c).Append(text[i + 1]);
                i++;
            }
            else
            {
                builder.Append(char.IsSurrogate(c) ? Replacement : c);
            }
        }

        return builder.ToString();
    }

    /// <summary>The number of code points of a well-formed string.</summary>
    public static int Length(string text)
    {
        var length = text.Length;
        foreach (var c in text)
        {
            if (char.IsHighSurrogate(c))
                length--;
        }

        return length;
    }

    /// <summary>The code points <paramref name="start"/> (included) to <paramref name="end"/> (excluded); 0 &lt;= start &lt;= end &lt;= length.</summary>
    public static string Substring(string text, int start, int end)
    {
        var from = Advance(text, 0, start);
        var to = Advance(text, from, end - start);
        return text.Substring(from, to - from);
    }

    static int Advance(string text, int offset, int codePoints)
    {
        for (var i = 0; i < codePoints; i++)
            offset += char.IsHighSurrogate(text[offset]) ? 2 : 1;
        return offset;
    }

    /// <summary>Orders two well-formed strings by code point (the order of their UTF-8 bytes); returns -1, 0 or 1.</summary>
    public static int Compare(string a, string b)
    {
        var length = Math.Min(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x != y)
                return Rank(x) < Rank(y) ? -1 : 1;
        }

        return a.Length == b.Length ? 0 : a.Length < b.Length ? -1 : 1;
    }

    // UTF-16 unit order differs from code point order in one place: a surrogate (D800-DFFF, part of
    // a code point above U+FFFF) must sort after E000-FFFF. Moving surrogates to the top fixes it
    // without decoding: at the first unit that differs, both strings are at a code point boundary.
    static int Rank(char c) => c >= 0xE000 ? c - 0x800 : c >= 0xD800 ? c + 0x2000 : c;

    /// <summary>The code points of a well-formed string, as numbers.</summary>
    public static JqArray Explode(string text)
    {
        var items = new JqValue[Length(text)];
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            int c = text[i];
            if (char.IsHighSurrogate(text[i]))
            {
                c = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }

            items[count++] = JqValue.FromNumber(c);
        }

        return JqArray.FromOwned(items);
    }

    /// <summary>Whether <paramref name="codePoint"/> is a Unicode scalar value (a code point that is not a surrogate).</summary>
    public static bool IsScalar(int codePoint) => codePoint is >= 0 and <= Max and (< 0xD800 or > 0xDFFF);

    /// <summary>Appends a Unicode scalar value.</summary>
    public static void Append(StringBuilder builder, int codePoint)
    {
        if (codePoint < 0x10000)
        {
            builder.Append((char)codePoint);
            return;
        }

        codePoint -= 0x10000;
        builder.Append((char)(0xD800 + (codePoint >> 10)));
        builder.Append((char)(0xDC00 + (codePoint & 0x3FF)));
    }

    /// <summary>The number of UTF-8 bytes of the UTF-16 unit at <paramref name="index"/> and of its pair, if any.</summary>
    public static int Utf8Length(string text, int index, out int units)
    {
        var c = text[index];
        units = char.IsHighSurrogate(c) && index + 1 < text.Length ? 2 : 1;
        return units == 2 ? 4 : c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
    }
}
