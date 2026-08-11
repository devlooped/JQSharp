using System.Text.Json;
using Devlooped;

namespace Devlooped.Tests;

public class JqVariableTests
{
    static string[] EvaluateToStrings(string expression, string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        return Jq.Evaluate(expression, document.RootElement).Select(static e => JsonSerializer.Serialize(e)).ToArray();
    }

    [Fact]
    public void Variable_binding_basic()
        => Assert.Equal(["10"], EvaluateToStrings("5 as $x | $x + $x", "null"));

    [Fact]
    public void Variable_binding_multiple()
        => Assert.Equal(["[1,2,1]"], EvaluateToStrings("1 as $x | 2 as $y | [$x,$y,$x]", "null"));

    [Fact]
    public void Variable_binding_with_generator()
        => Assert.Equal(["[1]", "[2]", "[3]"], EvaluateToStrings("[1,2,3][] as $x | [$x]", "null"));

    [Fact]
    public void Variable_binding_ignores_pipeline_input()
        => Assert.Equal(["43"], EvaluateToStrings("42 as $x | . | . | . + 432 | $x + 1", "34324"));

    [Fact]
    public void Variable_binding_precedence()
        => Assert.Equal(["-3"], EvaluateToStrings("1 + 2 as $x | -$x", "null"));

    [Fact]
    public void Variable_binding_chained()
        => Assert.Equal(["\"x,ay\""], EvaluateToStrings("\"x\" as $x | \"a\"+\"y\" as $y | $x+\",\"+$y", "null"));

    [Fact]
    public void Variable_binding_shadowing()
        => Assert.Equal(["[1,1,1]"], EvaluateToStrings("1 as $x | [$x,$x,$x as $x | $x]", "null"));

    [Fact]
    public void Variable_binding_inside_array_constructor()
        => Assert.Equal(["[1,-1]"], EvaluateToStrings("[-1 as $x | 1,$x]", "null"));

    [Fact]
    public void Array_destructuring_basic()
        => Assert.Equal(["1", "3", "null"], EvaluateToStrings("[1, {c:3, d:4}] as [$a, {c:$b, b:$c}] | $a, $b, $c", "null"));

    [Fact]
    public void Array_destructuring_with_generator()
        => Assert.Equal(["[null,1]", "[2,1]"], EvaluateToStrings(".[] as [$a, $b] | [$b, $a]", "[[1],[1,2,3]]"));

    [Fact]
    public void Object_destructuring_basic()
        => Assert.Equal(["[1,2,3]"], EvaluateToStrings(". as {as: $kw, \"str\": $str, (\"e\"+\"x\"+\"p\"): $exp} | [$kw, $str, $exp]", "{\"as\":1,\"str\":2,\"exp\":3}"));

    [Fact]
    public void Object_destructuring_shorthand()
        => Assert.Equal(["[1,2,3]"], EvaluateToStrings(". as {$a, b: [$c, {$d}]} | [$a, $c, $d]", "{\"a\":1,\"b\":[2,{\"d\":3}]}"));

    [Fact]
    public void ENV_variable_is_an_object()
        => Assert.Equal(["\"object\""], EvaluateToStrings("$ENV | type", "null"));

    [Fact]
    public void ENV_has_keys()
        => Assert.Equal(["true"], EvaluateToStrings("($ENV | keys | length) > 0", "null"));

    [Fact]
    public void Dynamic_index_with_variable()
        => Assert.Equal(["5", "6", "7"], EvaluateToStrings("[1,2,3][] as $x | [4,5,6,7][$x]", "null"));

    [Fact]
    public void External_variable_is_available_during_evaluation()
    {
        using var input = JsonDocument.Parse("""{"name":"Alice"}""");
        using var root = JsonDocument.Parse("""{"id":42}""");
        var variables = new Dictionary<string, JsonElement>
        {
            ["root"] = root.RootElement,
        };

        var results = Jq.Evaluate("{name: .name, id: $root.id}", input.RootElement, variables)
            .Select(static e => JsonSerializer.Serialize(e))
            .ToArray();

        Assert.Equal(["""{"name":"Alice","id":42}"""], results);
    }

    [Fact]
    public void External_variable_can_be_used_from_parsed_expression()
    {
        var expression = Jq.Parse("$greeting");
        using var input = JsonDocument.Parse("null");
        using var greeting = JsonDocument.Parse("\"hello\"");
        var variables = new Dictionary<string, JsonElement>
        {
            ["greeting"] = greeting.RootElement,
        };

        var results = expression.Evaluate(input.RootElement, variables)
            .Select(static e => e.GetString()!)
            .ToArray();

        Assert.Equal(["hello"], results);
    }

    [Fact]
    public void External_variable_missing_at_evaluation_throws()
    {
        var expression = Jq.Parse("$root");
        using var input = JsonDocument.Parse("null");

        var exception = Assert.Throws<JqException>(() => expression.Evaluate(input.RootElement).ToArray());
        Assert.Equal("$root is not defined", exception.Message);
    }

    [Fact]
    public void External_variable_name_must_not_start_with_dollar()
    {
        using var input = JsonDocument.Parse("null");
        using var value = JsonDocument.Parse("1");
        var variables = new Dictionary<string, JsonElement>
        {
            ["$root"] = value.RootElement,
        };

        var exception = Assert.Throws<ArgumentException>(() => Jq.Evaluate("$root", input.RootElement, variables).ToArray());
        Assert.Contains("must not start with '$'", exception.Message);
    }

    [Fact]
    public void External_variable_name_must_be_valid_identifier()
    {
        using var input = JsonDocument.Parse("null");
        using var value = JsonDocument.Parse("1");
        var variables = new Dictionary<string, JsonElement>
        {
            ["not-valid"] = value.RootElement,
        };

        var exception = Assert.Throws<ArgumentException>(() => Jq.Evaluate(".", input.RootElement, variables).ToArray());
        Assert.Contains("is not a valid jq identifier", exception.Message);
    }

    [Fact]
    public void External_variable_is_shadowed_by_as_binding()
    {
        using var input = JsonDocument.Parse("null");
        using var value = JsonDocument.Parse("1");
        var variables = new Dictionary<string, JsonElement>
        {
            ["x"] = value.RootElement,
        };

        var results = Jq.Evaluate("2 as $x | $x", input.RootElement, variables)
            .Select(static e => e.GetInt32())
            .ToArray();

        Assert.Equal([2], results);
    }

    [Fact]
    public void Multiple_external_variables_can_be_bound()
    {
        using var input = JsonDocument.Parse("null");
        using var a = JsonDocument.Parse("\"A\"");
        using var b = JsonDocument.Parse("\"B\"");
        var variables = new Dictionary<string, JsonElement>
        {
            ["a"] = a.RootElement,
            ["b"] = b.RootElement,
        };

        var results = Jq.Evaluate("[$a, $b]", input.RootElement, variables)
            .Select(static e => JsonSerializer.Serialize(e))
            .ToArray();

        Assert.Equal(["""["A","B"]"""], results);
    }
}
