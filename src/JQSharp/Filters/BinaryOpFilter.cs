using System.Text;
using System.Text.Json;

namespace Devlooped;

sealed class BinaryOpFilter : JqFilter
{
    readonly JqFilter left;
    readonly BinaryOp op;
    readonly JqFilter right;

    public BinaryOpFilter(JqFilter left, BinaryOp op, JqFilter right)
    {
        this.left = left;
        this.op = op;
        this.right = right;
    }

    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        foreach (var leftValue in left.Evaluate(input, env))
        {
            foreach (var rightValue in right.Evaluate(input, env))
                yield return EvaluatePair(leftValue, rightValue);
        }
    }

    JsonElement EvaluatePair(JsonElement leftValue, JsonElement rightValue)
    {
        return op switch
        {
            BinaryOp.Add => EvaluateAdd(leftValue, rightValue),
            BinaryOp.Subtract => EvaluateSubtract(leftValue, rightValue),
            BinaryOp.Multiply => EvaluateMultiply(leftValue, rightValue),
            BinaryOp.Divide => EvaluateDivide(leftValue, rightValue),
            BinaryOp.Modulo => EvaluateModulo(leftValue, rightValue),
            BinaryOp.Equal => CreateBooleanElement(StructurallyEqual(leftValue, rightValue)),
            BinaryOp.NotEqual => CreateBooleanElement(!StructurallyEqual(leftValue, rightValue)),
            BinaryOp.LessThan => CreateBooleanElement(CompareElements(leftValue, rightValue) < 0),
            BinaryOp.GreaterThan => CreateBooleanElement(CompareElements(leftValue, rightValue) > 0),
            BinaryOp.LessOrEqual => CreateBooleanElement(CompareElements(leftValue, rightValue) <= 0),
            BinaryOp.GreaterOrEqual => CreateBooleanElement(CompareElements(leftValue, rightValue) >= 0),
            BinaryOp.And => CreateBooleanElement(IsTruthy(leftValue) && IsTruthy(rightValue)),
            BinaryOp.Or => CreateBooleanElement(IsTruthy(leftValue) || IsTruthy(rightValue)),
            _ => throw new InvalidOperationException($"Unsupported operator '{op}'."),
        };
    }

    JsonElement EvaluateAdd(JsonElement leftValue, JsonElement rightValue)
    {
        if (leftValue.ValueKind == JsonValueKind.Null)
            return rightValue;
        if (rightValue.ValueKind == JsonValueKind.Null)
            return leftValue;

        if (leftValue.ValueKind == JsonValueKind.Number && rightValue.ValueKind == JsonValueKind.Number)
            return CreateNumberElement(leftValue.GetDouble() + rightValue.GetDouble());

        if (leftValue.ValueKind == JsonValueKind.String && rightValue.ValueKind == JsonValueKind.String)
            return CreateStringElement((leftValue.GetString() ?? string.Empty) + (rightValue.GetString() ?? string.Empty));

        if (leftValue.ValueKind == JsonValueKind.Array && rightValue.ValueKind == JsonValueKind.Array)
        {
            return CreateElement(writer =>
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

    JsonElement EvaluateSubtract(JsonElement leftValue, JsonElement rightValue)
    {
        if (leftValue.ValueKind == JsonValueKind.Number && rightValue.ValueKind == JsonValueKind.Number)
            return CreateNumberElement(leftValue.GetDouble() - rightValue.GetDouble());

        if (leftValue.ValueKind == JsonValueKind.Array && rightValue.ValueKind == JsonValueKind.Array)
        {
            var rightItems = rightValue.EnumerateArray().ToArray();
            return CreateElement(writer =>
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

    JsonElement EvaluateMultiply(JsonElement leftValue, JsonElement rightValue)
    {
        if (leftValue.ValueKind == JsonValueKind.Number && rightValue.ValueKind == JsonValueKind.Number)
            return CreateNumberElement(leftValue.GetDouble() * rightValue.GetDouble());

        if (leftValue.ValueKind == JsonValueKind.Object && rightValue.ValueKind == JsonValueKind.Object)
            return MergeObjects(leftValue, rightValue, recursive: true);

        if (leftValue.ValueKind == JsonValueKind.Null || rightValue.ValueKind == JsonValueKind.Null)
        {
            var other = leftValue.ValueKind == JsonValueKind.Null ? rightValue : leftValue;
            if (other.ValueKind == JsonValueKind.Number)
                throw CreateTypeError(leftValue, rightValue, "multiplied");

            return CreateNullElement();
        }

        if (leftValue.ValueKind == JsonValueKind.String && rightValue.ValueKind == JsonValueKind.Number)
            return RepeatString(leftValue.GetString() ?? string.Empty, rightValue.GetDouble(), leftValue, rightValue);

        if (leftValue.ValueKind == JsonValueKind.Number && rightValue.ValueKind == JsonValueKind.String)
            return RepeatString(rightValue.GetString() ?? string.Empty, leftValue.GetDouble(), leftValue, rightValue);

        throw CreateTypeError(leftValue, rightValue, "multiplied");
    }

    JsonElement EvaluateDivide(JsonElement leftValue, JsonElement rightValue)
    {
        if (leftValue.ValueKind == JsonValueKind.Number && rightValue.ValueKind == JsonValueKind.Number)
        {
            var divisor = rightValue.GetDouble();
            if (divisor == 0d)
            {
                throw new JqException(
                    $"number ({GetValueText(leftValue)}) and number ({GetValueText(rightValue)}) cannot be divided because the divisor is zero");
            }

            return CreateNumberElement(leftValue.GetDouble() / divisor);
        }

        if (leftValue.ValueKind == JsonValueKind.String && rightValue.ValueKind == JsonValueKind.String)
        {
            var source = leftValue.GetString() ?? string.Empty;
            var separator = rightValue.GetString() ?? string.Empty;
            var split = source.Split(separator, StringSplitOptions.None);
            return CreateElement(writer =>
            {
                writer.WriteStartArray();
                foreach (var item in split)
                    writer.WriteStringValue(item);
                writer.WriteEndArray();
            });
        }

        throw CreateTypeError(leftValue, rightValue, "divided");
    }

    JsonElement EvaluateModulo(JsonElement leftValue, JsonElement rightValue)
    {
        if (leftValue.ValueKind == JsonValueKind.Number && rightValue.ValueKind == JsonValueKind.Number)
        {
            var divisor = rightValue.GetDouble();
            if (divisor == 0d)
            {
                throw new JqException(
                    $"number ({GetValueText(leftValue)}) and number ({GetValueText(rightValue)}) cannot be divided (remainder) because the divisor is zero");
            }

            return CreateNumberElement(leftValue.GetDouble() % divisor);
        }

        throw CreateTypeError(leftValue, rightValue, "divided (remainder)");
    }

    JsonElement RepeatString(string text, double count, JsonElement leftValue, JsonElement rightValue)
    {
        var repeats = Math.Floor(count);
        if (double.IsNaN(repeats) || repeats <= 0d)
            return CreateNullElement();

        if (repeats > int.MaxValue)
            throw CreateTypeError(leftValue, rightValue, "multiplied");

        var builder = new StringBuilder();
        for (var i = 0; i < (int)repeats; i++)
            builder.Append(text);

        return CreateStringElement(builder.ToString());
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

        return CreateElement(writer =>
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
}

