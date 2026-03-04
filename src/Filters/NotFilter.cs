using System.Text.Json;

namespace Devlooped;

public sealed class NotFilter : JqFilter
{
    public override IEnumerable<JsonElement> Evaluate(JsonElement input)
    {
        var value = input.ValueKind is JsonValueKind.Null or JsonValueKind.False;
        yield return CreateElement(writer => writer.WriteBooleanValue(value));
    }
}
