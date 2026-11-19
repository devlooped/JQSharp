using System.Text;
using JQLite.Core.Values;

namespace JQLite.Core.Builtins;

/// <summary>
/// The regex flavor of §9.10: reads a jqlite pattern, rejects everything outside the flavor, and
/// writes an equivalent program for the engine (<see cref="RegexEngine"/>) together with the names
/// of its capturing groups in the order of their '('.
/// </summary>
/// <remarks>
/// The program is made of the instructions of <see cref="Op"/>, with the meaning the spec demands
/// spelled out: <c>.</c>, classes and <c>\d \w \s</c> as sets of code points, <c>$</c> as the end of
/// the input, <c>\b \B</c> as ASCII word boundaries. The <c>i</c> flag is done here: it folds ASCII
/// letters only (<see cref="RangeSet.AddAsciiCases"/>, the one place of the rule). No host regex
/// engine reads the flavor with one meaning, so none is used (docs/porting.md §3.3).
/// <para>
/// The limits of §11 are checked here, while the pattern is read (<see cref="Measure"/>), so that
/// the engine never meets a pattern beyond them and takes every one within.
/// </para>
/// <para>
/// The pattern is read in one pass, without recursion, and each instruction that jumps says how far
/// (not where to), so a piece of the program can be copied or moved as it is: a quantifier wraps or
/// repeats the instructions of the atom before it.
/// </para>
/// </remarks>
static class RegexSyntax
{
    /// <summary>The largest count of a quantifier, and of the counts of nested quantifiers multiplied.</summary>
    public const int MaxRepeat = 1000;

    /// <summary>The largest size of a pattern (§9.10): its items, counted repetitions written out.</summary>
    public const int MaxSize = 10000;

    /// <summary>The most groups open at once.</summary>
    public const int MaxNesting = 100;

    /// <summary>The most capturing groups.</summary>
    public const int MaxGroups = 1000;

    /// <summary>The reason of a pattern beyond <see cref="MaxSize"/>, <see cref="MaxNesting"/> or <see cref="MaxGroups"/>: a fixed text (§10.3).</summary>
    public const string TooLarge = "the pattern is too large";

    /// <summary>The flags of §9.10 that change the pattern itself.</summary>
    public readonly record struct Options(bool Extended, bool DotAll, bool IgnoreCase);

    /// <summary>The instructions of a program; <c>a</c> and <c>b</c> are their operands.</summary>
    public enum Op
    {
        /// <summary>The code point <c>a</c>.</summary>
        Char,

        /// <summary>A code point of the set <c>a</c> of the program.</summary>
        Set,

        /// <summary>Any code point.</summary>
        Any,

        /// <summary>Any code point but U+000A.</summary>
        AnyButLineFeed,

        /// <summary>Go on <c>a</c> instructions further; if that fails, <c>b</c> instructions further.</summary>
        Split,

        /// <summary>Go on <c>a</c> instructions further.</summary>
        Jump,

        /// <summary>Store the position in slot <c>a</c> (2g: the start of group g; 2g + 1: its end; group 0 is the match).</summary>
        Save,

        /// <summary>The start of the input.</summary>
        Begin,

        /// <summary>The end of the input.</summary>
        End,

        /// <summary>An ASCII word boundary.</summary>
        WordBoundary,

        /// <summary>Not an ASCII word boundary.</summary>
        NotWordBoundary,

        /// <summary>Store the position in mark <c>a</c>: where an iteration of a repetition starts.</summary>
        Mark,

        /// <summary>
        /// Go on <c>b</c> instructions further when the position is the one of mark <c>a</c> (an
        /// iteration that matched nothing ends its repetition); else to the next instruction.
        /// </summary>
        EmptyExit,

        /// <summary>The pattern matched.</summary>
        Match,
    }

    /// <summary>
    /// A translated pattern: the program (an instruction and its operands <c>a</c>, <c>b</c> per
    /// index), its sets of code points (sorted pairs low, high), the number of its marks, and one name
    /// (or null) per capturing group.
    /// </summary>
    public sealed class Translation(Op[] ops, int[] a, int[] b, int[][] sets, int marks, string?[] groups)
    {
        public Op[] Ops { get; } = ops;
        public int[] A { get; } = a;
        public int[] B { get; } = b;
        public int[][] Sets { get; } = sets;
        public int Marks { get; } = marks;
        public string?[] Groups { get; } = groups;
    }

