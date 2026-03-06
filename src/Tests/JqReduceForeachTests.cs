using System.Text.Json;
using Devlooped;

namespace Devlooped.Tests;

public class JqReduceForeachTests
{
    // reduce tests
    [Fact]
    public void Reduce_sum_array()
        => Assert.Equal(["7"], EvaluateToStrings("reduce .[] as $x (0; . + $x)", "[1,2,4]"));

    [Fact]
    public void Reduce_empty_array_returns_init()
        => Assert.Equal(["0"], EvaluateToStrings("reduce .[] as $x (0; . + $x)", "[]"));

    [Fact]
    public void Reduce_string_concat()
        => Assert.Equal(["\"abc\""], EvaluateToStrings("reduce .[] as $x (\"\"; . + $x)", "[\"a\",\"b\",\"c\"]"));

    [Fact]
    public void Reduce_with_range()
        => Assert.Equal(["10"], EvaluateToStrings("reduce range(5) as $x (0; . + $x)", "null"));

    [Fact]
    public void Reduce_destructure_array_pattern()
        => Assert.Equal(["5"], EvaluateToStrings("reduce .[] as [$i, {\"j\":$j}] (0; . + $i - $j)", "[[2,{\"j\":1}],[5,{\"j\":3}],[6,{\"j\":4}]]"));

    // foreach tests
    [Fact]
    public void Foreach_running_sum_2arg()
        => Assert.Equal(["1", "3", "6", "10", "15"], EvaluateToStrings("foreach .[] as $item (0; . + $item)", "[1,2,3,4,5]"));

    [Fact]
    public void Foreach_with_extract_3arg()
        => Assert.Equal(["[1,2]", "[2,6]", "[3,12]", "[4,20]", "[5,30]"], EvaluateToStrings("foreach .[] as $item (0; . + $item; [$item, . * 2])", "[1,2,3,4,5]"));

    [Fact]
    public void Foreach_empty_array_produces_no_output()
        => Assert.Empty(EvaluateToStrings("foreach .[] as $x (0; . + $x)", "[]"));

    [Fact]
    public void Foreach_with_range_generator()
        => Assert.Equal(["[0,1,2,3,4]"], EvaluateToStrings("[foreach range(5) as $item (0; $item)]", "null"));

    [Fact]
    public void Foreach_extract_filtering_with_select()
        => Assert.Equal(["2", "4", "6"], EvaluateToStrings("foreach .[] as $x (0; . + $x; if . % 2 == 0 then . else empty end)", "[1,1,1,1,1,1]"));

    [Fact]
    public void Reduce_followed_by_as_binding()
        => Assert.Equal(["6"], EvaluateToStrings("(reduce .[] as $x (0; . + $x)) as $y | $y", "[1,2,3]"));

    [Fact]
    public void Foreach_followed_by_as_binding()
        => Assert.Equal(["1", "3", "6"], EvaluateToStrings("[foreach .[] as $x (0; . + $x)] | .[]", "[1,2,3]"));

    static string[] EvaluateToStrings(string expression, string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        return Jq.Evaluate(expression, document.RootElement)
            .Select(static e => JsonSerializer.Serialize(e))
            .ToArray();
    }
}
