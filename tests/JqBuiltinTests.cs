using System.Text.Json;
using Devlooped;

namespace Devlooped.Tests;

public class JqBuiltinTests
{
    [Fact]
    public void Type_returns_null_for_null()
        => Assert.Equal(["\"null\""], EvaluateToStrings("type", "null"));

    [Fact]
    public void Type_returns_number_for_number()
        => Assert.Equal(["\"number\""], EvaluateToStrings("type", "42"));

    [Fact]
    public void Type_returns_string_for_string()
        => Assert.Equal(["\"string\""], EvaluateToStrings("type", "\"hello\""));

    [Fact]
    public void Type_returns_boolean_for_true()
        => Assert.Equal(["\"boolean\""], EvaluateToStrings("type", "true"));

    [Fact]
    public void Type_returns_array_for_array()
        => Assert.Equal(["\"array\""], EvaluateToStrings("type", "[1,2,3]"));

    [Fact]
    public void Type_returns_object_for_object()
        => Assert.Equal(["\"object\""], EvaluateToStrings("type", """{"a":1}"""));

    [Fact]
    public void Length_for_array_returns_count()
        => Assert.Equal(["3"], EvaluateToStrings("length", "[1,2,3]"));

    [Fact]
    public void Length_for_string_returns_character_count()
        => Assert.Equal(["5"], EvaluateToStrings("length", "\"hello\""));

    [Fact]
    public void Length_for_object_returns_property_count()
        => Assert.Equal(["2"], EvaluateToStrings("length", """{"a":1,"b":2}"""));

    [Fact]
    public void Length_for_null_returns_zero()
        => Assert.Equal(["0"], EvaluateToStrings("length", "null"));

    [Fact]
    public void Length_for_positive_number_returns_number()
        => Assert.Equal(["42"], EvaluateToStrings("length", "42"));

    [Fact]
    public void Length_for_negative_number_returns_absolute_value()
        => Assert.Equal(["5"], EvaluateToStrings("length", "-5"));

    [Fact]
    public void Length_for_fractional_number_returns_number()
        => Assert.Equal(["3.5"], EvaluateToStrings("length", "3.5"));

    [Fact]
    public void Length_throws_for_boolean()
        => Assert.ThrowsAny<Exception>(() => EvaluateToStrings("length", "true"));

    [Fact]
    public void Utf8bytelength_for_ascii_string_returns_byte_count()
        => Assert.Equal(["5"], EvaluateToStrings("utf8bytelength", "\"hello\""));

    [Fact]
    public void Utf8bytelength_for_empty_string_returns_zero()
        => Assert.Equal(["0"], EvaluateToStrings("utf8bytelength", "\"\""));

    [Fact]
    public void Infinite_returns_double_max_value()
    {
        var results = Evaluate("infinite", "null");
        Assert.Single(results);
        Assert.Equal(double.MaxValue, results[0].GetDouble());
    }

    [Fact]
    public void Nan_serializes_as_null()
        => Assert.Equal(["null"], EvaluateToStrings("nan", "null"));

    [Fact]
    public void Isnan_returns_true_for_nan()
        => Assert.Equal(["true"], EvaluateToStrings("nan | isnan", "null"));

    [Fact]
    public void Isnan_returns_false_for_non_nan()
        => Assert.Equal(["false"], EvaluateToStrings("1 | isnan", "null"));

    [Fact]
    public void Isinfinite_returns_true_for_infinite()
        => Assert.Equal(["true"], EvaluateToStrings("infinite | isinfinite", "null"));

    [Fact]
    public void Isinfinite_returns_false_for_finite_number()
        => Assert.Equal(["false"], EvaluateToStrings("1 | isinfinite", "null"));

    [Fact]
    public void Isfinite_returns_true_for_one()
        => Assert.Equal(["true"], EvaluateToStrings("1 | isfinite", "null"));

    [Fact]
    public void Isfinite_returns_true_for_zero()
        => Assert.Equal(["true"], EvaluateToStrings("0 | isfinite", "null"));

    [Fact]
    public void Isnormal_returns_true_for_one()
        => Assert.Equal(["true"], EvaluateToStrings("1 | isnormal", "null"));

