using System.Diagnostics;
using Devlooped.Core.Runtime;
using Devlooped.Core.Values;
using static Devlooped.Tests.Core.Runtime.Evaluate;

namespace Devlooped.Tests.Core.Runtime;

/// <summary>
/// The interpreter (spec §6 to §8, §11, §12) beyond the corpus: signals through every construct,
/// laziness, path mode, the depth limit and the host stack, and the host interface. Each case is
/// a program, its input and its outputs as one line (see <see cref="Evaluate.Line"/>).
/// </summary>
public class InterpreterTests
{
    [Theory]
    // try catches its body's errors, keeps the outputs before, and stops the body.
    [InlineData("[try (1, error(\"x\"), 3) catch .]", "[1,\"x\"]")]
    [InlineData("[.[] | try error catch .]", "error: \"Cannot iterate over null (null)\"")]
    [InlineData("[.[]?]", "[]")]
    [InlineData("try error({}) catch .", "{}")]
    [InlineData("[(1, 2) | try error catch . + 10]", "[11,12]")]
    // Errors raised by the handler propagate.
    [InlineData("try (try error(\"a\") catch error(\"b\")) catch .", "\"b\"")]
    // Errors of the consumer are not errors of the producer (§6.2), for try, ? and ?// alike.
    [InlineData("\"x\" | try ((try . catch \"too much\") | error) catch \"right\"", "\"right\"")]
    [InlineData("[try ((try (1,2) catch \"inner\") | if . == 2 then error(\"consumer\") else . end) catch (\"outer: \" + .)]", "[1,\"outer: consumer\"]")]
    [InlineData("try ((1, 2)? | error(tostring)) catch .", "\"1\"")]
    [InlineData("try (([[1]] | .[] as [$a] ?// $a | $a) | error(tostring)) catch .", "\"1\"")]
    // ?// tries the next pattern when matching fails or the body raises an error; errors with the last propagate.
    [InlineData("[[[1],2][] as [$a] ?// $a | $a]", "[1,2]")]
    [InlineData("[[[1]][] as [$a] ?// $a | if ($a | type) == \"number\" then error(\"n\") else $a end]", "[[1]]")]
    [InlineData("try (1 as [$a] ?// {$a} | $a) catch .", "\"Cannot index number with string (\\\"a\\\")\"")]
    // // propagates errors of its left side; its right side only when the left has no true output.
    [InlineData("try [(1, error(\"x\")) // 3] catch .", "\"x\"")]
    [InlineData("[empty // 3, (false, null) // 4, (1, null, 2) // 5]", "[3,4,1,2]")]
    // break stops its own label instance only, lexically, and is not an error.
    [InlineData("[label $f | def g: break $f; (label $f | 1, g, 2), 3]", "[1]")]
    [InlineData("[label $a | label $b | 1, break $a, 2]", "[1]")]
    [InlineData("label $out | try (0, break $out, 1) catch 99", "0")]
    [InlineData("def f: label $x | (1, break $x); [f, f]", "[1,1]")]
    // reduce and foreach with no and several outputs of the update.
    [InlineData("reduce (1,2,3) as $x (0; empty)", "null")]
    [InlineData("[reduce empty as $x (1, 2; .)]", "[1,2]")]
    [InlineData("reduce (1,2) as $x (0; ., 10)", "10")]
    [InlineData("[foreach (1,2) as $x (0; (. + 1, . + 10); .)]", "[1,10,11,20]")]
    [InlineData("[foreach (1,2) as $x (0; empty; .)]", "[]")]
    [InlineData("[foreach (1,2) as $x (0, 1; . + $x)]", "[1,3,2,4]")]
    // halt and halt_error stop everything; try does not catch them.
    [InlineData("1, halt, 2", "1, halt 0")]
    [InlineData("try (\"bye\" | halt_error(5)) catch 0", "halt 5 \"bye\"")]
    [InlineData("try halt_error(\"x\") catch .", "\"null (null) halt_error/1: number required\"")]
    public void Signals_pass_through_every_construct(string program, string expected)
        => Assert.Equal(expected, Line(program));

