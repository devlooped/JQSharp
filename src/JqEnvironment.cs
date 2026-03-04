using System.Collections.Immutable;
using System.Text.Json;

namespace Devlooped;

public sealed class JqEnvironment
{
    public static readonly JqEnvironment Empty = new(ImmutableDictionary<string, JsonElement>.Empty.WithComparers(StringComparer.Ordinal));

    private readonly ImmutableDictionary<string, JsonElement> bindings;

    private JqEnvironment(ImmutableDictionary<string, JsonElement> bindings) => this.bindings = bindings;

    public JqEnvironment Bind(string name, JsonElement value) => new(bindings.SetItem(name, value));

    public JsonElement Get(string name)
    {
        if (bindings.TryGetValue(name, out var value))
            return value;

        throw new JqException($"${name} is not defined");
    }

    public bool TryGet(string name, out JsonElement value) => bindings.TryGetValue(name, out value);
}
