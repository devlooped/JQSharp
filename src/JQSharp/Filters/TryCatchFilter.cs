using System.Text.Json;

namespace Devlooped;

public sealed class TryCatchFilter : JqFilter
{
    private readonly JqFilter body;
    private readonly JqFilter catchFilter;

    public TryCatchFilter(JqFilter body, JqFilter catchFilter)
    {
        this.body = body;
        this.catchFilter = catchFilter;
    }

    public JqFilter Body => body;

    public JqFilter CatchFilter => catchFilter;

    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        JsonElement[] values;
        try
        {
            values = body.Evaluate(input, env).ToArray();
        }
        catch (JqException ex)
        {
            var errorValue = ex.Value ?? CreateStringElement(ex.Message);
            values = catchFilter.Evaluate(errorValue, env).ToArray();
        }

        foreach (var value in values)
            yield return value;
    }
}

