using System.Text.Json;

namespace Devlooped;

public static class Jq
{
    public static IEnumerable<JsonElement> Evaluate(string expression, JsonElement input)
    {
        var filter = Parse(expression);
        IEnumerable<JsonElement> results;
        try
        {
            results = [.. filter.Evaluate(input)];
        }
        catch (JqHaltException)
        {
            yield break;
        }
        catch (JqBreakException ex)
        {
            throw new JqException($"break: label {ex.Label} not found");
        }

        foreach (var result in results)
            yield return result.Clone();
    }

    public static JqFilter Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new JqException("Filter expression cannot be empty.");

        return JqParser.Parse(expression);
    }
}
