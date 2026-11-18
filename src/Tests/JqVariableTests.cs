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

    // Depends on the environment of the test process, so it cannot be a case of spec/tests.
    [Fact]
    public void ENV_has_keys()
        => Assert.Equal(["true"], EvaluateToStrings("($ENV | keys | length) > 0", "null"));

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
        Assert.Equal("variables", exception.ParamName);
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
        Assert.Equal("variables", exception.ParamName);
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
