using System.Collections.Immutable;
using JQLite.Core.Binding;
using JQLite.Core.Runtime;
using JQLite.Core.Values;
using static JQLite.Core.Builtins.NativeTable;
using Path = JQLite.Core.Runtime.Path;

namespace JQLite.Core.Builtins;

/// <summary>
/// §9.10 <c>test/2</c> and <c>match/2</c>: the flags, the search loop and the match objects, with
/// offsets and lengths in code points. The pattern goes through <see cref="RegexSyntax"/> to the
/// host engine (<see cref="RegexEngine"/>), which works on UTF-16 units.
/// </summary>
static class RegexNatives
{
    public static void Register()
    {
        Add("test", 2, Test);
        Add("match", 2, Match);
    }

    // A search ready to run: the input with its code point map, the compiled pattern and flags.
    sealed class Search(string input, HostPattern pattern, string?[] groups, bool global, bool skipEmpty)
    {
        public string Input { get; } = input;
        public HostPattern Pattern { get; } = pattern;
        public string?[] Groups { get; } = groups;
        public bool Global { get; } = global;
        public bool SkipEmpty { get; } = skipEmpty;
    }

    static bool Test(Interpreter interpreter, JqValue input, JqValue[] arguments, out JqValue result, out JqValue error)
    {
        if (!TryPrepare(input, arguments[0], arguments[1], out var search, out error))
            return Fail(error, out result, out error);
        var found = false;
        Run(search, (_, _) =>
        {
            found = true;
            return false;
        });
        return Ok(JqValue.FromBool(found), out result, out error);
    }

    // match(re; flags): flags outermost, re innermost (re fastest); one output per reported match.
    static Signal? Match(Interpreter interpreter, Env? env, ImmutableArray<Node> arguments, JqValue input, Path? path, Emit emit)
        => interpreter.ForEach(arguments[1], env, input, null, (flags, _) =>
            interpreter.ForEach(arguments[0], env, input, null, (re, _) =>
            {
                if (!TryPrepare(input, re, flags, out var search, out var error))
                    return Interpreter.Fail(error);
                Signal? signal = null;
                Run(search, (spans, map) => (signal = Interpreter.Output(emit, MatchObject(search, spans, map), Path.Of(path))) is null);
                return signal;
            }));

    static bool TryPrepare(JqValue input, JqValue re, JqValue flags, out Search search, out JqValue error)
    {
        search = null!;
        if (input.Kind != JqKind.String)
            return Error(Errors.CannotBeMatched(input), out error);
        if (re.Kind != JqKind.String)
            return Error(Errors.NotAString(re), out error);
        if (flags.Kind is not (JqKind.String or JqKind.Null))
            return Error(Errors.NotAString(flags), out error);

        bool global = false, ignoreCase = false, extended = false, skipEmpty = false, dotAll = false;
        var letters = flags.Kind == JqKind.String ? flags.AsString() : "";
        foreach (var letter in letters)
        {
            switch (letter)
            {
                case 'g': global = true; break;
                case 'i': ignoreCase = true; break;
                case 'x': extended = true; break;
                case 'n': skipEmpty = true; break;
                case 'm' or 'p': dotAll = true; break;
                case 's' or 'l': break;
                default: return Error(Errors.InvalidModifiers(letters), out error);
            }
        }

        if (!RegexSyntax.TryTranslate(re.AsString(), new RegexSyntax.Options(extended, dotAll, ignoreCase), out var translation, out var reason)
            || !RegexEngine.TryCompile(translation.Pattern, translation.Lookaround, out var pattern, out reason))
            return Error(Errors.RegexFailure(reason), out error);

        search = new Search(input.AsString(), pattern, translation.Groups, global, skipEmpty);
        error = default;
        return true;
    }

    /// <summary>The search loop of §9.10, in code points; <paramref name="report"/> returns false to stop.</summary>
    static void Run(Search search, Func<int[], CodePointMap, bool> report)
    {
        var map = new CodePointMap(search.Input);
        var spans = new int[2 * (search.Groups.Length + 1)];
        for (var p = 0; p <= map.Length;)
        {
            if (!RegexEngine.TryMatch(search.Pattern, search.Input, map.Unit(p), spans))
                return;
            var start = map.CodePoint(spans[0]);
            var end = map.CodePoint(spans[1]);
            if (!(search.SkipEmpty && start == end) && (!report(spans, map) || !search.Global))
                return;
            p = end > start ? end : end + 1;
        }
    }

    static JqValue MatchObject(Search search, int[] spans, CodePointMap map)
    {
        var captures = new JqValue[search.Groups.Length];
        for (var g = 0; g < captures.Length; g++)
        {
            var name = search.Groups[g] is { } text ? JqValue.FromString(text) : JqValue.Null;
            var capture = new JqObjectBuilder(4);
            int start = spans[2 * g + 2], end = spans[2 * g + 3];
            if (start == end || start < 0)
            {
                // An empty or absent group has jq's other key order.
                capture.Set("offset", JqValue.FromNumber(start < 0 ? -1 : map.CodePoint(start)));
                capture.Set("string", start < 0 ? JqValue.Null : JqValue.FromString(""));
                capture.Set("length", JqValue.FromNumber(0));
            }
            else
            {
                AddSpan(capture, search.Input, map, start, end);
            }

            capture.Set("name", name);
            captures[g] = JqValue.FromObject(capture.ToObject());
        }

        var match = new JqObjectBuilder(4);
        AddSpan(match, search.Input, map, spans[0], spans[1]);
        match.Set("captures", JqValue.FromArray(JqArray.FromOwned(captures)));
        return JqValue.FromObject(match.ToObject());
    }

    // "offset", "length", "string" of a span given in UTF-16 units.
    static void AddSpan(JqObjectBuilder target, string input, CodePointMap map, int start, int end)
    {
        var offset = map.CodePoint(start);
        target.Set("offset", JqValue.FromNumber(offset));
        target.Set("length", JqValue.FromNumber(map.CodePoint(end) - offset));
        target.Set("string", JqValue.FromString(input[start..end]));
    }

    static bool Error(JqValue message, out JqValue error)
    {
        error = message;
        return false;
    }

    /// <summary>UTF-16 indices to code point indices and back, for a well-formed string.</summary>
    sealed class CodePointMap
    {
        readonly int[]? toCodePoint;
        readonly int[]? toUnit;

        public CodePointMap(string text)
        {
            Length = CodePoints.Length(text);
            if (Length == text.Length)
                return;
            toCodePoint = new int[text.Length + 1];
            toUnit = new int[Length + 1];
            var codePoint = 0;
            for (var i = 0; i < text.Length; i++)
            {
                toCodePoint[i] = codePoint;
                if (!char.IsLowSurrogate(text[i]))
                    toUnit[codePoint++] = i;
            }

            toCodePoint[text.Length] = Length;
            toUnit[Length] = text.Length;
        }

        /// <summary>The number of code points.</summary>
        public int Length { get; }

        public int CodePoint(int unit) => toCodePoint is null ? unit : toCodePoint[unit];

        public int Unit(int codePoint) => toUnit is null ? codePoint : toUnit[codePoint];
    }
}