    [Fact]
    public void Isnormal_returns_false_for_zero()
        => Assert.Equal(["false"], EvaluateToStrings("0 | isnormal", "null"));

    [Fact]
    public void Arrays_selector_filters_only_arrays()
        => Assert.Equal(["[[]]"], EvaluateToStrings("[.[] | arrays]", """[1,"a",[],{},null,true]"""));

    [Fact]
    public void Objects_selector_filters_only_objects()
        => Assert.Equal(["[{}]"], EvaluateToStrings("[.[] | objects]", """[1,"a",[],{},null,true]"""));

    [Fact]
    public void Strings_selector_filters_only_strings()
        => Assert.Equal(["[\"a\"]"], EvaluateToStrings("[.[] | strings]", """[1,"a",[],{},null,true]"""));

    [Fact]
    public void Numbers_selector_filters_only_numbers()
        => Assert.Equal(["[1]"], EvaluateToStrings("[.[] | numbers]", """[1,"a",[],{},null,true]"""));

    [Fact]
    public void Booleans_selector_filters_only_booleans()
        => Assert.Equal(["[true]"], EvaluateToStrings("[.[] | booleans]", """[1,"a",[],{},null,true]"""));

    [Fact]
    public void Nulls_selector_filters_only_nulls()
        => Assert.Equal(["[null]"], EvaluateToStrings("[.[] | nulls]", """[1,"a",[],{},null,true]"""));

    [Fact]
    public void Values_selector_filters_out_nulls()
        => Assert.Equal(["[1,\"a\",[],{},true]"], EvaluateToStrings("[.[] | values]", """[1,"a",[],{},null,true]"""));

    [Fact]
    public void Scalars_selector_filters_only_scalars()
        => Assert.Equal(["[1,\"a\",null,true]"], EvaluateToStrings("[.[] | scalars]", """[1,"a",[],{},null,true]"""));

    [Fact]
    public void Iterables_selector_filters_arrays_and_objects()
        => Assert.Equal(["[[],{}]"], EvaluateToStrings("[.[] | iterables]", """[1,"a",[],{},null,true]"""));

    [Fact]
    public void Keys_sorts_object_keys()
        => Assert.Equal(["[\"a\",\"b\"]"], EvaluateToStrings("keys", """{"b":2,"a":1}"""));

    [Fact]
    public void Keys_unsorted_preserves_insertion_order()
        => Assert.Equal(["[\"b\",\"a\"]"], EvaluateToStrings("""[{"key":"b","value":2},{"key":"a","value":1}] | from_entries | keys_unsorted""", "null"));

    [Fact]
    public void Keys_for_array_returns_indices()
        => Assert.Equal(["[0,1,2]"], EvaluateToStrings("keys", "[1,2,3]"));

    [Fact]
    public void Sort_orders_numbers()
        => Assert.Equal(["[1,2,3]"], EvaluateToStrings("sort", "[3,1,2]"));

    [Fact]
    public void Reverse_reverses_array()
        => Assert.Equal(["[2,1,3]"], EvaluateToStrings("reverse", "[3,1,2]"));

    [Fact]
    public void Unique_returns_sorted_unique_values()
        => Assert.Equal(["[1,2,3]"], EvaluateToStrings("unique", "[3,1,2,1]"));

    [Fact]
    public void Flatten_concatenates_nested_arrays()
        => Assert.Equal(["[1,2,3]"], EvaluateToStrings("flatten", "[[1,2],[3]]"));

    [Fact]
    public void Flatten_recursively_flattens_nested_structure()
        => Assert.Equal(["[1,2,3]"], EvaluateToStrings("flatten", "[1,[2,[3]]]"));

    [Fact]
    public void Add_sums_numbers()
        => Assert.Equal(["6"], EvaluateToStrings("add", "[1,2,3]"));

    [Fact]
    public void Add_concatenates_strings()
        => Assert.Equal(["\"ab\""], EvaluateToStrings("add", """["a","b"]"""));

    [Fact]
    public void Add_for_empty_array_returns_null()
        => Assert.Equal(["null"], EvaluateToStrings("add", "[]"));

    [Fact]
    public void Add_concatenates_arrays()
        => Assert.Equal(["[1,2]"], EvaluateToStrings("add", "[[1],[2]]"));

