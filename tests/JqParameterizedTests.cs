using System.Text.Json;
using Devlooped;

namespace Devlooped.Tests;

public class JqParameterizedTests
{
    [Fact]
    public void Select_filters_by_expression()
        => Assert.Equal(["{\"x\":1}"], EvaluateToStrings("select(.x > 0)", """{"x":1}"""));

    [Fact]
    public void Map_applies_expression_to_each_array_item()
        => Assert.Equal(["[2,3,4]"], EvaluateToStrings("map(. + 1)", "[1,2,3]"));

    [Fact]
    public void Map_can_nest_select_expression()
        => Assert.Equal(["[3,4]"], EvaluateToStrings("map(select(. > 2))", "[1,2,3,4]"));

    [Fact]
    public void Range_single_argument_generates_zero_based_sequence()
        => Assert.Equal(["[0,1,2]"], EvaluateToStrings("[range(3)]", "null"));

    [Fact]
    public void Range_two_arguments_generates_sequence_between_start_and_end()
        => Assert.Equal(["[1,2,3]"], EvaluateToStrings("[range(1;4)]", "null"));

    [Fact]
    public void Sort_by_orders_values_using_key_expression()
        => Assert.Equal(["[{\"x\":1},{\"x\":2}]"], EvaluateToStrings("sort_by(.x)", """[{"x":2},{"x":1}]"""));

    [Fact]
    public void Split_divides_string_using_token()
        => Assert.Equal(["[\"a\",\"b\",\"c\"]"], EvaluateToStrings("split(\"x\")", "\"axbxc\""));

    [Fact]
    public void Join_combines_array_items_with_separator()
        => Assert.Equal(["\"a,b,c\""], EvaluateToStrings("join(\",\")", """["a","b","c"]"""));

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
