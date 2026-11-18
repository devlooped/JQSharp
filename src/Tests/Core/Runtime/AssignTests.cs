using System.Text;
using Devlooped.Core.Binding;
using Devlooped.Core.Values;
using static Devlooped.Tests.Core.Runtime.Evaluate;

namespace Devlooped.Tests.Core.Runtime;

/// <summary>
/// The native <c>_modify</c> and <c>_assign</c> against their prelude definitions (§8.3): each
/// program is compiled twice, with the native overrides and without them, and both must give the
/// same outputs, errors included.
/// </summary>
public class AssignTests
{
    [Theory]
    // parent then child, child then parent, the same path twice
    [InlineData("(.a, .a.b) |= {b: 1}", "{\"a\":{\"b\":0}}")]
    [InlineData("(.a.b, .a) |= {x: .}", "{\"a\":{\"b\":0}}")]
    [InlineData("(.a, .a) |= . + 1", "{\"a\":1}")]
    [InlineData("(.a[0], .a, .a[0]) |= [.]", "{\"a\":[1,2]}")]
    // every path of a value, parents first
    [InlineData(".. |= (if type == \"number\" then . + 1 else . end)", "{\"a\":[1,{\"b\":2}],\"c\":3}")]
    [InlineData("[..] as $all | .. |= length", "[[1,[2]],{\"x\":3}]")]
    // updates that return the input itself, or structures holding it twice
    [InlineData(".a |= ., .b |= [., .]", "{\"a\":[1],\"b\":{\"c\":2}}")]
    [InlineData("(.a, .a[0]) |= [., .]", "{\"a\":[1]}")]
    [InlineData(". as $in | .a |= $in", "{\"a\":1}")]
    // empty updates (deletions at the end) mixed with updates
    [InlineData("(.[] | select(. >= 2)) |= empty", "[1,5,3,0,7]")]
    [InlineData(".[] |= (if . % 2 == 0 then empty else . * 10 end)", "[1,2,3,4,5]")]
    [InlineData("(.a, .b, .c) |= (if . == null then empty else . + 1 end)", "{\"a\":1,\"c\":3}")]
    [InlineData("(.a.x, .a) |= empty", "{\"a\":{\"x\":1}}")]
    [InlineData("to_entries |= map(select(.key != \"b\")) | from_entries", "{\"a\":1,\"b\":2}")]
    // errors midway, and their order
    [InlineData("(.a, .b, .c) |= (if . == 2 then error(\"two\") else . end)", "{\"a\":1,\"b\":2,\"c\":3}")]
    [InlineData("(.a, .b.x) |= 1", "{\"a\":0,\"b\":5}")]
    [InlineData("(.a, 1) |= 1", "{\"a\":0}")]
    [InlineData(".[0] |= 1", "{}")]
    [InlineData("(.a, .[0]) |= 1", "null")]
    [InlineData("(.a | .b) |= 1", "{\"a\":[1]}")]
    // _assign: several right-hand outputs, none, and paths of the right side's input
    [InlineData("(.a, .b) = (1, 2)", "{}")]
    [InlineData(".a = empty", "{}")]
    [InlineData("empty = 1", "{\"a\":0}")]
    [InlineData(".a = .b", "{\"a\":0,\"b\":1}")]
    [InlineData("(.a, .a.b) = 1", "{}")]
    [InlineData(".a[1:3] = [\"x\"]", "{\"a\":[0,1,2,3]}")]
    // slices, paths through null, negative indices, arithmetic updates
    [InlineData(".[1:3] |= map(. * 10)", "[0,1,2,3,4]")]
    [InlineData("(.[1:3], .[0]) |= (if type == \"array\" then [] else -1 end)", "[0,1,2,3,4]")]
    [InlineData(".[2:4][0] |= 9", "[0,1,2,3,4]")]
    [InlineData(".[1:2] |= \"x\"", "\"abc\"")]
    [InlineData(".a.b[2].c |= 1", "null")]
    [InlineData(".[-1] |= . + 1", "[1,2,3]")]
    [InlineData(".[-5] |= 1", "[1,2,3]")]
    [InlineData(".[nan] |= 1", "[1]")]
    [InlineData(".[3] |= 1", "[1]")]
    [InlineData(".[536870912] |= 1", "[]")]
    [InlineData(".[] += 1, .a -= 1, .b *= 2, .c /= 2, .d %= 2, .e //= 7", "{\"a\":1,\"b\":2,\"c\":4,\"d\":5,\"e\":null}")]
    [InlineData("map_values(. + 1)", "{\"a\":1,\"b\":2}")]
    [InlineData("walk(if type == \"number\" then . + 1 else . end)", "[1,[2,{\"a\":3}]]")]
    [InlineData("INDEX(.id)", "[{\"id\":1},{\"id\":2}]")]
    [InlineData("sub(\"b\"; \"x\")", "\"abc\"")]
    [InlineData("path(.a |= 1)", "{\"a\":0}")]
    [InlineData("path(empty = 1)", "{\"a\":0}")]
    [InlineData("[path(.a = 1)]", "{\"a\":0}")]
    [InlineData(".a |= (1, 2)", "{\"a\":0}")]
    [InlineData("(.a, .b) |= (.x |= 1)", "{\"a\":{},\"b\":null}")]
    public void Native_and_prelude_agree(string program, string input)
    {
        var native = Line(Compile(program, nativeOverrides: true), input);
        var prelude = Line(Compile(program, nativeOverrides: false), input);
        Assert.Equal(prelude, native);
    }

    [Fact]
    public void Native_updates_are_linear()
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal("4000", Line(Compile("[range(4000)] | .[] |= . + 1 | length", nativeOverrides: true), "null"));
        // The prelude definition copies the array once per element: 4000 × 64 KB.
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 32 * 1024 * 1024);
    }

    [Fact]
    public void Paths_of_ten_thousand_keys()
    {
        // A value nested 10,000 arrays deep and the path of its innermost 0, run on a 1 MB stack.
        const string Deep = "reduce range(10000) as $i (0; [.]) | [range(10000) | 0] as $p | ";
        Assert.Equal("1", OnSmallStack(Deep + "getpath($p) |= . + 1 | getpath($p)"));
        Assert.Equal("2", OnSmallStack(Deep + "setpath($p; 2) | getpath($p)"));
        Assert.Equal("[]", OnSmallStack(Deep + "delpaths([$p]) | getpath($p[1:])"));
        Assert.Equal("[]", OnSmallStack(Deep + "del(getpath($p)) | getpath($p[1:])"));
        Assert.Equal("3", OnSmallStack(Deep + "getpath($p) = 3 | getpath($p)"));
    }

    static BoundProgram Compile(string program, bool nativeOverrides)
        => Resolver.TryCompile(program, out var bound, out var error, nativeOverrides) ? bound : throw new Xunit.Sdk.XunitException(error.Message);

    static string Line(BoundProgram program, string input) => string.Join(", ", Run(program, Evaluate.Json(input)));
}
