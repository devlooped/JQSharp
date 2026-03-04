using System.Text.Json;

namespace Devlooped;

public sealed class IndexFilter : JqFilter
{
    private readonly int index;

    public IndexFilter(int index)
    {
        this.index = index;
    }

    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        if (input.ValueKind == JsonValueKind.Null)
        {
            yield return CreateNullElement();
            yield break;
        }

        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"Cannot index {GetTypeName(input)} with number {index}");

        var length = input.GetArrayLength();
        if (index >= 0)
        {
            if (index >= length)
            {
                yield return CreateNullElement();
                yield break;
            }

            yield return input[index];
            yield break;
        }

        var actualIndex = length + index;
        if (actualIndex < 0)
            throw new JqException("Out of bounds negative array index");

        yield return input[actualIndex];
    }
}
