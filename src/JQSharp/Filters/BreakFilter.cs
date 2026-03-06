using System.Text.Json;

namespace Devlooped;

public sealed class BreakFilter(string labelName) : JqFilter
{
    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        throw new JqBreakException(labelName);
    }
}
