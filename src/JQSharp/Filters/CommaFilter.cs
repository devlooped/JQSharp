using System.Text.Json;

namespace Devlooped;

public sealed class CommaFilter : JqFilter
{
    private readonly JqFilter left;
    private readonly JqFilter right;

    public CommaFilter(JqFilter left, JqFilter right)
    {
        this.left = left;
        this.right = right;
    }

    public JqFilter Left => left;

    public JqFilter Right => right;

    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        foreach (var value in left.Evaluate(input, env))
            yield return value;

        foreach (var value in right.Evaluate(input, env))
            yield return value;
    }
}
