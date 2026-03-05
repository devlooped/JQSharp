using System.Text.Json;

namespace Devlooped;

public sealed class LabelFilter(string labelName, JqFilter body) : JqFilter
{
    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        var results = new List<JsonElement>();
        try
        {
            foreach (var value in body.Evaluate(input, env))
                results.Add(value);
        }
        catch (JqBreakException ex) when (ex.Label == labelName)
        {
            // break matched this label; return collected values
        }

        return results;
    }
}
