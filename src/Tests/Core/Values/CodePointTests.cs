using JQLite.Core.Values;
using static JQLite.Tests.Core.Values.ValueHelpers;

namespace JQLite.Tests.Core.Values;

public class CodePointTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("aé😀b", 4)]
    [InlineData("😀😀", 2)]
    public void Length_counts_code_points(string text, int length)
        => Assert.Equal(length, CodePoints.Length(text));

    static string Slice(string input, string start, string end)
        => ValueOps.TrySlice(V(input), V(start), V(end), out var result, out var error) ? J(result) : "error: " + error.AsString();

    [Theory]
    [InlineData("\"aé😀b\"", "2", "3", "\"😀\"")]
    [InlineData("\"aé😀b\"", "-2", "null", "\"😀b\"")]
    [InlineData("\"aé😀b\"", "null", "2", "\"aé\"")]
    [InlineData("\"aé😀b\"", "1.5", "2.5", "\"é😀\"")]
    [InlineData("\"aé😀b\"", "3", "1", "\"\"")]
    [InlineData("\"aé😀b\"", "-100", "100", "\"aé😀b\"")]
    [InlineData("\"aé😀b\"", "nan", "nan", "\"aé😀b\"")]
    [InlineData("[1,2,3,4]", "1", "3", "[2,3]")]
    [InlineData("[1,2,3,4]", "-1", "null", "[4]")]
    [InlineData("[1,2,3,4]", "null", "-1", "[1,2,3]")]
    [InlineData("[1,2,3,4]", "0.2", "0.2", "[1]")]
    [InlineData("[1,2,3,4]", "5", "10", "[]")]
    [InlineData("null", "1", "2", "null")]
    [InlineData("[0,1,2,3,4,5,6,7,8,9]", "1.2", "3.5", "[1,2,3]")]
    [InlineData("\"abcdefghi\"", "-2", "null", "\"hi\"")]
    [InlineData("null", "\"a\"", "{}", "null")]
    [InlineData("1", "1", "2", "error: Cannot index number with object ({\"start\":1,\"end\":2})")]
    [InlineData("1", "\"a\"", "2", "error: Cannot index number with object ({\"start\":\"a\",\"end\":2})")]
    [InlineData("{}", "1", "null", "error: Cannot index object with object ({\"start\":1,\"end\":null})")]
    [InlineData("[1]", "\"a\"", "null", "error: Array/string slice indices must be integers")]
    [InlineData("\"ab\"", "0", "true", "error: Array/string slice indices must be integers")]
    public void Slices(string input, string start, string end, string expected)
        => Assert.Equal(expected, Slice(input, start, end));

    static string IndexSlice(string input, string key)
        => ValueOps.TryIndexSlice(V(input), V(key), out var result, out var error) ? J(result) : "error: " + error.AsString();

    // jq 1.8.2: `.[{"start":…,"end":…}]`.
    [Theory]
    [InlineData("[1,2,3]", "{\"start\":1,\"end\":null}", "[2,3]")]
    [InlineData("[1,2,3]", "{\"start\":1,\"end\":2,\"x\":0}", "[2]")]
    [InlineData("\"abc\"", "{\"start\":null,\"end\":null}", "\"abc\"")]
    [InlineData("\"abc\"", "{\"start\":1}", "error: Array/string slice indices must be integers")]
    [InlineData("[1,2,3]", "{\"end\":2}", "error: Array/string slice indices must be integers")]
    [InlineData("[1,2,3]", "{\"start\":\"a\",\"end\":2}", "error: Array/string slice indices must be integers")]
    [InlineData("null", "{\"start\":1}", "null")]
    [InlineData("null", "{\"a\":1}", "null")]
    [InlineData("1", "{\"start\":1}", "error: Cannot index number with object ({\"start\":1})")]
    [InlineData("{\"a\":1}", "{\"start\":1}", "error: Cannot index object with object ({\"start\":1})")]
    public void Slices_by_object_key(string input, string key, string expected)
        => Assert.Equal(expected, IndexSlice(input, key));

    static string ExplodeText(string input)
        => ValueOps.TryExplode(V(input), out var result, out var error) ? J(result) : "error: " + error.AsString();

    [Theory]
    [InlineData("\"aé😀b\"", "[97,233,128512,98]")]
    [InlineData("\"\"", "[]")]
    [InlineData("1", "error: explode input must be a string")]
    [InlineData("[\"a\"]", "error: explode input must be a string")]
    public void Explode(string input, string expected)
        => Assert.Equal(expected, ExplodeText(input));

    static string ImplodeText(string input)
        => ValueOps.TryImplode(V(input), out var result, out var error) ? J(result) : "error: " + error.AsString();

    [Theory]
    [InlineData("[97,233,128512,98]", "\"aé😀b\"")]
    [InlineData("[]", "\"\"")]
    [InlineData("[65.9]", "\"A\"")]
    [InlineData("[1114111]", "\"\U0010FFFF\"")]
    // jq 1.8.2: `[-0.5, 97, 1114112, 55296, -1, 97.9] | implode | explode` is [0,97,65533,65533,65533,97].
    [InlineData("[-0.5,97,1114112,55296,-1,97.9]", "\"\\u0000a\xFFFD\xFFFD\xFFFDa\"")]
    [InlineData("[57343,57344,1e300,-1e300]", "\"\xFFFD\xE000\xFFFD\xFFFD\"")]
    [InlineData("[\"a\"]", "error: string (\"a\") can't be imploded, unicode codepoint needs to be numeric")]
    [InlineData("[1,[1]]", "error: array ([1]) can't be imploded, unicode codepoint needs to be numeric")]
    [InlineData("[nan]", "error: number (null) can't be imploded, unicode codepoint needs to be numeric")]
    [InlineData("[null]", "error: null (null) can't be imploded, unicode codepoint needs to be numeric")]
    [InlineData("\"a\"", "error: implode input must be an array")]
    [InlineData("{}", "error: implode input must be an array")]
    public void Implode(string input, string expected)
        => Assert.Equal(expected, ImplodeText(input));

    // A fact, not a theory: the test host cannot carry lone surrogates in theory data.
    [Fact]
    public void Lone_surrogates_become_replacement_characters()
    {
        const char high = (char)0xD83D;
        const char low = (char)0xDE00;
        const char replacement = (char)0xFFFD;
        Assert.Equal($"a{replacement}b", JqValue.FromString($"a{high}b").AsString());
        Assert.Equal($"{replacement}", JqValue.FromString($"{low}").AsString());
        Assert.Equal($"{replacement}", JqValue.FromString($"{high}").AsString());
        Assert.Equal($"{high}{low}{replacement}", JqValue.FromString($"{high}{low}{high}").AsString());
        Assert.Equal($"{replacement}{replacement}", JqValue.FromString($"{low}{high}").AsString());
    }

    [Fact]
    public void Well_formed_strings_are_not_copied()
    {
        var text = "aé😀b";
        Assert.Same(text, JqValue.FromString(text).AsString());
    }
}
