using System.Text.Json;

namespace Devlooped;

sealed class ConditionalFilter : JqFilter
{
    readonly JqFilter condition;
    readonly JqFilter thenBranch;
    readonly JqFilter elseBranch;

    public ConditionalFilter(JqFilter condition, JqFilter thenBranch, JqFilter elseBranch)
    {
        this.condition = condition;
        this.thenBranch = thenBranch;
        this.elseBranch = elseBranch;
    }

    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        foreach (var conditionResult in condition.Evaluate(input, env))
        {
            var branch = IsTruthy(conditionResult) ? thenBranch : elseBranch;
            foreach (var value in branch.Evaluate(input, env))
                yield return value;
        }
    }
}

