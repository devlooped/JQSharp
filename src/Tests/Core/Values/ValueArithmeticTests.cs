using Devlooped.Core.Values;
using static Devlooped.Tests.Core.Values.ValueHelpers;

namespace Devlooped.Tests.Core.Values;

public class ValueArithmeticTests
{
    // The result as JSON text, or "error: <message>".
    static string Apply(string op, string a, string b)
    {
        JqValue result, error;
        var ok = op switch
        {
            "+" => ValueOps.TryAdd(V(a), V(b), out result, out error),
            "-" => ValueOps.TrySubtract(V(a), V(b), out result, out error),
            "*" => ValueOps.TryMultiply(V(a), V(b), out result, out error),
            "/" => ValueOps.TryDivide(V(a), V(b), out result, out error),
            "%" => ValueOps.TryModulo(V(a), V(b), out result, out error),
            _ => throw new ArgumentException(op),
        };
        return ok ? J(result) : "error: " + error.AsString();
    }

    [Theory]
    [InlineData("null", "1", "1")]
    [InlineData("1", "null", "1")]
    [InlineData("null", "null", "null")]
    [InlineData("null", "{\"a\":1}", "{\"a\":1}")]
    [InlineData("1", "2", "3")]
    [InlineData("0.1", "0.2", "0.30000000000000004")]
    [InlineData("1e308", "1e308", "1.7976931348623157e+308")]
    [InlineData("\"a\"", "\"b\"", "\"ab\"")]
    [InlineData("[1]", "[2,[3]]", "[1,2,[3]]")]
    [InlineData("[]", "[]", "[]")]
    [InlineData("{\"a\":1,\"b\":2}", "{\"c\":3,\"a\":4}", "{\"a\":4,\"b\":2,\"c\":3}")]
    [InlineData("{\"b\":1}", "{\"a\":2}", "{\"b\":1,\"a\":2}")]
    [InlineData("1", "\"a\"", "error: number (1) and string (\"a\") cannot be added")]
    [InlineData("{}", "[]", "error: object ({}) and array ([]) cannot be added")]
    [InlineData("true", "true", "error: boolean (true) and boolean (true) cannot be added")]
    [InlineData("false", "1", "error: boolean (false) and number (1) cannot be added")]
    [InlineData("{\"a\":\"very long string\"}", "1", "error: object ({\"a\":\"very ...) and number (1) cannot be added")]
    [InlineData("\"ééééééééé\"", "1", "error: string (\"ééééé...) and number (1) cannot be added")]
    public void Add(string a, string b, string expected) => Assert.Equal(expected, Apply("+", a, b));

    [Theory]
    [InlineData("5", "3", "2")]
    [InlineData("[1,2,3,1,[1]]", "[1,[1]]", "[2,3]")]
    [InlineData("[1,2]", "[]", "[1,2]")]
    [InlineData("[{\"a\":1,\"b\":2}]", "[{\"b\":2,\"a\":1}]", "[]")]
    [InlineData("\"a\"", "\"a\"", "error: string (\"a\") and string (\"a\") cannot be subtracted")]
    [InlineData("null", "1", "error: null (null) and number (1) cannot be subtracted")]
    [InlineData("{}", "{}", "error: object ({}) and object ({}) cannot be subtracted")]
    public void Subtract(string a, string b, string expected) => Assert.Equal(expected, Apply("-", a, b));

    [Theory]
    [InlineData("2", "3", "6")]
    [InlineData("\"ab\"", "3", "\"ababab\"")]
    [InlineData("3", "\"ab\"", "\"ababab\"")]
    [InlineData("\"ab\"", "1", "\"ab\"")]
    [InlineData("\"ab\"", "1.9", "\"ab\"")]
    [InlineData("\"ab\"", "0", "null")]
    [InlineData("\"ab\"", "0.5", "null")]
    [InlineData("\"ab\"", "-1", "null")]
    [InlineData("\"ab\"", "nan", "null")]
    [InlineData("\"\"", "5", "\"\"")]
    [InlineData("\"ab\"", "1e9", "error: Repeat string result too long")]
    [InlineData("{\"a\":{\"b\":1},\"c\":1}", "{\"a\":{\"d\":2},\"c\":{\"x\":1}}", "{\"a\":{\"b\":1,\"d\":2},\"c\":{\"x\":1}}")]
    [InlineData("{\"a\":{\"b\":{\"c\":1}}}", "{\"a\":{\"b\":{\"d\":2}},\"e\":3}", "{\"a\":{\"b\":{\"c\":1,\"d\":2}},\"e\":3}")]
    [InlineData("{\"a\":{\"b\":1}}", "{\"a\":1}", "{\"a\":1}")]
    [InlineData("[1]", "2", "error: array ([1]) and number (2) cannot be multiplied")]
    [InlineData("\"a\"", "\"b\"", "error: string (\"a\") and string (\"b\") cannot be multiplied")]
    [InlineData("null", "2", "error: null (null) and number (2) cannot be multiplied")]
    public void Multiply(string a, string b, string expected) => Assert.Equal(expected, Apply("*", a, b));

    [Theory]
    [InlineData("6", "4", "1.5")]
    [InlineData("-1", "3", "-0.3333333333333333")]
    [InlineData("1", "0", "error: number (1) and number (0) cannot be divided because the divisor is zero")]
    [InlineData("0", "-0", "error: number (0) and number (-0) cannot be divided because the divisor is zero")]
    [InlineData("\"a,b,c\"", "\",\"", "[\"a\",\"b\",\"c\"]")]
    [InlineData("\"a,b,\"", "\",\"", "[\"a\",\"b\",\"\"]")]
    [InlineData("\"a, b\"", "\", \"", "[\"a\",\"b\"]")]
    [InlineData("\"abc\"", "\"x\"", "[\"abc\"]")]
    [InlineData("\"\"", "\",\"", "[]")]
    [InlineData("\"aé😀\"", "\"\"", "[\"a\",\"é\",\"😀\"]")]
    [InlineData("[]", "1", "error: array ([]) and number (1) cannot be divided")]
    [InlineData("\"a\"", "1", "error: string (\"a\") and number (1) cannot be divided")]
    public void Divide(string a, string b, string expected) => Assert.Equal(expected, Apply("/", a, b));

    [Theory]
    [InlineData("5", "3", "2")]
    [InlineData("-5", "3", "-2")]
    [InlineData("5", "-3", "2")]
    [InlineData("5.9", "3.9", "2")]
    [InlineData("5", "-1", "0")]
    [InlineData("-9223372036854775808", "-1", "0")]
    [InlineData("1e300", "7", "0")]
    [InlineData("nan", "1", "null")]
    [InlineData("1", "nan", "null")]
    [InlineData("1", "0", "error: number (1) and number (0) cannot be divided because the divisor is zero")]
    [InlineData("5", "0.5", "error: number (5) and number (0.5) cannot be divided because the divisor is zero")]
    [InlineData("\"a\"", "1", "error: string (\"a\") and number (1) cannot be divided")]
    public void Modulo(string a, string b, string expected) => Assert.Equal(expected, Apply("%", a, b));

    [Fact]
    public void Operands_never_change()
    {
        var a = V("{\"a\":1,\"b\":{\"c\":2}}");
        var b = V("{\"b\":{\"d\":3},\"e\":4}");
        Assert.True(ValueOps.TryAdd(a, b, out _, out _));
        Assert.True(ValueOps.TryMultiply(a, b, out _, out _));
        Assert.Equal("{\"a\":1,\"b\":{\"c\":2}}", J(a));
        Assert.Equal("{\"b\":{\"d\":3},\"e\":4}", J(b));
    }
}
