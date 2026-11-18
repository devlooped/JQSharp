using System.Text;
using JQLite.Core.Values;

namespace JQLite.Core.Builtins;

/// <summary>
/// The regex dialect of §9.10: reads a jqlite pattern, rejects everything outside the dialect, and
/// writes an equivalent pattern for the host engine (<see cref="RegexEngine"/>) together with the
/// names of its capturing groups in the order of their '('.
/// </summary>
/// <remarks>
/// The output is in the common syntax of the engines, with the meaning the spec demands spelled out
/// so that no engine default leaks in: every code point is written as <c>\uXXXX</c> UTF-16 units
/// (a code point above U+FFFF as its surrogate pair in a non-capturing group), <c>.</c>, classes and
/// <c>\D \W \S</c> as alternatives of a BMP class without surrogates and the surrogate pairs of the
/// code points above U+FFFF, so that a match never splits a pair; <c>\d \w \s</c> as ASCII classes;
/// <c>$</c> as <c>\z</c>; <c>\b \B</c> as ASCII lookarounds (<see cref="Translation.Lookaround"/>);
/// named groups as plain groups (engines number them differently). The <c>i</c> flag is done here,
/// never by the engine: it folds ASCII letters only (<see cref="RangeSet.AddAsciiCases"/>, the one
/// place of the rule). A port whose engine works on code points writes the classes directly.
/// </remarks>
static class RegexSyntax
{
    public const int MaxRepeat = 1000;

    const string AnyPair = @"[\uD800-\uDBFF][\uDC00-\uDFFF]";

    /// <summary>The flags of §9.10 that change the pattern itself.</summary>
    public readonly record struct Options(bool Extended, bool DotAll, bool IgnoreCase);

    /// <summary>
    /// A translated pattern: the host pattern, one name (or null) per capturing group, and whether
    /// it uses lookaround (for \b and \B), which a linear-time engine may not run.
    /// </summary>
    public sealed record Translation(string Pattern, string?[] Groups, bool Lookaround);

    // The ASCII word boundaries of §9.10, by the ASCII \w; an engine's own \b may be Unicode.
    const string Word = "[0-9A-Za-z_]";
    const string Boundary = $"(?:(?<={Word})(?!{Word})|(?<!{Word})(?={Word}))";
    const string NotBoundary = $"(?:(?<={Word})(?={Word})|(?<!{Word})(?!{Word}))";

    // What precedes a quantifier: only an atom may be repeated.
    enum Previous
    {
        None,
        Atom,
        Quantifier,
        Anchor,
    }

    public static bool TryTranslate(string pattern, Options options, out Translation translation, out string reason)
    {
        var points = Decode(pattern);
        var output = new StringBuilder(pattern.Length * 6);
        var groups = new List<string?>();
        var open = 0;
        var previous = Previous.None;
        var lookaround = false;
        translation = new Translation("", [], false);
        reason = "";
        for (var i = 0; i < points.Length;)
        {
            var c = points[i];
            if (options.Extended && (IsExtendedSpace(c) || c == '#'))
            {
                i = SkipExtended(points, i);
                continue;
            }

            switch (c)
            {
                case '\\':
                    if (!TryEscape(points, ref i, output, options, ref previous, ref lookaround, out reason))
                        return false;
                    continue;
                case '[':
                    if (!TryClass(points, ref i, output, options, out reason))
                        return false;
                    previous = Previous.Atom;
                    continue;
                case '.':
                    output.Append(options.DotAll ? @"(?:[^\uD800-\uDFFF]|" : @"(?:[^\n\uD800-\uDFFF]|").Append(AnyPair).Append(')');
                    previous = Previous.Atom;
                    i++;
                    continue;
                case '^':
                    output.Append('^');
                    previous = Previous.Anchor;
                    i++;
                    continue;
                case '$':
                    output.Append(@"\z");
                    previous = Previous.Anchor;
                    i++;
                    continue;
                case '|':
                    output.Append('|');
                    previous = Previous.None;
                    i++;
                    continue;
                case '(':
                    if (!TryGroup(points, ref i, output, groups, out reason))
                        return false;
                    open++;
                    previous = Previous.None;
                    continue;
                case ')':
                    if (open == 0)
                        return Fail("unmatched close parenthesis", out reason);
                    open--;
                    output.Append(')');
                    previous = Previous.Atom;
                    i++;
                    continue;
                case '*' or '+' or '?' or '{':
                    if (previous != Previous.Atom)
                        return Fail(previous == Previous.Quantifier ? "nested quantifier" : "quantifier without an operand", out reason);
                    if (!TryQuantifier(points, ref i, output, options, out reason))
                        return false;
                    previous = Previous.Quantifier;
                    continue;
                case ']' or '}':
                    return Fail($"unescaped '{(char)c}'", out reason);
                default:
                    Literal(output, c, options);
                    previous = Previous.Atom;
                    i++;
                    continue;
            }
        }

        if (open > 0)
            return Fail("missing close parenthesis", out reason);
        translation = new Translation(output.ToString(), [.. groups], lookaround);
        return true;
    }

