using System.Text.Json;

namespace Devlooped;

public static class Jq
{
    public static IEnumerable<JsonElement> Evaluate(string expression, JsonElement input)
    {
        var filter = Parse(expression);
        foreach (var result in filter.Evaluate(input))
            yield return result.Clone();
    }

    public static JqFilter Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new JqException("Filter expression cannot be empty.");

        return JqParser.Parse(expression);
    }
}