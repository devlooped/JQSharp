using System.Text;
using System.Text.Json;

namespace Devlooped;

// Handles @format "string\(expr)" syntax.
// Literal parts are NOT formatted; only interpolated expression values are.
public sealed class FormattedStringFilter : JqFilter
{
    readonly string formatName;
    readonly (string? Literal, JqFilter? Expression)[] parts;

    public FormattedStringFilter(string formatName, (string? Literal, JqFilter? Expression)[] parts)
    {
        this.formatName = formatName;
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
                        // Apply the format to each interpolated expression value
                        clone.Append(FormatFilter.FormatValue(formatName, value));
                        expanded.Add(clone);
                    }
                }

                results = expanded;
            }
        }

        foreach (var sb in results)
            yield return CreateStringElement(sb.ToString());
    }
}
