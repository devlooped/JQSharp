using System.Runtime.CompilerServices;
using JQLite.Core.Values;
using static JQLite.Tests.Core.Values.ValueHelpers;

namespace JQLite.Tests.Core.Values;

public class CollectionTests
{
    static JqValue N(double number) => JqValue.FromNumber(number);

    static string J(JqArray array) => ValueHelpers.J(JqValue.FromArray(array));

    static string J(JqObject obj) => ValueHelpers.J(JqValue.FromObject(obj));

    [Fact]
    public void Value_is_sixteen_bytes()
        => Assert.Equal(16, Unsafe.SizeOf<JqValue>());

    [Fact]
    public void Kinds_and_truthiness()
    {
        Assert.Equal(JqKind.Null, default(JqValue).Kind);
        Assert.Equal(JqKind.False, JqValue.FromBool(false).Kind);
        Assert.Equal(JqKind.True, JqValue.FromBool(true).Kind);
        Assert.Equal(JqKind.Number, N(1).Kind);
        Assert.Equal(JqKind.String, JqValue.FromString("").Kind);
        Assert.Equal(JqKind.Array, JqValue.FromArray(JqArray.Empty).Kind);
        Assert.Equal(JqKind.Object, JqValue.FromObject(JqObject.Empty).Kind);
        Assert.False(JqValue.Null.IsTruthy);
        Assert.False(JqValue.False.IsTruthy);
        Assert.True(N(0).IsTruthy);
        Assert.True(JqValue.FromString("").IsTruthy);
        Assert.Equal("boolean", JqValue.True.TypeName);
        Assert.Throws<InvalidOperationException>(() => N(1).AsString());
    }

    [Fact]
    public void Appending_twice_to_the_same_base_gives_independent_arrays()
    {
        var root = JqArray.Empty.Append(N(1)).Append(N(2));
        var a = root.Append(N(3));
        var b = root.Append(N(4));
        var c = a.Append(N(5));
        var d = a.Concat(JqArray.Of(N(6), N(7)));

        Assert.Equal("[1,2]", J(root));
        Assert.Equal("[1,2,3]", J(a));
        Assert.Equal("[1,2,4]", J(b));
        Assert.Equal("[1,2,3,5]", J(c));
        Assert.Equal("[1,2,3,6,7]", J(d));
    }

    [Fact]
    public void Slices_share_storage_but_never_change()
    {
        var array = V("[1,2,3,4,5]").AsArray();
        var middle = array.Slice(1, 3);
        var tail = array.Slice(3, 5);
        var grown = middle.Append(N(9));
        var longer = tail.Append(N(8));

        Assert.Equal("[1,2,3,4,5]", J(array));
        Assert.Equal("[2,3]", J(middle));
        Assert.Equal("[2,3,9]", J(grown));
        Assert.Equal("[4,5,8]", J(longer));
        Assert.Equal("[4,5]", J(tail));
    }

    [Fact]
    public void Set_item_copies_one_level_and_pads_with_null()
    {
        var array = V("[1,[2]]").AsArray();
        Assert.Equal("[1,7]", J(array.SetItem(1, N(7))));
        Assert.Equal("[1,[2],null,5]", J(array.SetItem(3, N(5))));
        Assert.Equal("[1,[2],6]", J(array.SetItem(2, N(6))));
        Assert.Equal("[1,[2]]", J(array));
        Assert.Same(array[1].AsArray(), array.SetItem(0, N(0))[1].AsArray());
    }

    [Fact]
    public void Concurrent_appends_to_one_base_stay_independent()
    {
        var root = JqArray.Empty.Append(N(-1));
        var results = new JqArray[1000];
        Parallel.For(0, results.Length, i =>
        {
            var array = root.Append(N(i));
            for (var j = 0; j < 10; j++)
                array = array.Append(N(i * 100 + j));
            results[i] = array;
        });

        Assert.Equal("[-1]", J(root));
        for (var i = 0; i < results.Length; i++)
        {
            var array = results[i];
            Assert.Equal(12, array.Count);
            Assert.Equal(-1, array[0].AsNumber());
            Assert.Equal(i, array[1].AsNumber());
            for (var j = 0; j < 10; j++)
                Assert.Equal(i * 100 + j, array[2 + j].AsNumber());
        }
    }

    [Fact]
    public void Appending_in_a_loop_does_not_copy_the_array()
    {
        // reduce range(10000) as $i ([]; . + [$i]); copying on each append would allocate ~800 MB.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var array = JqArray.Empty;
        for (var i = 0; i < 10000; i++)
            array = array.Concat(JqArray.Of(N(i)));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(10000, array.Count);
        Assert.Equal(9999, array[9999].AsNumber());
        Assert.True(allocated < 4_000_000, $"{allocated:N0} bytes");
    }

