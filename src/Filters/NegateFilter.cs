using System.Text.Json;

namespace Devlooped;

public sealed class NegateFilter : JqFilter
{
    private readonly JqFilter inner;

    public NegateFilter(JqFilter inner)
    {
        this.inner = inner;
    }

    public override IEnumerable<JsonElement> Evaluate(JsonElement input)
    {
        foreach (var element in inner.Evaluate(input))
        {
            if (element.ValueKind != JsonValueKind.Number)
                throw new JqException($"{GetTypeName(element)} ({GetValueText(element)}) cannot be negated");

            var value = -element.GetDouble();
            yield return CreateNumberElement(value);
        }
    }
}
