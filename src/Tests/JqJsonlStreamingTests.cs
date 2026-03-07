using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Devlooped;

namespace Devlooped.Tests;

public class JqJsonlStreamingTests
{
    static IAsyncEnumerable<JsonElement> ParseJsonLines(string jsonl, CancellationToken cancellation = default)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonl));
        return JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(stream, true, new JsonSerializerOptions { AllowTrailingCommas = true }, cancellation);
    }

    [Fact]
    public async Task Jsonl_stream_extracts_field_from_each_element()
    {
        var jsonl = """
            {"name":"Alice","age":30}
            {"name":"Bob","age":25}
            {"name":"Charlie","age":35}
            """;

        var results = new List<string>();
        await foreach (var element in Jq.EvaluateAsync(".name", ParseJsonLines(jsonl)))
            results.Add(element.GetString()!);

        Assert.Equal(["Alice", "Bob", "Charlie"], results);
    }

    [Fact]
    public async Task Jsonl_stream_transforms_each_element()
    {
        var jsonl = """
            {"x":1}
            {"x":2}
            {"x":3}
            """;

        var results = new List<int>();
        await foreach (var element in Jq.EvaluateAsync(".x * 2", ParseJsonLines(jsonl)))
            results.Add(element.GetInt32());

        Assert.Equal([2, 4, 6], results);
    }

    [Fact]
    public async Task Jsonl_stream_expands_array_elements_across_inputs()
    {
        var jsonl = """
            {"items":[1,2]}
            {"items":[3,4]}
            """;

        var results = new List<int>();
        await foreach (var element in Jq.EvaluateAsync(".items[]", ParseJsonLines(jsonl)))
            results.Add(element.GetInt32());

        Assert.Equal([1, 2, 3, 4], results);
    }

    [Fact]
    public async Task Jsonl_stream_filters_elements_with_select()
    {
        var jsonl = """
            {"name":"Alice","active":true}
            {"name":"Bob","active":false}
            {"name":"Charlie","active":true}
            """;

        var results = new List<string>();
        await foreach (var element in Jq.EvaluateAsync("select(.active) | .name", ParseJsonLines(jsonl)))
            results.Add(element.GetString()!);

        Assert.Equal(["Alice", "Charlie"], results);
    }
}
