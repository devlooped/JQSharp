using System.Text.Json;

namespace Devlooped;

/// <summary>
/// Represents errors that occur during jq filter parsing or evaluation.
/// </summary>
public sealed class JqException : Exception
{
    /// <summary>
    /// Gets the JSON value passed to the jq <c>error</c> builtin, if any.
    /// </summary>
    /// <value>
    /// The <see cref="JsonElement"/> that caused the error when using the jq <c>error</c> builtin,
    /// or <see langword="null"/> when the exception was raised from a plain error message.
    /// </value>
    public JsonElement? Value { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="JqException"/> with the specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public JqException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="JqException"/> from a JSON value produced
    /// by the jq <c>error</c> builtin.
    /// </summary>
    /// <param name="value">
    /// The <see cref="JsonElement"/> passed to <c>error</c>. String values are used directly
    /// as the exception message; all other values use their raw JSON text.
    /// </param>
    public JqException(JsonElement value)
        : base(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText())
    {
        Value = value;
    }
}
