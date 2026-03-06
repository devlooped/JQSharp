using System.Text.Json;

namespace Devlooped;

public sealed class NegateFilter : JqFilter
{
    readonly JqFilter inner;

    public NegateFilter(JqFilter inner)
    {
        this.inner = inner;
    }

    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        foreach (var element in inner.Evaluate(input, env))
        {
            if (element.ValueKind != JsonValueKind.Number)
                throw new JqException($"{GetTypeName(element)} ({GetValueText(element)}) cannot be negated");

            var value = -element.GetDouble();
            yield return CreateNumberElement(value);
        }
    }
}

