using System.Collections;
using System.Collections.Generic;
using System.Text.Json;

namespace Devlooped;

public sealed class VariableFilter(string name) : JqFilter
{
    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        if (name == "ENV")
        {
            yield return BuildEnvObject();
            yield break;
        }
        yield return env.Get(name);
    }

    private static JsonElement BuildEnvObject()
    {
        var envVars = Environment.GetEnvironmentVariables();
        return CreateElement(writer =>
        {
            writer.WriteStartObject();
            foreach (DictionaryEntry entry in envVars)
            {
                if (entry.Key is string key && entry.Value is string value)
                    writer.WriteString(key, value);
            }

            writer.WriteEndObject();
        });
    }
}
