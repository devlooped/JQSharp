using System.Text.Json;
using Devlooped;

namespace Devlooped.Tests;

public class JqExpressionTests
{
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
    public void Uncaught_error_throws_JqException()
        => Assert.Throws<JqException>(() => EvaluateToStrings("\"hello\" | error", "null"));

    // Programs on several lines: the one-line programs of spec/tests cannot carry them.

    [Fact]
    public void Leading_comment_line_is_ignored()
        => Assert.Equal(["42"], EvaluateToStrings("""
            # comment
            .foo
            """, """{"foo":42}"""));

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

    static string[] EvaluateToStrings(string expression, string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        return Jq.Evaluate(expression, document.RootElement)
            .Select(static element => JsonSerializer.Serialize(element))
            .ToArray();
    }
}
