using System.Text;
using System.Text.Json;

namespace Devlooped;

public sealed class BinaryOpFilter : JqFilter
{
    private readonly JqFilter left;
    private readonly BinaryOp op;
    private readonly JqFilter right;

    public BinaryOpFilter(JqFilter left, BinaryOp op, JqFilter right)
    {
        this.left = left;
        this.op = op;
        this.right = right;
    }

    public override IEnumerable<JsonElement> Evaluate(JsonElement input)
    {
        foreach (var leftValue in left.Evaluate(input))
        {
            foreach (var rightValue in right.Evaluate(input))
                yield return EvaluatePair(leftValue, rightValue);
        }
    }

    public static bool StructurallyEqual(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
            return false;

        switch (a.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.True:
            case JsonValueKind.False:
                return a.ValueKind == b.ValueKind;
            case JsonValueKind.Number:
                return a.GetDouble().Equals(b.GetDouble());
            case JsonValueKind.String:
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Array:
            {
                var leftItems = a.EnumerateArray().ToArray();
                var rightItems = b.EnumerateArray().ToArray();
                if (leftItems.Length != rightItems.Length)
                    return false;

                for (var i = 0; i < leftItems.Length; i++)
                {
                    if (!StructurallyEqual(leftItems[i], rightItems[i]))
                        return false;
                }

                return true;
            }
            case JsonValueKind.Object:
            {
                var leftProperties = a.EnumerateObject().ToArray();
                var rightProperties = b.EnumerateObject().ToArray();
                if (leftProperties.Length != rightProperties.Length)
                    return false;

                var leftSorted = leftProperties.OrderBy(static property => property.Name, StringComparer.Ordinal).ToArray();
                var rightSorted = rightProperties.OrderBy(static property => property.Name, StringComparer.Ordinal).ToArray();

                for (var i = 0; i < leftSorted.Length; i++)
                {
                    if (!string.Equals(leftSorted[i].Name, rightSorted[i].Name, StringComparison.Ordinal))
                        return false;
                    if (!StructurallyEqual(leftSorted[i].Value, rightSorted[i].Value))
                        return false;
                }

                return true;
            }
            default:
                return false;
        }
    }

    private JsonElement EvaluatePair(JsonElement leftValue, JsonElement rightValue)
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

    private JsonElement EvaluateAdd(JsonElement leftValue, JsonElement rightValue)
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

    private JsonElement EvaluateSubtract(JsonElement leftValue, JsonElement rightValue)
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

    private JsonElement EvaluateMultiply(JsonElement leftValue, JsonElement rightValue)
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

    private JsonElement EvaluateDivide(JsonElement leftValue, JsonElement rightValue)
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

    private JsonElement EvaluateModulo(JsonElement leftValue, JsonElement rightValue)
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

    private JsonElement RepeatString(string text, double count, JsonElement leftValue, JsonElement rightValue)
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

    private static int CompareElements(JsonElement leftValue, JsonElement rightValue)
    {
        var leftRank = GetRank(leftValue);
        var rightRank = GetRank(rightValue);
        if (leftRank != rightRank)
            return leftRank.CompareTo(rightRank);

        switch (leftValue.ValueKind)
        {
            case JsonValueKind.Null:
                return 0;
            case JsonValueKind.False:
            case JsonValueKind.True:
                return GetBooleanValue(leftValue).CompareTo(GetBooleanValue(rightValue));
            case JsonValueKind.Number:
                return leftValue.GetDouble().CompareTo(rightValue.GetDouble());
            case JsonValueKind.String:
                return string.Compare(leftValue.GetString(), rightValue.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Array:
                return CompareArrays(leftValue, rightValue);
            case JsonValueKind.Object:
                return CompareObjects(leftValue, rightValue);
            default:
                return 0;
        }
    }

    private static int CompareArrays(JsonElement leftValue, JsonElement rightValue)
    {
        var leftItems = leftValue.EnumerateArray().ToArray();
        var rightItems = rightValue.EnumerateArray().ToArray();
        var min = Math.Min(leftItems.Length, rightItems.Length);
        for (var i = 0; i < min; i++)
        {
            var compared = CompareElements(leftItems[i], rightItems[i]);
            if (compared != 0)
                return compared;
        }

        return leftItems.Length.CompareTo(rightItems.Length);
    }

    private static int CompareObjects(JsonElement leftValue, JsonElement rightValue)
    {
        var leftSorted = leftValue.EnumerateObject()
            .OrderBy(static property => property.Name, StringComparer.Ordinal)
            .ToArray();
        var rightSorted = rightValue.EnumerateObject()
            .OrderBy(static property => property.Name, StringComparer.Ordinal)
            .ToArray();
        var min = Math.Min(leftSorted.Length, rightSorted.Length);
        for (var i = 0; i < min; i++)
        {
            var keyCompare = string.Compare(leftSorted[i].Name, rightSorted[i].Name, StringComparison.Ordinal);
            if (keyCompare != 0)
                return keyCompare;

            var valueCompare = CompareElements(leftSorted[i].Value, rightSorted[i].Value);
            if (valueCompare != 0)
                return valueCompare;
        }

        return leftSorted.Length.CompareTo(rightSorted.Length);
    }

    private static int GetRank(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => 0,
            JsonValueKind.False => 1,
            JsonValueKind.True => 2,
            JsonValueKind.Number => 3,
            JsonValueKind.String => 4,
            JsonValueKind.Array => 5,
            JsonValueKind.Object => 6,
            _ => 7,
        };
    }

    private static bool GetBooleanValue(JsonElement element) => element.ValueKind == JsonValueKind.True;

    private static bool IsTruthy(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null => false,
            JsonValueKind.False => false,
            _ => true,
        };
    }

    private static JsonElement MergeObjects(JsonElement leftValue, JsonElement rightValue, bool recursive)
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

    private static JsonElement CreateBooleanElement(bool value)
    {
        return CreateElement(writer => writer.WriteBooleanValue(value));
    }

    private static JsonElement CreateNumberElement(double value)
    {
        if (value >= long.MinValue &&
            value <= long.MaxValue &&
            Math.Floor(value) == value)
        {
            var integer = (long)value;
            return CreateElement(writer => writer.WriteNumberValue(integer));
        }

        return CreateElement(writer => writer.WriteNumberValue(value));
    }

    private static JqException CreateTypeError(JsonElement leftValue, JsonElement rightValue, string verbForOp)
    {
        return new JqException(
            $"{GetTypeName(leftValue)} ({GetValueText(leftValue)}) and {GetTypeName(rightValue)} ({GetValueText(rightValue)}) cannot be {verbForOp}");
    }
}
