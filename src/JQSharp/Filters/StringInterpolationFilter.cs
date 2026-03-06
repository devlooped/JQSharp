using System.Text;
using System.Text.Json;

namespace Devlooped;

public sealed class StringInterpolationFilter : JqFilter
{
    readonly (string? Literal, JqFilter? Expression)[] parts;

    public StringInterpolationFilter((string? Literal, JqFilter? Expression)[] parts)
    {
        this.parts = parts;
    }

    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        var results = new List<StringBuilder> { new() };

        foreach (var (literal, expression) in parts)
        {
            if (literal != null)
            {
                foreach (var sb in results)
                    sb.Append(literal);
            }
            else
            {
                var values = expression!.Evaluate(input, env).ToList();
                if (values.Count == 0)
                    yield break;

                var expanded = new List<StringBuilder>(results.Count * values.Count);
                foreach (var sb in results)
                {
                    foreach (var value in values)
                    {
                        var clone = new StringBuilder(sb.ToString());
                        clone.Append(ValueToString(value));
                        expanded.Add(clone);
                    }
                }

                results = expanded;
            }
        }

        foreach (var sb in results)
            yield return CreateStringElement(sb.ToString());
    }

    public static string ValueToString(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? string.Empty;

        return JsonSerializer.Serialize(value);
    }
}