    // \x outside a class: a literal, a shorthand class or an anchor.
    static bool TryEscape(int[] points, ref int i, StringBuilder output, Options options, ref Previous previous, ref bool lookaround, out string reason)
    {
        reason = "";
        if (i + 1 == points.Length)
            return Fail("trailing backslash", out reason);
        var c = points[i + 1];
        switch (c)
        {
            case 'b' or 'B':
                output.Append(c == 'b' ? Boundary : NotBoundary);
                lookaround = true;
                previous = Previous.Anchor;
                i += 2;
                return true;
            case 'd' or 'w' or 's' or 'D' or 'W' or 'S':
                var set = new RangeSet();
                set.AddShorthand(c);
                Class(output, set, negated: false);
                previous = Previous.Atom;
                i += 2;
                return true;
        }

        if (!TryEscapedLiteral(points, ref i, out var literal, out reason))
            return false;
        Literal(output, literal, options);
        previous = Previous.Atom;
        return true;
    }

    // The escapes that stand for one code point: the metacharacters, \t \n \r \f \v and \xHH.
    static bool TryEscapedLiteral(int[] points, ref int i, out int literal, out string reason)
    {
        reason = "";
        literal = 0;
        if (i + 1 == points.Length)
            return Fail("trailing backslash", out reason);
        var c = points[i + 1];
        switch (c)
        {
            case '\\' or '.' or '*' or '+' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '|' or '^' or '$' or '/' or '-':
                literal = c;
                break;
            case 't': literal = '\t'; break;
            case 'n': literal = '\n'; break;
            case 'r': literal = '\r'; break;
            case 'f': literal = '\f'; break;
            case 'v': literal = '\v'; break;
            case 'x':
                var high = i + 2 < points.Length ? HexValue(points[i + 2]) : -1;
                var low = i + 3 < points.Length ? HexValue(points[i + 3]) : -1;
                if (high < 0 || low < 0)
                    return Fail(@"\x must be followed by two hex digits", out reason);
                literal = high * 16 + low;
                i += 4;
                return true;
            default:
                return Fail($"invalid escape \\{Text(c)}", out reason);
        }

        i += 2;
        return true;
    }

    // ( (?: or (?<name>
    static bool TryGroup(int[] points, ref int i, StringBuilder output, List<string?> groups, out string reason)
    {
        reason = "";
        if (i + 1 < points.Length && points[i + 1] == '?')
        {
            if (i + 2 < points.Length && points[i + 2] == ':')
            {
                output.Append("(?:");
                i += 3;
                return true;
            }

            if (i + 2 < points.Length && points[i + 2] == '<')
            {
                var start = i + 3;
                var end = start;
                while (end < points.Length && points[end] != '>')
                    end++;
                if (end == points.Length || !IsGroupName(points, start, end))
                    return Fail("invalid group name", out reason);
                var name = Text(points, start, end);
                if (groups.Contains(name))
                    return Fail($"duplicate group name {name}", out reason);
                groups.Add(name);
                output.Append('(');
                i = end + 1;
                return true;
            }

            return Fail("unsupported group syntax (?", out reason);
        }

        groups.Add(null);
        output.Append('(');
        i++;
        return true;
    }

