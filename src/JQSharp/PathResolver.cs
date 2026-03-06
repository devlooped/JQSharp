using System.Buffers;
using System.Text.Json;

namespace Devlooped;

internal static class PathResolver
{
    public static IEnumerable<JsonElement[]> GetPaths(JqFilter filter, JsonElement input, JqEnvironment env)
    {
        switch (filter)
        {
            case IdentityFilter:
                yield return [];
                break;

            case FieldFilter field:
                yield return [CreateStringElement(field.FieldName)];
                break;

            case IndexFilter index:
                yield return [CreateNumberElement(index.Index)];
                break;

            case IterateFilter:
                if (input.ValueKind == JsonValueKind.Array)
                {
                    for (var i = 0; i < input.GetArrayLength(); i++)
                        yield return [CreateNumberElement(i)];
                }
                else if (input.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in input.EnumerateObject())
                        yield return [CreateStringElement(property.Name)];
                }
                break;

            case PipeFilter pipe:
                foreach (var left in GetPaths(pipe.Left, input, env))
                {
                    var baseValue = TryGetPathValue(input, left, out var value)
                        ? value
                        : CreateNullElement();
                    foreach (var right in GetPaths(pipe.Right, baseValue, env))
                        yield return left.Concat(right).ToArray();
                }
                break;

            case CommaFilter comma:
                foreach (var left in GetPaths(comma.Left, input, env))
                    yield return left;
                foreach (var right in GetPaths(comma.Right, input, env))
                    yield return right;
                break;

            case ParameterizedFilter { FilterName: "select", FilterArgs.Length: 1 } select:
                if (select.FilterArgs[0].Evaluate(input, env).Any(IsTruthy))
                    yield return [];
                break;

            case TryCatchFilter tryCatch:
                IEnumerable<JsonElement[]> tryPaths;
                try
                {
                    tryPaths = GetPaths(tryCatch.Body, input, env).ToArray();
                }
                catch (JqException)
                {
                    tryPaths = GetPaths(tryCatch.CatchFilter, input, env).ToArray();
                }

                foreach (var path in tryPaths)
                    yield return path;
                break;

            default:
                foreach (var path in filter.Evaluate(input, env))
                    yield return ParsePath(path);
                break;
        }
    }

    public static JsonElement[] ParsePath(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new JqException($"{JqFilter.GetTypeNameStatic(value)} ({JqFilter.GetValueTextStatic(value)}) is not a path array");

        var path = value.EnumerateArray().ToArray();
        foreach (var item in path)
        {
            if (item.ValueKind is not JsonValueKind.String and not JsonValueKind.Number)
                throw new JqException("Path entries must be strings or numbers");
            if (item.ValueKind == JsonValueKind.Number && !IsInteger(item.GetDouble()))
                throw new JqException("Path entries must be integer numbers");
        }

        return path;
    }

    public static JsonElement CreatePathValue(JsonElement[] path)
    {
        return CreateElement(writer =>
        {
            writer.WriteStartArray();
            foreach (var part in path)
                part.WriteTo(writer);
            writer.WriteEndArray();
        });
    }

    public static bool TryGetPathValue(JsonElement source, JsonElement[] path, out JsonElement value)
    {
        value = source;
        foreach (var part in path)
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                if (value.ValueKind != JsonValueKind.Object)
                    return false;
                if (!value.TryGetProperty(part.GetString() ?? "", out var property))
                    return false;
                value = property;
            }
            else if (part.ValueKind == JsonValueKind.Number)
            {
                if (value.ValueKind != JsonValueKind.Array)
                    return false;
                if (!TryReadIndex(part, value.GetArrayLength(), out var index))
                    return false;
                if (index < 0 || index >= value.GetArrayLength())
                    return false;
                value = value[index];
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    public static JsonElement SetPathValue(JsonElement source, JsonElement[] path, JsonElement value)
    {
        if (path.Length == 0)
            return value;

        return SetPathValueCore(source, path, value, 0);
    }

    private static JsonElement SetPathValueCore(JsonElement source, JsonElement[] path, JsonElement value, int depth)
    {
        if (depth == path.Length)
            return value;

        var part = path[depth];
        if (part.ValueKind == JsonValueKind.String)
        {
            var key = part.GetString() ?? "";
            Dictionary<string, JsonElement> objectValue;
            if (source.ValueKind == JsonValueKind.Object)
                objectValue = source.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            else if (source.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                objectValue = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            else
                throw new JqException($"{JqFilter.GetTypeNameStatic(source)} ({JqFilter.GetValueTextStatic(source)}) cannot be indexed with string \"{key}\"");

            var next = objectValue.TryGetValue(key, out var current)
                ? current
                : CreateNullElement();
            objectValue[key] = SetPathValueCore(next, path, value, depth + 1);

            return CreateElement(writer =>
            {
                writer.WriteStartObject();
                foreach (var property in objectValue)
                {
                    writer.WritePropertyName(property.Key);
                    property.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            });
        }

        if (part.ValueKind == JsonValueKind.Number)
        {
            if (!TryReadIndex(part, source.ValueKind == JsonValueKind.Array ? source.GetArrayLength() : 0, out var index))
                throw new JqException("Path index must be an integer");
            if (index < 0)
                throw new JqException("Out of bounds negative array index");

            List<JsonElement> arrayValue;
            if (source.ValueKind == JsonValueKind.Array)
                arrayValue = source.EnumerateArray().ToList();
            else if (source.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                arrayValue = [];
            else
                throw new JqException($"{JqFilter.GetTypeNameStatic(source)} ({JqFilter.GetValueTextStatic(source)}) cannot be indexed with number {index}");

            while (arrayValue.Count <= index)
                arrayValue.Add(CreateNullElement());
            arrayValue[index] = SetPathValueCore(arrayValue[index], path, value, depth + 1);

            return CreateElement(writer =>
            {
                writer.WriteStartArray();
                foreach (var item in arrayValue)
                    item.WriteTo(writer);
                writer.WriteEndArray();
            });
        }

        throw new JqException("Path entries must be strings or numbers");
    }

    public static JsonElement DeletePathValue(JsonElement source, JsonElement[] path)
    {
        if (path.Length == 0)
            return CreateNullElement();

        return DeletePathValueCore(source, path, 0);
    }

    private static JsonElement DeletePathValueCore(JsonElement source, JsonElement[] path, int depth)
    {
        var part = path[depth];
        var leaf = depth == path.Length - 1;

        if (part.ValueKind == JsonValueKind.String)
        {
            if (source.ValueKind != JsonValueKind.Object)
                return source;

            var key = part.GetString() ?? "";
            var members = new List<(string Name, JsonElement Value)>();
            foreach (var property in source.EnumerateObject())
            {
                if (property.Name != key)
                {
                    members.Add((property.Name, property.Value));
                    continue;
                }

                if (leaf)
                    continue;
                members.Add((property.Name, DeletePathValueCore(property.Value, path, depth + 1)));
            }

            return CreateElement(writer =>
            {
                writer.WriteStartObject();
                foreach (var member in members)
                {
                    writer.WritePropertyName(member.Name);
                    member.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            });
        }

        if (part.ValueKind == JsonValueKind.Number)
        {
            if (source.ValueKind != JsonValueKind.Array)
                return source;
            if (!TryReadIndex(part, source.GetArrayLength(), out var index))
                return source;
            if (index < 0 || index >= source.GetArrayLength())
                return source;

            var values = source.EnumerateArray().ToList();
            if (leaf)
                values.RemoveAt(index);
            else
                values[index] = DeletePathValueCore(values[index], path, depth + 1);

            return CreateElement(writer =>
            {
                writer.WriteStartArray();
                foreach (var value in values)
                    value.WriteTo(writer);
                writer.WriteEndArray();
            });
        }

        return source;
    }

    private static bool TryReadIndex(JsonElement value, int length, out int index)
    {
        index = 0;
        if (value.ValueKind != JsonValueKind.Number)
            return false;

        var number = value.GetDouble();
        if (!IsInteger(number))
            return false;

        index = (int)number;
        if (index < 0)
            index += length;

        return true;
    }

    private static bool IsInteger(double number) =>
        !double.IsNaN(number) &&
        !double.IsInfinity(number) &&
        Math.Floor(number) == number;

    private static JsonElement CreateElement(Action<Utf8JsonWriter> write)
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

    private static JsonElement CreateNullElement() => CreateElement(static writer => writer.WriteNullValue());

    private static JsonElement CreateStringElement(string value) => CreateElement(writer => writer.WriteStringValue(value));

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

    private static bool IsTruthy(JsonElement value) =>
        value.ValueKind != JsonValueKind.Null &&
        value.ValueKind != JsonValueKind.False;
}

