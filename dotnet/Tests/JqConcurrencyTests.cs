using System.Collections.Concurrent;
using System.Text.Json;
using JQLite;

namespace JQLite.Tests;

/// <summary>
/// An expression, the prelude and the modules are immutable and shared; one evaluation owns all
/// its state. Many threads evaluating at once, started together, must each get what a single
/// thread gets. No timing is involved: a shared mutable cell shows up as a wrong result.
/// </summary>
public class JqConcurrencyTests
{
    const int Threads = 32;
    const int Rounds = 500;

    // Variables ($n, $name), a recursive function, a closure parameter, reduce, label/break, limit,
    // a regex builtin, an update assignment, $ENV and a module import: each piece of shared state.
    const string Program = """
        import "funcs" as f;
        def fact: if . <= 1 then 1 else . * (. - 1 | fact) end;
        def twice(g): g | g;
        {
          fact: ($n | fact),
          twice: ($n | twice(. * 2)),
          sum: (reduce (.xs[] | . * $n) as $x (0; . + $x)),
          first_over: [label $out | .xs[] | if . > $n then ., break $out else empty end],
          limit: [limit(3; .xs[] | . + $n)],
          words: [.text | match("[a-z]+"; "g").string] | join($name),
          update: (.a |= . + $n | .a),
          env: $ENV.JQ_CONCURRENCY,
          module: ($n | f::addtwo)
        }
        """;

    static readonly JqOptions Options = new()
    {
        Environment = new Dictionary<string, string> { ["JQ_CONCURRENCY"] = "set" },
        Resolver = new JqFileResolver(Path.Combine(AppContext.BaseDirectory, "spec", "tests", "modules")),
    };

    static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    // The input and the variables of one case: distinct for each n.
    static (JsonElement Input, Dictionary<string, JsonElement> Variables) Case(int n)
        => (Json($$"""{"xs":[{{string.Join(",", Enumerable.Range(n - 2, 7))}}],"text":"ab{{n}}cd ef{{n}}gh","a":{{n * 10}}}"""),
            new() { ["n"] = Json(n.ToString()), ["name"] = Json($"\"<{n}>\"") });

    static string Evaluate(JqExpression expression, int n)
    {
        var (input, variables) = Case(n);
        return string.Join("\n", expression.Evaluate(input, variables).Select(static e => e.GetRawText()));
    }

    /// <summary>Runs <paramref name="work"/> on <see cref="Threads"/> threads that start together; returns what they report as wrong.</summary>
    static List<string> OnManyThreads(Action<int, ConcurrentBag<string>> work)
    {
        var wrong = new ConcurrentBag<string>();
        using var start = new Barrier(Threads);
        var threads = Enumerable.Range(0, Threads).Select(t => new Thread(() =>
        {
            try
            {
                start.SignalAndWait();
                work(t, wrong);
            }
            catch (Exception e)
            {
                wrong.Add($"thread {t}: {e}");
            }
        })).ToList();
        threads.ForEach(static thread => thread.Start());
        threads.ForEach(static thread => thread.Join());
        return [.. wrong];
    }

    [Fact]
    public void One_expression_gives_each_thread_the_single_threaded_result()
    {
        var expression = Jq.Parse(Program, Options);

        // Single-threaded, from a parse of its own: what each case must give.
        var reference = Jq.Parse(Program, Options);
        var expected = Enumerable.Range(3, 9).ToDictionary(static n => n, n => Evaluate(reference, n));
        Assert.Contains("\"module\":", expected[3]);
        Assert.Contains("\"env\":\"set\"", expected[3]);

        var wrong = OnManyThreads((t, wrong) =>
        {
            for (var i = 0; i < Rounds; i++)
            {
                var n = 3 + (t * 7 + i) % 9;
                var actual = Evaluate(expression, n);
                if (actual != expected[n])
                    wrong.Add($"thread {t}, round {i}, n = {n}: {actual}");
            }
        });

        Assert.Empty(wrong.Take(5));
    }

    [Fact]
    public void Different_expressions_parse_and_evaluate_at_once()
    {
        // The prelude is bound once per process, by whoever needs it first: when this test runs
        // alone, the threads race to bind it; after other tests, they share it. Either way
        // each parse must give a working expression.
        var wrong = OnManyThreads((t, wrong) =>
        {
            for (var i = 0; i < 20; i++)
            {
                var k = t * 100 + i;
                var expression = Jq.Parse($"def g: . + {k}; [limit(3; range(10) | g)] | map(select(. % 2 == 0)) | add // 0");
                var actual = string.Join(",", expression.Evaluate(Json("0")).Select(static e => e.GetRawText()));
                var even = Enumerable.Range(0, 3).Select(x => x + k).Where(static x => x % 2 == 0).Sum();
                if (actual != even.ToString())
                    wrong.Add($"thread {t}, round {i}: {actual}, expected {even}");
            }
        });

        Assert.Empty(wrong.Take(5));
    }
}
