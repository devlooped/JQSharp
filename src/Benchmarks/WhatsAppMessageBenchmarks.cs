using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Devlooped;
using JQLite;

[MemoryDiagnoser]
public class WhatsAppMessageBenchmarks
{
    byte[] messageJsonUtf8 = [];
    string messageJson = string.Empty;
    string query = string.Empty;
    JqExpression cachedExpression = default!;
    int expectedResultsPerMessage;

    [GlobalSetup]
    public void Setup()
    {
        var basePath = AppContext.BaseDirectory;
        messageJsonUtf8 = File.ReadAllBytes(Path.Combine(basePath, "WhatsApp", "Text.json"));
        messageJson = File.ReadAllText(Path.Combine(basePath, "WhatsApp", "Text.json"));
        query = File.ReadAllText(Path.Combine(basePath, "WhatsApp", "Message.jq"));
        cachedExpression = Jq.Parse(query);

        using (var document = JsonDocument.Parse(messageJsonUtf8))
            expectedResultsPerMessage = ConsumeResults(cachedExpression.Evaluate(document.RootElement));

        if (expectedResultsPerMessage <= 0)
            throw new InvalidOperationException("The benchmark query did not produce any results for the sample WhatsApp payload.");

        var wrapperSanity = JQ.ExecuteAsync(new JqParams(query)
        {
            Json = messageJson,
            RawOutput = false,
            CompactOutput = true,
            MonochromeOutput = true,
        }).GetAwaiter().GetResult();

        if (wrapperSanity.ExitCode != 0)
            throw new InvalidOperationException($"Devlooped.JQ failed during setup: {wrapperSanity.StandardError}");

        var wrapperResults = CountLines(wrapperSanity.StandardOutput);
        if (wrapperResults != expectedResultsPerMessage)
            throw new InvalidOperationException($"Expected {expectedResultsPerMessage} wrapper results but got {wrapperResults}.");
    }

    [Benchmark(Baseline = true)]
    public async Task<int> DevloopedJQ()
    {
        var result = await JQ.ExecuteAsync(new JqParams(query)
        {
            Json = messageJson,
            RawOutput = false,
            CompactOutput = true,
            MonochromeOutput = true,
        });

        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.StandardError);

        var outputCount = CountLines(result.StandardOutput);
        if (outputCount != expectedResultsPerMessage)
            throw new InvalidOperationException($"Expected {expectedResultsPerMessage} jq.exe results but got {outputCount}.");

        return outputCount;
    }

    [Benchmark]
    public int JQLiteWithoutExpressionCaching()
    {
        using var document = JsonDocument.Parse(messageJsonUtf8);
        var totalResults = ConsumeResults(Jq.Evaluate(query, document.RootElement));

        if (totalResults != expectedResultsPerMessage)
            throw new InvalidOperationException($"Expected {expectedResultsPerMessage} uncached jqlite results but got {totalResults}.");

        return totalResults;
    }

    [Benchmark]
    public int JQLiteWithExpressionCaching()
    {
        using var document = JsonDocument.Parse(messageJsonUtf8);
        var totalResults = ConsumeResults(cachedExpression.Evaluate(document.RootElement));

        if (totalResults != expectedResultsPerMessage)
            throw new InvalidOperationException($"Expected {expectedResultsPerMessage} cached jqlite results but got {totalResults}.");

        return totalResults;
    }

    static int ConsumeResults(IEnumerable<JsonElement> results)
    {
        var count = 0;

        foreach (var result in results)
        {
            _ = result.ValueKind;
            count++;
        }

        return count;
    }

    static int CountLines(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return 0;

        var count = 1;

        foreach (var ch in output)
        {
            if (ch == '\n')
                count++;
        }

        return count;
    }
}
