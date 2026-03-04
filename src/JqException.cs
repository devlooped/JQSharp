using System.Text.Json;

namespace Devlooped;

public sealed class JqException : Exception
{
    public JsonElement? Value { get; }

    public JqException(string message)
        : base(message)
    {
    }

    public JqException(JsonElement value)
        : base(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText())
    {
        Value = value;
    }
}
