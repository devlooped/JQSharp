using System.Text.Json;

namespace Devlooped;

public sealed class AlternativeFilter : JqFilter
{
    readonly JqFilter left;
    readonly JqFilter right;

    public AlternativeFilter(JqFilter left, JqFilter right)
    {
        this.left = left;
        this.right = right;
    }

    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        var anyTruthy = false;

        foreach (var value in left.Evaluate(input, env))
        {
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.False)
                continue;

            anyTruthy = true;
            yield return value;
        }

        if (anyTruthy)
            yield break;

        foreach (var value in right.Evaluate(input, env))
            yield return value;
    }
}

