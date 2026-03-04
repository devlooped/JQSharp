using System.Text.Json;
using Devlooped;

namespace Devlooped.Tests;

public class JqParameterizedTests
{
    [Fact]
    public void Has_returns_true_for_existing_object_key()
        => Assert.Equal(["true"], EvaluateToStrings("has(\"a\")", """{"a":1}"""));

    [Fact]
    public void Has_returns_false_for_missing_object_key()
        => Assert.Equal(["false"], EvaluateToStrings("has(\"b\")", """{"a":1}"""));

    [Fact]
    public void Has_returns_true_for_valid_array_index()
        => Assert.Equal(["true"], EvaluateToStrings("has(2)", "[1,2,3]"));

    [Fact]
    public void Has_returns_false_for_out_of_bounds_array_index()
        => Assert.Equal(["false"], EvaluateToStrings("has(5)", "[1,2,3]"));

    [Fact]
    public void In_is_reverse_of_has()
        => Assert.Equal(["[true,false]"], EvaluateToStrings("""["a","z"] | map(in({"a":1}))""", "null"));

    [Fact]
    public void Select_returns_input_when_condition_is_true()
        => Assert.Equal(["""{"x":1}"""], EvaluateToStrings("select(.x > 0)", """{"x":1}"""));

    [Fact]
    public void Select_produces_no_output_when_condition_is_false()
        => Assert.Equal([], EvaluateToStrings("select(.x > 0)", """{"x":-1}"""));

    [Fact]
    public void Contains_checks_recursive_containment()
        => Assert.Equal(["true"], EvaluateToStrings("""contains({"foo":"foo"})""", """{"foo":"foobar"}"""));

    [Fact]
    public void Contains_returns_false_when_not_contained()
        => Assert.Equal(["false"], EvaluateToStrings("""contains({"foo":"baz"})""", """{"foo":"foobar"}"""));

    [Fact]
    public void Inside_is_reverse_of_contains()
        => Assert.Equal(["true"], EvaluateToStrings("""inside({"foo":"foobar"})""", """{"foo":"foo"}"""));

    [Fact]
    public void Isempty_returns_true_for_empty_expression()
        => Assert.Equal(["true"], EvaluateToStrings("isempty(empty)", "null"));

    [Fact]
    public void Isempty_returns_false_for_non_empty_expression()
        => Assert.Equal(["false"], EvaluateToStrings("isempty(1,2,3)", "null"));

    [Fact]
    public void Any_with_condition_returns_true_when_any_element_matches()
        => Assert.Equal(["true"], EvaluateToStrings("any(. == true)", "[true,false,true]"));

    [Fact]
    public void Any_with_condition_returns_false_when_no_element_matches()
        => Assert.Equal(["false"], EvaluateToStrings("any(. == true)", "[false,false]"));

    [Fact]
    public void All_with_condition_returns_true_when_all_elements_match()
        => Assert.Equal(["true"], EvaluateToStrings("all(. > 0)", "[1,2,3]"));

    [Fact]
    public void All_with_condition_returns_false_when_some_element_does_not_match()
        => Assert.Equal(["false"], EvaluateToStrings("all(. > 0)", "[1,-1,3]"));

    [Fact]
    public void Any_with_generator_and_condition_tests_generator_outputs()
        => Assert.Equal(["true"], EvaluateToStrings("any(range(5); . >= 2)", "null"));

    [Fact]
    public void All_with_generator_and_condition_tests_all_generator_outputs()
        => Assert.Equal(["true"], EvaluateToStrings("all(range(5); . >= 0)", "null"));

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
