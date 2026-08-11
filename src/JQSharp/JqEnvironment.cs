using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Devlooped;

sealed class JqEnvironment
{
    public static readonly JqEnvironment Empty = new(
        ImmutableDictionary<string, JsonElement>.Empty.WithComparers(StringComparer.Ordinal),
        ImmutableDictionary<string, FilterClosure>.Empty.WithComparers(StringComparer.Ordinal),
        ImmutableDictionary<string, JsonElement>.Empty.WithComparers(StringComparer.Ordinal));

    readonly ImmutableDictionary<string, JsonElement> bindings;
    readonly ImmutableDictionary<string, FilterClosure> filterBindings;
    readonly ImmutableDictionary<string, JsonElement> moduleMetadata;

    JqEnvironment(
        ImmutableDictionary<string, JsonElement> bindings,
        ImmutableDictionary<string, FilterClosure> filterBindings,
        ImmutableDictionary<string, JsonElement> moduleMetadata)
    {
        this.bindings = bindings;
        this.filterBindings = filterBindings;
        this.moduleMetadata = moduleMetadata;
    }

    /// <summary>
    /// Creates an environment pre-bound with the given external variables.
    /// Keys must be valid jq identifiers and must not include a leading <c>$</c>.
    /// </summary>
    public static JqEnvironment FromVariables(IReadOnlyDictionary<string, JsonElement>? variables)
    {
        if (variables is null || variables.Count == 0)
            return Empty;

        var env = Empty;
        foreach (var pair in variables)
        {
            ValidateVariableName(pair.Key);
            env = env.Bind(pair.Key, pair.Value.Clone());
        }

        return env;
    }

    internal static void ValidateVariableName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (name.Length == 0)
            throw new ArgumentException("Variable name cannot be empty.");

        if (name[0] == '$')
            throw new ArgumentException($"Variable name '{name}' must not start with '$'.");

        if (!IsValidVariableName(name))
            throw new ArgumentException($"Variable name '{name}' is not a valid jq identifier.");
    }

    internal static bool IsValidVariableName(string name)
    {
        if (name.Length == 0)
            return false;

        if (!IsIdentifierStart(name[0]))
            return false;

        for (var i = 1; i < name.Length; i++)
        {
            if (!IsIdentifierPart(name[i]))
                return false;
        }

        return true;
    }

    static bool IsIdentifierStart(char ch) => ch == '_' || char.IsLetter(ch);

    static bool IsIdentifierPart(char ch) => IsIdentifierStart(ch) || char.IsDigit(ch);

    public JqEnvironment Bind(string name, JsonElement value) => new(bindings.SetItem(name, value), filterBindings, moduleMetadata);

    public JqEnvironment BindFilter(string name, FilterClosure closure) => new(bindings, filterBindings.SetItem(name, closure), moduleMetadata);

    public JqEnvironment WithModuleMetadata(ImmutableDictionary<string, JsonElement> metadata) => new(bindings, filterBindings, metadata);

    public JsonElement Get(string name)
    {
        if (bindings.TryGetValue(name, out var value))
            return value;

        throw new JqException($"${name} is not defined");
    }

    public bool TryGet(string name, out JsonElement value) => bindings.TryGetValue(name, out value);

    public bool TryGetFilter(string name, [MaybeNullWhen(false)] out FilterClosure closure) => filterBindings.TryGetValue(name, out closure);

    public bool TryGetModuleMetadata(string name, out JsonElement metadata) => moduleMetadata.TryGetValue(name, out metadata);
}
