using System.Text.Json;
using Devlooped;

namespace Devlooped.Tests;

public class JqTryCatchTests
{
    [Fact]
    public void Try_field_access_on_number_yields_empty()
        => Assert.Empty(EvaluateToStrings("try .foo", "42"));

    [Fact]
    public void Try_field_access_on_object_yields_value()
        => Assert.Equal(["\"bar\""], EvaluateToStrings("try .foo", """{"foo":"bar"}"""));

    [Fact]
    public void Try_error_catch_identity_returns_error_input()
        => Assert.Equal(["\"custom error\""], EvaluateToStrings("try error catch .", "\"custom error\""));

    [Fact]
    public void Try_nested_field_access_catch_identity_returns_non_empty_error_message()
    {
        var results = Evaluate("try (.foo | .bar) catch .", "42");
        Assert.Single(results);
        Assert.Equal(JsonValueKind.String, results[0].ValueKind);
        Assert.False(string.IsNullOrEmpty(results[0].GetString()));
    }

    [Fact]
    public void String_pipe_error_throws_jqexception()
        => Assert.Throws<JqException>(() => EvaluateToStrings("\"hello\" | error", "null"));

    [Fact]
    public void Number_pipe_error_throws_jqexception()
        => Assert.Throws<JqException>(() => EvaluateToStrings("42 | error", "null"));

    [Fact]
    public void Optional_field_access_on_number_yields_empty()
        => Assert.Empty(EvaluateToStrings(".foo?", "42"));

    [Fact]
    public void Optional_field_access_on_object_yields_value()
        => Assert.Equal(["1"], EvaluateToStrings(".foo?", """{"foo":1}"""));

    [Fact]
    public void Try_error_without_catch_yields_empty()
        => Assert.Empty(EvaluateToStrings("try error", "\"test\""));

    [Fact]
    public void Nested_try_catch_returns_outer_caught_value()
        => Assert.Equal(["\"nested\""], EvaluateToStrings("try (try error catch error) catch .", "\"nested\""));

    [Fact]
    public void Try_error_with_number_input_catches_number()
        => Assert.Equal(["42"], EvaluateToStrings("try (42 | error) catch .", "null"));

    [Fact]
    public void Try_error_with_null_input_catches_null()
        => Assert.Equal(["null"], EvaluateToStrings("try (null | error) catch .", "null"));

    [Fact]
    public void Try_error_with_array_input_catches_array()
        => Assert.Equal(["[1,2]"], EvaluateToStrings("try ([1,2] | error) catch .", "null"));

    [Fact]
    public void Try_error_with_true_input_catches_true()
        => Assert.Equal(["true"], EvaluateToStrings("try (true | error) catch .", "null"));

    [Fact]
    public void Label_break_stops_iteration_and_keeps_prior_outputs()
        => Assert.Equal(["[0,1,\"hi!\"]"], EvaluateToStrings("[(label $here | .[] | if .>1 then break $here else . end), \"hi!\"]", "[0,1,2]"));

    [Fact]
    public void Label_break_works_inside_foreach()
        => Assert.Equal(["[11,22,33]"], EvaluateToStrings("[label $out | foreach .[] as $item ([3, null]; if .[0] < 1 then break $out else [.[0] -1, $item] end; .[1])]", "[11,22,33,44,55,66,77,88,99]"));

    [Fact]
    public void Break_is_not_caught_by_try_catch()
        => Assert.Empty(EvaluateToStrings("label $out | try (0, break $out, 1) catch 99", "null"));

    [Fact]
    public void Break_without_matching_label_throws_parse_error()
    {
        var exception = Assert.Throws<JqException>(() => EvaluateToStrings(". as $foo | break $foo", "null"));
        Assert.Equal("$*label-foo is not defined", exception.Message);
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
