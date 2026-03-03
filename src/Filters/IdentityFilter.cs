using System.Text.Json;

namespace Devlooped;

public sealed class IdentityFilter : JqFilter
{
    public override IEnumerable<JsonElement> Evaluate(JsonElement input)
    {
        yield return input;
    }
}