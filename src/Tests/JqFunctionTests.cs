using System.Text.Json;
using Devlooped;

namespace Devlooped.Tests;

public class JqFunctionTests
{
    private static string[] EvaluateToStrings(string expression, string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        return Jq.Evaluate(expression, document.RootElement).Select(static e => JsonSerializer.Serialize(e)).ToArray();
    }

    [Fact]
    public void Function_zero_arg_adds_one()
        => Assert.Equal(["6"], EvaluateToStrings("def f: . + 1; f", "5"));

    [Fact]
    public void Function_zero_arg_can_emit_multiple_outputs()
        => Assert.Equal(["1000", "2000"], EvaluateToStrings("def f: (1000,2000); f", "null"));

    [Fact]
    public void Function_zero_arg_can_chain_with_another_function()
        => Assert.Equal(["8"], EvaluateToStrings("def f: . + 1; def g: . * 2; f | g", "3"));

    [Fact]
    public void Function_filter_parameter_identity()
        => Assert.Equal(["6"], EvaluateToStrings("def f(x): x; f(. + 1)", "5"));

    [Fact]
    public void Function_filter_parameter_re_evaluates_on_new_input()
        => Assert.Equal(["7"], EvaluateToStrings("def f(x): x | x; f(.+1)", "5"));

    [Fact]
    public void Function_filter_parameter_can_be_used_multiple_times_in_array()
        => Assert.Equal(["[6,6]"], EvaluateToStrings("def f(x): [x, x]; f(. + 1)", "5"));

    [Fact]
    public void Function_value_parameter_single_argument()
        => Assert.Equal(["11"], EvaluateToStrings("def f($a): $a + 1; f(10)", "null"));

    [Fact]
    public void Function_value_parameters_two_arguments()
        => Assert.Equal(["7"], EvaluateToStrings("def f($a; $b): $a + $b; f(3; 4)", "null"));

    [Fact]
    public void Function_mixed_filter_and_value_parameters()
        => Assert.Equal(["13"], EvaluateToStrings("def f(a; $b): a + $b; f(. * 2; 3)", "5"));

    [Fact]
    public void Function_lexical_scoping_captures_original_binding()
        => Assert.Equal(["6"], EvaluateToStrings("def f: . + 1; def g: f; def f: . + 100; g", "5"));

    [Fact]
    public void Function_later_definition_shadows_earlier_definition()
        => Assert.Equal(["2"], EvaluateToStrings("def f: 1; def f: 2; f", "null"));

    [Fact]
    public void Function_recursion_factorial()
        => Assert.Equal(["120"], EvaluateToStrings("def fact: if . <= 1 then 1 else . * ((. - 1) | fact) end; 5 | fact", "null"));

    [Fact]
    public void Function_recursion_sum_array()
        => Assert.Equal(["15"], EvaluateToStrings("def sum: if length == 0 then 0 else first + (.[1:] | sum) end; sum", "[1,2,3,4,5]"));

    [Fact]
    public void Function_closure_uses_captured_environment_for_filter_argument()
        => Assert.Equal(["[1,2000]"], EvaluateToStrings("2000 as $x | def f(x): 1 as $x | [$x, x]; f($x)", "null"));

    [Fact]
    public void Function_zero_arg_user_definition_shadows_empty_builtin()
        => Assert.Equal(["42"], EvaluateToStrings("def empty: 42; empty", "null"));

    [Fact]
    public void Function_zero_arg_user_definition_shadows_length_builtin()
        => Assert.Equal(["99"], EvaluateToStrings("def length: 99; length", "[1,2,3]"));

    [Fact]
    public void Function_nested_definition_inside_body()
        => Assert.Equal(["7"], EvaluateToStrings("def f: def g: . + 1; g | g; f", "5"));

    [Fact]
    public void Function_multiple_arities_can_coexist()
        => Assert.Equal(["[1,15]"], EvaluateToStrings("def f: 1; def f(x): x + 10; [f, f(5)]", "null"));

    [Fact]
    public void Function_filter_argument_generator_single_call_with_stream_input()
        => Assert.Equal(["[1,2,3]"], EvaluateToStrings("def f(x): [x]; f(1,2,3)", "null"));

    [Fact]
    public void Function_filter_argument_generator_re_evaluates_per_use()
        => Assert.Equal(["1", "2", "1", "2"], EvaluateToStrings("def f(x): x | x; f(1,2)", "null"));

    [Fact]
    public void Function_inner_definition_does_not_leak_to_outer_scope()
        => Assert.Equal(["2", "1"], EvaluateToStrings("def outer: 1; (def outer: 2; outer), outer", "null"));

    [Fact]
    public void Function_recursive_accumulator_pattern_with_limit()
        => Assert.Equal(["2", "4", "8", "16", "32"], EvaluateToStrings("def repeat(f): ., (f | repeat(f)); 1 | limit(5; (repeat(. * 2) | select(. > 1)))", "null"));
}
