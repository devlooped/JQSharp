using System.Text.Json;
using Devlooped;

namespace Devlooped.Tests;

public class JqStringInterpolationTests
{
    [Fact]
    public void Basic_interpolation_substitutes_field_value()
        => Assert.Equal(["\"Hello World\""], EvaluateToStrings("\"Hello \\(.name)\"", """{"name":"World"}"""));

    [Fact]
    public void Multiple_interpolations_combine_values()
        => Assert.Equal(["\"1 and 2\""], EvaluateToStrings("\"\\(.a) and \\(.b)\"", """{"a":1,"b":2}"""));

    [Fact]
    public void Nested_string_expression_inside_interpolation()
        => Assert.Equal(["\"interpolation\""], EvaluateToStrings("\"inter\\(\"pol\" + \"ation\")\"", "null"));

    [Fact]
    public void Interpolation_converts_number_to_string()
        => Assert.Equal(["\"42\""], EvaluateToStrings("\"\\(.)\"", "42"));

    [Fact]
    public void Interpolation_keeps_string_value_unquoted()
        => Assert.Equal(["\"hello\""], EvaluateToStrings("\"\\(.)\"", "\"hello\""));

    [Fact]
    public void Interpolation_converts_null_to_string()
        => Assert.Equal(["\"null\""], EvaluateToStrings("\"\\(.)\"", "null"));

    [Fact]
    public void Interpolation_converts_boolean_to_string()
        => Assert.Equal(["\"true\""], EvaluateToStrings("\"\\(.)\"", "true"));

    [Fact]
    public void Interpolation_converts_array_to_string()
        => Assert.Equal(["\"[1,2]\""], EvaluateToStrings("\"\\(.)\"", "[1,2]"));

    [Fact]
    public void Interpolation_converts_object_to_compact_json_string()
    {
        var results = Evaluate("\"\\(.)\"", """{"a":1}""");
        Assert.Single(results);
        Assert.Equal("{\"a\":1}", results[0].GetString());
    }

    [Fact]
    public void Interpolation_with_multiple_outputs_emits_cartesian_results()
        => Assert.Equal(["\"val: 1\"", "\"val: 2\""], EvaluateToStrings("\"val: \\(.[])\"", "[1,2]"));

    [Fact]
    public void Interpolation_with_empty_expression_produces_no_output()
        => Assert.Empty(EvaluateToStrings("\"\\(empty)\"", "null"));

    [Fact]
    public void String_without_interpolation_still_evaluates()
        => Assert.Equal(["\"hello\""], EvaluateToStrings("\"hello\"", "null"));

    [Fact]
    public void Interpolation_evaluates_complex_expression()
        => Assert.Equal(["\"3\""], EvaluateToStrings("\"\\(.a + .b)\"", """{"a":1,"b":2}"""));

    [Fact]
    public void Adjacent_interpolations_concatenate_values()
        => Assert.Equal(["\"xy\""], EvaluateToStrings("\"\\(.a)\\(.b)\"", """{"a":"x","b":"y"}"""));

    [Fact]
    public void Nested_string_interpolations_resolve_inside_out()
        => Assert.Equal(["\"abcd\""], EvaluateToStrings("\"a\\(\"b\\(\"c\")\")d\"", "null"));

    [Fact]
    public void Interpolation_supports_if_then_else_expression()
        => Assert.Equal(["\"yes\""], EvaluateToStrings("\"\\(if . then \"yes\" else \"no\" end)\"", "true"));

    [Fact]
    public void Escapes_and_interpolation_produce_expected_newline()
    {
        var results = Evaluate("\"line1\\nvalue: \\(.x)\"", """{"x":42}""");
        Assert.Single(results);
        Assert.Equal("line1\nvalue: 42", results[0].GetString());
    }

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
