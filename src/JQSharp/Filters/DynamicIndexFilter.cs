using System.Text.Json;

namespace Devlooped;

sealed class DynamicIndexFilter : JqFilter
{
    readonly JqFilter? source;
    readonly JqFilter indexExpression;

    public DynamicIndexFilter(JqFilter indexExpr)
    {
        indexExpression = indexExpr;
    }

    public JqFilter IndexExpression => indexExpression;

    public JqFilter? Source => source;

    public DynamicIndexFilter(JqFilter source, JqFilter indexExpr) : this(indexExpr)
    {
        this.source = source;
    }

    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        var containers = source is null ? [input] : source.Evaluate(input, env).ToArray();
        var indexValues = indexExpression.Evaluate(input, env).ToArray();

        foreach (var container in containers)
        {
            if (container.ValueKind == JsonValueKind.Null)
            {
                yield return CreateNullElement();
                continue;
            }

            foreach (var idxVal in indexValues)
            {
                if (idxVal.ValueKind == JsonValueKind.Number && container.ValueKind == JsonValueKind.Array)
                {
                    var idx = idxVal.GetInt32();
                    var len = container.GetArrayLength();
                    if (idx < 0)
                        idx = len + idx;

                    if (idx >= 0 && idx < len)
                        yield return container[idx];
                    else
                        yield return CreateNullElement();

                    continue;
                }

                if (idxVal.ValueKind == JsonValueKind.String && container.ValueKind == JsonValueKind.Object)
                {
                    var key = idxVal.GetString()!;
                    if (container.TryGetProperty(key, out var property))
                        yield return property;
                    else
                        yield return CreateNullElement();

                    continue;
                }

                throw new JqException($"Cannot index {GetTypeName(container)} with {GetTypeName(idxVal)}");
            }
        }
    }
}
