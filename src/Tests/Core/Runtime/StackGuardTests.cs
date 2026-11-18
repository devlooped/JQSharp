using System.Text;
using JQLite.Core.Runtime;
using static JQLite.Tests.Core.Runtime.Evaluate;

namespace JQLite.Tests.Core.Runtime;

/// <summary>
/// MaxDepth is the only recursion limit a program meets (spec §11): on a small host stack, deep
/// evaluations continue on a fresh stack (StackGuard). Each case runs on a thread with a 1 MB or a
/// 256 KB stack, in whatever build the tests run (Debug by default).
/// </summary>
public class StackGuardTests
{
    public static TheoryData<int> Stacks => new() { 1024 * 1024, 256 * 1024 };

    static string Repeat(string text, int count) => new StringBuilder().Insert(0, text, count).ToString();

    static EvaluationOptions Depth(int maxDepth) => new() { MaxDepth = maxDepth };

    public static TheoryData<string, string, string> Nestings => new()
    {
        // opening, closing, and the output for 1000 levels around "1" (or around "N" in a key)
        { "[", "]", Repeat("[", 1000) + "1" + Repeat("]", 1000) },
        { "{a: ", "}", Repeat("{\"a\":", 1000) + "1" + Repeat("}", 1000) },
        { ".[", "]", "error: \"Cannot index null with null (null)\"" },
        { "\"\\(", ")\"", "\"1\"" },
        { "reduce . as $x (", ";.)", "1" },
        { "[1 + ", "]", "error" },
        { "1 + (", ")", "1001" },
        { "[label $x | ", "]", Repeat("[", 1000) + "1" + Repeat("]", 1000) },
        { "if . then 1 else ", " end", "1" },
        { "try ", "", "1" },
    };

    [Theory]
    [MemberData(nameof(Nestings))]
    public void Nesting_to_the_parser_limit_works(string open, string close, string expected)
    {
        foreach (var stack in new[] { 1024 * 1024, 256 * 1024 })
        {
            var result = OnStack(stack, Repeat(open, 1000) + "1" + Repeat(close, 1000));
            if (expected == "error")
                Assert.StartsWith("error: \"number (1) and array ([2]) cannot be added", result);
            else
                Assert.Equal(expected, result);
        }
    }

    public static TheoryData<string, int> Recursions => new()
    {
        // program with N, stack
        { "def f: if . == 0 then 0 else (. - 1 | f) + 1 end; N | f", 1024 * 1024 },
        { "def f: if . == 0 then 0 else (. - 1 | f) + 1 end; N | f", 256 * 1024 },
        { "def f: if . == 0 then 0 else [. - 1 | f] | length end; N | f", 256 * 1024 },
        { "def f: if . == 0 then 0 else ((. - 1, . - 1) | f) end; label $out | (N | f) | ., break $out", 1024 * 1024 },
        { "def f: if . == 0 then 0 else ((. - 1, . - 1) | f) end; label $out | (N | f) | ., break $out", 256 * 1024 },
        { "def f(g): if . == 0 then g else (. - 1 | f(g)) + 0 end; N | f(0)", 256 * 1024 },
    };

    [Theory]
    [MemberData(nameof(Recursions))]
    public void Recursion_reaches_exactly_MaxDepth(string program, int stack)
    {
        foreach (var maxDepth in new[] { 1000, 5000 })
        {
            // N | f makes N + 1 calls.
            var deepest = OnStack(stack, program.Replace("N", (maxDepth - 1).ToString()), Depth(maxDepth));
            Assert.DoesNotContain("error", deepest);
            Assert.Equal("error: \"Maximum call depth exceeded\"", OnStack(stack, program.Replace("N", maxDepth.ToString()), Depth(maxDepth)));
        }
    }

    [Fact]
    public void Hops_nest()
        => Assert.Equal("19999", OnStack(256 * 1024, "def f: if . == 0 then 0 else (. - 1 | f) + 1 end; 19999 | f", Depth(20000)));

    [Theory]
    [MemberData(nameof(Stacks))]
    public void A_stop_of_the_host_comes_back_across_hops(int stack)
    {
        var program = Compile("def f: if . == 0 then (1, 2, 3) else (. - 1 | f) + 0 end; 900 | f");
        string[]? outputs = null;
        var thread = new Thread(() => outputs = Run(program, Evaluate.Json("null"), take: 1), stack);
        thread.Start();
        thread.Join();
        Assert.Equal(["1"], outputs!);
    }

    [Theory]
    [MemberData(nameof(Stacks))]
    public void An_error_comes_back_across_hops(int stack)
        => Assert.Equal("\"deep\"", OnStack(stack, "try (def f: if . == 0 then error(\"deep\") else (. - 1 | f) + 1 end; 900 | f) catch ."));

    static string OnStack(int stack, string program, EvaluationOptions? options = null)
    {
        var bound = Compile(program);
        string? result = null;
        var thread = new Thread(() => result = string.Join(", ", Run(bound, Evaluate.Json("null"), options)), stack);
        thread.Start();
        thread.Join();
        return result!;
    }
}