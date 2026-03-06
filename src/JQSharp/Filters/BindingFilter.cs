using System.Text.Json;

namespace Devlooped;

sealed class BindingFilter(JqFilter expression, JqPattern pattern, JqFilter body) : JqFilter
{
    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        foreach (var value in expression.Evaluate(input, env))
        {
            var newEnv = pattern.Match(value, env, input);
            foreach (var result in body.Evaluate(input, newEnv))
                yield return result;
        }
    }
}
