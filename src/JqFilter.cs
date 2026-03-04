using System.Buffers;
using System.Text.Json;

namespace Devlooped;

public abstract class JqFilter
{
    public abstract IEnumerable<JsonElement> Evaluate(JsonElement input);

    protected static JsonElement CreateElement(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
            writer.Flush();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    protected static JsonElement CreateNullElement() => CreateElement(static writer => writer.WriteNullValue());

    protected static JsonElement CreateStringElement(string value) => CreateElement(writer => writer.WriteStringValue(value));

    protected static JsonElement CreateNumberElement(double value)
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

    protected static JsonElement CreateBooleanElement(bool value)
    {
        return CreateElement(writer => writer.WriteBooleanValue(value));
    }

    protected static bool IsTruthy(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null => false,
            JsonValueKind.False => false,
            _ => true,
        };
    }

    protected internal static int CompareElements(JsonElement left, JsonElement right)
    {
        var leftRank = GetRank(left);
        var rightRank = GetRank(right);
        if (leftRank != rightRank)
            return leftRank.CompareTo(rightRank);

        return left.ValueKind switch
        {
            JsonValueKind.Null => 0,
            JsonValueKind.False or JsonValueKind.True => GetBooleanValue(left).CompareTo(GetBooleanValue(right)),
            JsonValueKind.Number => left.GetDouble().CompareTo(right.GetDouble()),
            JsonValueKind.String => string.Compare(left.GetString(), right.GetString(), StringComparison.Ordinal),
            JsonValueKind.Array => CompareArrays(left, right),
            JsonValueKind.Object => CompareObjects(left, right),
            _ => 0,
        };
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

    protected static string GetTypeName(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            _ => "unknown",
        };
    }

    protected static string GetValueText(JsonElement element) => element.GetRawText();

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
}
