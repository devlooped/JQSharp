using System.Text.Json;

namespace Devlooped;

public sealed class ArrayFilter : JqFilter
{
    private readonly JqFilter inner;

    public ArrayFilter(JqFilter inner)
    {
        this.inner = inner;
    }

    public override IEnumerable<JsonElement> Evaluate(JsonElement input)
    {
        var items = inner.Evaluate(input).ToArray();
        var result = CreateElement(writer =>
        {
            writer.WriteStartArray();
            foreach (var item in items)
                item.WriteTo(writer);
            writer.WriteEndArray();
        });

        yield return result;
    }
}