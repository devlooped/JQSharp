using System.Text.Json;

namespace Devlooped;

public abstract class JqPattern
{
    public abstract JqEnvironment Match(JsonElement value, JqEnvironment env, JsonElement input);

    public virtual bool TryMatch(JsonElement value, JqEnvironment env, JsonElement input, out JqEnvironment matchedEnv)
    {
        try
        {
            matchedEnv = Match(value, env, input);
            return true;
        }
        catch (JqException)
        {
            matchedEnv = env;
            return false;
        }
    }

    public abstract IEnumerable<string> VariableNames { get; }
}

public sealed class VariablePattern(string name) : JqPattern
{
    public string Name { get; } = name;

    public override IEnumerable<string> VariableNames => [Name];

    public override JqEnvironment Match(JsonElement value, JqEnvironment env, JsonElement input) => env.Bind(Name, value);
}

public sealed class ArrayPattern(JqPattern[] elements) : JqPattern
{
    public JqPattern[] Elements { get; } = elements;

    public override IEnumerable<string> VariableNames => Elements.SelectMany(static element => element.VariableNames);

    public override bool TryMatch(JsonElement value, JqEnvironment env, JsonElement input, out JqEnvironment matchedEnv)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            matchedEnv = env;
            return false;
        }

        return base.TryMatch(value, env, input, out matchedEnv);
    }

    public override JqEnvironment Match(JsonElement value, JqEnvironment env, JsonElement input)
    {
        for (var i = 0; i < Elements.Length; i++)
        {
            JsonElement elementValue;
            if (value.ValueKind == JsonValueKind.Array && i < value.GetArrayLength())
                elementValue = value[i];
            else
                elementValue = JqFilter.CreateNullElementStatic();

            env = Elements[i].Match(elementValue, env, input);
        }

        return env;
    }
}

public sealed class ObjectPattern(IReadOnlyList<(JqFilter KeyExpr, JqPattern ValuePattern)> entries) : JqPattern
{
    public IReadOnlyList<(JqFilter KeyExpr, JqPattern ValuePattern)> Entries { get; } = entries;

    public override IEnumerable<string> VariableNames => Entries.SelectMany(static entry => entry.ValuePattern.VariableNames);

    public override bool TryMatch(JsonElement value, JqEnvironment env, JsonElement input, out JqEnvironment matchedEnv)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            matchedEnv = env;
            return false;
        }

        return base.TryMatch(value, env, input, out matchedEnv);
    }

    public override JqEnvironment Match(JsonElement value, JqEnvironment env, JsonElement input)
    {
        foreach (var (keyExpr, valuePattern) in Entries)
        {
            var keyResult = keyExpr.Evaluate(input, env).FirstOrDefault();
            if (keyResult.ValueKind != JsonValueKind.String)
            {
                throw new JqException(
                    $"Cannot use {JqFilter.GetTypeNameStatic(keyResult)} ({JqFilter.GetValueTextStatic(keyResult)}) as object key");
            }

            var key = keyResult.GetString()!;
            JsonElement fieldValue;
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property))
                fieldValue = property;
            else
                fieldValue = JqFilter.CreateNullElementStatic();

            env = valuePattern.Match(fieldValue, env, input);
        }

        return env;
    }
}
