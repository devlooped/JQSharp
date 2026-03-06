using System.Text.Json;

namespace Devlooped;

sealed class DestructuringAlternativeFilter(JqFilter expression, JqPattern[] patterns, JqFilter body) : JqFilter
{
    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        foreach (var value in expression.Evaluate(input, env))
        {
            JqEnvironment? newEnv = null;
            var matchedPatternIndex = -1;

            for (var i = 0; i < patterns.Length; i++)
            {
                if (!patterns[i].TryMatch(value, env, input, out var matchedEnv))
                    continue;

                newEnv = matchedEnv;
                matchedPatternIndex = i;
                break;
            }

            if (newEnv is null)
                throw new JqException("Pattern did not match value in destructuring alternative.");

            var selectedVariables = new HashSet<string>(patterns[matchedPatternIndex].VariableNames, StringComparer.Ordinal);
            foreach (var pattern in patterns)
            {
                if (ReferenceEquals(pattern, patterns[matchedPatternIndex]))
                    continue;

                foreach (var name in pattern.VariableNames)
                {
                    if (selectedVariables.Contains(name))
                        continue;

                    newEnv = newEnv.Bind(name, JqFilter.CreateNullElementStatic());
                }
            }

            foreach (var result in body.Evaluate(input, newEnv))
                yield return result;
        }
    }
}
