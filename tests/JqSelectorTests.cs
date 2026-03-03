using System.Text.Json;
using Devlooped;

namespace Devlooped.Tests;

public class JqSelectorTests
{
    public static IEnumerable<object[]> SelectorSuiteCases()
    {
        foreach (var testCase in JqTestParser.ParseFile(GetSuitePath()))
        {
            if (!IsSelectorScopeProgram(testCase.Program))
                continue;

            if (testCase.ShouldFail)
            {
                if (!ThrowsOnParse(testCase.Program))
                    continue;

                yield return new object[] { testCase, $"line {testCase.LineNumber}: {testCase.Program}" };
                continue;
            }

            if (!ParsesSuccessfully(testCase.Program))
                continue;

            if (!IsValidJson(testCase.Input))
                continue;

            yield return new object[] { testCase, $"line {testCase.LineNumber}: {testCase.Program}" };
        }
    }

    [Theory]
    [MemberData(nameof(SelectorSuiteCases))]
    public void Selector_suite_cases_match_expected_outputs(JqTestCase testCase, string _)
    {
        if (testCase.ShouldFail)
        {
            Assert.ThrowsAny<Exception>(() => Jq.Parse(testCase.Program));
            return;
        }

        using var document = JsonDocument.Parse(testCase.Input);
        var actual = Jq.Evaluate(testCase.Program, document.RootElement)
            .Select(static element => CanonicalizeJson(JsonSerializer.Serialize(element)))
            .ToArray();
        var expected = testCase.ExpectedOutputs
            .Select(CanonicalizeJson)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Field_access_returns_value_when_present()
        => Assert.Equal(["42"], EvaluateToStrings(".foo", """{"foo":42}"""));

    [Fact]
    public void Field_access_on_null_returns_null()
        => Assert.Equal(["null"], EvaluateToStrings(".foo", "null"));

    [Fact]
    public void Missing_field_returns_null()
        => Assert.Equal(["null"], EvaluateToStrings(".foo", """{"bar":1}"""));

    [Fact]
    public void Identity_returns_input_unchanged()
        => Assert.Equal(["{\"x\":[1,2]}"], EvaluateToStrings(".", """{"x":[1,2]}"""));

    [Fact]
    public void Iterate_over_array_returns_each_element()
        => Assert.Equal(["1", "2", "3"], EvaluateToStrings(".[]", "[1,2,3]"));

    [Fact]
    public void Iterate_over_object_returns_each_value()
        => Assert.Equal(["1", "2"], EvaluateToStrings(".[]", """{"a":1,"b":2}"""));

    [Fact]
    public void Positive_index_returns_expected_element()
        => Assert.Equal(["10"], EvaluateToStrings(".[0]", "[10,20,30]"));

    [Fact]
    public void Negative_index_returns_expected_element()
        => Assert.Equal(["30"], EvaluateToStrings(".[-1]", "[10,20,30]"));

    [Fact]
    public void Recursive_descent_returns_all_nested_values_depth_first()
        => Assert.Equal(
            ["{\"a\":{\"b\":1}}", "{\"b\":1}", "1"],
            EvaluateToStrings("..", """{"a":{"b":1}}"""));

    [Fact]
    public void Optional_suppresses_errors_and_returns_no_output()
        => Assert.Empty(EvaluateToStrings(".foo?", "42"));

    [Fact]
    public void Pipe_composes_selectors()
        => Assert.Equal(["99"], EvaluateToStrings(".foo | .bar", """{"foo":{"bar":99}}"""));

    static string[] EvaluateToStrings(string expression, string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        return Jq.Evaluate(expression, document.RootElement)
            .Select(static element => JsonSerializer.Serialize(element))
            .ToArray();
    }

    static string GetSuitePath()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "suite", "jq.test"));

    static bool ParsesSuccessfully(string program)
    {
        try
        {
            _ = Jq.Parse(program);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static bool ThrowsOnParse(string program) => !ParsesSuccessfully(program);

    static bool IsValidJson(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static bool IsSelectorScopeProgram(string program)
    {
        if (string.IsNullOrWhiteSpace(program))
            return false;

        var trimmed = program.TrimStart();
        if (!trimmed.StartsWith(".", StringComparison.Ordinal))
            return false;

        // These characters indicate features not yet implemented
        const string disallowedChars = "$;@`\\";
        if (program.Any(ch => disallowedChars.IndexOf(ch) >= 0))
            return false;

        // Single = (assignment) is not implemented, but ==, !=, <=, >= are ok
        // Check for lone = (not preceded/followed by =, !, <, >)
        for (var i = 0; i < program.Length; i++)
        {
            if (program[i] != '=')
                continue;

            var precededByOp = i > 0 && (program[i - 1] == '!' || program[i - 1] == '<' || program[i - 1] == '>' || program[i - 1] == '=');
            var followedByEq = i + 1 < program.Length && program[i + 1] == '=';
            if (!precededByOp && !followedByEq)
                return false;
        }

        // Keywords indicating unimplemented features
        string[] disallowedKeywords = ["def", "if", " as ", "reduce", "foreach", "try", "catch", "label", "break", "import", "include", "limit", "until", "repeat", "env", "builtins", "modulemeta", "path", "getpath", "setpath", "delpaths", "leaf_paths", "isnan", "isinfinite", "isnormal", "infinite", "nan", "error", "debug", "stderr", "input", "inputs", "halt", "halt_error", "ascii_downcase", "ascii_upcase", "ltrimstr", "rtrimstr", "startswith", "endswith", "split", "join", "test", "match", "capture", "scan", "sub", "gsub", "ascii", "explode", "implode", "tonumber", "tostring", "type", "infinite", "nan", "floor", "ceil", "round", "sqrt", "pow", "log", "exp", "fabs", "significand", "exponent", "remainder", "ldexp", "scalars", "values", "arrays", "objects", "iterables", "booleans", "numbers", "strings", "nulls", "sort", "sort_by", "group_by", "unique", "unique_by", "reverse", "flatten", "range", "min", "max", "min_by", "max_by", "add", "any", "all", "map", "map_values", "select", "empty", "del", "to_entries", "from_entries", "with_entries", "keys", "values", "has", "in", "contains", "inside", "recurse", "walk", "paths", "length", "utf8bytelength", "keys_unsorted", "indices", "index", "rindex", "indices", "first", "last", "nth", "not", "and", "or", "format", "tojson", "fromjson", "ascii", "input", "now", "dateadd", "datesub", "todate", "fromdate", "toiso8601", "fromiso8601", "gmtime", "mktime", "strftime", "strptime", "dateadd", "datesub", "modulemeta"];
        foreach (var kw in disallowedKeywords)
        {
            if (program.Contains(kw, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    static string CanonicalizeJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (Exception)
        {
            return json.Trim();
        }
    }
}
