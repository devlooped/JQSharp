using System.Runtime.CompilerServices;
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
    /// <param name="resolver">
    /// An optional <see cref="JqResolver"/> used to resolve <c>include</c> and <c>import</c> statements in
    /// the expression. When <see langword="null"/>, any <c>include</c> or <c>import</c> statement encountered
    /// during parsing will throw a <see cref="JqException"/>.
    /// </param>
    /// <returns>A parsed <see cref="JqExpression"/> ready for evaluation.</returns>
    /// <exception cref="JqException">Thrown when the expression is empty or invalid.</exception>
    /// <remarks>
    /// The returned <see cref="JqExpression"/> is thread-safe and can be cached and
    /// evaluated concurrently from multiple threads.
    /// </remarks>
    public static JqExpression Parse(string expression, JqResolver? resolver = null)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new JqException("Filter expression cannot be empty.");

        return new JqExpression(JqParser.Parse(expression, resolver));
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

    /// <summary>
    /// Evaluates a jq filter expression against the given JSON input and returns the matching results,
    /// with optional external variable bindings.
    /// </summary>
    /// <param name="expression">The jq filter expression to evaluate.</param>
    /// <param name="input">The JSON element to use as input for the filter.</param>
    /// <param name="variables">
    /// Optional external variables to bind before evaluation, analogous to jq's
    /// <c>--arg</c>/<c>--argjson</c>. Dictionary keys are variable names without a
    /// leading <c>$</c> and must be valid jq identifiers (for example, <c>"root"</c>
    /// binds <c>$root</c>).
    /// </param>
    /// <returns>An enumerable of <see cref="JsonElement"/> values produced by the filter.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when a variable name is empty, starts with <c>$</c>, or is not a valid jq identifier.
    /// </exception>
    /// <exception cref="JqException">Thrown when the expression is empty, invalid, or causes an error during evaluation.</exception>
    public static IEnumerable<JsonElement> Evaluate(string expression, JsonElement input, IReadOnlyDictionary<string, JsonElement>? variables)
        => Parse(expression).Evaluate(input, variables);

    /// <summary>
    /// Evaluates a jq filter expression against each element in an asynchronous sequence and yields the matching results.
    /// </summary>
    /// <param name="expression">The jq filter expression to evaluate.</param>
    /// <param name="input">The asynchronous sequence of <see cref="JsonElement"/> values to evaluate the filter against.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous iteration.</param>
    /// <returns>
    /// An asynchronous sequence of <see cref="JsonElement"/> values produced by applying the filter
    /// to each element in <paramref name="input"/>.
    /// </returns>
    /// <exception cref="JqException">Thrown when the expression is empty, invalid, or causes an error during evaluation.</exception>
    public static IAsyncEnumerable<JsonElement> EvaluateAsync(string expression, IAsyncEnumerable<JsonElement> input, CancellationToken cancellationToken = default)
        => EvaluateAsync(expression, input, variables: null, cancellationToken);

    /// <summary>
    /// Evaluates a jq filter expression against each element in an asynchronous sequence and yields the matching results,
    /// with optional external variable bindings.
    /// </summary>
    /// <param name="expression">The jq filter expression to evaluate.</param>
    /// <param name="input">The asynchronous sequence of <see cref="JsonElement"/> values to evaluate the filter against.</param>
    /// <param name="variables">
    /// Optional external variables to bind before evaluation, analogous to jq's
    /// <c>--arg</c>/<c>--argjson</c>. Dictionary keys are variable names without a
    /// leading <c>$</c> and must be valid jq identifiers.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the asynchronous iteration.</param>
    /// <returns>
    /// An asynchronous sequence of <see cref="JsonElement"/> values produced by applying the filter
    /// to each element in <paramref name="input"/>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when a variable name is empty, starts with <c>$</c>, or is not a valid jq identifier.
    /// </exception>
    /// <exception cref="JqException">Thrown when the expression is empty, invalid, or causes an error during evaluation.</exception>
    public static async IAsyncEnumerable<JsonElement> EvaluateAsync(
        string expression,
        IAsyncEnumerable<JsonElement> input,
        IReadOnlyDictionary<string, JsonElement>? variables,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var parsed = Parse(expression);
        await foreach (var element in EvaluateAsync(parsed, input, variables, cancellationToken))
            yield return element;
    }

    /// <summary>
    /// Evaluates a parsed <see cref="JqExpression"/> against each element in an asynchronous sequence and yields the matching results.
    /// </summary>
    /// <param name="expression">The pre-parsed <see cref="JqExpression"/> to evaluate.</param>
    /// <param name="input">The asynchronous sequence of <see cref="JsonElement"/> values to evaluate the expression against.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous iteration.</param>
    /// <returns>
    /// An asynchronous sequence of <see cref="JsonElement"/> values produced by applying the expression
    /// to each element in <paramref name="input"/>.
    /// </returns>
    /// <remarks>
    /// Prefer this overload when evaluating the same expression against multiple streams, as it avoids
    /// re-parsing the filter on every call.
    /// </remarks>
    public static IAsyncEnumerable<JsonElement> EvaluateAsync(JqExpression expression, IAsyncEnumerable<JsonElement> input, CancellationToken cancellationToken = default)
        => EvaluateAsync(expression, input, variables: null, cancellationToken);

    /// <summary>
    /// Evaluates a parsed <see cref="JqExpression"/> against each element in an asynchronous sequence and yields the matching results,
    /// with optional external variable bindings.
    /// </summary>
    /// <param name="expression">The pre-parsed <see cref="JqExpression"/> to evaluate.</param>
    /// <param name="input">The asynchronous sequence of <see cref="JsonElement"/> values to evaluate the expression against.</param>
    /// <param name="variables">
    /// Optional external variables to bind before evaluation, analogous to jq's
    /// <c>--arg</c>/<c>--argjson</c>. Dictionary keys are variable names without a
    /// leading <c>$</c> and must be valid jq identifiers.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the asynchronous iteration.</param>
    /// <returns>
    /// An asynchronous sequence of <see cref="JsonElement"/> values produced by applying the expression
    /// to each element in <paramref name="input"/>.
    /// </returns>
    /// <remarks>
    /// Prefer this overload when evaluating the same expression against multiple streams, as it avoids
    /// re-parsing the filter on every call.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Thrown when a variable name is empty, starts with <c>$</c>, or is not a valid jq identifier.
    /// </exception>
    public static async IAsyncEnumerable<JsonElement> EvaluateAsync(
        JqExpression expression,
        IAsyncEnumerable<JsonElement> input,
        IReadOnlyDictionary<string, JsonElement>? variables,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var element in input.WithCancellation(cancellationToken))
        {
            foreach (var result in expression.Evaluate(element, variables))
                yield return result;
        }
    }
}
