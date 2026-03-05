using System.Text.Json;
using System.Text.RegularExpressions;

namespace Devlooped;

public sealed class ParameterizedFilter : JqFilter
{
    private static readonly HashSet<string> knownNames = new(StringComparer.Ordinal)
    {
        "has", "contains", "inside", "startswith", "endswith", "ltrimstr", "rtrimstr", "trimstr", "split", "join", "index", "rindex", "indices", "in", "INDEX", "IN", "JOIN", "getpath", "delpaths", "bsearch", "flatten", "combinations", "error", "halt_error",
        "test", "match", "capture", "scan", "splits", "sub", "gsub",
        "select", "map", "map_values", "sort_by", "group_by", "unique_by", "min_by", "max_by", "any", "all", "recurse", "paths", "walk", "del", "path", "pick", "isempty", "add",
        "range", "limit", "skip", "first", "last", "nth", "while", "until", "repeat", "with_entries", "setpath",
        "atan2", "pow", "fmax", "fmin", "fmod", "hypot", "remainder", "fma", "ldexp", "scalbln",
        "strptime", "strftime", "strflocaltime",
    };

    private static readonly string[] knownBuiltinArities =
    [
        "has/1", "contains/1", "inside/1", "startswith/1", "endswith/1", "ltrimstr/1", "rtrimstr/1", "trimstr/1", "split/1", "join/1", "index/1", "rindex/1", "indices/1", "in/1", "INDEX/1", "INDEX/2", "IN/1", "IN/2", "JOIN/2", "getpath/1", "delpaths/1", "bsearch/1", "flatten/1", "combinations/1", "error/1", "halt_error/1",
        "test/1", "test/2", "match/1", "match/2", "capture/1", "capture/2", "scan/1", "scan/2", "split/2", "splits/1", "splits/2", "sub/2", "sub/3", "gsub/2", "gsub/3",
        "select/1", "map/1", "map_values/1", "sort_by/1", "group_by/1", "unique_by/1", "min_by/1", "max_by/1", "any/1", "all/1", "recurse/1", "paths/1", "walk/1", "del/1", "path/1", "pick/1", "isempty/1", "add/1",
        "range/1", "range/2", "range/3", "any/2", "all/2", "recurse/2", "limit/2", "skip/2", "first/1", "last/1", "nth/1", "nth/2", "while/2", "until/2", "repeat/1", "with_entries/1", "setpath/2",
        "atan2/2", "pow/2", "fmax/2", "fmin/2", "fmod/2", "hypot/2", "remainder/2", "fma/3", "ldexp/2", "scalbln/2",
        "strptime/1", "strftime/1", "strflocaltime/1",
    ];

    private readonly string name;
    private readonly JqFilter[] args;
    private JqEnvironment _env = JqEnvironment.Empty;

    public ParameterizedFilter(string name, JqFilter[] args)
    {
        this.name = name;
        this.args = args;
    }

    public string FilterName => name;

    public JqFilter[] FilterArgs => args;

    public static bool IsKnown(string name) => knownNames.Contains(name);

    public static IEnumerable<string> KnownBuiltinArities => knownBuiltinArities;

    public static bool Contains(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.String && b.ValueKind == JsonValueKind.String)
            return (a.GetString() ?? "").Contains(b.GetString() ?? "", StringComparison.Ordinal);

        if (a.ValueKind == JsonValueKind.Array && b.ValueKind == JsonValueKind.Array)
        {
            var left = a.EnumerateArray().ToArray();
            foreach (var required in b.EnumerateArray())
            {
                var found = false;
                foreach (var candidate in left)
                {
                    if (Contains(candidate, required))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                    return false;
            }

            return true;
        }

        if (a.ValueKind == JsonValueKind.Object && b.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in b.EnumerateObject())
            {
                if (!a.TryGetProperty(property.Name, out var candidate))
                    return false;
                if (!Contains(candidate, property.Value))
                    return false;
            }

            return true;
        }

