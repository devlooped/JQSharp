using System.Text;
using System.Text.Json;

namespace Devlooped;

enum CompoundAssignOp
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Alternative,
}

sealed class CompoundAssignmentFilter(JqFilter pathExpr, CompoundAssignOp op, JqFilter rhsExpr) : JqFilter
{
    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        var rhsValues = rhsExpr.Evaluate(input, env).ToList();
        if (rhsValues.Count == 0)
            yield break;

        var rhsValue = rhsValues[0];
        var result = input;
        foreach (var path in PathResolver.GetPaths(pathExpr, input, env))
        {
            if (!PathResolver.TryGetPathValue(result, path, out var currentValue))
                currentValue = CreateNullElement();

            var newValue = op switch
            {
                CompoundAssignOp.Alternative => IsTruthy(currentValue) ? currentValue : rhsValue,
                _ => ApplyBinaryOp(op, currentValue, rhsValue),
            };
            result = PathResolver.SetPathValue(result, path, newValue);
        }

        yield return result;
    }

    static JsonElement ApplyBinaryOp(CompoundAssignOp op, JsonElement left, JsonElement right)
    {
        return op switch
        {
            CompoundAssignOp.Add => EvaluateAdd(left, right),
            CompoundAssignOp.Subtract => EvaluateSubtract(left, right),
            CompoundAssignOp.Multiply => EvaluateMultiply(left, right),
            CompoundAssignOp.Divide => EvaluateDivide(left, right),
            CompoundAssignOp.Modulo => EvaluateModulo(left, right),
            _ => throw new InvalidOperationException($"Unsupported assignment operator '{op}'."),
        };
    }

    static JsonElement EvaluateAdd(JsonElement leftValue, JsonElement rightValue)
    {
        if (leftValue.ValueKind == JsonValueKind.Null)
            return rightValue;
        if (rightValue.ValueKind == JsonValueKind.Null)
            return leftValue;

        if (leftValue.ValueKind == JsonValueKind.Number && rightValue.ValueKind == JsonValueKind.Number)
            return CreateNumberElementStatic(leftValue.GetDouble() + rightValue.GetDouble());

        if (leftValue.ValueKind == JsonValueKind.String && rightValue.ValueKind == JsonValueKind.String)
            return CreateStringElementStatic((leftValue.GetString() ?? string.Empty) + (rightValue.GetString() ?? string.Empty));

        if (leftValue.ValueKind == JsonValueKind.Array && rightValue.ValueKind == JsonValueKind.Array)
        {
            return CreateElementStatic(writer =>
            {
                writer.WriteStartArray();
                foreach (var value in leftValue.EnumerateArray())
                    value.WriteTo(writer);
                foreach (var value in rightValue.EnumerateArray())
                    value.WriteTo(writer);
                writer.WriteEndArray();
            });
        }

        if (leftValue.ValueKind == JsonValueKind.Object && rightValue.ValueKind == JsonValueKind.Object)
            return MergeObjects(leftValue, rightValue, recursive: false);

        throw CreateTypeError(leftValue, rightValue, "added");
    }

    static JsonElement EvaluateSubtract(JsonElement leftValue, JsonElement rightValue)
    {
        if (leftValue.ValueKind == JsonValueKind.Number && rightValue.ValueKind == JsonValueKind.Number)
            return CreateNumberElementStatic(leftValue.GetDouble() - rightValue.GetDouble());

        if (leftValue.ValueKind == JsonValueKind.Array && rightValue.ValueKind == JsonValueKind.Array)
        {
            var rightItems = rightValue.EnumerateArray().ToArray();
            return CreateElementStatic(writer =>
            {
                writer.WriteStartArray();
                foreach (var item in leftValue.EnumerateArray())
                {
                    if (rightItems.Any(rightItem => StructurallyEqual(item, rightItem)))
                        continue;

                    item.WriteTo(writer);
                }
                writer.WriteEndArray();
            });
        }

        throw CreateTypeError(leftValue, rightValue, "subtracted");
    }

    static JsonElement EvaluateMultiply(JsonElement leftValue, JsonElement rightValue)
    {
        if (leftValue.ValueKind == JsonValueKind.Number && rightValue.ValueKind == JsonValueKind.Number)
            return CreateNumberElementStatic(leftValue.GetDouble() * rightValue.GetDouble());

        if (leftValue.ValueKind == JsonValueKind.Object && rightValue.ValueKind == JsonValueKind.Object)
            return MergeObjects(leftValue, rightValue, recursive: true);

        if (leftValue.ValueKind == JsonValueKind.Null || rightValue.ValueKind == JsonValueKind.Null)
        {
            var other = leftValue.ValueKind == JsonValueKind.Null ? rightValue : leftValue;
            if (other.ValueKind == JsonValueKind.Number)
                throw CreateTypeError(leftValue, rightValue, "multiplied");

            return JqFilter.CreateNullElementStatic();
        }

        if (leftValue.ValueKind == JsonValueKind.String && rightValue.ValueKind == JsonValueKind.Number)
            return RepeatString(leftValue.GetString() ?? string.Empty, rightValue.GetDouble(), leftValue, rightValue);

        if (leftValue.ValueKind == JsonValueKind.Number && rightValue.ValueKind == JsonValueKind.String)
            return RepeatString(rightValue.GetString() ?? string.Empty, leftValue.GetDouble(), leftValue, rightValue);

        throw CreateTypeError(leftValue, rightValue, "multiplied");
    }

    static JsonElement EvaluateDivide(JsonElement leftValue, JsonElement rightValue)
    {
        if (leftValue.ValueKind == JsonValueKind.Number && rightValue.ValueKind == JsonValueKind.Number)
        {
            var divisor = rightValue.GetDouble();
            if (divisor == 0d)
            {
                throw new JqException(
                    $"number ({GetValueText(leftValue)}) and number ({GetValueText(rightValue)}) cannot be divided because the divisor is zero");
            }

            return CreateNumberElementStatic(leftValue.GetDouble() / divisor);
        }

        if (leftValue.ValueKind == JsonValueKind.String && rightValue.ValueKind == JsonValueKind.String)
        {
            var source = leftValue.GetString() ?? string.Empty;
            var separator = rightValue.GetString() ?? string.Empty;
            var split = source.Split(separator, StringSplitOptions.None);
            return CreateElementStatic(writer =>
            {
                writer.WriteStartArray();
                foreach (var item in split)
                    writer.WriteStringValue(item);
                writer.WriteEndArray();
            });
        }

        throw CreateTypeError(leftValue, rightValue, "divided");
    }

    static JsonElement EvaluateModulo(JsonElement leftValue, JsonElement rightValue)
    {
        if (leftValue.ValueKind == JsonValueKind.Number && rightValue.ValueKind == JsonValueKind.Number)
        {
            var divisor = rightValue.GetDouble();
            if (divisor == 0d)
            {
                throw new JqException(
                    $"number ({GetValueText(leftValue)}) and number ({GetValueText(rightValue)}) cannot be divided (remainder) because the divisor is zero");
            }

            return CreateNumberElementStatic(leftValue.GetDouble() % divisor);
        }

        throw CreateTypeError(leftValue, rightValue, "divided (remainder)");
    }

    static JsonElement RepeatString(string text, double count, JsonElement leftValue, JsonElement rightValue)
    {
        var repeats = Math.Floor(count);
        if (double.IsNaN(repeats) || repeats <= 0d)
            return JqFilter.CreateNullElementStatic();

        if (repeats > int.MaxValue)
            throw CreateTypeError(leftValue, rightValue, "multiplied");

        var builder = new StringBuilder();
        for (var i = 0; i < (int)repeats; i++)
            builder.Append(text);

        return CreateStringElementStatic(builder.ToString());
    }

    static JsonElement MergeObjects(JsonElement leftValue, JsonElement rightValue, bool recursive)
    {
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var property in leftValue.EnumerateObject())
            merged[property.Name] = property.Value.Clone();

        foreach (var property in rightValue.EnumerateObject())
        {
            if (recursive &&
                merged.TryGetValue(property.Name, out var existing) &&
                existing.ValueKind == JsonValueKind.Object &&
                property.Value.ValueKind == JsonValueKind.Object)
            {
                merged[property.Name] = MergeObjects(existing, property.Value, recursive: true);
                continue;
            }

            merged[property.Name] = property.Value.Clone();
        }

        return CreateElementStatic(writer =>
        {
            writer.WriteStartObject();
            foreach (var pair in merged)
            {
                writer.WritePropertyName(pair.Key);
                pair.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        });
    }

    static JqException CreateTypeError(JsonElement leftValue, JsonElement rightValue, string verbForOp)
    {
        return new JqException(
            $"{GetTypeName(leftValue)} ({GetValueText(leftValue)}) and {GetTypeName(rightValue)} ({GetValueText(rightValue)}) cannot be {verbForOp}");
    }

    static JsonElement CreateElementStatic(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
            writer.Flush();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    static JsonElement CreateNumberElementStatic(double value)
    {
        if (value >= long.MinValue &&
            value <= long.MaxValue &&
            Math.Floor(value) == value)
        {
            var integer = (long)value;
            return CreateElementStatic(writer => writer.WriteNumberValue(integer));
        }

        return CreateElementStatic(writer => writer.WriteNumberValue(value));
    }

    static JsonElement CreateStringElementStatic(string value) =>
        CreateElementStatic(writer => writer.WriteStringValue(value));
}

