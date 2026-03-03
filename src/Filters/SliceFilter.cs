using System.Text.Json;

namespace Devlooped;

public sealed class SliceFilter : JqFilter
{
    private readonly int? start;
    private readonly int? end;

    public SliceFilter(int? start, int? end)
    {
        this.start = start;
        this.end = end;
    }

    public override IEnumerable<JsonElement> Evaluate(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Null)
        {
            yield return CreateNullElement();
            yield break;
        }

        if (input.ValueKind == JsonValueKind.Array)
        {
            var items = input.EnumerateArray().ToArray();
            var range = Normalize(items.Length);
            var result = CreateElement(writer =>
            {
                writer.WriteStartArray();
                for (var i = range.Start; i < range.End; i++)
                    items[i].WriteTo(writer);
                writer.WriteEndArray();
            });

            yield return result;
            yield break;
        }

        if (input.ValueKind == JsonValueKind.String)
        {
            var text = input.GetString() ?? string.Empty;
            var range = Normalize(text.Length);
            var length = range.End - range.Start;
            if (length < 0)
                length = 0;

            var result = CreateStringElement(text.Substring(range.Start, length));
            yield return result;
            yield break;
        }

        throw new JqException($"Cannot slice {GetTypeName(input)}");
    }

    private (int Start, int End) Normalize(int length)
    {
        var normalizedStart = NormalizeBound(start, length, 0);
        var normalizedEnd = NormalizeBound(end, length, length);

        if (normalizedStart < 0)
            normalizedStart = 0;
        if (normalizedStart > length)
            normalizedStart = length;

        if (normalizedEnd < 0)
            normalizedEnd = 0;
        if (normalizedEnd > length)
            normalizedEnd = length;

        if (normalizedStart >= normalizedEnd)
            return (normalizedStart, normalizedStart);

        return (normalizedStart, normalizedEnd);
    }

    private static int NormalizeBound(int? value, int length, int fallback)
    {
        if (!value.HasValue)
            return fallback;

        var normalized = value.Value;
        if (normalized < 0)
            normalized = length + normalized;

        return normalized;
    }
}