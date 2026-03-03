using System.Text.Json;

namespace Devlooped;

public sealed class OptionalFilter : JqFilter
{
    private readonly JqFilter inner;

    public OptionalFilter(JqFilter inner)
    {
        this.inner = inner;
    }

    public override IEnumerable<JsonElement> Evaluate(JsonElement input)
    {
        JsonElement[] values;
        try
        {
            values = inner.Evaluate(input).ToArray();
        }
        catch (JqException)
        {
            yield break;
        }

        foreach (var value in values)
            yield return value;
    }
}
