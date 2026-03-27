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
