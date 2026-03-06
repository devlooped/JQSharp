using System;
using System.Buffers;
using System.Text.Json;

namespace Devlooped;

abstract class JqFilter
{
    public abstract IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env);

    public IEnumerable<JsonElement> Evaluate(JsonElement input) => Evaluate(input, JqEnvironment.Empty);

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

    internal static JsonElement CreateNullElementStatic() => CreateNullElement();

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

    protected static JsonElement CreateMathResult(double value)
    {
        if (double.IsNaN(value))
            return CreateNullElement();
        if (double.IsPositiveInfinity(value))
            return CreateNumberElement(double.MaxValue);
        if (double.IsNegativeInfinity(value))
            return CreateNumberElement(-double.MaxValue);
        return CreateNumberElement(value);
    }

    protected static void RequireNumber(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Number)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not a number");
    }

    protected static void RequireString(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.String)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not a string");
    }

    static readonly DateTimeOffset UnixEpoch = new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

    protected static DateTimeOffset FromUnixTimestamp(double seconds)
    {
        try
        {
            return UnixEpoch.AddSeconds(Math.Truncate(seconds));
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new JqException($"Timestamp {seconds} is out of range");
        }
    }

    protected static double ToUnixTimestamp(DateTimeOffset dt)
        => (dt - UnixEpoch).TotalSeconds;

    protected static JsonElement CreateBrokenDownTime(DateTimeOffset dt)
    {
        var dow = (int)dt.DayOfWeek; // 0=Sunday
        var yday = dt.DayOfYear - 1; // 0-based
        return CreateElement(writer =>
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(dt.Year);
            writer.WriteNumberValue(dt.Month - 1); // 0-based month
            writer.WriteNumberValue(dt.Day);
            writer.WriteNumberValue(dt.Hour);
            writer.WriteNumberValue(dt.Minute);
            writer.WriteNumberValue(dt.Second);
            writer.WriteNumberValue(dow);
            writer.WriteNumberValue(yday);
            writer.WriteEndArray();
        });
    }

    protected static DateTimeOffset ParseBrokenDownTime(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() < 6)
            throw new JqException("Expected array of at least 6 elements for broken-down time");

        var year = input[0].GetInt32();
        var month = input[1].GetInt32() + 1; // convert 0-based to 1-based
        var day = input[2].GetInt32();
        var hour = input[3].GetInt32();
        var minute = input[4].GetInt32();
        var second = input[5].GetInt32();

        try
        {
            return new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new JqException("Invalid broken-down time values");
        }
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

    internal static bool StructurallyEqual(JsonElement a, JsonElement b)
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

    internal static string GetTypeNameStatic(JsonElement element) => GetTypeName(element);

    internal static string GetValueTextStatic(JsonElement element) => GetValueText(element);

    static int CompareArrays(JsonElement leftValue, JsonElement rightValue)
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

    static int CompareObjects(JsonElement leftValue, JsonElement rightValue)
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

    static int GetRank(JsonElement element)
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

    static bool GetBooleanValue(JsonElement element) => element.ValueKind == JsonValueKind.True;
}

