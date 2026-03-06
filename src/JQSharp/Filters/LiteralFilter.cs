using System.Text.Json;

namespace Devlooped;

public sealed class LiteralFilter : JqFilter
{
    readonly JsonElement value;

    public LiteralFilter(JsonElement value)
    {
        this.value = value.Clone();
    }

    public static LiteralFilter FromRawJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new LiteralFilter(document.RootElement.Clone());
    }

    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        yield return value;
    }
}
