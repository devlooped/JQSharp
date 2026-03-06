using System.Text.Json;

namespace Devlooped;

sealed class PipeFilter : JqFilter
{
    readonly JqFilter left;
    readonly JqFilter right;

    public PipeFilter(JqFilter left, JqFilter right)
    {
        this.left = left;
        this.right = right;
    }

    public JqFilter Left => left;

    public JqFilter Right => right;

    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        foreach (var intermediate in left.Evaluate(input, env))
        {
            foreach (var value in right.Evaluate(intermediate, env))
                yield return value;
        }
    }
}
