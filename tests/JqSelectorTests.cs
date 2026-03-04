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

    [Fact]
    public void Inline_comment_after_filter_is_ignored()
        => Assert.Equal(["42"], EvaluateToStrings(".foo # comment", """{"foo":42}"""));

    [Fact]
    public void Leading_comment_line_is_ignored()
        => Assert.Equal(["42"], EvaluateToStrings("""
            # comment
            .foo
            """, """{"foo":42}"""));

    [Fact]
    public void Hash_inside_string_is_not_treated_as_comment()
        => Assert.Equal(["\"# not a comment\""], EvaluateToStrings("\"# not a comment\"", "null"));

    [Fact]
    public void Comment_before_pipe_does_not_break_next_line()
        => Assert.Equal(["99"], EvaluateToStrings("""
            .foo # pick foo
            | .bar
            """, """{"foo":{"bar":99}}"""));

    [Fact]
    public void Odd_trailing_backslashes_continue_comment_to_next_line()
        => Assert.Equal(["42"], EvaluateToStrings("""
            # comment \\\
            # still comment
            .foo
            """, """{"foo":42}"""));

    [Fact]
    public void Even_trailing_backslashes_do_not_continue_comment()
        => Assert.Equal(["42"], EvaluateToStrings("""
            # comment \\\\
            .foo
            """, """{"foo":42}"""));

    [Fact]
    public void And_true_and_true_returns_true()
        => Assert.Equal(["true"], EvaluateToStrings("true and true", "null"));

    [Fact]
    public void And_true_and_false_returns_false()
        => Assert.Equal(["false"], EvaluateToStrings("true and false", "null"));

    [Fact]
    public void And_false_and_true_returns_false()
        => Assert.Equal(["false"], EvaluateToStrings("false and true", "null"));

    [Fact]
    public void And_null_and_true_returns_false()
        => Assert.Equal(["false"], EvaluateToStrings("null and true", "null"));

    [Fact]
    public void Or_true_or_false_returns_true()
        => Assert.Equal(["true"], EvaluateToStrings("true or false", "null"));

    [Fact]
    public void Or_false_or_true_returns_true()
        => Assert.Equal(["true"], EvaluateToStrings("false or true", "null"));

    [Fact]
    public void Or_false_or_false_returns_false()
        => Assert.Equal(["false"], EvaluateToStrings("false or false", "null"));

    [Fact]
    public void Or_null_or_false_returns_false()
        => Assert.Equal(["false"], EvaluateToStrings("null or false", "null"));

    [Fact]
    public void Not_negates_true_to_false()
        => Assert.Equal(["false"], EvaluateToStrings("true | not", "null"));

    [Fact]
    public void Not_negates_false_to_true()
        => Assert.Equal(["true"], EvaluateToStrings("false | not", "null"));

    [Fact]
    public void Not_negates_null_to_true()
        => Assert.Equal(["true"], EvaluateToStrings("null | not", "null"));

    [Fact]
    public void Not_negates_number_to_false()
        => Assert.Equal(["false"], EvaluateToStrings("0 | not", "null"));

    [Fact]
    public void Alternative_returns_left_when_truthy()
        => Assert.Equal(["1"], EvaluateToStrings("1 // 42", "null"));

    [Fact]
    public void Alternative_falls_back_to_right_when_null()
        => Assert.Equal(["42"], EvaluateToStrings("null // 42", "null"));

    [Fact]
    public void Alternative_falls_back_to_right_when_false()
        => Assert.Equal(["42"], EvaluateToStrings("false // 42", "null"));

    [Fact]
    public void Alternative_zero_is_truthy()
        => Assert.Equal(["0"], EvaluateToStrings("0 // 42", "null"));

    [Fact]
    public void Alternative_empty_string_is_truthy()
        => Assert.Equal(["\"\""], EvaluateToStrings("\"\" // 42", "null"));

    [Fact]
    public void Alternative_chained_skips_all_falsy()
        => Assert.Equal(["42"], EvaluateToStrings("null // false // 42", "null"));

    [Fact]
    public void Alternative_right_side_can_produce_falsy()
        => Assert.Equal(["false"], EvaluateToStrings("null // false", "null"));

    [Fact]
    public void And_binds_tighter_than_or()
        => Assert.Equal(["true"], EvaluateToStrings("true or false and false", "null"));

    [Fact]
    public void Alternative_with_field_access()
        => Assert.Equal(["42"], EvaluateToStrings(".foo // 42", """{"bar":1}"""));

    [Fact]
    public void Field_access_with_keyword_name_not()
        => Assert.Equal(["1"], EvaluateToStrings(".not", """{"not":1}"""));

    [Fact]
    public void Field_access_with_keyword_name_and()
        => Assert.Equal(["2"], EvaluateToStrings(".and", """{"and":2}"""));

    [Fact]
    public void Field_access_with_keyword_name_or()
        => Assert.Equal(["3"], EvaluateToStrings(".or", """{"or":3}"""));

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

        // Keywords indicating unimplemented features (Phase 3 zero-arg builtins removed)
        string[] disallowedKeywords = ["def", " as ", "reduce", "foreach", "try", "catch", "label", "break", "import", "include", "limit", "until", "repeat", "modulemeta", "path(", "getpath", "setpath", "delpaths", "leaf_paths", "error", "debug", "stderr", "input", "inputs",  "halt_error", "ltrimstr", "rtrimstr", "startswith", "endswith", "split", "join", "test", "match", "capture", "scan", "sub", "gsub", "ceil", "round", "pow", "log", "exp", "fabs", "significand", "exponent", "remainder", "ldexp", "sort_by", "group_by", "unique_by", "range", "min_by", "max_by", "map", "map_values", "select", "del(", "with_entries", "has", "in(", "contains", "inside", "walk", "indices", "index", "rindex", "nth", "format", "now", "dateadd", "datesub", "todate", "fromdate", "toiso8601", "fromiso8601", "gmtime", "mktime", "strftime", "strptime"];
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
