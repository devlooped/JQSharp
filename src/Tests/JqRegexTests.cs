using System.Text.Json;
using Devlooped;

namespace Devlooped.Tests;

public class JqRegexTests
{
    [Fact]
    public void Test_returns_true_when_pattern_matches()
        => Assert.Equal(["true"], EvaluateToStrings("""test("foo")""", "\"foobar\""));

    [Fact]
    public void Test_with_flags_supports_case_insensitive_matching()
        => Assert.Equal(["true"], EvaluateToStrings("""test("foo";"i")""", "\"FoObAr\""));

    [Fact]
    public void Match_returns_match_object_for_first_match()
        => Assert.Equal(
            ["""{"offset":1,"length":2,"string":"oo","captures":[{"offset":1,"length":2,"string":"oo","name":null}]}"""],
            EvaluateToStrings("""match("(o+)")""", "\"foobar\""));

    [Fact]
    public void Match_with_global_flag_returns_all_matches()
        => Assert.Equal(
            ["""[{"offset":1,"length":1,"string":"o","captures":[]},{"offset":2,"length":1,"string":"o","captures":[]}]"""],
            EvaluateToStrings("""[match("o";"g")]""", "\"foobar\""));

    [Fact]
    public void Match_without_match_throws()
        => Assert.ThrowsAny<Exception>(() => EvaluateToStrings("""match("z")""", "\"foo\""));

    [Fact]
    public void Capture_returns_named_capture_object()
        => Assert.Equal(
            ["""{"a":"foo","b":"bar"}"""],
            EvaluateToStrings("""capture("(?<a>\\w+)-(?<b>\\w+)")""", "\"foo-bar\""));

    [Fact]
    public void Capture_with_flags_supports_case_insensitive_matching()
        => Assert.Equal(
            ["""{"a":"FOO","b":"bar"}"""],
            EvaluateToStrings("""capture("(?<a>foo)-(?<b>bar)";"i")""", "\"FOO-bar\""));

    [Fact]
    public void Scan_returns_all_matches_when_no_capture_groups()
        => Assert.Equal(
            ["""["123","456"]"""],
            EvaluateToStrings("""[scan("\\d+")]""", "\"abc123def456\""));

    [Fact]
    public void Scan_with_groups_returns_group_arrays()
        => Assert.Equal(
            ["""[["A","1"],["b","22"]]"""],
            EvaluateToStrings("""[scan("([a-z])(\\d+)";"ig")]""", "\"A1b22\""));

    [Fact]
    public void Split_regex_overload_splits_with_pattern_and_flags()
        => Assert.Equal(
            ["""["a","b","c"]"""],
            EvaluateToStrings("""split("\\s+";"")""", "\"a  b  c\""));

    [Fact]
    public void Splits_returns_stream_of_pieces()
        => Assert.Equal(
            ["""["foo","bar"]"""],
            EvaluateToStrings("""[splits("\\s+")]""", "\"foo bar\""));

    [Fact]
    public void Splits_with_flags_supports_case_insensitive_pattern()
        => Assert.Equal(
            ["""["","",""]"""],
            EvaluateToStrings("""[splits("foo";"i")]""", "\"FOOfoo\""));

    [Fact]
    public void Sub_replaces_first_match_only()
        => Assert.Equal(["\"f0o\""], EvaluateToStrings("""sub("o";"0")""", "\"foo\""));

    [Fact]
    public void Sub_with_flags_replaces_first_match_with_case_insensitive_pattern()
        => Assert.Equal(["\"F0O\""], EvaluateToStrings("""sub("o";"0";"i")""", "\"FoO\""));

    [Fact]
    public void Sub_supports_expression_replacement_with_named_captures()
        => Assert.Equal(["\"foo\""], EvaluateToStrings("""sub("(?<x>\\w+)"; .x)""", "\"foo\""));

    [Fact]
    public void Gsub_replaces_all_matches()
        => Assert.Equal(["\"f00\""], EvaluateToStrings("""gsub("o";"0")""", "\"foo\""));

    [Fact]
    public void Gsub_with_flags_replaces_all_matches_with_case_insensitive_pattern()
        => Assert.Equal(["\"F00\""], EvaluateToStrings("""gsub("o";"0";"i")""", "\"FoO\""));

    [Fact]
    public void Gsub_supports_expression_replacement_with_named_captures()
        => Assert.Equal(
            ["\"1-a 2-b\""],
            EvaluateToStrings("""gsub("(?<x>[a-z])(?<n>\\d)"; .n + "-" + .x)""", "\"a1 b2\""));

    [Fact]
    public void Regex_builtins_throw_for_non_string_input()
        => Assert.ThrowsAny<Exception>(() => EvaluateToStrings("""test("a")""", "123"));

    static string[] EvaluateToStrings(string expression, string inputJson)
        => Evaluate(expression, inputJson)
            .Select(static element => JsonSerializer.Serialize(element))
            .ToArray();

    static JsonElement[] Evaluate(string expression, string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        return Jq.Evaluate(expression, document.RootElement)
            .ToArray();
    }
}