    [Fact]
    public void Any_returns_true_when_any_truthy()
        => Assert.Equal(["true"], EvaluateToStrings("any", "[true,false]"));

    [Fact]
    public void Any_returns_false_when_no_truthy_values()
        => Assert.Equal(["false"], EvaluateToStrings("any", "[false,null]"));

    [Fact]
    public void Any_for_empty_array_returns_false()
        => Assert.Equal(["false"], EvaluateToStrings("any", "[]"));

    [Fact]
    public void All_returns_true_when_all_truthy()
        => Assert.Equal(["true"], EvaluateToStrings("all", "[true,true]"));

    [Fact]
    public void All_returns_false_when_any_falsy()
        => Assert.Equal(["false"], EvaluateToStrings("all", "[1,false]"));

    [Fact]
    public void All_for_empty_array_returns_true()
        => Assert.Equal(["true"], EvaluateToStrings("all", "[]"));

    [Fact]
    public void Min_returns_smallest_number()
        => Assert.Equal(["1"], EvaluateToStrings("min", "[3,1,2]"));

    [Fact]
    public void Max_returns_largest_number()
        => Assert.Equal(["3"], EvaluateToStrings("max", "[3,1,2]"));

    [Fact]
    public void To_entries_converts_object_to_key_value_array()
        => Assert.Equal(["[{\"key\":\"a\",\"value\":1},{\"key\":\"b\",\"value\":2}]"], EvaluateToStrings("to_entries", """{"a":1,"b":2}"""));

    [Fact]
    public void From_entries_builds_object_from_key_field()
        => Assert.Equal(["{\"a\":1}"], EvaluateToStrings("from_entries", """[{"key":"a","value":1}]"""));

    [Fact]
    public void From_entries_builds_object_from_name_field()
        => Assert.Equal(["{\"b\":2}"], EvaluateToStrings("from_entries", """[{"name":"b","value":2}]"""));

    [Fact]
    public void Paths_for_nested_object_returns_all_paths()
        => Assert.Equal(["[[\"a\"],[\"a\",\"b\"]]"], EvaluateToStrings("[paths]", """{"a":{"b":1}}"""));

    [Fact]
    public void Paths_for_nested_array_returns_all_paths()
        => Assert.Equal(["[[0],[1],[1,0]]"], EvaluateToStrings("[paths]", "[1,[2]]"));

    [Fact]
    public void Paths_for_null_returns_empty_array()
        => Assert.Equal(["[]"], EvaluateToStrings("[paths]", "null"));

    [Fact]
    public void Transpose_for_rectangular_matrix()
        => Assert.Equal(["[[1,3],[2,4]]"], EvaluateToStrings("transpose", "[[1,2],[3,4]]"));

    [Fact]
    public void Transpose_pads_missing_values_with_null()
        => Assert.Equal(["[[1,3],[2,null]]"], EvaluateToStrings("transpose", "[[1,2],[3]]"));

    [Fact]
    public void Combinations_returns_cartesian_product()
        => Assert.Equal(["[[1,3],[1,4],[2,3],[2,4]]"], EvaluateToStrings("[combinations]", "[[1,2],[3,4]]"));

    [Fact]
    public void Tonumber_parses_numeric_string()
        => Assert.Equal(["42"], EvaluateToStrings("tonumber", "\"42\""));

    [Fact]
    public void Tonumber_for_number_returns_same_number()
        => Assert.Equal(["42"], EvaluateToStrings("tonumber", "42"));

    [Fact]
    public void Tostring_for_number()
        => Assert.Equal(["\"42\""], EvaluateToStrings("tostring", "42"));

    [Fact]
    public void Tostring_for_string()
        => Assert.Equal(["\"hello\""], EvaluateToStrings("tostring", "\"hello\""));

    [Fact]
    public void Tostring_for_boolean()
        => Assert.Equal(["\"true\""], EvaluateToStrings("tostring", "true"));

    [Fact]
    public void Tostring_for_null()
        => Assert.Equal(["\"null\""], EvaluateToStrings("tostring", "null"));

