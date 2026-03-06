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
}