    // What precedes a quantifier: only an atom may be repeated.
    enum Previous
    {
        None,
        Atom,
        Quantifier,
        Anchor,
    }

    public static bool TryTranslate(string pattern, Options options, out Translation translation, out string reason)
        => new Translator(Decode(pattern), options).TryTranslate(out translation, out reason);

    // An open group, or the whole pattern: where it starts, where its current alternative starts, the
    // jumps of its earlier alternatives to its end, and whether it can match nothing (for the loops
    // around it): `before` is whether every item of the current alternative but the last can, Last
    // whether the last can (null: no item), Any whether an earlier alternative can.
    sealed class Frame(int start, int group)
    {
        bool before = true;

        public int Start { get; } = start;
        public int Group { get; } = group;
        public int Alternative { get; set; } = start;
        public List<int> Jumps { get; } = [];
        public bool? Last { get; set; }
        public bool Any { get; private set; }

        public void Add(bool nullable)
        {
            before = before && Last != false;
            Last = nullable;
        }

        // Ends the current alternative.
        public void Close()
        {
            Any = Any || before && Last != false;
            before = true;
            Last = null;
        }
    }

    sealed class Translator(int[] points, Options options)
    {
        readonly List<Op> ops = [];
        readonly List<int> a = [];
        readonly List<int> b = [];
        readonly List<int[]> sets = [];
        readonly List<string?> groups = [];
        readonly List<Frame> frames = [];
        readonly Measure measure = new();
        int marks;
        int i;
        Previous previous = Previous.None;
        // Where the instructions of the last atom start.
        int atom;
        string reason = "";

        public bool TryTranslate(out Translation translation, out string failure)
        {
            var translated = Translate();
            translation = translated ? new Translation([.. ops], [.. a], [.. b], [.. sets], marks, [.. groups]) : null!;
            failure = translated ? "" : reason;
            return translated;
        }

        bool Translate()
        {
            Emit(Op.Save, 0);
            var frame = new Frame(ops.Count, 0);
            frames.Add(frame);
            while (i < points.Length)
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
                        if (!TryEscape())
                            return false;
                        if (!measure.Add(1))
                            return Fail(TooLarge);
                        continue;
                    case '[':
                        if (!TryClass(out var items))
                            return false;
                        if (!measure.Add(items))
                            return Fail(TooLarge);
                        continue;
                    case '.':
                        StartAtom();
                        Emit(options.DotAll ? Op.Any : Op.AnyButLineFeed);
                        if (!measure.Add(1))
                            return Fail(TooLarge);
                        i++;
                        continue;
                    case '^':
                        Anchor(Op.Begin);
                        if (!measure.Add(1))
                            return Fail(TooLarge);
                        i++;
                        continue;
                    case '$':
                        Anchor(Op.End);
                        if (!measure.Add(1))
                            return Fail(TooLarge);
                        i++;
                        continue;
                    case '|':
                        if (!measure.Add(1))
                            return Fail(TooLarge);
                        // The alternative so far, or the rest: it ends with a jump to the end of the group.
                        Insert(frame.Alternative, Op.Split, 1, ops.Count - frame.Alternative + 2);
                        frame.Jumps.Add(ops.Count);
                        Emit(Op.Jump);
                        frame.Alternative = ops.Count;
                        frame.Close();
                        previous = Previous.None;
                        i++;
                        continue;
                    case '(':
                        if (measure.Depth == MaxNesting)
                            return Fail(TooLarge);
                        var start = ops.Count;
                        if (!TryGroup())
                            return false;
                        if (!measure.Open())
                            return Fail(TooLarge);
                        frame = new Frame(start, ops.Count > start ? groups.Count : 0) { Alternative = ops.Count };
                        frames.Add(frame);
                        previous = Previous.None;
                        continue;
                    case ')':
                        if (frames.Count == 1)
                            return Fail("unmatched close parenthesis");
                        measure.Close();
                        End(frame);
                        if (frame.Group > 0)
                            Emit(Op.Save, 2 * frame.Group + 1);
                        frames.RemoveAt(frames.Count - 1);
                        var group = frame;
                        frame = frames[^1];
                        frame.Add(group.Any);
                        atom = group.Start;
                        previous = Previous.Atom;
                        i++;
                        continue;
                    case '*' or '+' or '?' or '{':
                        if (previous != Previous.Atom)
                            return Fail(previous == Previous.Quantifier ? "nested quantifier" : "quantifier without an operand");
                        if (!TryQuantifier(frame))
                            return false;
                        previous = Previous.Quantifier;
                        continue;
                    case ']' or '}':
                        return Fail($"unescaped '{(char)c}'");
                    default:
                        Literal(c);
                        if (!measure.Add(1))
                            return Fail(TooLarge);
                        i++;
                        continue;
                }
            }

