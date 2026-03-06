using System.Text.Json;

namespace Devlooped;

sealed class PlainAssignmentFilter(JqFilter pathExpr, JqFilter valueExpr) : JqFilter
{
    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        foreach (var value in valueExpr.Evaluate(input, env))
        {
            var result = input;
            foreach (var path in PathResolver.GetPaths(pathExpr, input, env))
                result = PathResolver.SetPathValue(result, path, value);
            yield return result;
        }
    }
}

