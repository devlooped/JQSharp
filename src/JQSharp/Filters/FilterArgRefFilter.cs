using System.Text.Json;

namespace Devlooped;

sealed class FilterArgRefFilter(string paramName) : JqFilter
{
    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        if (env.TryGetFilter(paramName, out var closure))
            return closure.Filter.Evaluate(input, closure.CapturedEnv);

        throw new JqException($"filter parameter {paramName} is not defined");
    }
}
