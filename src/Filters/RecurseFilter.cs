using System.Text.Json;

namespace Devlooped;

public sealed class RecurseFilter : JqFilter
{
    public override IEnumerable<JsonElement> Evaluate(JsonElement input)
    {
        foreach (var item in Traverse(input))
            yield return item;
    }

    private static IEnumerable<JsonElement> Traverse(JsonElement current)
    {
        yield return current;

        if (current.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in current.EnumerateArray())
            {
                foreach (var nested in Traverse(element))
                    yield return nested;
            }
        }
        else if (current.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in current.EnumerateObject())
            {
                foreach (var nested in Traverse(property.Value))
                    yield return nested;
            }
        }
    }
}