    // * + ? {n} {n,} {n,m}, each optionally followed by ? (lazy).
    static bool TryQuantifier(int[] points, ref int i, StringBuilder output, Options options, out string reason)
    {
        reason = "";
        var c = points[i];
        if (c == '{')
        {
            var at = i + 1;
            if (!TryNumber(points, ref at, out var min))
                return Fail("'{' is not a quantifier", out reason);
            var max = min;
            var unbounded = false;
            if (at < points.Length && points[at] == ',')
            {
                at++;
                if (at < points.Length && points[at] == '}')
                    unbounded = true;
                else if (!TryNumber(points, ref at, out max))
                    return Fail("'{' is not a quantifier", out reason);
            }

            if (at == points.Length || points[at] != '}')
                return Fail("'{' is not a quantifier", out reason);
            if (min > MaxRepeat || !unbounded && (max > MaxRepeat || max < min))
                return Fail("invalid repetition count", out reason);
            output.Append('{').Append(min);
            if (unbounded)
                output.Append(',');
            else if (max != min)
                output.Append(',').Append(max);
            output.Append('}');
            i = at + 1;
        }
        else
        {
            output.Append((char)c);
            i++;
        }

        if (options.Extended)
        {
            while (i < points.Length && (IsExtendedSpace(points[i]) || points[i] == '#'))
                i = SkipExtended(points, i);
        }

        if (i < points.Length && points[i] == '?')
        {
            output.Append('?');
            i++;
        }

        return true;
    }

    static bool TryNumber(int[] points, ref int at, out int value)
    {
        value = 0;
        var start = at;
        while (at < points.Length && points[at] is >= '0' and <= '9')
        {
            value = Math.Min(value * 10 + (points[at] - '0'), MaxRepeat + 1);
            at++;
        }

        return at > start;
    }

    // [...] or [^...]: literals, escapes, ranges of literals and shorthands.
    static bool TryClass(int[] points, ref int i, StringBuilder output, Options options, out string reason)
    {
        reason = "";
        var at = i + 1;
        var negated = at < points.Length && points[at] == '^';
        if (negated)
            at++;
        var first = at;
        var set = new RangeSet();
        var previousLiteral = -1;
        while (true)
        {
            if (at == points.Length)
                return Fail("missing ']'", out reason);
            var c = points[at];
            if (c == ']')
                break;
            if (c == '[')
                return Fail("unescaped '[' in a class", out reason);

            if (c == '-' && at != first && at + 1 < points.Length && points[at + 1] != ']')
            {
                // A range: the item before must be a single literal, the one after a literal too.
                if (previousLiteral < 0)
                    return Fail("'-' must be first, last, escaped or between two literals", out reason);
                at++;
                if (!TryClassLiteral(points, ref at, out var high, out reason))
                    return false;
                if (high < previousLiteral)
                    return Fail("range out of order", out reason);
                set.Add(previousLiteral, high);
                previousLiteral = -1;
                continue;
            }

            if (c == '\\' && at + 1 < points.Length && points[at + 1] is 'd' or 'w' or 's' or 'D' or 'W' or 'S')
            {
                set.AddShorthand(points[at + 1]);
                previousLiteral = -1;
                at += 2;
                continue;
            }

            if (!TryClassLiteral(points, ref at, out var literal, out reason))
                return false;
            set.Add(literal, literal);
            previousLiteral = literal;
        }

        if (at == first)
            return Fail("empty class", out reason);
        // The i flag folds before a negation applies: [^a] matches neither a nor A.
        if (options.IgnoreCase)
            set.AddAsciiCases();
        Class(output, set, negated);
        i = at + 1;
        return true;
    }

    static bool TryClassLiteral(int[] points, ref int at, out int literal, out string reason)
    {
        reason = "";
        literal = points[at];
        if (literal == '\\')
            return TryEscapedLiteral(points, ref at, out literal, out reason);
        if (literal is '[' or ']')
            return Fail($"unescaped '{(char)literal}' in a class", out reason);
        at++;
        return true;
    }

