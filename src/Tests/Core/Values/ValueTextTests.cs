using System.Text;
using Devlooped.Core.Values;
using static Devlooped.Tests.Core.Values.ValueHelpers;

namespace Devlooped.Tests.Core.Values;

public class ValueTextTests
{
    [Theory]
    [InlineData(0d, "0")]
    [InlineData(1d, "1")]
    [InlineData(-1d, "-1")]
    [InlineData(1.5, "1.5")]
    [InlineData(-1.5, "-1.5")]
    [InlineData(0.5, "0.5")]
    [InlineData(100d, "100")]
    [InlineData(1e15, "1000000000000000")]
    [InlineData(1e20, "100000000000000000000")]
    [InlineData(123456789012345680000d, "123456789012345680000")]
    [InlineData(1e21, "1e+21")]
    [InlineData(-1e21, "-1e+21")]
    [InlineData(1.5e300, "1.5e+300")]
    [InlineData(1e-6, "0.000001")]
    [InlineData(1.5e-6, "0.0000015")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(1.23e-18, "1.23e-18")]
    [InlineData(0.1 + 0.2, "0.30000000000000004")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(1.7976931348623157e308, "1.7976931348623157e+308")]
    [InlineData(9007199254740993d, "9007199254740992")]
    [InlineData(123.456, "123.456")]
    [InlineData(double.NaN, "null")]
    [InlineData(double.PositiveInfinity, "1.7976931348623157e+308")]
    [InlineData(double.NegativeInfinity, "-1.7976931348623157e+308")]
    public void Number_text(double number, string expected)
        => Assert.Equal(expected, ValueText.FormatNumber(number));

    [Fact]
    public void Negative_zero_text()
        => Assert.Equal("-0", ValueText.FormatNumber(-0.0));

    [Fact]
    public void Number_text_round_trips()
    {
        var random = new Random(42);
        for (var i = 0; i < 10000; i++)
        {
            var number = BitConverter.Int64BitsToDouble(random.NextInt64());
            if (!double.IsFinite(number))
                continue;
            var text = ValueText.FormatNumber(number);
            Assert.True(ValueText.TryParseNumber(text, out var back), text);
            Assert.Equal(number, back);
        }
    }

