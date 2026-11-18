using System.Globalization;
using System.Text;

namespace Devlooped.Core.Values;

/// <summary>
/// The one JSON writer, number formatter and JSON reader of jqlite.
/// </summary>
/// <remarks>
/// Canonical text is compact JSON. Strings escape only <c>"</c>, <c>\</c>, the control characters
/// below U+0020 (<c>\b \f \n \r \t</c>, the others <c>\u00xx</c>) and U+007F, as jq does. Numbers
/// are the shortest digits that read back to the same double, laid out as ECMAScript's
/// <c>Number::toString</c>; NaN is <c>null</c> and ±Infinity are ±<c>1.7976931348623157e+308</c>.
/// </remarks>
static class ValueText
{
    const string MaxDouble = "1.7976931348623157e+308";

    /// <summary>The deepest level the writer writes (§5.1, §11).</summary>
    public const int MaxWriteDepth = 10001;

    const string TooDeep = "<skipped: too deep>";

    public static string ToJson(JqValue value)
    {
        var builder = new StringBuilder();
        Write(builder, value);
        return builder.ToString();
    }

    /// <summary>
    /// Writes the canonical text of <paramref name="value"/>. No recursion: the containers being
    /// written are on an explicit stack, so nesting depth is not limited by the host stack. A value
    /// nested deeper than <see cref="MaxWriteDepth"/> levels (the outermost value is level 1) is
    /// written as <c>&lt;skipped: too deep&gt;</c>, as jq 1.8.2 does for scalars and containers alike.
    /// </summary>
    public static void Write(StringBuilder builder, JqValue value)
    {
        var stack = new List<(JqValue Container, int Index)>();
        var current = value;
        while (true)
        {
            // stack.Count is the number of containers around current: its level minus one.
            if (stack.Count >= MaxWriteDepth)
                builder.Append(TooDeep);
            else if (WriteOpening(builder, current))
            {
                // A container with elements: write its first one next.
                stack.Add((current, 0));
                current = Element(builder, current, 0);
                continue;
            }

            // current is written: go on with the next element, closing the containers that end.
            while (true)
            {
                if (stack.Count == 0)
                    return;

                var (container, index) = stack[^1];
                index++;
                if (index < Count(container))
                {
                    stack[^1] = (container, index);
                    builder.Append(',');
                    current = Element(builder, container, index);
                    break;
                }

                builder.Append(container.Kind == JqKind.Array ? ']' : '}');
                stack.RemoveAt(stack.Count - 1);
            }
        }
    }

    // Writes a scalar or an empty container whole and returns false; writes the opening bracket
    // of a container with elements and returns true.
    static bool WriteOpening(StringBuilder builder, JqValue value)
    {
        switch (value.Kind)
        {
            case JqKind.Null:
                builder.Append("null");
                return false;
            case JqKind.False:
                builder.Append("false");
                return false;
            case JqKind.True:
                builder.Append("true");
                return false;
            case JqKind.Number:
                builder.Append(FormatNumber(value.AsNumber()));
                return false;
            case JqKind.String:
                WriteString(builder, value.AsString());
                return false;
            case JqKind.Array:
                builder.Append('[');
                if (value.AsArray().Count > 0)
                    return true;
                builder.Append(']');
                return false;
            default:
                builder.Append('{');
                if (value.AsObject().Count > 0)
                    return true;
                builder.Append('}');
                return false;
        }
    }

    static int Count(JqValue container) => container.Kind == JqKind.Array ? container.AsArray().Count : container.AsObject().Count;

    // The element at index; for an object, writes its key and colon first.
    static JqValue Element(StringBuilder builder, JqValue container, int index)
    {
        if (container.Kind == JqKind.Array)
            return container.AsArray()[index];

        var obj = container.AsObject();
        WriteString(builder, obj.KeyAt(index));
        builder.Append(':');
        return obj.ValueAt(index);
    }

