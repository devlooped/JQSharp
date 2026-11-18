using System.Collections;
using System.Text.Json;
using Devlooped.Core.Binding;
using Devlooped.Core.Runtime;
using Devlooped.Core.Values;
using Devlooped.Host;
using Devlooped.Json;

namespace Devlooped;

/// <summary>
/// A jq expression parsed and bound once by <see cref="Jq.Parse"/>, to evaluate on any number of inputs.
/// </summary>
/// <remarks>
/// Immutable and safe to evaluate concurrently from many threads, each evaluation with its own input
/// and variables. Results are <see cref="JsonElement"/>s of their own, valid after the input's
/// document is disposed; their text is the canonical text of the value (NaN as <c>null</c>,
/// infinities as ±1.7976931348623157e+308).
/// </remarks>
public sealed class JqExpression
{
    readonly BoundProgram program;
    readonly Func<JqValue> environment;
    readonly IClock clock;
    readonly int maxDepth;

    internal JqExpression(BoundProgram program, JqOptions options)
    {
        this.program = program;
        clock = new TimeProviderClock(options.TimeProvider);
        maxDepth = options.MaxDepth;
        if (options.Environment is { } variables)
        {
            var snapshot = ToObject(variables);
            environment = () => snapshot;
        }
        else
        {
            environment = ProcessEnvironment;
        }
    }

    /// <summary>
    /// Evaluates the expression on <paramref name="input"/> and returns all its results, computed
    /// before the list is returned.
    /// </summary>
    /// <param name="input">The input value.</param>
    /// <param name="variables">
    /// Host variables, by name without <c>$</c> (<c>"root"</c> is <c>$root</c>): each name a jq identifier,
    /// <c>ENV</c> excluded. A variable the expression uses but the host does not give is the runtime
    /// error <c>$name is not defined</c>; a binding in the expression shadows a host variable.
    /// </param>
    /// <exception cref="ArgumentException">A variable name is not a valid identifier, starts with <c>$</c>, or is <c>ENV</c>.</exception>
    /// <exception cref="JqException">An error reached the top of the expression (<see cref="JqHaltException"/> for <c>halt_error</c>); no results are returned.</exception>
    public IReadOnlyList<JsonElement> Evaluate(JsonElement input, IReadOnlyDictionary<string, JsonElement>? variables = null)
    {
        var results = new List<JsonElement>();
        Evaluate(input, result =>
        {
            results.Add(result);
            return true;
        }, variables);
        return results;
    }

    /// <summary>
    /// Evaluates the expression on <paramref name="input"/>, giving each result to
    /// <paramref name="onResult"/> as soon as it is produced; the callback returns
    /// <see langword="false"/> to stop the evaluation (stopping is not an error).
    /// </summary>
    /// <param name="input">The input value.</param>
    /// <param name="onResult">Receives each result; returns <see langword="true"/> to go on.</param>
    /// <param name="variables">Host variables, as for <see cref="Evaluate(JsonElement, IReadOnlyDictionary{string, JsonElement}?)"/>.</param>
    /// <exception cref="ArgumentException">A variable name is not a valid identifier, starts with <c>$</c>, or is <c>ENV</c>.</exception>
    /// <exception cref="JqException">An error reached the top of the expression, after the results produced before it were given to <paramref name="onResult"/>.</exception>
    public void Evaluate(JsonElement input, Func<JsonElement, bool> onResult, IReadOnlyDictionary<string, JsonElement>? variables = null)
    {
        ArgumentNullException.ThrowIfNull(onResult);
        var options = new EvaluationOptions
        {
            Variables = variables is null ? null : Convert(variables),
            Environment = environment,
            Clock = clock,
            MaxDepth = maxDepth,
        };

        var outcome = Interpreter.Run(program, JsonElementAdapter.ToValue(input), options, value => onResult(JsonElementAdapter.ToElement(value)));
        if (outcome.Kind == OutcomeKind.Error)
            throw new JqException(Message(outcome.Value), JsonElementAdapter.ToElement(outcome.Value));
        if (outcome.Kind == OutcomeKind.Halt && outcome.HasValue)
            throw new JqHaltException(Message(outcome.Value), JsonElementAdapter.ToElement(outcome.Value), outcome.Code);
    }

    // The message of an error that reaches the host (spec §10.1).
    static string Message(JqValue value)
        => value.Kind == JqKind.String ? value.AsString() : ValueText.ToJson(value) + " (not a string)";

    // Validates the names (spec §12) and converts the values, once per evaluation.
    static Dictionary<string, JqValue> Convert(IReadOnlyDictionary<string, JsonElement> variables)
    {
        var converted = new Dictionary<string, JqValue>(variables.Count, StringComparer.Ordinal);
        foreach (var (name, value) in variables)
        {
            if (!IsIdentifier(name))
                throw new ArgumentException($"'{name}' is not a valid variable name: give a jq identifier without '$'.", nameof(variables));
            if (name == "ENV")
                throw new ArgumentException("$ENV cannot be a host variable: use JqOptions.Environment.", nameof(variables));
            converted[name] = JsonElementAdapter.ToValue(value);
        }

        return converted;
    }

    static bool IsIdentifier(string? name)
    {
        if (string.IsNullOrEmpty(name) || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
            return false;
        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                return false;
        }

        return true;
    }

    static JqValue ProcessEnvironment()
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            variables[(string)entry.Key] = (string?)entry.Value ?? "";
        return ToObject(variables);
    }

    static JqValue ToObject(IReadOnlyDictionary<string, string> variables)
    {
        var builder = new JqObjectBuilder(variables.Count + 1);
        foreach (var (name, value) in variables)
            builder.Set(CodePoints.Sanitize(name), JqValue.FromString(value));
        return JqValue.FromObject(builder.ToObject());
    }
}