    [Theory]
    [InlineData("first(range(1000000000))", "0")]
    [InlineData("[limit(3; repeat(1))]", "[1,1,1]")]
    [InlineData("isempty(repeat(1))", "false")]
    [InlineData("[label $f | 1, break $f, error(\"unreached\")]", "[1]")]
    [InlineData("first(range(10; infinite))", "10")]
    [InlineData("[limit(1; 1, error(\"unreached\"))]", "[1]")]
    [InlineData("[.[] | select(. > 1)] | first", "2")]
    [InlineData("any(range(1000000000); . > 2)", "true")]
    public void Consumers_that_stop_stop_their_producers(string program, string expected)
    {
        var watch = Stopwatch.StartNew();
        Assert.Equal(expected, Line(program, "[1,2,3]"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"{program} took {watch.Elapsed}");
    }

    [Fact]
    public void The_host_stops_the_stream()
        => Assert.Equal(["0", "1", "2"], Run("range(1000000000)", take: 3));

    [Theory]
    // §8.1, construct by construct.
    [InlineData("path(.)", "[]")]
    [InlineData("[path(.a.b, .c[1:2], .c[])]", "[[\"a\",\"b\"],[\"c\",{\"start\":1,\"end\":2}],[\"c\",0],[\"c\",1]]")]
    [InlineData("[path(.a[.k])]", "[[\"a\",\"x\"]]")]
    [InlineData("[path(.z?, (.c | try .[] catch .), (.a | .b))]", "[[\"z\"],[\"c\",0],[\"c\",1],[\"a\",\"b\"]]")]
    [InlineData("[path(if .k == \"x\" then .a else .c end)]", "[[\"a\"]]")]
    [InlineData("[path(.z // .a)]", "[[\"a\"]]")]
    [InlineData("[path(.k as $k | .a[$k])]", "[[\"a\",\"x\"]]")]
    [InlineData("[path(reduce (\"a\", \"b\") as $k (.; .[$k]))]", "[[\"a\",\"b\"]]")]
    [InlineData("[path(foreach (\"a\", \"b\") as $k (.; .[$k]))]", "[[\"a\"],[\"a\",\"b\"]]")]
    [InlineData("[path(label $x | .a, break $x)]", "[[\"a\"]]")]
    [InlineData("def f(p): p | .b; [path(f(.a))]", "[[\"a\",\"b\"]]")]
    [InlineData("[path(empty), path(getpath([\"a\", \"q\"]))]", "[[\"a\",\"q\"]]")]
    [InlineData("[path(first(.c[]), last(.c[]), limit(1; .c[]), skip(1; .c[]))]", "[[\"c\",0],[\"c\",1],[\"c\",0],[\"c\",1]]")]
    [InlineData("[path(.. | select(type == \"number\"))]", "[[\"a\",\"b\"],[\"c\",0],[\"c\",1]]")]
    [InlineData("[path(.c | recurse(if . == null then empty else .[0]? end))]", "[[\"c\"],[\"c\",0]]")]
    // The three path errors.
    [InlineData("try path(1) catch .", "\"Invalid path expression with result 1\"")]
    [InlineData("try path($__loc__ | .a) catch .", "\"Invalid path expression near attempt to access element \\\"a\\\" of null\"")]
    [InlineData("try path(.k | ascii_upcase | .[]) catch .", "\"Invalid path expression near attempt to iterate through \\\"X\\\"\"")]
    [InlineData("try path(.k + \"y\") catch .", "\"Invalid path expression with result \\\"xy\\\"\"")]
    public void Paths_are_part_of_evaluation(string program, string expected)
        => Assert.Equal(expected, Line(program, "{\"a\":{\"b\":0},\"k\":\"x\",\"c\":[1,2]}", new EvaluationOptions { Variables = new Dictionary<string, JqValue> { ["__loc__"] = JqValue.Null } }));

    [Theory]
    [InlineData(".a = 1", "{\"a\":1,\"b\":[1,2]}")]
    [InlineData(".b[] |= . * 10", "{\"a\":0,\"b\":[10,20]}")]
    [InlineData(".b[1] += 5", "{\"a\":0,\"b\":[1,7]}")]
    [InlineData("[.a += (1, 2)] | map(.a)", "[1,2]")]
    [InlineData(".c //= \"d\"", "{\"a\":0,\"b\":[1,2],\"c\":\"d\"}")]
    [InlineData("(.a, .z) = 9", "{\"a\":9,\"b\":[1,2],\"z\":9}")]
    [InlineData(".b |= map(select(. > 1))", "{\"a\":0,\"b\":[2]}")]
    [InlineData("(.b[] | select(. == 1)) |= empty", "{\"a\":0,\"b\":[2]}")]
    [InlineData("del(.a, .b[0])", "{\"b\":[2]}")]
    [InlineData("to_entries", "[{\"key\":\"a\",\"value\":0},{\"key\":\"b\",\"value\":[1,2]}]")]
    [InlineData("with_entries(.value |= tostring)", "{\"a\":\"0\",\"b\":\"[1,2]\"}")]
    [InlineData("[paths]", "[[\"a\"],[\"b\"],[\"b\",0],[\"b\",1]]")]
    [InlineData("def _modify(p; f): \"mine\"; .a |= 1", "\"mine\"")]
    public void Assignment_works_through_the_prelude(string program, string expected)
        => Assert.Equal(expected, Line(program, "{\"a\":0,\"b\":[1,2]}"));

    [Theory]
    [InlineData("$x", "1")]
    [InlineData("[$x, $y]", "[1,\"two\"]")]
    [InlineData("$x as $x | $x + 1", "2")]
    [InlineData("$missing", "error: \"$missing is not defined\"")]
    [InlineData("try $missing catch .", "\"$missing is not defined\"")]
    [InlineData("$ENV.HOME, env.HOME", "\"/home\", \"/home\"")]
    [InlineData("1 as $ENV | $ENV", "1")]
    public void Host_variables_and_ENV(string program, string expected)
        => Assert.Equal(expected, Line(program, options: new EvaluationOptions
        {
            Variables = new Dictionary<string, JqValue> { ["x"] = JqValue.FromNumber(1), ["y"] = JqValue.FromString("two") },
            Environment = JqValue.FromObject(JqObject.Empty.Set("HOME", JqValue.FromString("/home"))),
        }));

    [Fact]
    public void A_native_without_an_implementation_is_a_runtime_error()
    {
        Assert.Equal("error: \"sqrt/0 is not implemented\"", Line("4 | sqrt"));
        Assert.Equal("\"caught\"", Line("try sqrt catch \"caught\""));
        // Binding succeeds: the catalogue has the entry.
        Assert.Equal("[1]", Line("[1, (if false then sqrt else empty end)]"));
    }

    [Fact]
    public void One_program_runs_concurrently_with_different_variables()
    {
        var program = Compile("def f: if . == 0 then $n else (. - 1 | f) end; [range(200) | {a: f}] | map(.a) | add");
        var results = new string[64];
        Parallel.For(0, results.Length, i =>
        {
            var options = new EvaluationOptions { Variables = new Dictionary<string, JqValue> { ["n"] = JqValue.FromNumber(i) } };
            results[i] = string.Join(", ", Run(program, JqValue.FromNumber(50), options));
        });

        for (var i = 0; i < results.Length; i++)
            Assert.Equal((200 * i).ToString(), results[i]);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(50)]
    public void The_call_depth_is_limited(int maxDepth)
    {
        var options = new EvaluationOptions { MaxDepth = maxDepth };
        const string Count = "def f: if . == 0 then 0 else (. - 1 | f) end; ";
        // n | f makes n + 1 calls.
        Assert.Equal("0", OnSmallStack(Count + (maxDepth - 1) + " | f", options: options));
        Assert.Equal("error: \"Maximum call depth exceeded\"", OnSmallStack(Count + maxDepth + " | f", options: options));
        Assert.Equal("\"Maximum call depth exceeded\"", OnSmallStack("try (" + Count + "100000 | f) catch .", options: options));
    }

    [Fact]
    public void Tail_recursion_reaches_the_limit_without_the_host_stack()
        => Assert.Equal("error: \"Maximum call depth exceeded\"", OnSmallStack("def f: if . == 0 then 0 else (. - 1 | f) end; 100000 | f"));

    [Theory]
    // Recursion whose outputs flow back through every level, beyond what a 1 MB stack holds.
    [InlineData("def f: if . == 0 then 0 else (. - 1 | f) + 1 end; 100000 | f")]
    [InlineData("def f: if . == 0 then 0 else [. - 1 | f] end; 100000 | f")]
    [InlineData("def f: if . == 0 then 0 else ((. - 1, . - 1) | f) end; first(100000 | f)")]
    public void The_host_stack_is_never_exhausted(string program)
        => Assert.Equal("error: \"Maximum call depth exceeded\"", OnSmallStack(program, options: new EvaluationOptions { MaxDepth = 1_000_000 }));

    [Theory]
    // Natives marked *loop* do not consume the call depth.
    [InlineData("[limit(5000; repeat(1))] | length", "5000")]
    [InlineData("[range(100000)] | length", "100000")]
    [InlineData("last(range(100000))", "99999")]
    [InlineData("0 | until(. >= 100000; . + 1)", "100000")]
    [InlineData("[0 | while(. < 100000; . + 1)] | length", "100000")]
    [InlineData("[skip(99998; range(100000))]", "[99998,99999]")]
    public void Loop_natives_do_not_consume_the_depth(string program, string expected)
        => Assert.Equal(expected, OnSmallStack(program, options: new EvaluationOptions { MaxDepth = 10 }));

    [Theory]
    [InlineData("[1, 2] | [.[] as $x | $x * 10]", "[10,20]")]
    [InlineData("[(1,2) + (10,20)]", "[11,12,21,22]")]
    [InlineData("[{a: (1,2), b: (3,4)}]", "[{\"a\":1,\"b\":3},{\"a\":1,\"b\":4},{\"a\":2,\"b\":3},{\"a\":2,\"b\":4}]")]
    [InlineData("[\"\\(1,2)-\\(3,4)\"]", "[\"1-3\",\"2-3\",\"1-4\",\"2-4\"]")]
    [InlineData("[range(0,1; 3,4)]", "[0,1,2,0,1,2,3,1,2,1,2,3]")]
    [InlineData("[.[(0,1)]]", "[1,2]")]
    [InlineData("[range(10)] | [.[(1,2):(3,4)] | length]", "[2,3,1,2]")]
    [InlineData("[(true, false) and (true, false)]", "[true,false,false]")]
    [InlineData("[(true, false) or (true, false)]", "[true,true,false]")]
    [InlineData("[-(1, 2)]", "[-1,-2]")]
    [InlineData("[{(\"a\", \"b\"): 1}]", "[{\"a\":1},{\"b\":1}]")]
    [InlineData("{\"x1\":\"a\",\"x2\":\"b\"} | [{\"x\\(1,2)\"}]", "[{\"x1\":\"a\"},{\"x2\":\"b\"}]")]
    [InlineData("[. as [$a, $b] | {$a, $b}]", "[{\"a\":1,\"b\":2}]")]
    [InlineData(". as {(\"a\", \"b\"): $v} ?// $v | $v", "[1,2]")]
    [InlineData("{\"a\":1,\"b\":2} | [. as {(\"a\", \"b\"): $v} | $v]", "[1,2]")]
    public void Outputs_come_in_the_order_of_the_spec(string program, string expected)
        => Assert.Equal(expected, Line(program, "[1,2]"));
}