using System.Text.Json;
using Devlooped;

namespace Devlooped.Tests;

/// <summary>
/// Runaway recursion through the public API (spec §11): the error is <c>Maximum call depth exceeded</c>
/// and the process survives, on a normal thread and on a small one. The exact boundary on small
/// stacks is also checked at the core, in StackGuardTests; here it is the public contract.
/// </summary>
public class JqDepthTests
{
    // N | f makes N + 1 calls of f.
    const string Tail = "def f: if . == 0 then 0 else (. - 1 | f) end; f";
    const string NonTail = "def f: if . == 0 then 0 else (. - 1 | f) + 1 end; f";

    public static TheoryData<string, int?, int, string> Boundaries => new()
    {
        // program, MaxDepth (null: the default, 1000), the deepest N that fits, its output
        { Tail, null, 999, "0" },
        { NonTail, null, 999, "999" },
        { Tail, 3000, 2999, "0" },
        { NonTail, 3000, 2999, "2999" },
    };

    static JsonElement Number(int value)
    {
        using var document = JsonDocument.Parse(value.ToString());
        return document.RootElement.Clone();
    }

    static JqExpression Parse(string program, int? maxDepth)
        => Jq.Parse(program, maxDepth is { } depth ? new JqOptions { MaxDepth = depth } : null);

    static T OnSmallStack<T>(Func<T> run)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = run();
            }
            catch (Exception e)
            {
                failure = e;
            }
        }, 256 * 1024);
        thread.Start();
        thread.Join();
        return failure is null ? result : throw new AggregateException(failure);
    }

    [Theory]
    [InlineData(Tail)]
    [InlineData(NonTail)]
    public void Runaway_recursion_throws_and_the_process_survives(string program)
    {
        var expression = Jq.Parse(program);
        var error = Assert.Throws<JqException>(() => expression.Evaluate(Number(100000)));
        Assert.Equal("Maximum call depth exceeded", error.Message);
        Assert.Equal("\"Maximum call depth exceeded\"", error.Value!.Value.GetRawText());
    }

    [Theory]
    [InlineData(Tail)]
    [InlineData(NonTail)]
    public void Runaway_recursion_on_a_small_stack_throws(string program)
    {
        var expression = Jq.Parse(program);
        var error = OnSmallStack(() => Assert.Throws<JqException>(() => expression.Evaluate(Number(100000))));
        Assert.Equal("Maximum call depth exceeded", error.Message);
    }

    [Fact]
    public void Runaway_recursion_is_caught_by_the_program()
    {
        var results = Jq.Parse("def f: if . == 0 then 0 else (. - 1 | f) + 1 end; try f catch .").Evaluate(Number(100000));
        Assert.Equal(["\"Maximum call depth exceeded\""], results.Select(static e => e.GetRawText()));
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void The_last_call_below_MaxDepth_succeeds_and_the_next_fails(string program, int? maxDepth, int deepest, string output)
    {
        var expression = Parse(program, maxDepth);
        foreach (var small in new[] { false, true })
        {
            string Run(int n) => OnStack(small, () => string.Join(",", expression.Evaluate(Number(n)).Select(static e => e.GetRawText())));
            Assert.Equal(output, Run(deepest));
            var error = OnStack(small, () => Assert.Throws<JqException>(() => expression.Evaluate(Number(deepest + 1))));
            Assert.Equal("Maximum call depth exceeded", error.Message);
        }
    }

    static T OnStack<T>(bool small, Func<T> run) => small ? OnSmallStack(run) : run();
}
