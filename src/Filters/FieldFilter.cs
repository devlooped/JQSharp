using System.Text.Json;

namespace Devlooped;

public sealed class FieldFilter : JqFilter
{
    private readonly string field;

    public FieldFilter(string field)
    {
        this.field = field;
    }

    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        if (input.ValueKind == JsonValueKind.Null)
        {
            yield return CreateNullElement();
            yield break;
        }

        if (input.ValueKind != JsonValueKind.Object)
            throw new JqException($"Cannot index {GetTypeName(input)} with string \"{field}\"");

        if (input.TryGetProperty(field, out var value))
            yield return value;
        else
            yield return CreateNullElement();
    }
}
