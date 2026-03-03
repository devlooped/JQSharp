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
}