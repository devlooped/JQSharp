using System.Text.Json;

namespace Devlooped;

/// <summary>
/// Provides methods for parsing and evaluating jq filter expressions against JSON data.
/// </summary>
public static class Jq
{
    /// <summary>
    /// Parses a jq filter expression and returns a reusable <see cref="JqExpression"/>
    /// that can be evaluated against multiple JSON inputs without re-parsing.
    /// </summary>
    /// <param name="expression">The jq filter expression to parse.</param>
    /// <returns>A parsed <see cref="JqExpression"/> ready for evaluation.</returns>
    /// <exception cref="JqException">Thrown when the expression is empty or invalid.</exception>
    /// <remarks>
    /// The returned <see cref="JqExpression"/> is thread-safe and can be cached and
    /// evaluated concurrently from multiple threads.
    /// </remarks>
    public static JqExpression Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new JqException("Filter expression cannot be empty.");

        return new JqExpression(JqParser.Parse(expression));
    }

    /// <summary>
    /// Evaluates a jq filter expression against the given JSON input and returns the matching results.
    /// </summary>
    /// <param name="expression">The jq filter expression to evaluate.</param>
    /// <param name="input">The JSON element to use as input for the filter.</param>
    /// <returns>An enumerable of <see cref="JsonElement"/> values produced by the filter.</returns>
    /// <exception cref="JqException">Thrown when the expression is empty, invalid, or causes an error during evaluation.</exception>
    public static IEnumerable<JsonElement> Evaluate(string expression, JsonElement input)
        => Parse(expression).Evaluate(input);
}
