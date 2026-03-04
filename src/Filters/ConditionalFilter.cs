using System.Text.Json;

namespace Devlooped;

public sealed class ConditionalFilter : JqFilter
{
    private readonly JqFilter condition;
    private readonly JqFilter thenBranch;
    private readonly JqFilter elseBranch;

    public ConditionalFilter(JqFilter condition, JqFilter thenBranch, JqFilter elseBranch)
    {
        this.condition = condition;
        this.thenBranch = thenBranch;
        this.elseBranch = elseBranch;
    }

    public override IEnumerable<JsonElement> Evaluate(JsonElement input)
    {
        foreach (var conditionResult in condition.Evaluate(input))
        {
            var branch = IsTruthy(conditionResult) ? thenBranch : elseBranch;
            foreach (var value in branch.Evaluate(input))
                yield return value;
        }
    }
}