    // A class as alternatives: a BMP class without surrogates, then the surrogate pairs of its code
    // points above U+FFFF. A negated class keeps the host's negation for the BMP part (so that the
    // host's case folding applies before it) and spells out the complement above U+FFFF.
    static void Class(StringBuilder output, RangeSet set, bool negated)
    {
        var bmp = new StringBuilder();
        foreach (var (low, high) in set.Ranges)
        {
            if (low > 0xFFFF)
                break;
            AppendBmpRange(bmp, low, Math.Min(high, 0xFFFF));
        }

        var alternatives = new List<string>();
        if (negated)
            alternatives.Add($@"[^{bmp}\uD800-\uDFFF]");
        else if (bmp.Length > 0)
            alternatives.Add($"[{bmp}]");
        foreach (var (low, high) in (negated ? set.Complement() : set).Ranges)
        {
            if (high > 0xFFFF)
                AppendAstralRange(alternatives, Math.Max(low, 0x10000), high);
        }

        // An empty set (a negated class of everything): a class no UTF-16 unit is in.
        output.Append("(?:").Append(alternatives.Count == 0 ? @"[^\u0000-\uFFFF]" : string.Join('|', alternatives)).Append(')');
    }

    // A BMP range without the surrogates.
    static void AppendBmpRange(StringBuilder output, int low, int high)
    {
        if (low < 0xD800)
            AppendRange(output, low, Math.Min(high, 0xD7FF));
        if (high > 0xDFFF)
            AppendRange(output, Math.Max(low, 0xE000), high);
    }

    static void AppendRange(StringBuilder output, int low, int high)
    {
        if (low > high)
            return;
        Unit(output, low);
        if (high > low)
        {
            output.Append('-');
            Unit(output, high);
        }
    }

    // The code points low..high above U+FFFF as alternatives of surrogate pairs.
    static void AppendAstralRange(List<string> alternatives, int low, int high)
    {
        var (lowHigh, lowLow) = Pair(low);
        var (highHigh, highLow) = Pair(high);
        if (lowHigh == highHigh)
        {
            alternatives.Add(PairRange(lowHigh, lowHigh, lowLow, highLow));
            return;
        }

        alternatives.Add(PairRange(lowHigh, lowHigh, lowLow, 0xDFFF));
        if (highHigh - lowHigh > 1)
            alternatives.Add(PairRange(lowHigh + 1, highHigh - 1, 0xDC00, 0xDFFF));
        alternatives.Add(PairRange(highHigh, highHigh, 0xDC00, highLow));
    }

    static string PairRange(int firstLow, int firstHigh, int secondLow, int secondHigh)
    {
        var output = new StringBuilder("[");
        AppendRange(output, firstLow, firstHigh);
        output.Append("][");
        AppendRange(output, secondLow, secondHigh);
        return output.Append(']').ToString();
    }

    // A code point; under the i flag an ASCII letter becomes the class of its two cases.
    static void Literal(StringBuilder output, int c, Options options)
    {
        if (options.IgnoreCase && c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'))
        {
            var set = new RangeSet();
            set.Add(c, c);
            set.AddAsciiCases();
            Class(output, set, negated: false);
            return;
        }

        if (c <= 0xFFFF)
        {
            Unit(output, c);
            return;
        }

        var (high, low) = Pair(c);
        output.Append("(?:");
        Unit(output, high);
        Unit(output, low);
        output.Append(')');
    }

    static void Unit(StringBuilder output, int unit)
    {
        const string hex = "0123456789ABCDEF";
        output.Append(@"\u").Append(hex[unit >> 12]).Append(hex[(unit >> 8) & 0xF]).Append(hex[(unit >> 4) & 0xF]).Append(hex[unit & 0xF]);
    }

    static (int High, int Low) Pair(int c) => (0xD800 + ((c - 0x10000) >> 10), 0xDC00 + ((c - 0x10000) & 0x3FF));

    // With the x flag: whitespace and a # comment up to the next line feed.
    static int SkipExtended(int[] points, int i)
    {
        if (points[i] != '#')
            return i + 1;
        while (i < points.Length && points[i] != '\n')
            i++;
        return i;
    }

    static bool IsExtendedSpace(int c) => c is (>= 0x09 and <= 0x0D) or 0x20;

    static bool IsGroupName(int[] points, int start, int end)
    {
        if (end == start || points[start] is >= '0' and <= '9')
            return false;
        for (var i = start; i < end; i++)
        {
            if (points[i] is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_'))
                return false;
        }

        return true;
    }

    static int HexValue(int c) => c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;

    static int[] Decode(string text)
    {
        var points = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length)
            {
                points.Add(char.ConvertToUtf32(text[i], text[i + 1]));
                i++;
            }
            else
            {
                points.Add(text[i]);
            }
        }

        return [.. points];
    }

