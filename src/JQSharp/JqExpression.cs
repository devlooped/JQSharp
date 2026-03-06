using System.Text.Json;

namespace Devlooped;

/// <summary>
/// Represents a parsed jq filter expression that can be evaluated against
/// multiple JSON inputs without re-parsing.
/// </summary>
/// <remarks>
/// Obtain instances via <see cref="Jq.Parse(string)"/>. This type is
/// thread-safe: a single <see cref="JqExpression"/> can be evaluated
/// concurrently from multiple threads because the underlying AST and
/// environment are immutable.
/// </remarks>
public sealed class JqExpression
{
    readonly JqFilter filter;

    internal JqExpression(JqFilter filter) => this.filter = filter;

    /// <summary>
    /// Evaluates this parsed expression against the given JSON input and
    /// returns the matching results.
    /// </summary>
    /// <param name="input">The JSON element to use as input for the filter.</param>
    /// <returns>An enumerable of <see cref="JsonElement"/> values produced by the filter.</returns>
    /// <exception cref="JqException">Thrown when the expression causes an error during evaluation.</exception>
    public IEnumerable<JsonElement> Evaluate(JsonElement input)
    {
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
}
