using System.Text.Json;

namespace Devlooped;

public sealed class PipeFilter : JqFilter
{
    private readonly JqFilter left;
    private readonly JqFilter right;

    public PipeFilter(JqFilter left, JqFilter right)
    {
        this.left = left;
        this.right = right;
    }

    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        foreach (var intermediate in left.Evaluate(input, env))
        {
            foreach (var value in right.Evaluate(intermediate, env))
                yield return value;
        }
    }
}
