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
        => Evaluate(input, variables: null);

    /// <summary>
    /// Evaluates this parsed expression against the given JSON input and
    /// returns the matching results, with optional external variable bindings.
    /// </summary>
    /// <param name="input">The JSON element to use as input for the filter.</param>
    /// <param name="variables">
    /// Optional external variables to bind before evaluation, analogous to jq's
    /// <c>--arg</c>/<c>--argjson</c>. Dictionary keys are variable names without a
    /// leading <c>$</c> and must be valid jq identifiers (for example, <c>"root"</c>
    /// binds <c>$root</c>). Values are available to the expression like variables
    /// declared with <c>as</c>, and are resolved dynamically at evaluation time
    /// (similar to <c>$ENV</c>).
    /// </param>
    /// <returns>An enumerable of <see cref="JsonElement"/> values produced by the filter.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when a variable name is empty, starts with <c>$</c>, or is not a valid jq identifier.
    /// </exception>
    /// <exception cref="JqException">Thrown when the expression causes an error during evaluation.</exception>
    public IEnumerable<JsonElement> Evaluate(JsonElement input, IReadOnlyDictionary<string, JsonElement>? variables)
    {
        var env = JqEnvironment.FromVariables(variables);
        IEnumerable<JsonElement> results;
        try
        {
            results = [.. filter.Evaluate(input, env)];
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
