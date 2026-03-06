using System.Text.Json;

namespace Devlooped;

public sealed class ObjectFilter : JqFilter
{
    private readonly (JqFilter Key, JqFilter Value)[] pairs;

    public ObjectFilter((JqFilter Key, JqFilter Value)[] pairs)
    {
        this.pairs = pairs;
    }

    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        var result = CreateElement(writer =>
        {
            writer.WriteStartObject();
            foreach (var pair in pairs)
            {
                var keyResults = pair.Key.Evaluate(input, env).ToArray();
                if (keyResults.Length == 0)
                    throw new JqException("Object key expression produced no results");

                var key = keyResults[0];
                if (key.ValueKind != JsonValueKind.String)
                    throw new JqException("Object key expression must evaluate to a string");

                writer.WritePropertyName(key.GetString()!);

                var valueResults = pair.Value.Evaluate(input, env).ToArray();
                if (valueResults.Length == 0)
                    writer.WriteNullValue();
                else
                    valueResults[0].WriteTo(writer);
            }

            writer.WriteEndObject();
        });

        yield return result;
    }
}
