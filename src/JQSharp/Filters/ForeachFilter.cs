using System.Text.Json;

namespace Devlooped;

sealed class ForeachFilter(JqFilter expression, JqPattern pattern, JqFilter init, JqFilter update, JqFilter? extract) : JqFilter
{
    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        foreach (var initResult in init.Evaluate(input, env))
        {
            var accumulator = initResult;

            foreach (var value in expression.Evaluate(input, env))
            {
                var iterEnv = pattern.Match(value, env, input);
                accumulator = update.Evaluate(accumulator, iterEnv).FirstOrDefault();

                if (extract is null)
                {
                    yield return accumulator;
                    continue;
                }

                foreach (var result in extract.Evaluate(accumulator, iterEnv))
                    yield return result;
            }
        }
    }
}
