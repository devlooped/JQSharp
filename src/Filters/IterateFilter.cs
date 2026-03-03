using System.Text.Json;

namespace Devlooped;

public sealed class IterateFilter : JqFilter
{
    public override IEnumerable<JsonElement> Evaluate(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Null)
            yield break;

        if (input.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in input.EnumerateArray())
                yield return item;

            yield break;
        }

        if (input.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in input.EnumerateObject())
                yield return property.Value;

            yield break;
        }

        throw new JqException($"Cannot iterate over {GetTypeName(input)} ({GetValueText(input)})");
    }
}