            if (frames.Count > 1)
                return Fail("missing close parenthesis");
            End(frame);
            Emit(Op.Save, 1);
            Emit(Op.Match);
            return true;
        }

        // The end of a group or of the pattern: the jumps of its alternatives arrive here.
        void End(Frame frame)
        {
            foreach (var jump in frame.Jumps)
                a[jump] = ops.Count - jump;
            frame.Close();
        }

        void Emit(Op op, int first = 0, int second = 0)
        {
            ops.Add(op);
            a.Add(first);
            b.Add(second);
        }

        void Insert(int at, Op op, int first, int second)
        {
            ops.Insert(at, op);
            a.Insert(at, first);
            b.Insert(at, second);
        }

        // An atom starts here: a quantifier after it repeats the instructions from here on.
        void StartAtom()
        {
            atom = ops.Count;
            previous = Previous.Atom;
            frames[^1].Add(false);
        }

        void Anchor(Op op)
        {
            Emit(op);
            previous = Previous.Anchor;
            frames[^1].Add(true);
        }

        // \x outside a class: a literal, a shorthand class or an anchor.
        bool TryEscape()
        {
            if (i + 1 == points.Length)
                return Fail("trailing backslash");
            var c = points[i + 1];
            switch (c)
            {
                case 'b' or 'B':
                    Anchor(c == 'b' ? Op.WordBoundary : Op.NotWordBoundary);
                    i += 2;
                    return true;
                case 'd' or 'w' or 's' or 'D' or 'W' or 'S':
                    var set = new RangeSet();
                    set.AddShorthand(c);
                    StartAtom();
                    Class(set, negated: false);
                    i += 2;
                    return true;
            }

            if (!TryEscapedLiteral(points, ref i, out var literal, out reason))
                return false;
            Literal(literal);
            return true;
        }

        // ( (?: or (?<name>: a capturing group starts with the instruction that stores its start.
        bool TryGroup()
        {
            if (i + 1 < points.Length && points[i + 1] == '?')
            {
                if (i + 2 < points.Length && points[i + 2] == ':')
                {
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
                        return Fail("invalid group name");
                    var name = Text(points, start, end);
                    if (groups.Contains(name))
                        return Fail($"duplicate group name {name}");
                    if (groups.Count == MaxGroups)
                        return Fail(TooLarge);
                    groups.Add(name);
                    Emit(Op.Save, 2 * groups.Count);
                    i = end + 1;
                    return true;
                }

                return Fail("unsupported group syntax (?");
            }

            if (groups.Count == MaxGroups)
                return Fail(TooLarge);
            groups.Add(null);
            Emit(Op.Save, 2 * groups.Count);
            i++;
            return true;
        }

        // * + ? {n} {n,} {n,m}, each optionally followed by ? (lazy).
        bool TryQuantifier(Frame frame)
        {
            var c = points[i];
            int min;
            // -1: no maximum.
            int max;
            // The count of §9.10: how many times the operand is written out.
            var count = 1;
            if (c == '{')
            {
                var at = i + 1;
                if (!TryNumber(points, ref at, out min))
                    return Fail("'{' is not a quantifier");
                max = min;
                var unbounded = false;
                if (at < points.Length && points[at] == ',')
                {
                    at++;
                    if (at < points.Length && points[at] == '}')
                        unbounded = true;
                    else if (!TryNumber(points, ref at, out max))
                        return Fail("'{' is not a quantifier");
                }

                if (at == points.Length || points[at] != '}')
                    return Fail("'{' is not a quantifier");
                if (min > MaxRepeat || !unbounded && (max > MaxRepeat || max < min))
                    return Fail("invalid repetition count");
                count = Math.Max(unbounded ? min : max, 1);
                if (!measure.Multiply(count))
                    return Fail("invalid repetition count");
                if (unbounded)
                    max = -1;
                i = at + 1;
            }
            else
            {
                min = c == '+' ? 1 : 0;
                max = c == '?' ? 1 : -1;
                i++;
            }

            if (!measure.Repeat(count))
                return Fail(TooLarge);

            if (options.Extended)
            {
                while (i < points.Length && (IsExtendedSpace(points[i]) || points[i] == '#'))
                    i = SkipExtended(points, i);
            }

            var lazy = false;
            if (i < points.Length && points[i] == '?')
            {
                lazy = true;
                i++;
            }

            return Repeat(frame, min, max, lazy);
        }

        // Repeats the last atom: its instructions min times, then a loop (no maximum) or max - min
        // optional copies. Each copy is an iteration. When the atom can match nothing and there can be
        // several iterations, each marks where it starts, and one that matched nothing ends the
        // repetition, whatever its counts (§9.10): `"ca" | match("(c??){2}a")` captures "" at 1.
        bool Repeat(Frame frame, int min, int max, bool lazy)
        {
            var start = atom;
            var length = ops.Count - start;
            var nullable = frame.Last != false;
            var marked = nullable && (max < 0 || max > 1);
            var iteration = length + (marked ? 2 : 0);
            var size = min * iteration + (max < 0 ? iteration + 2 : (max - min) * (iteration + 1));
            var body = new Body(ops.GetRange(start, length).ToArray(), a.GetRange(start, length).ToArray(), b.GetRange(start, length).ToArray(), marked ? marks++ : -1, start + size);
            ops.RemoveRange(start, length);
            a.RemoveRange(start, length);
            b.RemoveRange(start, length);
            for (var n = 0; n < min; n++)
                Copy(body);
            if (max < 0)
            {
                // again: split (iteration, end); iteration; jump again; end:
                var again = ops.Count;
                Emit(Op.Split, lazy ? body.End - again : 1, lazy ? 1 : body.End - again);
                Copy(body);
                Emit(Op.Jump, again - ops.Count);
            }
            else
            {
                // split (iteration, end); iteration; split (iteration, end); iteration; …; end:
                for (var n = max - min; n > 0; n--)
                {
                    var exit = body.End - ops.Count;
                    Emit(Op.Split, lazy ? exit : 1, lazy ? 1 : exit);
                    Copy(body);
                }
            }

            frame.Last = min == 0 || nullable;
            return true;
        }

        // One iteration of a repetition: the atom, between its mark and the exit of an empty iteration.
        void Copy(Body body)
        {
            if (body.Mark >= 0)
                Emit(Op.Mark, body.Mark);
            for (var k = 0; k < body.Ops.Length; k++)
                Emit(body.Ops[k], body.A[k], body.B[k]);
            if (body.Mark >= 0)
                Emit(Op.EmptyExit, body.Mark, body.End - ops.Count);
        }

        // [...] or [^...]: literals, escapes, ranges of literals and shorthands; items is their number.
        bool TryClass(out int items)
        {
            items = 0;
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
                    return Fail("missing ']'");
                var c = points[at];
                if (c == ']')
                    break;
                if (c == '[')
                    return Fail("unescaped '[' in a class");

                if (c == '-' && at != first && at + 1 < points.Length && points[at + 1] != ']')
                {
                    // A range: the item before must be a single literal, the one after a literal too.
                    if (previousLiteral < 0)
                        return Fail("'-' must be first, last, escaped or between two literals");
                    at++;
                    if (!TryClassLiteral(points, ref at, out var high, out reason))
                        return false;
                    if (high < previousLiteral)
                        return Fail("range out of order");
                    set.Add(previousLiteral, high);
                    previousLiteral = -1;
                    continue;
                }

                if (c == '\\' && at + 1 < points.Length && points[at + 1] is 'd' or 'w' or 's' or 'D' or 'W' or 'S')
                {
                    set.AddShorthand(points[at + 1]);
                    previousLiteral = -1;
                    items++;
                    at += 2;
                    continue;
                }

                if (!TryClassLiteral(points, ref at, out var literal, out reason))
                    return false;
                set.Add(literal, literal);
                previousLiteral = literal;
                items++;
            }

            if (at == first)
                return Fail("empty class");
            // The i flag folds before a negation applies: [^a] matches neither a nor A.
            if (options.IgnoreCase)
                set.AddAsciiCases();
            StartAtom();
            Class(set, negated);
            i = at + 1;
            return true;
        }

        // A class as a set of the program: a negated class is its complement.
        void Class(RangeSet set, bool negated)
        {
            var ranges = (negated ? set.Complement() : set).Ranges;
            var pairs = new int[2 * ranges.Count];
            for (var k = 0; k < ranges.Count; k++)
            {
                pairs[2 * k] = ranges[k].Low;
                pairs[2 * k + 1] = ranges[k].High;
            }

            sets.Add(pairs);
            Emit(Op.Set, sets.Count - 1);
        }

        // A code point; under the i flag an ASCII letter becomes the class of its two cases.
        void Literal(int c)
        {
            StartAtom();
            if (options.IgnoreCase && c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'))
            {
                var set = new RangeSet();
                set.Add(c, c);
                set.AddAsciiCases();
                Class(set, negated: false);
                return;
            }

            Emit(Op.Char, c);
        }

        bool Fail(string text)
        {
            reason = text;
            return false;
        }
    }

    // The instructions of a repeated atom; its mark (-1 when its iterations are not marked) and
    // the index where the repetition ends.
    sealed class Body(Op[] ops, int[] a, int[] b, int mark, int end)
    {
        public Op[] Ops { get; } = ops;
        public int[] A { get; } = a;
        public int[] B { get; } = b;
        public int Mark { get; } = mark;
        public int End { get; } = end;
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

    /// <summary>
    /// The limits of §11 on the part of a pattern read so far: its size, the groups open, and the
    /// counts of the counted quantifiers nested in one another.
    /// </summary>
    sealed class Measure
    {
        // Per open group: the size when it opened.
        readonly List<int> starts = [];
        // For the pattern and per open group: the largest product of the counts nested in it so far.
        readonly List<int> products = [1];
        int size;
        // The last item, the operand of a quantifier: its size and the largest product of the counts nested in it.
        int atom;
        int atomProduct;

        /// <summary>The groups open.</summary>
        public int Depth => starts.Count;

        /// <summary>An item of this size (a literal, a class, an anchor, a '|'); false when the pattern is too large.</summary>
        public bool Add(int itemSize)
        {
            atom = itemSize;
            atomProduct = 1;
            size += itemSize;
            return size <= MaxSize;
        }

        /// <summary>A group opens: it counts 1; false when the pattern is too large.</summary>
        public bool Open()
        {
            starts.Add(size);
            products.Add(1);
            return Add(1);
        }

        /// <summary>A group closes: it is the last item, with all it holds.</summary>
        public void Close()
        {
            atom = size - starts[^1];
            atomProduct = products[^1];
            starts.RemoveAt(starts.Count - 1);
            products.RemoveAt(products.Count - 1);
            products[^1] = Math.Max(products[^1], atomProduct);
        }

        /// <summary>A counted quantifier on the last item; false when the nested counts multiply above <see cref="MaxRepeat"/>.</summary>
        public bool Multiply(int count)
        {
            var product = count * atomProduct;
            products[^1] = Math.Max(products[^1], product);
            return product <= MaxRepeat;
        }

        /// <summary>A quantifier on the last item: it counts 1, and the item once more for each count above 1; false when the pattern is too large.</summary>
        public bool Repeat(int count)
        {
            size += atom * (count - 1) + 1;
            return size <= MaxSize;
        }
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
