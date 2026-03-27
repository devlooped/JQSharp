using System.Collections.Immutable;
using System.Text.Json;

namespace Devlooped;

sealed class ModuleMetadataFilter(JqFilter inner, ImmutableDictionary<string, JsonElement> metadata) : JqFilter
{
    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
        => inner.Evaluate(input, env.WithModuleMetadata(metadata));
}
