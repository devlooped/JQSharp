using Devlooped.Core.Values;
using static Devlooped.Tests.Core.Values.ValueHelpers;

namespace Devlooped.Tests.Core.Values;

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
    [InlineData("1", "1", "2", "error: Cannot index number with object")]
    [InlineData("{}", "1", "2", "error: Cannot index object with object")]
    [InlineData("[1]", "\"a\"", "2", "error: Start and end indices of an array slice must be numbers")]
    public void Slices(string input, string start, string end, string expected)
        => Assert.Equal(expected, Slice(input, start, end));

    [Fact]
    public void Explode_gives_code_points()
        => Assert.Equal("[97,233,128512,98]", J(JqValue.FromArray(CodePoints.Explode("aé😀b"))));

    static string ImplodeText(string input)
        => ValueOps.TryImplode(V(input), out var result, out var error) ? J(result) : "error: " + error.AsString();

    [Theory]
    [InlineData("[97,233,128512,98]", "\"aé😀b\"")]
    [InlineData("[]", "\"\"")]
    [InlineData("[65.9]", "\"A\"")]
    [InlineData("[1114111]", "\"\U0010FFFF\"")]
    [InlineData("[1114112]", "error: array ([1114112]) can't be imploded, unicode codepoint needs to be a valid code point")]
    [InlineData("[-1]", "error: array ([-1]) can't be imploded, unicode codepoint needs to be a valid code point")]
    [InlineData("[55296]", "error: array ([55296]) can't be imploded, unicode codepoint needs to be a valid code point")]
    [InlineData("[\"a\"]", "error: array ([\"a\"]) can't be imploded, unicode codepoint needs to be numeric")]
    [InlineData("[nan]", "error: array ([null]) can't be imploded, unicode codepoint needs to be numeric")]
    [InlineData("\"a\"", "error: string (\"a\") cannot be imploded, must be an array of codepoints")]
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
