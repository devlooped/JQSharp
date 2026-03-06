using System.Text.Json;

namespace Devlooped;

/// <summary>
/// Provides methods for evaluating jq filter expressions against JSON data.
/// </summary>
public static class Jq
{
    /// <summary>
    /// Evaluates a jq filter expression against the given JSON input and returns the matching results.
    /// </summary>
    /// <param name="expression">The jq filter expression to evaluate.</param>
    /// <param name="input">The JSON element to use as input for the filter.</param>
    /// <returns>An enumerable of <see cref="JsonElement"/> values produced by the filter.</returns>
    /// <exception cref="JqException">Thrown when the expression is empty, invalid, or causes an error during evaluation.</exception>
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

    internal static JqFilter Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new JqException("Filter expression cannot be empty.");

        return JqParser.Parse(expression);
    }
}
