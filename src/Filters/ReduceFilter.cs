using System.Text.Json;

namespace Devlooped;

public sealed class ReduceFilter(JqFilter expression, JqPattern pattern, JqFilter init, JqFilter update) : JqFilter
{
    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        foreach (var initial in init.Evaluate(input, env))
        {
            var accumulator = initial;

            foreach (var value in expression.Evaluate(input, env))
            {
                var iterationEnvironment = pattern.Match(value, env, input);
                accumulator = update.Evaluate(accumulator, iterationEnvironment).FirstOrDefault();
            }

            yield return accumulator;
        }
    }
}
