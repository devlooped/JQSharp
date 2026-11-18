using System.Runtime.CompilerServices;
using System.Text.Json;
using Devlooped.Core.Binding;
using Devlooped.Host;

namespace Devlooped;

/// <summary>Parses and evaluates jq expressions (the jqlite profile of jq 1.8) on <see cref="JsonElement"/> values.</summary>
public static class Jq
{
    static readonly JqOptions defaults = new();

    /// <summary>
    /// Parses and binds <paramref name="expression"/>, loading its modules through the options'
    /// resolver, into an expression to evaluate any number of times, concurrently if need be.
    /// An empty expression is <c>.</c>.
    /// </summary>
    /// <param name="expression">The jq expression.</param>
    /// <param name="options">Modules, <c>$ENV</c>, clock and depth limit for every evaluation of it; the defaults when <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is <see langword="null"/>.</exception>
    /// <exception cref="JqParseException">A syntax or binding error (an unknown function, label or module, a cycle of imports), with its line and column.</exception>
    public static JqExpression Parse(string expression, JqOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(expression);
        options ??= defaults;
        var modules = options.Resolver is { } resolver ? new ResolverSource(resolver) : null;
        if (!Resolver.TryCompile(expression, out var program, out var error, modules: modules))
        {
            var where = error.Module is null ? "" : $"{error.Module}, ";
            throw new JqParseException($"{error.Message} at {where}line {error.Line}, column {error.Column}", error.Line, error.Column);
        }

        return new JqExpression(program, options);
    }

    /// <summary>Parses <paramref name="expression"/> with the default options and evaluates it on <paramref name="input"/>, returning all its results.</summary>
    /// <param name="expression">The jq expression.</param>
    /// <param name="input">The input value.</param>
    /// <param name="variables">Host variables, as for <see cref="JqExpression.Evaluate(JsonElement, IReadOnlyDictionary{string, JsonElement}?)"/>.</param>
    /// <exception cref="ArgumentException">A variable name is not valid.</exception>
    /// <exception cref="JqException">A syntax or binding error (<see cref="JqParseException"/>), or an error that reached the top of the expression.</exception>
    public static IReadOnlyList<JsonElement> Evaluate(string expression, JsonElement input, IReadOnlyDictionary<string, JsonElement>? variables = null)
        => Parse(expression).Evaluate(input, variables);

    /// <summary>
    /// Parses <paramref name="expression"/> with the default options and evaluates it on each value of
    /// <paramref name="input"/> in turn (a JSON Lines stream, for example), yielding the results of each.
    /// </summary>
    /// <param name="expression">The jq expression.</param>
    /// <param name="input">The input values.</param>
    /// <param name="variables">Host variables for every evaluation.</param>
    /// <param name="cancellationToken">Stops reading <paramref name="input"/>.</param>
    /// <exception cref="JqException">A syntax or binding error, when the sequence is first enumerated, or an error that reached the top of the expression for one of the inputs.</exception>
    public static async IAsyncEnumerable<JsonElement> EvaluateAsync(string expression, IAsyncEnumerable<JsonElement> input,
        IReadOnlyDictionary<string, JsonElement>? variables = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var result in EvaluateAsync(Parse(expression), input, variables, cancellationToken))
            yield return result;
    }

    /// <summary>
    /// Evaluates <paramref name="expression"/> on each value of <paramref name="input"/> in turn (a
    /// JSON Lines stream, for example), yielding the results of each; the results of one input are
    /// computed before they are yielded. Each value is evaluated alone, as by
    /// <see cref="JqExpression.Evaluate(JsonElement, IReadOnlyDictionary{string, JsonElement}?)"/>:
    /// <c>halt</c> ends only the evaluation of its value.
    /// </summary>
    /// <param name="expression">The parsed expression.</param>
    /// <param name="input">The input values.</param>
    /// <param name="variables">Host variables for every evaluation.</param>
    /// <param name="cancellationToken">Stops reading <paramref name="input"/>.</param>
    /// <exception cref="JqException">An error that reached the top of the expression for one of the inputs.</exception>
    public static async IAsyncEnumerable<JsonElement> EvaluateAsync(JqExpression expression, IAsyncEnumerable<JsonElement> input,
        IReadOnlyDictionary<string, JsonElement>? variables = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);
        await foreach (var element in input.WithCancellation(cancellationToken))
        {
            foreach (var result in expression.Evaluate(element, variables))
                yield return result;
        }
    }
}
