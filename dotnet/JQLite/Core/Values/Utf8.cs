using System.Text;

namespace JQLite.Core.Values;

/// <summary>
/// UTF-8 encoding and decoding as plain loops over code points and bytes, for the formats that
/// work on bytes (§9.9: <c>@uri</c>, <c>@urid</c>, <c>@base64</c>, <c>@base64d</c>).
/// </summary>
static class Utf8
{
    /// <summary>Appends the UTF-8 bytes of a well-formed string.</summary>
    public static void Encode(string text, List<byte> bytes)
    {
        for (var i = 0; i < text.Length; i++)
        {
            int c = text[i];
            if (char.IsHighSurrogate(text[i]))
            {
                c = 0x10000 + ((c - 0xD800) << 10) + (text[i + 1] - 0xDC00);
                i++;
            }

            Encode(c, bytes);
        }
    }

    /// <summary>Appends the UTF-8 bytes of a Unicode scalar value.</summary>
    public static void Encode(int codePoint, List<byte> bytes)
    {
        if (codePoint < 0x80)
        {
            bytes.Add((byte)codePoint);
        }
        else if (codePoint < 0x800)
        {
            bytes.Add((byte)(0xC0 | (codePoint >> 6)));
            bytes.Add((byte)(0x80 | (codePoint & 0x3F)));
        }
        else if (codePoint < 0x10000)
        {
            bytes.Add((byte)(0xE0 | (codePoint >> 12)));
            bytes.Add((byte)(0x80 | ((codePoint >> 6) & 0x3F)));
            bytes.Add((byte)(0x80 | (codePoint & 0x3F)));
        }
        else
        {
            bytes.Add((byte)(0xF0 | (codePoint >> 18)));
            bytes.Add((byte)(0x80 | ((codePoint >> 12) & 0x3F)));
            bytes.Add((byte)(0x80 | ((codePoint >> 6) & 0x3F)));
            bytes.Add((byte)(0x80 | (codePoint & 0x3F)));
        }
    }

    /// <summary>The text of well-formed UTF-8 bytes; false when any sequence is ill-formed.</summary>
    public static bool TryDecode(List<byte> bytes, out string text)
    {
        var builder = new StringBuilder(bytes.Count);
        for (var i = 0; i < bytes.Count;)
        {
            i = Next(bytes, i, out var codePoint);
            if (codePoint < 0)
            {
                text = "";
                return false;
            }

            CodePoints.Append(builder, codePoint);
        }

        text = builder.ToString();
        return true;
    }

    /// <summary>The text of UTF-8 bytes, each ill-formed sequence (as <see cref="Next"/> delimits it) replaced by U+FFFD.</summary>
    public static string DecodeReplacing(List<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Count);
        for (var i = 0; i < bytes.Count;)
        {
            i = Next(bytes, i, out var codePoint);
            CodePoints.Append(builder, codePoint < 0 ? CodePoints.Replacement : codePoint);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Decodes the sequence at <paramref name="index"/> and returns the index after it;
    /// <paramref name="codePoint"/> is -1 when the sequence is ill-formed, and the index is then
    /// the end of its maximal ill-formed subpart (Unicode §3.9, §4.1 of the spec): the bytes up to
    /// the first one that cannot continue a well-formed sequence, at least one byte. The second
    /// byte's range depends on the first (E0: A0-BF, ED: 80-9F, F0: 90-BF, F4: 80-8F), which rules
    /// out overlong forms, surrogates and code points above U+10FFFF.
    /// </summary>
    static int Next(List<byte> bytes, int index, out int codePoint)
    {
        int first = bytes[index];
        codePoint = -1;
        if (first < 0x80)
        {
            codePoint = first;
            return index + 1;
        }

        var length = first is >= 0xC2 and <= 0xDF ? 2 : first is >= 0xE0 and <= 0xEF ? 3 : first is >= 0xF0 and <= 0xF4 ? 4 : 0;
        if (length == 0)
            return index + 1;

        var low = first == 0xE0 ? 0xA0 : first == 0xF0 ? 0x90 : 0x80;
        var high = first == 0xED ? 0x9F : first == 0xF4 ? 0x8F : 0xBF;
        var value = first & (0x7F >> length);
        for (var i = 1; i < length; i++)
        {
            if (index + i == bytes.Count)
                return index + i;
            int next = bytes[index + i];
            if (next < low || next > high)
                return index + i;
            value = (value << 6) | (next & 0x3F);
            (low, high) = (0x80, 0xBF);
        }

        codePoint = value;
        return index + length;
    }
}