        return StructurallyEqual(a, b);
    }

    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        _env = env;
        return (name, args.Length) switch
        {
            ("has", 1) => EvaluateHas(input),
            ("contains", 1) => EvaluateContains(input),
            ("inside", 1) => EvaluateInside(input),
            ("startswith", 1) => EvaluateStartswith(input),
            ("endswith", 1) => EvaluateEndswith(input),
            ("ltrimstr", 1) => EvaluateTrimstr(input, left: true, right: false),
            ("rtrimstr", 1) => EvaluateTrimstr(input, left: false, right: true),
            ("trimstr", 1) => EvaluateTrimstr(input, left: true, right: true),
            ("split", 1) => EvaluateSplit(input),
            ("join", 1) => EvaluateJoin(input),
            ("index", 1) => EvaluateIndex(input, reverse: false),
            ("rindex", 1) => EvaluateIndex(input, reverse: true),
            ("indices", 1) => EvaluateIndices(input),
            ("in", 1) => EvaluateIn(input),
            ("INDEX", 1) => EvaluateINDEX1(input),
            ("INDEX", 2) => EvaluateINDEX2(input),
            ("IN", 1) => EvaluateIN1(input),
            ("IN", 2) => EvaluateIN2(input),
            ("JOIN", 2) => EvaluateJOIN2(input),
            ("getpath", 1) => EvaluateGetPath(input),
            ("delpaths", 1) => EvaluateDelpaths(input),
            ("bsearch", 1) => EvaluateBsearch(input),
            ("flatten", 1) => EvaluateFlatten(input),
            ("combinations", 1) => EvaluateCombinations(input),
            ("error", 1) => throw new JqException(args[0].Evaluate(input, _env).FirstOrDefault(CreateNullElement())),
            ("halt_error", 1) => throw new JqHaltException(ReadInt(args[0], input, 5)),
            ("test", 1) => EvaluateTest(input),
            ("test", 2) => EvaluateTest(input),
            ("match", 1) => EvaluateMatch(input),
            ("match", 2) => EvaluateMatch(input),
            ("capture", 1) => EvaluateCapture(input),
            ("capture", 2) => EvaluateCapture(input),
            ("scan", 1) => EvaluateScan(input),
            ("scan", 2) => EvaluateScan(input),
            ("split", 2) => EvaluateRegexSplit(input),
            ("splits", 1) => EvaluateSplits(input),
            ("splits", 2) => EvaluateSplits(input),
            ("sub", 2) => EvaluateSub(input),
            ("sub", 3) => EvaluateSub(input),
            ("gsub", 2) => EvaluateGsub(input),
            ("gsub", 3) => EvaluateGsub(input),

            ("select", 1) => EvaluateSelect(input),
            ("map", 1) => EvaluateMap(input),
            ("map_values", 1) => EvaluateMapValues(input),
            ("sort_by", 1) => EvaluateSortBy(input),
            ("group_by", 1) => EvaluateGroupBy(input),
            ("unique_by", 1) => EvaluateUniqueBy(input),
            ("min_by", 1) => EvaluateMinBy(input),
            ("max_by", 1) => EvaluateMaxBy(input),
            ("any", 1) => EvaluateAny(input),
            ("all", 1) => EvaluateAll(input),
            ("recurse", 1) => EvaluateRecurse(input),
            ("paths", 1) => EvaluatePaths(input),
            ("walk", 1) => EvaluateWalk(input),
            ("del", 1) => EvaluateDel(input),
            ("path", 1) => EvaluatePath(input),
            ("pick", 1) => EvaluatePick(input),
            ("isempty", 1) => [CreateBooleanElement(!args[0].Evaluate(input, _env).Any())],
            ("add", 1) => EvaluateAdd(input),

            ("range", 1) => EvaluateRange(input),
            ("range", 2) => EvaluateRange(input),
            ("range", 3) => EvaluateRange(input),
            ("any", 2) => EvaluateAny2(input),
            ("all", 2) => EvaluateAll2(input),
            ("recurse", 2) => EvaluateRecurse2(input),
            ("limit", 2) => EvaluateLimit(input),
            ("skip", 2) => EvaluateSkip(input),
            ("first", 1) => EvaluateFirst(input),
            ("last", 1) => EvaluateLast(input),
            ("nth", 1) => EvaluateNth(input),
            ("nth", 2) => EvaluateNth2(input),
            ("while", 2) => EvaluateWhile(input),
            ("until", 2) => EvaluateUntil(input),
            ("repeat", 1) => EvaluateRepeat(input),
            ("with_entries", 1) => EvaluateWithEntries(input),
            ("setpath", 2) => EvaluateSetpath(input),
            ("atan2", 2) => EvaluateMathBinary(input, Math.Atan2),
            ("pow", 2) => EvaluateMathBinary(input, Math.Pow),
            ("fmax", 2) => EvaluateMathBinary(input, Math.Max),
            ("fmin", 2) => EvaluateMathBinary(input, Math.Min),
            ("fmod", 2) => EvaluateMathBinary(input, static (x, y) => x % y),
            ("hypot", 2) => EvaluateMathBinary(input, static (x, y) => Math.Sqrt(x * x + y * y)),
            ("remainder", 2) => EvaluateMathBinary(input, Math.IEEERemainder),
            ("ldexp", 2) => EvaluateMathBinary(input, static (x, y) => x * Math.Pow(2, (int)y)),
            ("scalbln", 2) => EvaluateMathBinary(input, static (x, y) => x * Math.Pow(2, (int)y)),
            ("fma", 3) => EvaluateMathTernary(input, Math.FusedMultiplyAdd),
            ("strftime", 1) => EvaluateStrftime(input),
            ("strflocaltime", 1) => EvaluateStrflocaltime(input),
            ("strptime", 1) => EvaluateStrptime(input),

            _ => throw new JqException($"Unknown function '{name}/{args.Length}'."),
        };
    }

    private IEnumerable<JsonElement> EvaluateHas(JsonElement input)
    {
        foreach (var key in args[0].Evaluate(input, _env))
            yield return CreateBooleanElement(Has(input, key));
    }

    private static bool Has(JsonElement container, JsonElement key)
    {
        if (container.ValueKind == JsonValueKind.Object && key.ValueKind == JsonValueKind.String)
            return container.TryGetProperty(key.GetString() ?? "", out _);
        if (container.ValueKind == JsonValueKind.Array && key.ValueKind == JsonValueKind.Number && TryReadIndex(key, container.GetArrayLength(), out var index))
            return index >= 0 && index < container.GetArrayLength();

        return false;
    }

    private IEnumerable<JsonElement> EvaluateContains(JsonElement input)
    {
        foreach (var value in args[0].Evaluate(input, _env))
            yield return CreateBooleanElement(Contains(input, value));
    }

    private IEnumerable<JsonElement> EvaluateInside(JsonElement input)
    {
        foreach (var value in args[0].Evaluate(input, _env))
            yield return CreateBooleanElement(Contains(value, input));
    }

    private IEnumerable<JsonElement> EvaluateStartswith(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.String)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not a string");

        var value = input.GetString() ?? "";
        foreach (var prefix in args[0].Evaluate(input, _env))
        {
            if (prefix.ValueKind != JsonValueKind.String)
                throw new JqException($"{GetTypeName(prefix)} ({GetValueText(prefix)}) is not a string");

            yield return CreateBooleanElement(value.StartsWith(prefix.GetString() ?? "", StringComparison.Ordinal));
        }
    }

    private IEnumerable<JsonElement> EvaluateEndswith(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.String)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not a string");

        var value = input.GetString() ?? "";
        foreach (var suffix in args[0].Evaluate(input, _env))
        {
            if (suffix.ValueKind != JsonValueKind.String)
                throw new JqException($"{GetTypeName(suffix)} ({GetValueText(suffix)}) is not a string");

            yield return CreateBooleanElement(value.EndsWith(suffix.GetString() ?? "", StringComparison.Ordinal));
        }
    }

    private IEnumerable<JsonElement> EvaluateTrimstr(JsonElement input, bool left, bool right)
    {
        if (input.ValueKind != JsonValueKind.String)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not a string");

        var source = input.GetString() ?? "";
        foreach (var token in args[0].Evaluate(input, _env))
        {
            if (token.ValueKind != JsonValueKind.String)
                throw new JqException($"{GetTypeName(token)} ({GetValueText(token)}) is not a string");

            var value = source;
            var needle = token.GetString() ?? "";

            if (needle.Length == 0)
            {
                yield return CreateStringElement(value);
                continue;
            }

            if (left && value.StartsWith(needle, StringComparison.Ordinal))
                value = value[needle.Length..];
            if (right && value.EndsWith(needle, StringComparison.Ordinal))
                value = value[..^needle.Length];

            yield return CreateStringElement(value);
        }
    }

    private IEnumerable<JsonElement> EvaluateSplit(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.String)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not a string");

        var source = input.GetString() ?? "";
        foreach (var separator in args[0].Evaluate(input, _env))
        {
            if (separator.ValueKind != JsonValueKind.String)
                throw new JqException($"{GetTypeName(separator)} ({GetValueText(separator)}) is not a string");

            var token = separator.GetString() ?? "";
            var pieces = token == ""
                ? source.EnumerateRunes().Select(static rune => rune.ToString()).ToArray()
                : source.Split(token, StringSplitOptions.None);

            yield return CreateElement(writer =>
            {
                writer.WriteStartArray();
                foreach (var piece in pieces)
                    writer.WriteStringValue(piece);
                writer.WriteEndArray();
            });
        }
    }

    private IEnumerable<JsonElement> EvaluateJoin(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an array");

        foreach (var separator in args[0].Evaluate(input, _env))
        {
            if (separator.ValueKind != JsonValueKind.String)
                throw new JqException($"{GetTypeName(separator)} ({GetValueText(separator)}) is not a string");

            var token = separator.GetString() ?? "";
            var values = new List<string>();
            foreach (var item in input.EnumerateArray())
            {
                values.Add(item.ValueKind switch
                {
                    JsonValueKind.Null => "",
                    JsonValueKind.String => item.GetString() ?? "",
                    JsonValueKind.Number => item.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => throw new JqException($"Cannot join {GetTypeName(item)} values"),
                });
            }

            yield return CreateStringElement(string.Join(token, values));
        }
    }

    private IEnumerable<JsonElement> EvaluateTest(JsonElement input)
    {
        var source = ReadRegexInput(input);
        foreach (var regex in EnumerateRegexes(input, 0, args.Length == 2 ? 1 : null))
            yield return CreateBooleanElement(FindFirstRegexMatch(source, regex).Success);
    }

    private IEnumerable<JsonElement> EvaluateMatch(JsonElement input)
    {
        var source = ReadRegexInput(input);
        foreach (var regex in EnumerateRegexes(input, 0, args.Length == 2 ? 1 : null))
        {
            if (regex.Global)
            {
                foreach (var match in EnumerateRegexMatches(source, regex))
                    yield return CreateRegexMatchObject(regex.Regex, match);

                continue;
            }

            var first = FindFirstRegexMatch(source, regex);
            if (!first.Success)
                throw new JqException("input does not match regex");

            yield return CreateRegexMatchObject(regex.Regex, first);
        }
    }

    private IEnumerable<JsonElement> EvaluateCapture(JsonElement input)
    {
        var source = ReadRegexInput(input);
        foreach (var regex in EnumerateRegexes(input, 0, args.Length == 2 ? 1 : null))
        {
            var match = FindFirstRegexMatch(source, regex);
            if (!match.Success)
                throw new JqException("input does not match regex");

            yield return CreateCaptureObject(regex.Regex, match);
        }
    }

    private IEnumerable<JsonElement> EvaluateScan(JsonElement input)
    {
        var source = ReadRegexInput(input);
        foreach (var regex in EnumerateRegexes(input, 0, args.Length == 2 ? 1 : null))
        {
            var groupCount = regex.Regex.GetGroupNumbers().Count(number => number != 0);
            foreach (var match in EnumerateRegexMatches(source, new RegexSpec(regex.Regex, true, regex.IgnoreEmpty)))
            {
                if (groupCount == 0)
                {
                    yield return CreateStringElement(match.Value);
                    continue;
                }

                yield return CreateElement(writer =>
                {
                    writer.WriteStartArray();
                    for (var i = 1; i < match.Groups.Count; i++)
                    {
                        var group = match.Groups[i];
                        if (group.Success)
                            writer.WriteStringValue(group.Value);
                        else
                            writer.WriteNullValue();
                    }
                    writer.WriteEndArray();
                });
            }
        }
    }

    private IEnumerable<JsonElement> EvaluateRegexSplit(JsonElement input)
    {
        var source = ReadRegexInput(input);
        foreach (var regex in EnumerateRegexes(input, 0, 1))
        {
            var pieces = regex.Regex.Split(source);
            yield return CreateElement(writer =>
            {
                writer.WriteStartArray();
                foreach (var piece in pieces)
                    writer.WriteStringValue(piece);
                writer.WriteEndArray();
            });
        }
    }

    private IEnumerable<JsonElement> EvaluateSplits(JsonElement input)
    {
        var source = ReadRegexInput(input);
        foreach (var regex in EnumerateRegexes(input, 0, args.Length == 2 ? 1 : null))
        {
            foreach (var piece in regex.Regex.Split(source))
                yield return CreateStringElement(piece);
        }
    }

    private IEnumerable<JsonElement> EvaluateSub(JsonElement input)
    {
        var source = ReadRegexInput(input);
        foreach (var regex in EnumerateRegexes(input, 0, args.Length == 3 ? 2 : null))
        {
            var match = FindFirstRegexMatch(source, regex);
            if (!match.Success)
            {
                yield return CreateStringElement(source);
                continue;
            }

            var matchObject = CreateRegexMatchObject(regex.Regex, match, promoteNamedCaptures: true);
            var prefix = source[..match.Index];
            var suffix = source[(match.Index + match.Length)..];
            foreach (var replacement in EnumerateReplacementStrings(matchObject))
                yield return CreateStringElement(prefix + replacement + suffix);
        }
    }

    private IEnumerable<JsonElement> EvaluateGsub(JsonElement input)
    {
        var source = ReadRegexInput(input);
        foreach (var regex in EnumerateRegexes(input, 0, args.Length == 3 ? 2 : null))
        {
            var matches = EnumerateRegexMatches(source, new RegexSpec(regex.Regex, true, regex.IgnoreEmpty)).ToArray();
            if (matches.Length == 0)
            {
                yield return CreateStringElement(source);
                continue;
            }

            var outputs = new List<string> { "" };
            var position = 0;
            foreach (var match in matches)
            {
                var literal = source.Substring(position, match.Index - position);
                var replacements = EnumerateReplacementStrings(CreateRegexMatchObject(regex.Regex, match, promoteNamedCaptures: true)).ToArray();
                if (replacements.Length == 0)
                {
                    outputs.Clear();
                    break;
                }

                var next = new List<string>(outputs.Count * replacements.Length);
                foreach (var output in outputs)
                {
                    foreach (var replacement in replacements)
                        next.Add(output + literal + replacement);
                }

                outputs = next;
                position = match.Index + match.Length;
            }

            var suffix = source[position..];
            foreach (var output in outputs)
                yield return CreateStringElement(output + suffix);
        }
    }

    private string ReadRegexInput(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.String)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not a string");

        return input.GetString() ?? "";
    }

    private IEnumerable<RegexSpec> EnumerateRegexes(JsonElement input, int patternArgIndex, int? flagsArgIndex)
    {
        if (flagsArgIndex is null)
        {
            foreach (var value in args[patternArgIndex].Evaluate(input, _env))
                yield return CompileRegex(ReadRegexPattern(value), "");

            yield break;
        }

        foreach (var patternValue in args[patternArgIndex].Evaluate(input, _env))
        {
            var pattern = ReadRegexPattern(patternValue);
            foreach (var flagsValue in args[flagsArgIndex.Value].Evaluate(input, _env))
                yield return CompileRegex(pattern, ReadRegexFlags(flagsValue));
        }
    }

    private static string ReadRegexPattern(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new JqException($"{GetTypeName(value)} ({GetValueText(value)}) is not a string");

        return value.GetString() ?? "";
    }

    private static string ReadRegexFlags(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return "";
        if (value.ValueKind != JsonValueKind.String)
            throw new JqException($"{GetTypeName(value)} ({GetValueText(value)}) is not a string");

        return value.GetString() ?? "";
    }

    private static RegexSpec CompileRegex(string pattern, string flags)
    {
        var options = RegexOptions.CultureInvariant;
        var global = false;
        var ignoreEmpty = false;
        foreach (var flag in flags)
        {
            switch (flag)
            {
                case 'i':
                    options |= RegexOptions.IgnoreCase;
                    break;
                case 'x':
                    options |= RegexOptions.IgnorePatternWhitespace;
                    break;
                case 's':
                    options |= RegexOptions.Singleline;
                    break;
                case 'm':
                    options |= RegexOptions.Multiline;
                    break;
                case 'g':
                    global = true;
                    break;
                case 'n':
                    ignoreEmpty = true;
                    break;
            }
        }

        try
        {
            return new RegexSpec(new Regex(pattern, options), global, ignoreEmpty);
        }
        catch (ArgumentException ex)
        {
            throw new JqException(ex.Message);
        }
    }

    private static Match FindFirstRegexMatch(string source, RegexSpec spec)
    {
        var position = 0;
        while (position <= source.Length)
        {
            var match = spec.Regex.Match(source, position);
            if (!match.Success)
                return match;

            if (!spec.IgnoreEmpty || match.Length > 0)
                return match;

            position = match.Index + 1;
        }

        return Match.Empty;
    }

    private static IEnumerable<Match> EnumerateRegexMatches(string source, RegexSpec spec)
    {
        if (!spec.Global)
        {
            var first = FindFirstRegexMatch(source, spec);
            if (first.Success)
                yield return first;
            yield break;
        }

        var position = 0;
        while (position <= source.Length)
        {
            var match = spec.Regex.Match(source, position);
            if (!match.Success)
                yield break;

            if (!spec.IgnoreEmpty || match.Length > 0)
                yield return match;

            position = match.Length == 0 ? match.Index + 1 : match.Index + match.Length;
        }
    }

    private IEnumerable<string> EnumerateReplacementStrings(JsonElement matchObject)
    {
        foreach (var value in args[1].Evaluate(matchObject, _env))
        {
            if (value.ValueKind != JsonValueKind.String)
                throw new JqException($"{GetTypeName(value)} ({GetValueText(value)}) is not a string");

            yield return value.GetString() ?? "";
        }
    }

    private static JsonElement CreateRegexMatchObject(Regex regex, Match match, bool promoteNamedCaptures = false)
    {
        return CreateElement(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("offset", match.Index);
            writer.WriteNumber("length", match.Length);
            writer.WriteString("string", match.Value);
            writer.WritePropertyName("captures");
            writer.WriteStartArray();
            for (var i = 1; i < match.Groups.Count; i++)
            {
                var group = match.Groups[i];
                var name = regex.GroupNameFromNumber(i);
                var named = !int.TryParse(name, out _);

                writer.WriteStartObject();
                writer.WriteNumber("offset", group.Success ? group.Index : -1);
                writer.WriteNumber("length", group.Success ? group.Length : 0);
                if (group.Success)
                    writer.WriteString("string", group.Value);
                else
                    writer.WriteNull("string");
                if (named)
                    writer.WriteString("name", name);
                else
                    writer.WriteNull("name");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (promoteNamedCaptures)
            {
                foreach (var groupName in regex.GetGroupNames())
                {
                    if (int.TryParse(groupName, out _))
                        continue;

                    var group = match.Groups[groupName];
                    writer.WritePropertyName(groupName);
                    if (group.Success)
                        writer.WriteStringValue(group.Value);
                    else
                        writer.WriteNullValue();
                }
            }
            writer.WriteEndObject();
        });
    }

    private static JsonElement CreateCaptureObject(Regex regex, Match match)
    {
        return CreateElement(writer =>
        {
            writer.WriteStartObject();
            foreach (var name in regex.GetGroupNames())
            {
                if (name == "0" || int.TryParse(name, out _))
                    continue;

                var group = match.Groups[name];
                writer.WritePropertyName(name);
                if (group.Success)
                    writer.WriteStringValue(group.Value);
                else
                    writer.WriteNullValue();
            }
            writer.WriteEndObject();
        });
    }

    private readonly record struct RegexSpec(Regex Regex, bool Global, bool IgnoreEmpty);

    private IEnumerable<JsonElement> EvaluateIndex(JsonElement input, bool reverse)
    {
        foreach (var needle in args[0].Evaluate(input, _env))
        {
            if (input.ValueKind == JsonValueKind.String)
            {
                if (needle.ValueKind != JsonValueKind.String)
                    throw new JqException($"{GetTypeName(needle)} ({GetValueText(needle)}) is not a string");

                var source = input.GetString() ?? "";
                var token = needle.GetString() ?? "";
                var index = reverse
                    ? source.LastIndexOf(token, StringComparison.Ordinal)
                    : source.IndexOf(token, StringComparison.Ordinal);

                if (index >= 0)
                    yield return CreateNumberElement(index);
                continue;
            }

            if (input.ValueKind == JsonValueKind.Array)
            {
                var index = FindIndex(input.EnumerateArray().ToArray(), needle, reverse);
                if (index >= 0)
                    yield return CreateNumberElement(index);
                continue;
            }

            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) cannot be indexed");
        }
    }

    private static int FindIndex(JsonElement[] haystack, JsonElement needle, bool reverse)
    {
        var targetArray = needle.ValueKind == JsonValueKind.Array ? needle.EnumerateArray().ToArray() : null;
        if (targetArray is not null)
        {
            var last = haystack.Length - targetArray.Length;
            if (targetArray.Length == 0)
                return reverse ? haystack.Length : 0;

            if (!reverse)
            {
                for (var i = 0; i <= last; i++)
                {
                    if (MatchesAt(haystack, targetArray, i))
                        return i;
                }
            }
            else
            {
                for (var i = last; i >= 0; i--)
                {
                    if (MatchesAt(haystack, targetArray, i))
                        return i;
                }
            }

            return -1;
        }

        if (!reverse)
        {
            for (var i = 0; i < haystack.Length; i++)
            {
                if (StructurallyEqual(haystack[i], needle))
                    return i;
            }
        }
        else
        {
            for (var i = haystack.Length - 1; i >= 0; i--)
            {
                if (StructurallyEqual(haystack[i], needle))
                    return i;
            }
        }

        return -1;
    }

    private static bool MatchesAt(JsonElement[] haystack, JsonElement[] target, int start)
    {
        for (var i = 0; i < target.Length; i++)
        {
            if (!StructurallyEqual(haystack[start + i], target[i]))
                return false;
        }

        return true;
    }

    private IEnumerable<JsonElement> EvaluateIndices(JsonElement input)
    {
        foreach (var needle in args[0].Evaluate(input, _env))
        {
            if (input.ValueKind == JsonValueKind.String)
            {
                if (needle.ValueKind != JsonValueKind.String)
                    throw new JqException($"{GetTypeName(needle)} ({GetValueText(needle)}) is not a string");

                var source = input.GetString() ?? "";
                var token = needle.GetString() ?? "";
                var positions = new List<int>();
                if (token == "")
                {
                    for (var i = 0; i <= source.Length; i++)
                        positions.Add(i);
                }
                else
                {
                    var start = 0;
                    while (start <= source.Length - token.Length)
                    {
                        var found = source.IndexOf(token, start, StringComparison.Ordinal);
                        if (found < 0)
                            break;
                        positions.Add(found);
                        start = found + 1;
                    }
                }

                yield return CreateElement(writer =>
                {
                    writer.WriteStartArray();
                    foreach (var position in positions)
                        writer.WriteNumberValue(position);
                    writer.WriteEndArray();
                });
                continue;
            }

            if (input.ValueKind == JsonValueKind.Array)
            {
                var haystack = input.EnumerateArray().ToArray();
                var positions = new List<int>();
                var targetArray = needle.ValueKind == JsonValueKind.Array ? needle.EnumerateArray().ToArray() : null;
                if (targetArray is not null)
                {
                    if (targetArray.Length == 0)
                    {
                        for (var i = 0; i <= haystack.Length; i++)
                            positions.Add(i);
                    }
                    else
                    {
                        for (var i = 0; i <= haystack.Length - targetArray.Length; i++)
                        {
                            if (MatchesAt(haystack, targetArray, i))
                                positions.Add(i);
                        }
                    }
                }
                else
                {
                    for (var i = 0; i < haystack.Length; i++)
                    {
                        if (StructurallyEqual(haystack[i], needle))
                            positions.Add(i);
                    }
                }

                yield return CreateElement(writer =>
                {
                    writer.WriteStartArray();
                    foreach (var position in positions)
                        writer.WriteNumberValue(position);
                    writer.WriteEndArray();
                });
                continue;
            }

            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) cannot be indexed");
        }
    }

    private IEnumerable<JsonElement> EvaluateIn(JsonElement input)
    {
        foreach (var value in args[0].Evaluate(input, _env))
            yield return CreateBooleanElement(Has(value, input));
    }

    private IEnumerable<JsonElement> EvaluateINDEX1(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"Cannot index {GetTypeName(input)}: not an array");

        yield return BuildIndexObject(input.EnumerateArray(), args[0]);
    }

    private IEnumerable<JsonElement> EvaluateINDEX2(JsonElement input)
    {
        var stream = args[0].Evaluate(input, _env);
        yield return BuildIndexObject(stream, args[1]);
    }

    private JsonElement BuildIndexObject(IEnumerable<JsonElement> stream, JqFilter keySelector)
    {
        var valuesByKey = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var value in stream)
        {
            var key = keySelector.Evaluate(value, _env).FirstOrDefault(CreateNullElement());
            var propertyName = key.ValueKind == JsonValueKind.String
                ? key.GetString() ?? ""
                : key.GetRawText();
            valuesByKey[propertyName] = value;
        }

        return CreateElement(writer =>
        {
            writer.WriteStartObject();
            foreach (var entry in valuesByKey)
            {
                writer.WritePropertyName(entry.Key);
                entry.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        });
    }

    private IEnumerable<JsonElement> EvaluateIN1(JsonElement input)
    {
        var found = false;
        foreach (var candidate in args[0].Evaluate(input, _env))
        {
            if (!StructurallyEqual(input, candidate))
                continue;

            found = true;
            break;
        }

        yield return CreateBooleanElement(found);
    }

    private IEnumerable<JsonElement> EvaluateIN2(JsonElement input)
    {
        var source = args[0].Evaluate(input, _env).ToArray();
        var stream = args[1].Evaluate(input, _env).ToArray();
        var found = source.Any(left => stream.Any(right => StructurallyEqual(left, right)));
        yield return CreateBooleanElement(found);
    }

    private IEnumerable<JsonElement> EvaluateJOIN2(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"Cannot join {GetTypeName(input)}: not an array");

        var index = args[0].Evaluate(input, _env).FirstOrDefault(CreateNullElement());
        if (index.ValueKind != JsonValueKind.Object)
            throw new JqException($"{GetTypeName(index)} ({GetValueText(index)}) is not an object");

        yield return CreateElement(writer =>
        {
            writer.WriteStartArray();
            foreach (var value in input.EnumerateArray())
            {
                var key = args[1].Evaluate(value, _env).FirstOrDefault(CreateNullElement());
                var propertyName = key.ValueKind == JsonValueKind.String
                    ? key.GetString() ?? ""
                    : key.GetRawText();

                writer.WriteStartArray();
                value.WriteTo(writer);
                if (index.TryGetProperty(propertyName, out var match))
                    match.WriteTo(writer);
                else
                    writer.WriteNullValue();
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        });
    }

    private IEnumerable<JsonElement> EvaluateGetPath(JsonElement input)
    {
        foreach (var path in args[0].Evaluate(input, _env))
        {
            var pathArray = PathResolver.ParsePath(path);
            if (PathResolver.TryGetPathValue(input, pathArray, out var value))
                yield return value;
            else
                yield return CreateNullElement();
        }
    }

    private IEnumerable<JsonElement> EvaluateDelpaths(JsonElement input)
    {
        var result = input;
        foreach (var value in args[0].Evaluate(input, _env))
        {
            if (value.ValueKind == JsonValueKind.Array &&
                value.EnumerateArray().All(static item => item.ValueKind == JsonValueKind.Array))
            {
                foreach (var path in value.EnumerateArray())
                    result = PathResolver.DeletePathValue(result, PathResolver.ParsePath(path));
            }
            else
            {
                result = PathResolver.DeletePathValue(result, PathResolver.ParsePath(value));
            }
        }

        yield return result;
    }

    private IEnumerable<JsonElement> EvaluateBsearch(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an array");

        var values = input.EnumerateArray().ToArray();
        foreach (var needle in args[0].Evaluate(input, _env))
        {
            var low = 0;
            var high = values.Length - 1;
            while (low <= high)
            {
                var mid = low + ((high - low) / 2);
                var compared = CompareElements(values[mid], needle);
                if (compared == 0)
                {
                    yield return CreateNumberElement(mid);
                    goto Done;
                }

                if (compared < 0)
                    low = mid + 1;
                else
                    high = mid - 1;
            }

            yield return CreateNumberElement(-(low + 1));
        Done:
            continue;
        }
    }

    private IEnumerable<JsonElement> EvaluateFlatten(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) cannot be flattened");

        foreach (var depth in args[0].Evaluate(input, _env))
        {
            if (depth.ValueKind != JsonValueKind.Number)
                throw new JqException($"{GetTypeName(depth)} ({GetValueText(depth)}) is not a number");

            var values = new List<JsonElement>();
            Flatten(input, (int)Math.Floor(depth.GetDouble()), values);
            yield return CreateElement(writer =>
            {
                writer.WriteStartArray();
                foreach (var value in values)
                    value.WriteTo(writer);
                writer.WriteEndArray();
            });
        }
    }

    private static void Flatten(JsonElement input, int depth, List<JsonElement> values)
    {
        foreach (var value in input.EnumerateArray())
        {
            if (depth > 0 && value.ValueKind == JsonValueKind.Array)
                Flatten(value, depth - 1, values);
            else
                values.Add(value);
        }
    }

    private IEnumerable<JsonElement> EvaluateCombinations(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an array");

        var values = input.EnumerateArray().ToArray();
        foreach (var count in args[0].Evaluate(input, _env))
        {
            if (count.ValueKind != JsonValueKind.Number)
                throw new JqException($"{GetTypeName(count)} ({GetValueText(count)}) is not a number");

            var n = (int)Math.Floor(count.GetDouble());
            if (n < 0)
                continue;

            foreach (var combination in Power(values, n, []))
                yield return combination;
        }
    }

    private static IEnumerable<JsonElement> Power(JsonElement[] values, int n, List<JsonElement> current)
    {
        if (n == 0)
        {
            yield return CreateElement(writer =>
            {
                writer.WriteStartArray();
                foreach (var value in current)
                    value.WriteTo(writer);
                writer.WriteEndArray();
            });
            yield break;
        }

        foreach (var value in values)
        {
            current.Add(value);
            foreach (var nested in Power(values, n - 1, current))
                yield return nested;
            current.RemoveAt(current.Count - 1);
        }
    }

    private IEnumerable<JsonElement> EvaluateSelect(JsonElement input)
    {
        foreach (var value in args[0].Evaluate(input, _env))
        {
            if (IsTruthy(value))
                yield return input;
        }
    }

    private IEnumerable<JsonElement> EvaluateMap(JsonElement input)
    {
        var values = new List<JsonElement>();
        if (input.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in input.EnumerateArray())
            {
                foreach (var value in args[0].Evaluate(element, _env))
                    values.Add(value);
            }
        }
        else if (input.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in input.EnumerateObject())
            {
                foreach (var value in args[0].Evaluate(property.Value, _env))
                    values.Add(value);
            }
        }
        else
        {
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is neither array nor object");
        }

        yield return CreateElement(writer =>
        {
            writer.WriteStartArray();
            foreach (var value in values)
                value.WriteTo(writer);
            writer.WriteEndArray();
        });
    }

    private IEnumerable<JsonElement> EvaluateMapValues(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Array)
        {
            var values = new List<JsonElement>();
            foreach (var element in input.EnumerateArray())
            {
                var first = args[0].Evaluate(element, _env).ToArray();
                if (first.Length > 0)
                    values.Add(first[0]);
            }

            yield return CreateElement(writer =>
            {
                writer.WriteStartArray();
                foreach (var value in values)
                    value.WriteTo(writer);
                writer.WriteEndArray();
            });
            yield break;
        }

        if (input.ValueKind == JsonValueKind.Object)
        {
            var values = new List<(string Key, JsonElement Value)>();
            foreach (var property in input.EnumerateObject())
            {
                var first = args[0].Evaluate(property.Value, _env).ToArray();
                if (first.Length > 0)
                    values.Add((property.Name, first[0]));
            }

            yield return CreateElement(writer =>
            {
                writer.WriteStartObject();
                foreach (var value in values)
                {
                    writer.WritePropertyName(value.Key);
                    value.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            });
            yield break;
        }

        throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is neither array nor object");
    }

    private IEnumerable<JsonElement> EvaluateSortBy(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an array");

        var values = input.EnumerateArray().Select(item =>
            (Item: item, Key: args[0].Evaluate(item, _env).FirstOrDefault(CreateNullElement()))).ToArray();

        Array.Sort(values, static (left, right) => CompareElements(left.Key, right.Key));
        yield return CreateElement(writer =>
        {
            writer.WriteStartArray();
            foreach (var value in values)
                value.Item.WriteTo(writer);
            writer.WriteEndArray();
        });
    }

    private IEnumerable<JsonElement> EvaluateGroupBy(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an array");

        var values = input.EnumerateArray().Select(item =>
            (Item: item, Key: args[0].Evaluate(item, _env).FirstOrDefault(CreateNullElement()))).ToArray();
        Array.Sort(values, static (left, right) => CompareElements(left.Key, right.Key));

        yield return CreateElement(writer =>
        {
            writer.WriteStartArray();
            for (var i = 0; i < values.Length;)
            {
                writer.WriteStartArray();
                var key = values[i].Key;
                while (i < values.Length && StructurallyEqual(values[i].Key, key))
                {
                    values[i].Item.WriteTo(writer);
                    i++;
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        });
    }

    private IEnumerable<JsonElement> EvaluateUniqueBy(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an array");

        var values = input.EnumerateArray().Select(item =>
            (Item: item, Key: args[0].Evaluate(item, _env).FirstOrDefault(CreateNullElement()))).ToArray();
        Array.Sort(values, static (left, right) => CompareElements(left.Key, right.Key));

        yield return CreateElement(writer =>
        {
            writer.WriteStartArray();
            JsonElement? previous = null;
            foreach (var value in values)
            {
                if (previous is not null && StructurallyEqual(previous.Value, value.Key))
                    continue;

                value.Item.WriteTo(writer);
                previous = value.Key;
            }
            writer.WriteEndArray();
        });
    }

    private IEnumerable<JsonElement> EvaluateMinBy(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an array");

        var values = input.EnumerateArray().ToArray();
        if (values.Length == 0)
        {
            yield return CreateNullElement();
            yield break;
        }

        var selected = values[0];
        var selectedKey = args[0].Evaluate(values[0], _env).FirstOrDefault(CreateNullElement());
        for (var i = 1; i < values.Length; i++)
        {
            var key = args[0].Evaluate(values[i], _env).FirstOrDefault(CreateNullElement());
            if (CompareElements(key, selectedKey) < 0)
            {
                selected = values[i];
                selectedKey = key;
            }
        }

        yield return selected;
    }

    private IEnumerable<JsonElement> EvaluateMaxBy(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an array");

        var values = input.EnumerateArray().ToArray();
        if (values.Length == 0)
        {
            yield return CreateNullElement();
            yield break;
        }

        var selected = values[0];
        var selectedKey = args[0].Evaluate(values[0], _env).FirstOrDefault(CreateNullElement());
        for (var i = 1; i < values.Length; i++)
        {
            var key = args[0].Evaluate(values[i], _env).FirstOrDefault(CreateNullElement());
            if (CompareElements(key, selectedKey) > 0)
            {
                selected = values[i];
                selectedKey = key;
            }
        }

        yield return selected;
    }

    private IEnumerable<JsonElement> EvaluateAny(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an array");

        foreach (var value in input.EnumerateArray())
        {
            if (args[0].Evaluate(value, _env).Any(IsTruthy))
            {
                yield return CreateBooleanElement(true);
                yield break;
            }
        }

        yield return CreateBooleanElement(false);
    }

    private IEnumerable<JsonElement> EvaluateAll(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an array");

        foreach (var value in input.EnumerateArray())
        {
            if (!args[0].Evaluate(value, _env).Any(IsTruthy))
            {
                yield return CreateBooleanElement(false);
                yield break;
            }
        }

        yield return CreateBooleanElement(true);
    }

    private IEnumerable<JsonElement> EvaluateRecurse(JsonElement input)
    {
        foreach (var value in Recurse1(input, args[0]))
            yield return value;
    }

    private IEnumerable<JsonElement> Recurse1(JsonElement value, JqFilter next)
    {
        yield return value;

        JsonElement[] outputs;
        try
        {
            outputs = next.Evaluate(value, _env).ToArray();
        }
        catch (JqException)
        {
            yield break;
        }

        foreach (var output in outputs)
        {
            foreach (var nested in Recurse1(output, next))
                yield return nested;
        }
    }

    private IEnumerable<JsonElement> EvaluatePaths(JsonElement input)
    {
        foreach (var path in EnumerateAllPaths(input, []))
        {
            if (path.Length == 0)
                continue;

            if (PathResolver.TryGetPathValue(input, path, out var value) && args[0].Evaluate(value, _env).Any(IsTruthy))
                yield return PathResolver.CreatePathValue(path);
        }
    }

    private static IEnumerable<JsonElement[]> EnumerateAllPaths(JsonElement input, JsonElement[] prefix)
    {
        if (input.ValueKind == JsonValueKind.Array)
        {
            for (var i = 0; i < input.GetArrayLength(); i++)
            {
                var next = prefix.Concat([CreateNumberElement(i)]).ToArray();
                yield return next;
                foreach (var nested in EnumerateAllPaths(input[i], next))
                    yield return nested;
            }
            yield break;
        }

        if (input.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in input.EnumerateObject())
            {
                var next = prefix.Concat([CreateStringElement(property.Name)]).ToArray();
                yield return next;
                foreach (var nested in EnumerateAllPaths(property.Value, next))
                    yield return nested;
            }
        }
    }

    private IEnumerable<JsonElement> EvaluateWalk(JsonElement input)
    {
        yield return WalkCore(input, args[0]);
    }

    private JsonElement WalkCore(JsonElement input, JqFilter f)
    {
        if (input.ValueKind == JsonValueKind.Array)
        {
            var values = new List<JsonElement>();
            foreach (var item in input.EnumerateArray())
                values.Add(WalkCore(item, f));

            var mapped = CreateElement(writer =>
            {
                writer.WriteStartArray();
                foreach (var value in values)
                    value.WriteTo(writer);
                writer.WriteEndArray();
            });

            return f.Evaluate(mapped, _env).FirstOrDefault(CreateNullElement());
        }

        if (input.ValueKind == JsonValueKind.Object)
        {
            var values = new List<(string Name, JsonElement Value)>();
            foreach (var property in input.EnumerateObject())
                values.Add((property.Name, WalkCore(property.Value, f)));

            var mapped = CreateElement(writer =>
            {
                writer.WriteStartObject();
                foreach (var value in values)
                {
                    writer.WritePropertyName(value.Name);
                    value.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            });

            return f.Evaluate(mapped, _env).FirstOrDefault(CreateNullElement());
        }

        return f.Evaluate(input, _env).FirstOrDefault(CreateNullElement());
    }

    private IEnumerable<JsonElement> EvaluateDel(JsonElement input)
    {
        var result = input;
        foreach (var path in PathResolver.GetPaths(args[0], input, _env))
            result = PathResolver.DeletePathValue(result, path);
        yield return result;
    }

    private IEnumerable<JsonElement> EvaluatePath(JsonElement input)
    {
        foreach (var path in PathResolver.GetPaths(args[0], input, _env))
            yield return PathResolver.CreatePathValue(path);
    }

    private IEnumerable<JsonElement> EvaluatePick(JsonElement input)
    {
        var result = CreateNullElement();
        foreach (var path in PathResolver.GetPaths(args[0], input, _env))
        {
            if (!PathResolver.TryGetPathValue(input, path, out var value))
                value = CreateNullElement();

            result = PathResolver.SetPathValue(result, path, value);
        }

        yield return result;
    }

    private IEnumerable<JsonElement> EvaluateAdd(JsonElement input)
    {
        var values = args[0].Evaluate(input, _env).ToArray();
        if (values.Length == 0)
        {
            yield return CreateNullElement();
            yield break;
        }

        var current = values[0];
        for (var i = 1; i < values.Length; i++)
            current = Add(current, values[i]);

        yield return current;
    }

    private static JsonElement Add(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Null)
            return right;
        if (right.ValueKind == JsonValueKind.Null)
            return left;

        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number)
            return CreateNumberElement(left.GetDouble() + right.GetDouble());

        if (left.ValueKind == JsonValueKind.String && right.ValueKind == JsonValueKind.String)
            return CreateStringElement((left.GetString() ?? "") + (right.GetString() ?? ""));

        if (left.ValueKind == JsonValueKind.Array && right.ValueKind == JsonValueKind.Array)
        {
            return CreateElement(writer =>
            {
                writer.WriteStartArray();
                foreach (var item in left.EnumerateArray())
                    item.WriteTo(writer);
                foreach (var item in right.EnumerateArray())
                    item.WriteTo(writer);
                writer.WriteEndArray();
            });
        }

        if (left.ValueKind == JsonValueKind.Object && right.ValueKind == JsonValueKind.Object)
        {
            return CreateElement(writer =>
            {
                writer.WriteStartObject();
                var rightNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in right.EnumerateObject())
                    rightNames.Add(property.Name);

                foreach (var property in left.EnumerateObject())
                {
                    if (rightNames.Contains(property.Name))
                        continue;

                    writer.WritePropertyName(property.Name);
                    property.Value.WriteTo(writer);
                }

                foreach (var property in right.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    property.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            });
        }

        throw new JqException($"{GetTypeName(left)} ({GetValueText(left)}) and {GetTypeName(right)} ({GetValueText(right)}) cannot be added");
    }

    private IEnumerable<JsonElement> EvaluateRange(JsonElement input)
    {
        var start = 0d;
        var end = 0d;
        var by = 1d;

        if (args.Length == 1)
        {
            foreach (var value in args[0].Evaluate(input, _env))
            {
                end = ReadNumber(value);
                foreach (var result in Range(start, end, by))
                    yield return CreateNumberElement(result);
            }
            yield break;
        }

        foreach (var from in args[0].Evaluate(input, _env))
        {
            start = ReadNumber(from);
            foreach (var to in args[1].Evaluate(input, _env))
            {
                end = ReadNumber(to);
                if (args.Length == 2)
                {
                    foreach (var result in Range(start, end, 1))
                        yield return CreateNumberElement(result);
                    continue;
                }

                foreach (var step in args[2].Evaluate(input, _env))
                {
                    by = ReadNumber(step);
                    foreach (var result in Range(start, end, by))
                        yield return CreateNumberElement(result);
                }
            }
        }
    }

    private static IEnumerable<double> Range(double start, double end, double by)
    {
        if (by == 0)
            yield break;

        if (by > 0)
        {
            for (var i = start; i < end; i += by)
                yield return i;
            yield break;
        }

        for (var i = start; i > end; i += by)
            yield return i;
    }

    private IEnumerable<JsonElement> EvaluateAny2(JsonElement input)
    {
        foreach (var value in args[0].Evaluate(input, _env))
        {
            if (args[1].Evaluate(value, _env).Any(IsTruthy))
            {
                yield return CreateBooleanElement(true);
                yield break;
            }
        }

        yield return CreateBooleanElement(false);
    }

    private IEnumerable<JsonElement> EvaluateAll2(JsonElement input)
    {
        foreach (var value in args[0].Evaluate(input, _env))
        {
            if (!args[1].Evaluate(value, _env).Any(IsTruthy))
            {
                yield return CreateBooleanElement(false);
                yield break;
            }
        }

        yield return CreateBooleanElement(true);
    }

    private IEnumerable<JsonElement> EvaluateRecurse2(JsonElement input)
    {
        foreach (var value in Recurse2(input, args[0], args[1]))
            yield return value;
    }

    private IEnumerable<JsonElement> Recurse2(JsonElement input, JqFilter next, JqFilter condition)
    {
        if (!condition.Evaluate(input, _env).Any(IsTruthy))
            yield break;

        yield return input;
        JsonElement[] outputs;
        try
        {
            outputs = next.Evaluate(input, _env).ToArray();
        }
        catch (JqException)
        {
            yield break;
        }

        foreach (var output in outputs)
        {
            foreach (var nested in Recurse2(output, next, condition))
                yield return nested;
        }
    }

    private IEnumerable<JsonElement> EvaluateLimit(JsonElement input)
    {
        var n = Math.Max(ReadInt(args[0], input, 0), 0);
        if (n == 0)
            yield break;

        var count = 0;
        foreach (var value in args[1].Evaluate(input, _env))
        {
            yield return value;
            count++;
            if (count >= n)
                yield break;
        }
    }

    private IEnumerable<JsonElement> EvaluateSkip(JsonElement input)
    {
        var n = Math.Max(ReadInt(args[0], input, 0), 0);
        var count = 0;
        foreach (var value in args[1].Evaluate(input, _env))
        {
            if (count < n)
            {
                count++;
                continue;
            }

            yield return value;
        }
    }

    private IEnumerable<JsonElement> EvaluateFirst(JsonElement input)
    {
        foreach (var value in args[0].Evaluate(input, _env))
        {
            yield return value;
            yield break;
        }
    }

    private IEnumerable<JsonElement> EvaluateLast(JsonElement input)
    {
        var found = false;
        JsonElement last = default;
        foreach (var value in args[0].Evaluate(input, _env))
        {
            found = true;
            last = value;
        }

        if (found)
            yield return last;
    }

    private IEnumerable<JsonElement> EvaluateNth(JsonElement input)
    {
        var n = ReadInt(args[0], input, -1);
        if (n < 0)
            yield break;

        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an array");

        if (n < input.GetArrayLength())
            yield return input[n];
    }

    private IEnumerable<JsonElement> EvaluateNth2(JsonElement input)
    {
        var n = ReadInt(args[0], input, -1);
        if (n < 0)
            yield break;

        var i = 0;
        foreach (var value in args[1].Evaluate(input, _env))
        {
            if (i == n)
            {
                yield return value;
                yield break;
            }

            i++;
        }
    }

    private IEnumerable<JsonElement> EvaluateWhile(JsonElement input)
    {
        var current = input;
        while (args[0].Evaluate(current, _env).Any(IsTruthy))
        {
            yield return current;
            current = args[1].Evaluate(current, _env).FirstOrDefault(CreateNullElement());
        }
    }

    private IEnumerable<JsonElement> EvaluateUntil(JsonElement input)
    {
        var current = input;
        while (!args[0].Evaluate(current, _env).Any(IsTruthy))
            current = args[1].Evaluate(current, _env).FirstOrDefault(CreateNullElement());

        yield return current;
    }

    private IEnumerable<JsonElement> EvaluateRepeat(JsonElement input)
    {
        foreach (var value in Repeat1(input, args[0]))
            yield return value;
    }

    private IEnumerable<JsonElement> Repeat1(JsonElement input, JqFilter update)
    {
        JsonElement[] outputs;
        try
        {
            outputs = update.Evaluate(input, _env).ToArray();
        }
        catch (JqException)
        {
            yield break;
        }

        foreach (var value in outputs)
        {
            yield return value;
            foreach (var nested in Repeat1(value, update))
                yield return nested;
        }
    }

    private IEnumerable<JsonElement> EvaluateWithEntries(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not an object");

        var entries = input.EnumerateObject().Select(property => CreateElement(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("key", property.Name);
            writer.WritePropertyName("value");
            property.Value.WriteTo(writer);
            writer.WriteEndObject();
        })).ToArray();

        var transformed = new List<JsonElement>();
        foreach (var entry in entries)
        {
            foreach (var output in args[0].Evaluate(entry, _env))
                transformed.Add(output);
        }

        yield return CreateElement(writer =>
        {
            writer.WriteStartObject();
            foreach (var value in transformed)
            {
                if (value.ValueKind != JsonValueKind.Object)
                    throw new JqException("with_entries expects object entries");

                if (!value.TryGetProperty("key", out var key))
                    continue;

                writer.WritePropertyName(key.ValueKind == JsonValueKind.String ? key.GetString() ?? "" : key.GetRawText());
                if (value.TryGetProperty("value", out var mapped))
                    mapped.WriteTo(writer);
                else
                    writer.WriteNullValue();
            }
            writer.WriteEndObject();
        });
    }

    private IEnumerable<JsonElement> EvaluateSetpath(JsonElement input)
    {
        var value = args[1].Evaluate(input, _env).FirstOrDefault(CreateNullElement());
        var result = input;
        foreach (var path in args[0].Evaluate(input, _env))
            result = PathResolver.SetPathValue(result, PathResolver.ParsePath(path), value);

        yield return result;
    }

    // Phase 13: Math functions
    private IEnumerable<JsonElement> EvaluateMathBinary(JsonElement input, Func<double, double, double> fn)
    {
        foreach (var a in args[0].Evaluate(input, _env))
        {
            RequireNumber(a);
            foreach (var b in args[1].Evaluate(input, _env))
            {
                RequireNumber(b);
                yield return CreateMathResult(fn(a.GetDouble(), b.GetDouble()));
            }
        }
    }

    private IEnumerable<JsonElement> EvaluateMathTernary(JsonElement input, Func<double, double, double, double> fn)
    {
        foreach (var a in args[0].Evaluate(input, _env))
        {
            RequireNumber(a);
            foreach (var b in args[1].Evaluate(input, _env))
            {
                RequireNumber(b);
                foreach (var c in args[2].Evaluate(input, _env))
                {
                    RequireNumber(c);
                    yield return CreateMathResult(fn(a.GetDouble(), b.GetDouble(), c.GetDouble()));
                }
            }
        }
    }

    private IEnumerable<JsonElement> EvaluateStrftime(JsonElement input)
    {
        RequireNumber(input);
        var dt = FromUnixTimestamp(input.GetDouble());
        foreach (var fmtEl in args[0].Evaluate(input, _env))
        {
            if (fmtEl.ValueKind != JsonValueKind.String)
                throw new JqException($"{GetTypeName(fmtEl)} ({GetValueText(fmtEl)}) is not a string");
            var fmt = fmtEl.GetString() ?? "";
            yield return CreateStringElement(StrftimeFormat.Format(dt, fmt));
        }
    }

    private IEnumerable<JsonElement> EvaluateStrflocaltime(JsonElement input)
    {
        RequireNumber(input);
        var dt = FromUnixTimestamp(input.GetDouble()).ToLocalTime();
        foreach (var fmtEl in args[0].Evaluate(input, _env))
        {
            if (fmtEl.ValueKind != JsonValueKind.String)
                throw new JqException($"{GetTypeName(fmtEl)} ({GetValueText(fmtEl)}) is not a string");
            var fmt = fmtEl.GetString() ?? "";
            yield return CreateStringElement(StrftimeFormat.Format(dt, fmt));
        }
    }

    private IEnumerable<JsonElement> EvaluateStrptime(JsonElement input)
    {
        RequireString(input);
        var str = input.GetString() ?? "";
        foreach (var fmtEl in args[0].Evaluate(input, _env))
        {
            if (fmtEl.ValueKind != JsonValueKind.String)
                throw new JqException($"{GetTypeName(fmtEl)} ({GetValueText(fmtEl)}) is not a string");
            var fmt = fmtEl.GetString() ?? "";
            var parts = StrftimeFormat.Parse(str, fmt);
            yield return CreateElement(writer =>
            {
                writer.WriteStartArray();
                foreach (var part in parts)
                    writer.WriteNumberValue(part);
                writer.WriteEndArray();
            });
        }
    }

    private static bool TryReadIndex(JsonElement value, int length, out int index)
    {
        index = 0;
        if (value.ValueKind != JsonValueKind.Number)
            return false;

        var number = value.GetDouble();
        if (!IsInteger(number))
            return false;

        index = (int)number;
        if (index < 0)
            index += length;

        return true;
    }

    private static bool IsInteger(double number) =>
        !double.IsNaN(number) &&
        !double.IsInfinity(number) &&
        Math.Floor(number) == number;

    private static double ReadNumber(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number)
            throw new JqException($"{GetTypeName(value)} ({GetValueText(value)}) is not a number");

        return value.GetDouble();
    }

    private int ReadInt(JqFilter filter, JsonElement input, int fallback)
    {
        var result = filter.Evaluate(input, _env).FirstOrDefault(CreateNumberElement(fallback));
        if (result.ValueKind != JsonValueKind.Number)
            throw new JqException($"{GetTypeName(result)} ({GetValueText(result)}) is not a number");

        return (int)Math.Floor(result.GetDouble());
    }
}


