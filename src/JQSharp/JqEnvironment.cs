using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Devlooped;

public sealed class JqEnvironment
{
    public static readonly JqEnvironment Empty = new(
        ImmutableDictionary<string, JsonElement>.Empty.WithComparers(StringComparer.Ordinal),
        ImmutableDictionary<string, FilterClosure>.Empty.WithComparers(StringComparer.Ordinal));

    private readonly ImmutableDictionary<string, JsonElement> bindings;
    private readonly ImmutableDictionary<string, FilterClosure> filterBindings;

    private JqEnvironment(
        ImmutableDictionary<string, JsonElement> bindings,
        ImmutableDictionary<string, FilterClosure> filterBindings)
    {
        this.bindings = bindings;
        this.filterBindings = filterBindings;
    }

    public JqEnvironment Bind(string name, JsonElement value) => new(bindings.SetItem(name, value), filterBindings);

    public JqEnvironment BindFilter(string name, FilterClosure closure) => new(bindings, filterBindings.SetItem(name, closure));

    public JsonElement Get(string name)
    {
        if (bindings.TryGetValue(name, out var value))
            return value;

        throw new JqException($"${name} is not defined");
    }

    public bool TryGet(string name, out JsonElement value) => bindings.TryGetValue(name, out value);

    public bool TryGetFilter(string name, [MaybeNullWhen(false)] out FilterClosure closure) => filterBindings.TryGetValue(name, out closure);
}
