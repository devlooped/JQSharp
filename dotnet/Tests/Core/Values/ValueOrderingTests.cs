using JQLite.Core.Values;
using static JQLite.Tests.Core.Values.ValueHelpers;

namespace JQLite.Tests.Core.Values;

public class ValueOrderingTests
{
    // In jq's order; no two are equal.
    static readonly string[] Ordered =
    [
        "null",
        "false",
        "true",
        "-1e300",
        "-1",
        "0",
        "0.5",
        "1",
        "1e300",
        "\"\"",
        "\"A\"",
        "\"a\"",
        "\"ab\"",
        "\"b\"",
        "\"é\"",
        "\"\xE000\"",
        "\"\xFFFF\"",
        "\"😀\"",
        "[]",
        "[null]",
        "[1]",
        "[1,2]",
        "[1,3]",
        "[2]",
        "[\"a\"]",
        "[[]]",
        "{}",
        "{\"a\":1}",
        "{\"a\":2}",
        "{\"a\":[]}",
        "{\"a\":1,\"b\":1}",
        "{\"a\":2,\"b\":1}",
        "{\"a\":1,\"c\":0}",
        "{\"b\":0}",
        "{\"é\":0}",
        "{\"😀\":0}",
    ];

    [Fact]
    public void Every_pair_is_ordered()
    {
        for (var i = 0; i < Ordered.Length; i++)
        {
            var a = V(Ordered[i]);
            Assert.Equal(0, ValueOps.Compare(a, V(Ordered[i])));
            Assert.True(ValueOps.AreEqual(a, V(Ordered[i])), Ordered[i]);
            for (var j = i + 1; j < Ordered.Length; j++)
            {
                var b = V(Ordered[j]);
                Assert.True(ValueOps.Compare(a, b) == -1, $"{Ordered[i]} < {Ordered[j]}");
                Assert.True(ValueOps.Compare(b, a) == 1, $"{Ordered[j]} > {Ordered[i]}");
                Assert.False(ValueOps.AreEqual(a, b), $"{Ordered[i]} != {Ordered[j]}");
            }
        }
    }

    [Fact]
    public void Deep_values_compare()
    {
        var a = V(new string('[', 10000) + new string(']', 10000));
        var b = V(new string('[', 10000) + "1" + new string(']', 10000));
        Assert.True(ValueOps.AreEqual(a, V(J(a))));
        Assert.Equal(-1, ValueOps.Compare(a, b));
    }

    [Fact]
    public void Objects_order_by_key_sets_first()
        => Assert.Equal(-1, ValueOps.Compare(V("{\"a\":2,\"b\":1}"), V("{\"a\":1,\"c\":0}")));

    [Fact]
    public void Object_key_order_does_not_matter()
    {
        var a = V("{\"a\":1,\"b\":[1,{\"x\":2,\"y\":3}]}");
        var b = V("{\"b\":[1,{\"y\":3,\"x\":2}],\"a\":1}");
        Assert.True(ValueOps.AreEqual(a, b));
        Assert.Equal(0, ValueOps.Compare(a, b));
    }

    [Fact]
    public void Strings_order_by_code_point_not_utf16_unit()
    {
        // U+E000 is one unit 0xE000; U+1F600 is the pair D83D DE00: ordinal UTF-16 puts the pair first.
        Assert.True(string.CompareOrdinal("😀", "\xE000") < 0);
        Assert.Equal(-1, CodePoints.Compare("\xE000", "😀"));
        Assert.Equal(1, CodePoints.Compare("a😀", "a\xFFFD"));
        Assert.Equal(-1, CodePoints.Compare("😀", "😁"));
        Assert.Equal(-1, CodePoints.Compare("a", "a😀"));
    }

    [Fact]
    public void Nan_sorts_below_numbers_compares_equal_to_nan_but_is_never_equal()
    {
        var nan = JqValue.FromNumber(double.NaN);
        Assert.Equal(-1, ValueOps.Compare(nan, JqValue.FromNumber(double.NegativeInfinity)));
        Assert.Equal(1, ValueOps.Compare(JqValue.FromNumber(-1e300), nan));
        Assert.Equal(1, ValueOps.Compare(nan, JqValue.True));
        Assert.Equal(-1, ValueOps.Compare(nan, JqValue.FromString("")));
        // A total order (spec §4.3, §13): [nan < nan, nan <= nan, nan > nan, nan >= nan] is [false,true,false,true].
        Assert.Equal(0, ValueOps.Compare(nan, nan));
        Assert.Equal(0, ValueOps.Compare(V("[nan,{\"a\":nan}]"), V("[nan,{\"a\":nan}]")));
        // == stays IEEE: nan == nan is false, also inside containers.
        Assert.False(ValueOps.AreEqual(nan, nan));
        Assert.False(ValueOps.AreEqual(V("[nan]"), V("[nan]")));
        Assert.Equal(-1, ValueOps.Compare(V("[nan]"), V("[0]")));
    }

    [Fact]
    public void Sort_keeps_nans_together_and_stable()
    {
        var items = V("[1,nan,\"a\",-1,nan,null]").AsArray();
        Assert.Equal([5, 1, 4, 3, 0, 2], ValueOps.SortOrder(items));
    }

    [Theory]
    [InlineData("1", "1.0")]
    [InlineData("-0", "0")]
    [InlineData("1e2", "100")]
    [InlineData("null", "null")]
    [InlineData("[1,[2,{\"a\":null}]]", "[1,[2,{\"a\":null}]]")]
    [InlineData("\"😀\"", "\"\\ud83d\\ude00\"")]
    public void Equal_values(string a, string b)
    {
        Assert.True(ValueOps.AreEqual(V(a), V(b)));
        Assert.Equal(0, ValueOps.Compare(V(a), V(b)));
    }

    [Theory]
    [InlineData("1", "\"1\"")]
    [InlineData("false", "null")]
    [InlineData("[1,2]", "[2,1]")]
    [InlineData("{\"a\":1}", "{\"a\":1,\"b\":null}")]
    [InlineData("{\"a\":1}", "{\"b\":1}")]
    public void Unequal_values(string a, string b)
        => Assert.False(ValueOps.AreEqual(V(a), V(b)));

    [Fact]
    public void Sort_orders_every_kind()
    {
        var shuffled = Enumerable.Reverse(Ordered).Select(V).ToArray();
        var sorted = ValueOps.Sort(JqArray.Of(shuffled));
        Assert.Equal("[" + string.Join(",", Ordered.Select(x => J(V(x)))) + "]", J(JqValue.FromArray(sorted)));
    }

    [Fact]
    public void Sort_order_is_stable()
    {
        var keys = V("[2,1,2,1,0,2]").AsArray();
        Assert.Equal([4, 1, 3, 0, 2, 5], ValueOps.SortOrder(keys));
        Assert.Empty(ValueOps.SortOrder(JqArray.Empty));
        Assert.Equal([0], ValueOps.SortOrder(V("[7]").AsArray()));
    }

    [Fact]
    public void Sort_matches_a_reference_sort_on_random_input()
    {
        var random = new Random(7);
        var items = new JqValue[1000];
        for (var i = 0; i < items.Length; i++)
            items[i] = JqValue.FromNumber(random.Next(100));

        var order = ValueOps.SortOrder(JqArray.Of(items));
        for (var i = 1; i < order.Length; i++)
        {
            var c = ValueOps.Compare(items[order[i - 1]], items[order[i]]);
            Assert.True(c < 0 || (c == 0 && order[i - 1] < order[i]));
        }
    }
}