    static string Text(int c)
    {
        var builder = new StringBuilder();
        CodePoints.Append(builder, c);
        return builder.ToString();
    }

    static string Text(int[] points, int start, int end)
    {
        var builder = new StringBuilder();
        for (var i = start; i < end; i++)
            CodePoints.Append(builder, points[i]);
        return builder.ToString();
    }

    static bool Fail(string text, out string reason)
    {
        reason = text;
        return false;
    }

    /// <summary>A set of code points as sorted, disjoint, non-adjacent ranges.</summary>
    sealed class RangeSet
    {
        readonly List<(int Low, int High)> ranges = [];
        bool normalized = true;

        public IReadOnlyList<(int Low, int High)> Ranges
        {
            get
            {
                Normalize();
                return ranges;
            }
        }

        public void Add(int low, int high)
        {
            ranges.Add((low, high));
            normalized = false;
        }

        // The ASCII classes of §9.10 and their complements.
        public void AddShorthand(int c)
        {
            var set = new RangeSet();
            switch (c)
            {
                case 'd' or 'D':
                    set.Add('0', '9');
                    break;
                case 'w' or 'W':
                    set.Add('0', '9');
                    set.Add('A', 'Z');
                    set.Add('a', 'z');
                    set.Add('_', '_');
                    break;
                default:
                    set.Add('\t', '\r');
                    set.Add(' ', ' ');
                    break;
            }

            foreach (var (low, high) in c is 'D' or 'W' or 'S' ? set.Complement().Ranges : set.Ranges)
                Add(low, high);
        }

        /// <summary>
        /// The i flag of §9.10: adds the other case of every ASCII letter in the set (A-Z and a-z,
        /// 32 apart); no other code point folds.
        /// </summary>
        public void AddAsciiCases()
        {
            var added = new List<(int Low, int High)>();
            foreach (var (low, high) in Ranges)
            {
                var upperLow = Math.Max(low, 'A');
                var upperHigh = Math.Min(high, 'Z');
                if (upperLow <= upperHigh)
                    added.Add((upperLow + 32, upperHigh + 32));
                var lowerLow = Math.Max(low, 'a');
                var lowerHigh = Math.Min(high, 'z');
                if (lowerLow <= lowerHigh)
                    added.Add((lowerLow - 32, lowerHigh - 32));
            }

            foreach (var (low, high) in added)
                Add(low, high);
        }

        public RangeSet Complement()
        {
            var complement = new RangeSet();
            var next = 0;
            foreach (var (low, high) in Ranges)
            {
                if (low > next)
                    complement.Add(next, low - 1);
                next = high + 1;
            }

            if (next <= CodePoints.Max)
                complement.Add(next, CodePoints.Max);
            return complement;
        }

        void Normalize()
        {
            if (normalized)
                return;
            ranges.Sort(static (a, b) => a.Low.CompareTo(b.Low));
            var merged = new List<(int Low, int High)>(ranges.Count);
            foreach (var range in ranges)
            {
                if (merged.Count > 0 && range.Low <= merged[^1].High + 1)
                    merged[^1] = (merged[^1].Low, Math.Max(merged[^1].High, range.High));
                else
                    merged.Add(range);
            }

            ranges.Clear();
            ranges.AddRange(merged);
            normalized = true;
        }
    }
}
