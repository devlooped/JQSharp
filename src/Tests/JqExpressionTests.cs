using System.Text.Json;
using Devlooped;

namespace Devlooped.Tests;

public class JqExpressionTests
{
    [Fact]
    public void Parse_concat()
    {
        var expression = Jq.Parse(
            """
            { "$type": (."type" // "typing") } + .
            """);

        Assert.NotNull(expression);
    }

    [Fact]
    public void Parsed_concat_expression_includes_existing_type_when_present()
    {
        using var doc = JsonDocument.Parse("""{"type":"event","name":"foo"}""");
        var results = Jq.Evaluate("""{ "$type": (."type" // "typing") } + .""", doc.RootElement)
            .Select(static element => JsonSerializer.Serialize(element))
            .ToArray();

        Assert.Single(results);
        using var output = JsonDocument.Parse(results[0]);
        Assert.Equal("event", output.RootElement.GetProperty("$type").GetString());
        Assert.Equal("event", output.RootElement.GetProperty("type").GetString());
        Assert.Equal("foo", output.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void Parsed_concat_expression_defaults_type_when_missing()
    {
        using var doc = JsonDocument.Parse("""{"name":"foo"}""");
        var results = Jq.Evaluate("""{ "$type": (."type" // "typing") } + .""", doc.RootElement)
            .Select(static element => JsonSerializer.Serialize(element))
            .ToArray();

        Assert.Single(results);
        using var output = JsonDocument.Parse(results[0]);
        Assert.Equal("typing", output.RootElement.GetProperty("$type").GetString());
        Assert.Equal("foo", output.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void Parse_returns_non_null_expression()
    {
        var expression = Jq.Parse(".");
        Assert.NotNull(expression);
    }

    [Fact]
    public void Parse_throws_for_empty_expression()
        => Assert.Throws<JqException>(() => Jq.Parse(""));

    [Fact]
    public void Parse_throws_for_whitespace_expression()
        => Assert.Throws<JqException>(() => Jq.Parse("   "));

    [Fact]
    public void Parse_throws_for_invalid_expression()
        => Assert.Throws<JqException>(() => Jq.Parse("[invalid!!!"));

    [Fact]
    public void Parsed_expression_evaluates_correctly()
    {
        var expression = Jq.Parse(".name");
        using var doc = JsonDocument.Parse("""{"name":"Alice"}""");

        var results = expression.Evaluate(doc.RootElement)
            .Select(e => JsonSerializer.Serialize(e))
            .ToArray();

        Assert.Equal(["\"Alice\""], results);
    }

    [Fact]
    public void Parsed_expression_can_be_reused_across_inputs()
    {
        var expression = Jq.Parse(".x + .y");

        using var doc1 = JsonDocument.Parse("""{"x":1,"y":2}""");
        using var doc2 = JsonDocument.Parse("""{"x":10,"y":20}""");
        using var doc3 = JsonDocument.Parse("""{"x":100,"y":200}""");

        var r1 = expression.Evaluate(doc1.RootElement).Select(e => e.GetInt32()).Single();
        var r2 = expression.Evaluate(doc2.RootElement).Select(e => e.GetInt32()).Single();
        var r3 = expression.Evaluate(doc3.RootElement).Select(e => e.GetInt32()).Single();

        Assert.Equal(3, r1);
        Assert.Equal(30, r2);
        Assert.Equal(300, r3);
    }

    [Fact]
    public void Parsed_expression_matches_Evaluate_convenience_method()
    {
        using var doc = JsonDocument.Parse("""[1,2,3]""");

        var expression = Jq.Parse("map(. * 2)");

        var fromExpression = expression.Evaluate(doc.RootElement)
            .Select(e => JsonSerializer.Serialize(e))
            .ToArray();

        var fromConvenience = Jq.Evaluate("map(. * 2)", doc.RootElement)
            .Select(e => JsonSerializer.Serialize(e))
            .ToArray();

        Assert.Equal(fromConvenience, fromExpression);
    }

    [Fact]
    public void Parsed_expression_handles_multiple_outputs()
    {
        var expression = Jq.Parse(".[]");
        using var doc = JsonDocument.Parse("[10, 20, 30]");

        var results = expression.Evaluate(doc.RootElement)
            .Select(e => e.GetInt32())
            .ToArray();

        Assert.Equal([10, 20, 30], results);
    }

    [Fact]
    public void Parsed_expression_halt_yields_empty()
    {
        var expression = Jq.Parse("halt");
        using var doc = JsonDocument.Parse("null");

        var results = expression.Evaluate(doc.RootElement).ToArray();

        Assert.Empty(results);
    }

    [Fact]
    public void Parsed_expression_break_without_label_throws()
    {
        // A bare break without a surrounding label should throw JqException.
        var expression = Jq.Parse("label $out | foreach .[] as $x (0; . + $x; if . > 3 then ., break $out else . end)");
        using var doc = JsonDocument.Parse("[1,2,3,5]");

        // This should succeed (label is present).
        var results = expression.Evaluate(doc.RootElement)
            .Select(e => e.GetInt32())
            .ToArray();

        Assert.Equal([1, 3, 6], results);
    }
}