    [Fact]
    public void Tojson_serializes_string_as_json_string_literal()
    {
        var results = Evaluate("tojson", "\"hello\"");
        Assert.Single(results);
        Assert.Equal("\"hello\"", results[0].GetString());
    }

    [Fact]
    public void Fromjson_parses_embedded_json_object_string()
        => Assert.Equal(["{\"a\":1}"], EvaluateToStrings("fromjson", "\"{\\\"a\\\":1}\""));

    [Fact]
    public void Explode_converts_string_to_code_points()
        => Assert.Equal(["[104,101,108,108,111]"], EvaluateToStrings("explode", "\"hello\""));

    [Fact]
    public void Implode_converts_code_points_to_string()
        => Assert.Equal(["\"hello\""], EvaluateToStrings("implode", "[104,101,108,108,111]"));

    [Fact]
    public void Ascii_downcase_converts_uppercase_ascii_to_lowercase()
        => Assert.Equal(["\"hello\""], EvaluateToStrings("ascii_downcase", "\"Hello\""));

    [Fact]
    public void Ascii_upcase_converts_lowercase_ascii_to_uppercase()
        => Assert.Equal(["\"HELLO\""], EvaluateToStrings("ascii_upcase", "\"hello\""));

    [Fact]
    public void Abs_for_negative_number()
        => Assert.Equal(["5"], EvaluateToStrings("abs", "-5"));

    [Fact]
    public void Abs_for_positive_number()
        => Assert.Equal(["42"], EvaluateToStrings("abs", "42"));

    [Fact]
    public void Floor_for_positive_fraction()
        => Assert.Equal(["1"], EvaluateToStrings("floor", "1.5"));

    [Fact]
    public void Floor_for_negative_fraction()
        => Assert.Equal(["-2"], EvaluateToStrings("floor", "-1.5"));

    [Fact]
    public void Sqrt_for_perfect_square()
        => Assert.Equal(["2"], EvaluateToStrings("sqrt", "4"));

    [Fact]
    public void Sqrt_for_two_is_approximately_expected_value()
    {
        var results = Evaluate("sqrt", "2");
        Assert.Single(results);
        Assert.Equal(1.4142135623730951, results[0].GetDouble(), 12);
    }

    [Fact]
    public void Empty_produces_no_output()
        => Assert.Empty(EvaluateToStrings("empty", "null"));

    [Fact]
    public void First_returns_first_element()
        => Assert.Equal(["1"], EvaluateToStrings("first", "[1,2,3]"));

    [Fact]
    public void Last_returns_last_element()
        => Assert.Equal(["3"], EvaluateToStrings("last", "[1,2,3]"));

    [Fact]
    public void First_for_empty_array_produces_no_output()
        => Assert.Empty(EvaluateToStrings("first", "[]"));

    [Fact]
    public void Last_for_empty_array_produces_no_output()
        => Assert.Empty(EvaluateToStrings("last", "[]"));

    [Fact]
    public void Recurse_matches_recursive_descent_operator()
    {
        var recurse = EvaluateToStrings("recurse", """{"a":{"b":1}}""");
        var recursiveDescent = EvaluateToStrings("..", """{"a":{"b":1}}""");
        Assert.Equal(recursiveDescent, recurse);
    }

    [Fact]
    public void Halt_produces_no_output()
        => Assert.Empty(EvaluateToStrings("halt", "null"));

    [Fact]
    public void Env_type_is_object()
        => Assert.Equal(["\"object\""], EvaluateToStrings("env | type", "null"));

    [Fact]
    public void Builtins_length_is_positive()
        => Assert.Equal(["true"], EvaluateToStrings("builtins | length > 0", "null"));

    [Fact]
    public void Keys_throws_for_null()
        => Assert.ThrowsAny<Exception>(() => EvaluateToStrings("keys", "null"));

    [Fact]
    public void Keys_throws_for_number()
        => Assert.ThrowsAny<Exception>(() => EvaluateToStrings("keys", "42"));

    [Fact]
    public void Abs_throws_for_null()
        => Assert.ThrowsAny<Exception>(() => EvaluateToStrings("abs", "null"));

    [Fact]
    public void Length_throws_for_true()
        => Assert.ThrowsAny<Exception>(() => EvaluateToStrings("length", "true"));

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