    /// <summary>Writes <paramref name="text"/> as a JSON string literal.</summary>
    public static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        var run = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c >= 0x20 && c != '"' && c != '\\' && c != 0x7F)
                continue;

            builder.Append(text, run, i - run);
            run = i + 1;
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
            }
        }

        builder.Append(text, run, text.Length - run).Append('"');
    }

    /// <summary>
    /// The JSON text of <paramref name="value"/>; when longer than <paramref name="maxBytes"/> UTF-8
    /// bytes, its longest prefix of at most <paramref name="keepBytes"/> bytes that ends on a code
    /// point boundary, then "..." and the closing delimiter of the value (<c>"</c>, <c>]</c> or
    /// <c>}</c>), as jq 1.8.2 writes them: <c>"aaa..."</c>, <c>[1,2,...]</c>, <c>{"a":...}</c>.
    /// </summary>
    public static string ToJsonTruncated(JqValue value, int maxBytes, int keepBytes)
    {
        var text = ToJson(value);
        if (text.Length * 3 <= maxBytes || Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return text;

        var bytes = 0;
        var end = 0;
        while (end < text.Length)
        {
            var size = CodePoints.Utf8Length(text, end, out var units);
            if (bytes + size > keepBytes)
                break;
            bytes += size;
            end += units;
        }

        return string.Concat(text.AsSpan(0, end), value.Kind == JqKind.String ? "...\"" : value.Kind == JqKind.Array ? "...]" : "...}");
    }

    /// <summary>The canonical text of a number: shortest round-trip digits in ECMAScript <c>Number::toString</c> layout.</summary>
    public static string FormatNumber(double number)
    {
        if (double.IsNaN(number))
            return "null";
        if (double.IsInfinity(number))
            return number > 0 ? MaxDouble : "-" + MaxDouble;
        if (number == 0)
            return double.IsNegative(number) ? "-0" : "0";
        // Fast path: integers below 1e15 print as themselves.
        if (Math.Abs(number) < 1e15 && number == Math.Truncate(number))
            return ((long)number).ToString(CultureInfo.InvariantCulture);

        // .NET's "R" gives the shortest round-trip digits; only the layout changes below.
        var text = number.ToString("R", CultureInfo.InvariantCulture);
        var negative = text[0] == '-';
        var body = negative ? text[1..] : text;
        var exponent = 0;
        var e = body.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(body[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            body = body[..e];
        }

        var dot = body.IndexOf('.');
        var digits = dot < 0 ? body : string.Concat(body.AsSpan(0, dot), body.AsSpan(dot + 1));
        // n: the position of the decimal point relative to the first digit (value = 0.digits * 10^n).
        var n = (dot < 0 ? body.Length : dot) + exponent;
        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0')
            lead++;
        digits = digits[lead..].TrimEnd('0');
        n -= lead;
        var k = digits.Length;

        var builder = new StringBuilder(32);
        if (negative)
            builder.Append('-');
        if (k <= n && n <= 21)
        {
            builder.Append(digits).Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            builder.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            builder.Append("0.").Append('0', -n).Append(digits);
        }
        else
        {
            builder.Append(digits[0]);
            if (k > 1)
                builder.Append('.').Append(digits, 1, k - 1);
            builder.Append('e').Append(n - 1 < 0 ? '-' : '+').Append(Math.Abs(n - 1).ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reads the numeric text of §5.3 (<c>tonumber</c>, and the numbers of JSON text): an optional
    /// sign, then digits with an optional fraction (<c>1.</c> and <c>.5</c> included) and exponent,
    /// or, in any ASCII case, <c>nan</c> or <c>snan</c> (NaN), <c>inf</c> or <c>infinity</c>. No white space.
    /// </summary>
    public static bool TryParseNumber(string text, out double number)
    {
        number = 0;
        var i = 0;
        var negative = false;
        if (i < text.Length && text[i] is '+' or '-')
        {
            negative = text[i] == '-';
            i++;
        }

        var word = text.AsSpan(i);
        if (word.Equals("nan", StringComparison.OrdinalIgnoreCase) || word.Equals("snan", StringComparison.OrdinalIgnoreCase))
        {
            number = double.NaN;
            return true;
        }

        if (word.Equals("inf", StringComparison.OrdinalIgnoreCase) || word.Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            number = negative ? double.NegativeInfinity : double.PositiveInfinity;
            return true;
        }

        var digits = 0;
        for (; i < text.Length && char.IsAsciiDigit(text[i]); i++)
            digits++;
        if (i < text.Length && text[i] == '.')
        {
            for (i++; i < text.Length && char.IsAsciiDigit(text[i]); i++)
                digits++;
        }

        if (digits == 0)
            return false;

        if (i < text.Length && text[i] is 'e' or 'E')
        {
            i++;
            if (i < text.Length && text[i] is '+' or '-')
                i++;
            var exponentDigits = 0;
            for (; i < text.Length && char.IsAsciiDigit(text[i]); i++)
                exponentDigits++;
            if (exponentDigits == 0)
                return false;
        }

        // The grammar is checked above; the host parser only converts (out of range gives ±Infinity or 0).
        return i == text.Length && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>
    /// Reads exactly one JSON value from <paramref name="text"/> (§5.3; jq's <c>fromjson</c>). On
    /// failure <paramref name="error"/> is the whole message, <c>&lt;reason&gt; (while parsing
    /// '&lt;text&gt;')</c>, the reason with the line and column where reading stopped (except for
    /// <c>Expected JSON value</c> and <c>Unexpected extra JSON values</c>, as in jq).
    /// </summary>
    public static bool TryParse(string text, out JqValue value, out JqValue error)
        => new JsonReader(text, sequence: false).Read(out value, out error);

    /// <summary>
    /// Reads a sequence of JSON values separated by white space (a data module, §9.11) as one
    /// array; no value at all is the empty array. Errors as <see cref="TryParse"/>.
    /// </summary>
    public static bool TryParseSequence(string text, out JqValue values, out JqValue error)
        => new JsonReader(text, sequence: true).Read(out values, out error);

    /// <summary>
    /// A JSON reader without recursion (an explicit stack), so deep input cannot exhaust the host
    /// stack. Like jq, a literal is any run of characters up to white space, a quote or one of
    /// <c>[]{},:</c>; a raw control character inside a string is an error, and every lone
    /// surrogate (escaped or raw) becomes U+FFFD.
    /// </summary>
    sealed class JsonReader(string text, bool sequence)
    {
        const int MaxDepth = 10000;

        sealed class Frame(bool isObject)
        {
            public readonly bool IsObject = isObject;
            public JqArray Array = JqArray.Empty;
            public JqObjectBuilder? Object = isObject ? new JqObjectBuilder() : null;
            public string Key = "";

            public void Add(JqValue value)
            {
                if (Object is not null)
                    Object.Set(Key, value);
                else
                    Array = Array.Append(value);
            }

            public JqValue ToValue() => Object is not null ? JqValue.FromObject(Object.ToObject()) : JqValue.FromArray(Array);
        }

        int pos;
        JqValue failure;
        // The values read so far, when reading a sequence.
        readonly List<JqValue>? values = sequence ? [] : null;

        public bool Read(out JqValue value, out JqValue error)
        {
            value = default;
            var ok = ReadDocument(out var result);
            error = ok ? default : failure;
            if (ok)
                value = result;
            return ok;
        }

        bool ReadDocument(out JqValue result)
        {
            result = default;
            if (text.Length > 0 && text[0] == (char)0xFEFF) // a byte order mark
                pos = 1;
            SkipSpace();
            if (pos == text.Length && values is not null)
            {
                result = JqValue.FromArray(JqArray.Empty);
                return true;
            }

            if (pos == text.Length)
                return FailWithoutPosition(Errors.ExpectedValue);

            var stack = new List<Frame>();
            while (true)
            {
                // A value starts at pos (white space skipped).
                if (pos == text.Length)
                    return Fail(Errors.UnfinishedAtEof);

                JqValue current;
                var c = text[pos];
                if (c is '[' or '{')
                {
                    if (stack.Count == MaxDepth)
                        return Fail(Errors.DepthLimit, pos + 1);
                    pos++;
                    SkipSpace();
                    var frame = new Frame(c == '{');
                    if (pos < text.Length && text[pos] == (c == '{' ? '}' : ']'))
                    {
                        pos++;
                        current = frame.ToValue();
                    }
                    else
                    {
                        stack.Add(frame);
                        if (frame.IsObject && !ReadKey(frame))
                            return false;
                        continue;
                    }
                }
                else if (c == '"')
                {
                    pos++;
                    if (!ReadString(out var s))
                        return false;
                    current = JqValue.FromString(s);
                }
                else if (c is ']' or '}')
                {
                    if (stack.Count == 0)
                        return Fail(Errors.Unmatched(c), pos + 1);
                    return Fail(stack[^1].IsObject ? Errors.KeyValuePairs : Errors.ExpectedArrayElement, pos + 1);
                }
                else if (c is ',' or ':')
                {
                    return Fail(Errors.ExpectedValueBefore(c), pos + 1);
                }
                else if (!ReadLiteral(out current))
                {
                    return false;
                }

                // Attach the value to its container; close containers as they end.
                while (true)
                {
                    SkipSpace();
                    if (stack.Count == 0 && values is not null)
                    {
                        // A sequence goes on with the next value, if any.
                        values.Add(current);
                        if (pos < text.Length)
                            break;
                        result = JqValue.FromArray(JqArray.FromOwned([.. values]));
                        return true;
                    }

                    if (stack.Count == 0)
                    {
                        if (pos != text.Length)
                            return FailWithoutPosition(Errors.ExtraValues);
                        result = current;
                        return true;
                    }

                    var top = stack[^1];
                    top.Add(current);
                    if (pos == text.Length)
                        return Fail(Errors.UnfinishedAtEof);

                    var d = text[pos++];
                    if (d == (top.IsObject ? '}' : ']'))
                    {
                        stack.RemoveAt(stack.Count - 1);
                        current = top.ToValue();
                        continue;
                    }

                    if (d != ',')
                        return Fail(d is ']' or '}' ? Errors.Unmatched(d) : Errors.ExpectedSeparator, pos);

                    SkipSpace();
                    if (top.IsObject && !ReadKey(top))
                        return false;
                    break;
                }
            }
        }

        // Reads `"key" :` and the white space after it.
        bool ReadKey(Frame frame)
        {
            if (pos == text.Length)
                return Fail(Errors.UnfinishedAtEof);
            if (text[pos] != '"')
                return Fail(Errors.KeysMustBeStrings, pos + 1);
            pos++;
            if (!ReadString(out var key))
                return false;
            SkipSpace();
            if (pos == text.Length)
                return Fail(Errors.UnfinishedAtEof);
            if (text[pos] != ':')
                return Fail(Errors.KeyValuePairs, pos + 1);
            pos++;
            SkipSpace();
            frame.Key = CodePoints.Sanitize(key);
            return true;
        }

        // Reads the rest of a string after its opening quote. Escapes decode to UTF-16 units as
        // they are; JqValue.FromString then turns lone surrogates into U+FFFD.
        bool ReadString(out string value)
        {
            value = "";
            var builder = new StringBuilder();
            var run = pos;
            while (true)
            {
                if (pos == text.Length)
                    return Fail(Errors.UnfinishedAtEof);
                var c = text[pos];
                if (c == '"')
                {
                    builder.Append(text, run, pos - run);
                    pos++;
                    value = builder.ToString();
                    return true;
                }

                if (c < 0x20)
                    return Fail(Errors.ControlCharacter, pos + 1);

                if (c != '\\')
                {
                    pos++;
                    continue;
                }

                builder.Append(text, run, pos - run);
                pos++;
                if (pos == text.Length)
                    return Fail(Errors.UnfinishedAtEof);
                var e = text[pos++];
                switch (e)
                {
                    case '"' or '\\' or '/':
                        builder.Append(e);
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'u':
                        var unit = 0;
                        for (var i = 0; i < 4; i++)
                        {
                            if (pos == text.Length)
                                return Fail(Errors.UnfinishedAtEof);
                            var digit = HexValue(text[pos++]);
                            if (digit < 0)
                                return Fail(Errors.InvalidUnicodeEscape, pos);
                            unit = unit * 16 + digit;
                        }

                        builder.Append((char)unit);
                        break;
                    default:
                        return Fail(Errors.InvalidEscape, pos);
                }

                run = pos;
            }
        }

        bool ReadLiteral(out JqValue value)
        {
            value = default;
            var start = pos;
            while (pos < text.Length && !IsDelimiter(text[pos]))
                pos++;
            var token = text[start..pos];
            switch (token)
            {
                case "null":
                    value = JqValue.Null;
                    return true;
                case "true":
                    value = JqValue.True;
                    return true;
                case "false":
                    value = JqValue.False;
                    return true;
            }

            if (!TryParseNumber(token, out var number))
                return Fail(char.IsAsciiLetter(token[0]) ? Errors.InvalidLiteral : Errors.InvalidNumericLiteral, pos);
            value = JqValue.FromNumber(number);
            return true;
        }

        static bool IsDelimiter(char c) => c is ' ' or '\t' or '\n' or '\r' or '[' or ']' or '{' or '}' or ',' or ':' or '"';

        static int HexValue(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };

        void SkipSpace()
        {
            while (pos < text.Length && text[pos] is ' ' or '\t' or '\n' or '\r')
                pos++;
        }

        bool Fail(string message) => Fail(message, text.Length);

        // consumed: how many characters were read when the error was found; the column counts
        // them on their line, as jq does ("{" fails at line 1, column 1).
        bool Fail(string message, int consumed)
        {
            var line = 1;
            var lineStart = 0;
            for (var i = 0; i < consumed && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }

            return FailWithoutPosition(Errors.AtPosition(message, line, consumed - lineStart));
        }

        bool FailWithoutPosition(string reason)
        {
            failure = Errors.WhileParsing(reason, text);
            return false;
        }
    }
}
