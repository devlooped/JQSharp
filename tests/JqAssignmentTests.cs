using System.Text.Json;
using Devlooped;

namespace Devlooped.Tests;

public class JqAssignmentTests
{
    [Fact]
    public void Update_assignment_updates_field()
        => Assert.Equal(["{\"foo\":43}"], EvaluateToStrings(".foo |= .+1", """{"foo":42}"""));

    [Fact]
    public void Update_assignment_updates_array_elements()
        => Assert.Equal(["[2,3,4]"], EvaluateToStrings(".[] |= .+1", "[1,2,3]"));

    [Fact]
    public void Update_assignment_updates_root()
        => Assert.Equal(["42"], EvaluateToStrings(". |= .+1", "41"));

    [Fact]
    public void Plain_assignment_sets_literal_value()
        => Assert.Equal(["{\"foo\":\"bar\"}"], EvaluateToStrings(".foo = \"bar\"", """{"foo":1}"""));

    [Fact]
    public void Plain_assignment_uses_original_input_for_rhs()
        => Assert.Equal(["{\"foo\":2,\"bar\":2}"], EvaluateToStrings(".foo = .bar", """{"foo":1,"bar":2}"""));

    [Fact]
    public void Plain_assignment_sets_each_array_element()
        => Assert.Equal(["[1,1,1]"], EvaluateToStrings(".[] = 1", "[4,5,6]"));

    [Fact]
    public void Compound_add_assignment_updates_elements()
        => Assert.Equal(["[3,4,5]"], EvaluateToStrings(".[] += 2", "[1,2,3]"));

    [Fact]
    public void Compound_subtract_assignment_updates_elements()
        => Assert.Equal(["[3,4,5]"], EvaluateToStrings(".[] -= 2", "[5,6,7]"));

    [Fact]
    public void Compound_multiply_assignment_updates_elements()
        => Assert.Equal(["[2,4,6]"], EvaluateToStrings(".[] *= 2", "[1,2,3]"));

    [Fact]
    public void Compound_divide_assignment_updates_elements()
        => Assert.Equal(["[1,2,3]"], EvaluateToStrings(".[] /= 2", "[2,4,6]"));

    [Fact]
    public void Compound_modulo_assignment_updates_elements()
        => Assert.Equal(["[1,0,1]"], EvaluateToStrings(".[] %= 2", "[1,2,3]"));

    [Fact]
    public void Alternative_assignment_sets_default_when_missing_or_null()
        => Assert.Equal(["{\"foo\":\"default\"}"], EvaluateToStrings(".foo //= \"default\"", """{"foo":null}"""));

    [Fact]
    public void Alternative_assignment_keeps_existing_truthy_value()
        => Assert.Equal(["{\"foo\":\"value\"}"], EvaluateToStrings(".foo //= \"default\"", """{"foo":"value"}"""));

    [Fact]
    public void Update_assignment_supports_multi_path()
        => Assert.Equal(["{\"a\":2,\"b\":3}"], EvaluateToStrings("(.a, .b) |= .+1", """{"a":1,"b":2}"""));

    [Fact]
    public void Update_assignment_supports_select_in_path()
        => Assert.Equal(["[1,20,30]"], EvaluateToStrings("(.[] | select(. >= 2)) |= . * 10", "[1,2,3]"));

    [Fact]
    public void Update_assignment_deletes_path_when_rhs_is_empty()
        => Assert.Equal(["[2,3]"], EvaluateToStrings("(.[] | select(. < 2)) |= empty", "[0,1,2,3]"));

    [Fact]
    public void Plain_assignment_auto_vivifies_arrays_and_objects()
        => Assert.Equal(["[4,null,[null,null,null,1]]"], EvaluateToStrings(".[2][3] = 1", "[4]"));

    [Fact]
    public void Plain_assignment_supports_nested_path()
        => Assert.Equal(["{\"foo\":[null,{\"bar\":\"x\"}]}"], EvaluateToStrings(".foo[1].bar = \"x\"", "{}"));

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

