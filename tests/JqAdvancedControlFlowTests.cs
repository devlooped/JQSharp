using Devlooped;
using System.Text.Json;

namespace Devlooped.Tests;

public class JqAdvancedControlFlowTests
{
    private static string[] EvaluateToStrings(string expression, string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        return Jq.Evaluate(expression, document.RootElement)
            .Select(static e => JsonSerializer.Serialize(e))
            .ToArray();
    }

    [Fact]
    public void Label_break_stops_at_first_value_greater_than_one_in_first_case()
        => Assert.Equal(["[0,1,\"hi!\"]"], EvaluateToStrings("[(label $here | .[] | if .>1 then break $here else . end), \"hi!\"]", "[0,1,2]"));

    [Fact]
    public void Label_break_stops_at_first_value_greater_than_one_in_second_case()
        => Assert.Equal(["[0,\"hi!\"]"], EvaluateToStrings("[(label $here | .[] | if .>1 then break $here else . end), \"hi!\"]", "[0,2,1]"));

    [Fact]
    public void Label_break_inside_foreach_keeps_prefix()
        => Assert.Equal(["[11,22,33]"], EvaluateToStrings("[label $out | foreach .[] as $item ([3, null]; if .[0] < 1 then break $out else [.[0] -1, $item] end; .[1])]", "[11,22,33,44,55,66,77,88,99]"));

    [Fact]
    public void Break_without_matching_label_reports_parse_error()
    {
        var exception = Assert.Throws<JqException>(() => EvaluateToStrings(". as $foo | break $foo", "null"));
        Assert.Contains("$*label-foo is not defined", exception.Message);
    }

    [Fact]
    public void Label_break_inside_array_stops_following_values()
        => Assert.Equal(["[1]"], EvaluateToStrings("[label $x | 1, break $x, 2]", "null"));

    [Fact]
    public void Label_without_break_yields_all_values()
        => Assert.Equal(["[1,2,3]"], EvaluateToStrings("[label $x | 1,2,3]", "null"));

    [Fact]
    public void Destructuring_alternatives_support_multiple_fallback_patterns()
        => Assert.Equal(
            ["[1,null,2,3,null,null]", "[4,5,null,null,7,null]", "[null,null,null,null,null,\"foo\"]"],
            EvaluateToStrings(".[] | . as {$a, b: [$c, {$d}]} ?// [$a, {$b}, $e] ?// $f | [$a, $b, $c, $d, $e, $f]", """[{"a":1, "b":[2,{"d":3}]}, [4, {"b":5, "c":6}, 7, 8, 9], "foo"]"""));

    [Fact]
    public void Destructuring_alternatives_fallback_to_value_binding()
        => Assert.Equal(["[3]", "[4]", "[5]", "6"], EvaluateToStrings(".[] | . as {a:$a} ?// {a:$a} ?// $a | $a", "[[3],[4],[5],6]"));

    [Fact]
    public void Destructuring_alternatives_throw_when_all_alternatives_fail()
        => Assert.ThrowsAny<Exception>(() => EvaluateToStrings(".[] | . as {a:$a} ?// {a:$a} ?// {a:$a} | $a", "[[3],[4],[5],6]"));

    [Fact]
    public void Destructuring_alternatives_variable_catch_all_first()
        => Assert.Equal(["[3]", "[4]", "[5]", "6"], EvaluateToStrings(".[] | . as $a ?// {a:$a} ?// {a:$a} | $a", "[[3],[4],[5],6]"));

    [Fact]
    public void Destructuring_alternatives_object_then_variable()
        => Assert.Equal(["[3]", "[4]", "[5]", "6"], EvaluateToStrings(".[] | . as {a:$a} ?// $a ?// {a:$a} | $a", "[[3],[4],[5],6]"));

    [Fact]
    public void Destructuring_alternatives_two_patterns_object_match()
        => Assert.Equal(["1"], EvaluateToStrings(". as {$a} ?// $a | $a", """{"a":1}"""));

    [Fact]
    public void Destructuring_alternatives_two_patterns_variable_fallback()
        => Assert.Equal(["42"], EvaluateToStrings(". as {$a} ?// $a | $a", "42"));

    [Fact]
    public void Index_builds_lookup_object_from_stream()
        => Assert.Equal(
            ["{\"0\":[0,\"foo0\"],\"1\":[1,\"foo1\"],\"2\":[2,\"foo2\"],\"3\":[3,\"foo3\"],\"4\":[4,\"foo4\"]}"],
            EvaluateToStrings("INDEX(range(5)|[., \"foo\\(.)\"]; .[0])", "null"));

    [Fact]
    public void Index_builds_lookup_object_from_input_array()
        => Assert.Equal(
            ["{\"foo\":{\"name\":\"foo\",\"val\":1},\"bar\":{\"name\":\"bar\",\"val\":2}}"],
            EvaluateToStrings("""[{"name":"foo","val":1},{"name":"bar","val":2}] | INDEX(.name)""", "null"));

    [Fact]
    public void Join_matches_items_against_index_by_key()
        => Assert.Equal(
            ["[[[5,\"foo\"],null],[[3,\"bar\"],[3,\"efg\"]],[[1,\"foobar\"],[1,\"bcd\"]]]"],
            EvaluateToStrings("JOIN({\"0\":[0,\"abc\"],\"1\":[1,\"bcd\"],\"2\":[2,\"def\"],\"3\":[3,\"efg\"],\"4\":[4,\"fgh\"]}; .[0]|tostring)", "[[5,\"foo\"],[3,\"bar\"],[1,\"foobar\"]]"));

    [Fact]
    public void In_returns_true_for_range_five_to_nine_in_range_ten()
        => Assert.Equal(["true", "true", "true", "true", "true"], EvaluateToStrings("range(5;10)|IN(range(10))", "null"));

    [Fact]
    public void In_returns_expected_true_false_sequence_for_step_range()
        => Assert.Equal(["false", "true", "false", "false", "true", "false", "false", "false"], EvaluateToStrings("range(5;13)|IN(range(0;10;3))", "null"));

    [Fact]
    public void In_returns_false_for_values_not_in_range()
        => Assert.Equal(["false", "false"], EvaluateToStrings("range(10;12)|IN(range(10))", "null"));

    [Fact]
    public void In_with_two_ranges_returns_false_when_no_overlap_on_input_side()
        => Assert.Equal(["false"], EvaluateToStrings("IN(range(10;20); range(10))", "null"));

    [Fact]
    public void In_with_two_ranges_returns_true_when_overlap_exists()
        => Assert.Equal(["true"], EvaluateToStrings("IN(range(5;20); range(10))", "null"));
}