    [Fact]
    public void Adding_keys_in_a_loop_does_not_copy_the_object()
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var obj = JqObject.Empty;
        for (var i = 0; i < 10000; i++)
            obj = obj.Merge(JqObject.Empty.Set("k" + i, N(i)));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(10000, obj.Count);
        Assert.True(obj.TryGetValue("k1234", out var value));
        Assert.Equal(1234, value.AsNumber());
        Assert.True(allocated < 16_000_000, $"{allocated:N0} bytes");
    }

    [Fact]
    public void Object_keeps_insertion_order()
    {
        var obj = JqObject.Empty.Set("b", N(1)).Set("a", N(2)).Set("c", N(3));
        Assert.Equal("{\"b\":1,\"a\":2,\"c\":3}", J(obj));
        Assert.Equal("{\"b\":1,\"a\":9,\"c\":3}", J(obj.Set("a", N(9))));
        Assert.Equal("{\"b\":1,\"c\":3}", J(obj.Remove("a")));
        Assert.Equal("{\"b\":1,\"c\":3,\"a\":4}", J(obj.Remove("a").Set("a", N(4))));
        Assert.Same(obj, obj.Remove("x"));
        Assert.Equal("{\"b\":1,\"a\":2,\"c\":3}", J(obj));
        Assert.Equal(new[] { "a", "b", "c" }, obj.SortedKeys());
    }

    [Fact]
    public void Object_merge_keeps_insertion_order()
    {
        var left = V("{\"z\":1,\"y\":2,\"x\":3}").AsObject();
        Assert.Equal("{\"z\":1,\"y\":5,\"x\":3,\"w\":4}", J(left.Merge(V("{\"w\":4,\"y\":5}").AsObject())));
        Assert.Equal("{\"z\":1,\"y\":2,\"x\":3,\"a\":0,\"b\":1}", J(left.Merge(V("{\"a\":0,\"b\":1}").AsObject())));
        Assert.Equal("{\"z\":1,\"y\":2,\"x\":3}", J(left));
    }

    [Fact]
    public void Versions_of_a_large_object_see_only_their_keys()
    {
        var versions = new JqObject[21];
        versions[0] = JqObject.Empty;
        for (var i = 1; i <= 20; i++)
            versions[i] = versions[i - 1].Set("k" + i, N(i));

        // A branch from an older version takes a slot already used by a newer one: it copies.
        var branch = versions[10].Set("k15", N(-15));

        for (var i = 1; i <= 20; i++)
        {
            Assert.Equal(i, versions[i].Count);
            Assert.True(versions[i].ContainsKey("k" + i));
            Assert.Equal(i < 20, !versions[i].ContainsKey("k20"));
            Assert.False(versions[i].ContainsKey("k" + (i + 1)));
        }

        Assert.Equal(-15, branch.TryGetValue("k15", out var v) ? v.AsNumber() : 0);
        Assert.Equal(15, versions[15].TryGetValue("k15", out v) ? v.AsNumber() : 0);
        Assert.False(versions[12].ContainsKey("k15"));
        Assert.Equal(10, branch.IndexOf("k15"));
    }

    [Fact]
    public void Concurrent_key_adds_to_one_base_stay_independent()
    {
        var root = JqObject.Empty;
        for (var i = 0; i < 20; i++)
            root = root.Set("base" + i, N(i));

        var results = new JqObject[500];
        Parallel.For(0, results.Length, i =>
        {
            var obj = root.Set("own" + i, N(i));
            for (var j = 0; j < 5; j++)
                obj = obj.Set($"own{i}_{j}", N(j));
            results[i] = obj;
        });

        Assert.Equal(20, root.Count);
        for (var i = 0; i < results.Length; i++)
        {
            var obj = results[i];
            Assert.Equal(26, obj.Count);
            Assert.Equal("own" + i, obj.KeyAt(20));
            Assert.True(obj.TryGetValue("own" + i, out var value));
            Assert.Equal(i, value.AsNumber());
            Assert.False(obj.ContainsKey("own" + ((i + 1) % results.Length)));
            Assert.False(root.ContainsKey("own" + i));
        }
    }

    [Fact]
    public void Builder_replaces_in_place_and_removes()
    {
        var builder = new JqObjectBuilder();
        for (var i = 0; i < 12; i++)
            builder.Set("k" + i, N(i));
        builder.Set("k3", N(30));
        Assert.True(builder.Remove("k0"));
        Assert.False(builder.Remove("missing"));
        var obj = builder.ToObject();

        Assert.Equal(11, obj.Count);
        Assert.Equal("k1", obj.KeyAt(0));
        Assert.Equal(30, obj.TryGetValue("k3", out var v) ? v.AsNumber() : 0);
        Assert.False(obj.ContainsKey("k0"));
        Assert.Throws<InvalidOperationException>(() => builder.Set("x", N(1)));
    }
}
