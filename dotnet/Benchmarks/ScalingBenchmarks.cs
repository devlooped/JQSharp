using System.Text.Json;
using BenchmarkDotNet.Attributes;
using JQLite;

/// <summary>
/// Programs whose cost must grow in step with their size: each runs a cached expression on the
/// input <c>null</c>, and its result is checked once in setup.
/// </summary>
[MemoryDiagnoser]
public class ScalingBenchmarks
{
    JsonDocument document = default!;
    JqExpression mapAndAdd = default!;
    JqExpression appendInReduce = default!;
    JqExpression updateEachElement = default!;
    JqExpression addKeysInReduce = default!;
    JqExpression longRange = default!;
    JqExpression gsubManyMatches = default!;
    JqExpression splitManyMatches = default!;
    JqExpression joinManyStrings = default!;
    JqExpression addManyStrings = default!;

    [GlobalSetup]
    public void Setup()
    {
        document = JsonDocument.Parse("null");
        mapAndAdd = Jq.Parse("[range(100000)] | map(. + 1) | add");
        appendInReduce = Jq.Parse("reduce range(10000) as $i ([]; . + [$i]) | length");
        updateEachElement = Jq.Parse("[range(4000)] | .[] |= . + 1 | length");
        addKeysInReduce = Jq.Parse("[range(10000)] | INDEX(.) | length");
        longRange = Jq.Parse("range(2000000)");
        gsubManyMatches = Jq.Parse("\"ab\" * 20000 | gsub(\"a\"; \"\") | length");
        splitManyMatches = Jq.Parse("\"ab\" * 20000 | split(\"a\"; null) | length");
        joinManyStrings = Jq.Parse("[range(20000) | \"ab\"] | join(\",\") | length");
        addManyStrings = Jq.Parse("[range(20000) | \"ab\"] | add | length");

        Check("5000050000", MapAndAdd());
        Check("10000", AppendInReduce());
        Check("4000", UpdateEachElement());
        Check("10000", AddKeysInReduce());
        Check("0", FirstOfLongRange());
        Check("20000", GsubManyMatches());
        Check("20001", SplitManyMatches());
        Check("59999", JoinManyStrings());
        Check("40000", AddManyStrings());
    }

    [GlobalCleanup]
    public void Cleanup() => document.Dispose();

    [Benchmark]
    public JsonElement MapAndAdd() => mapAndAdd.Evaluate(document.RootElement)[0];

    [Benchmark]
    public JsonElement AppendInReduce() => appendInReduce.Evaluate(document.RootElement)[0];

    [Benchmark]
    public JsonElement UpdateEachElement() => updateEachElement.Evaluate(document.RootElement)[0];

    // An object grown by one key per step through |= (INDEX): the update must not copy it.
    [Benchmark]
    public JsonElement AddKeysInReduce() => addKeysInReduce.Evaluate(document.RootElement)[0];

    // "Taking only the first result" is the streaming overload: it stops the evaluation when the callback returns false.
    [Benchmark]
    public JsonElement FirstOfLongRange()
    {
        var first = default(JsonElement);
        longRange.Evaluate(document.RootElement, result =>
        {
            first = result;
            return false;
        });
        return first;
    }

    // One match every two code points: each match must cost the same however long the input.
    [Benchmark]
    public JsonElement GsubManyMatches() => gsubManyMatches.Evaluate(document.RootElement)[0];

    [Benchmark]
    public JsonElement SplitManyMatches() => splitManyMatches.Evaluate(document.RootElement)[0];

    // Each string added to the accumulator must cost its own length, not the accumulator's.
    [Benchmark]
    public JsonElement JoinManyStrings() => joinManyStrings.Evaluate(document.RootElement)[0];

    [Benchmark]
    public JsonElement AddManyStrings() => addManyStrings.Evaluate(document.RootElement)[0];

    static void Check(string expected, JsonElement actual)
    {
        if (actual.GetRawText() != expected)
            throw new InvalidOperationException($"Expected {expected} but got {actual.GetRawText()}.");
    }
}