    [Theory]
    [InlineData("é<", "\"é<\"")]
    [InlineData("a\"b\\c/d", "\"a\\\"b\\\\c/d\"")]
    [InlineData("\b\f\n\r\t", "\"\\b\\f\\n\\r\\t\"")]
    [InlineData("\u0000\u0001\u001f", "\"\\u0000\\u0001\\u001f\"")]
    [InlineData("\u007f\u0080", "\"\\u007f\u0080\"")]
    [InlineData("😀", "\"😀\"")]
    [InlineData("\u2028", "\"\u2028\"")]
    public void String_escaping(string text, string expected)
        => Assert.Equal(expected, J(JqValue.FromString(text)));

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("\"\"")]
    [InlineData("[1,2.5,\"x\",null,true,false,{},[[]]]")]
    [InlineData("{\"b\":1,\"a\":[1,{\"z\":null,\"y\":\"é\"}],\"c\":{\"d\":[]}}")]
    [InlineData("-0")]
    public void Reader_round_trips(string json)
        => Assert.Equal(json, J(V(json)));

    [Theory]
    [InlineData(" \t\r\n[ 1 , { \"a\" : 2 } ] \n", "[1,{\"a\":2}]")]
    [InlineData("\uFEFF1", "1")]
    [InlineData("1.0", "1")]
    [InlineData("1E2", "100")]
    [InlineData("1e-2", "0.01")]
    [InlineData("1.", "1")]
    [InlineData(".5", "0.5")]
    [InlineData("+1", "1")]
    [InlineData("007", "7")]
    [InlineData("1e1000", "1.7976931348623157e+308")]
    [InlineData("-1e1000", "-1.7976931348623157e+308")]
    [InlineData("1e-1000", "0")]
    [InlineData("nan", "null")]
    [InlineData("NaN", "null")]
    [InlineData("[-nan]", "[null]")]
    [InlineData("Infinity", "1.7976931348623157e+308")]
    [InlineData("-Infinity", "-1.7976931348623157e+308")]
    [InlineData("\"\\u00e9\\/\\\"\"", "\"é/\\\"\"")]
    [InlineData("\"\\ud83d\\ude00\"", "\"😀\"")]
    [InlineData("\"\\ud800\"", "\"\uFFFD\"")]
    [InlineData("\"\\udc00x\"", "\"\uFFFDx\"")]
    [InlineData("\"\\ud800\\ud800\"", "\"\uFFFD\uFFFD\"")]
    [InlineData("\"\\ud800\\u0041\"", "\"\uFFFDA\"")]
    [InlineData("\"a\nb\"", "\"a\\nb\"")]
    [InlineData("{\"a\":1,\"a\":2}", "{\"a\":2}")]
    [InlineData("{\"\\ud800\":1}", "{\"\uFFFD\":1}")]
    public void Reader_values(string json, string expected)
        => Assert.Equal(expected, J(V(json)));

    [Theory]
    [InlineData("", "Expected JSON value at line 1, column 0")]
    [InlineData("   ", "Expected JSON value at line 1, column 3")]
    [InlineData("{", "Unfinished JSON term at EOF at line 1, column 1")]
    [InlineData("[1,2", "Unfinished JSON term at EOF at line 1, column 4")]
    [InlineData("\"abc", "Unfinished JSON term at EOF at line 1, column 4")]
    [InlineData("[1 2]", "Expected separator between values at line 1, column 4")]
    [InlineData("[1,\n2 3]", "Expected separator between values at line 2, column 3")]
    [InlineData("1 2", "Unexpected extra JSON values at line 1, column 3")]
    [InlineData("tru", "Invalid literal at line 1, column 3")]
    [InlineData("nul", "Invalid literal at line 1, column 3")]
    [InlineData("[1x]", "Invalid numeric literal at line 1, column 3")]
    [InlineData("1e", "Invalid numeric literal at line 1, column 2")]
    [InlineData("'a'", "Invalid numeric literal at line 1, column 3")]
    [InlineData("\"\\q\"", "Invalid escape at line 1, column 3")]
    [InlineData("\"\\u12G4\"", "Invalid \\uXXXX escape at line 1, column 6")]
    [InlineData("{1:2}", "Object keys must be strings at line 1, column 2")]
    [InlineData("{\"a\" 1}", "Objects must consist of key:value pairs at line 1, column 6")]
    [InlineData("[1,]", "Expected another array element at line 1, column 4")]
    [InlineData("]", "Unmatched ']' at line 1, column 1")]
    [InlineData("[1}", "Unmatched '}' at line 1, column 3")]
    [InlineData(",", "Expected value before ',' at line 1, column 1")]
    public void Reader_failures(string json, string message)
    {
        Assert.False(ValueText.TryParse(json, out _, out var error));
        Assert.Equal(message, error.AsString());
    }

    [Fact]
    public void Reader_depth_limit()
    {
        var ok = new string('[', 10000) + new string(']', 10000);
        Assert.True(ValueText.TryParse(ok, out var deep, out _));
        Assert.Equal(ok, J(deep));

        Assert.False(ValueText.TryParse(new string('[', 10001), out _, out var error));
        Assert.StartsWith("Exceeds depth limit for parsing", error.AsString());
    }

    [Theory]
    [InlineData("12", 12d)]
    [InlineData("-1.5e3", -1500d)]
    [InlineData("0.1", 0.1)]
    [InlineData(".5", 0.5)]
    [InlineData("5.", 5d)]
    [InlineData("+3", 3d)]
    [InlineData("inf", double.PositiveInfinity)]
    [InlineData("-INFINITY", double.NegativeInfinity)]
    public void Number_reading(string text, double expected)
    {
        Assert.True(ValueText.TryParseNumber(text, out var number));
        Assert.Equal(expected, number);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("0x10")]
    [InlineData(".")]
    [InlineData("-")]
    [InlineData("1e+")]
    [InlineData("1,5")]
    [InlineData("abc")]
    public void Number_reading_failures(string text)
        => Assert.False(ValueText.TryParseNumber(text, out _));

    [Fact]
    public void Nan_reads_as_nan()
    {
        Assert.True(ValueText.TryParseNumber("nan", out var number));
        Assert.True(double.IsNaN(number));
    }

    [Fact]
    public void Text_is_culture_invariant()
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1.5", ValueText.FormatNumber(1.5));
            Assert.Equal("1.5e+300", ValueText.FormatNumber(1.5e300));
            Assert.Equal("[1.5]", J(V("[1.5]")));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Truncated_dump_keeps_whole_code_points()
    {
        Assert.Equal("[1,2]", ValueText.ToJsonTruncated(V("[1,2]"), 14));
        Assert.Equal("{\"a\":\"very ...", ValueText.ToJsonTruncated(V("{\"a\":\"very long string\"}"), 14));
        var text = ValueText.ToJsonTruncated(JqValue.FromString("ééééééééé"), 14);
        Assert.Equal("\"ééééé...", text);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= 14);
    }
}
