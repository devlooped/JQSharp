using Devlooped.Core.Values;

namespace Devlooped.Core.Builtins;

/// <summary>The <c>@name</c> formats of §9.9.</summary>
static class Formats
{
    /// <summary>Applies the format <paramref name="name"/> (without '@') to <paramref name="value"/>, giving a string.</summary>
    public static bool TryApply(string name, JqValue value, out JqValue result, out JqValue error)
    {
        switch (name)
        {
            case "text":
                return NativeTable.Ok(JqValue.FromString(StringNatives.ToText(value)), out result, out error);
            case "json":
                return NativeTable.Ok(JqValue.FromString(ValueText.ToJson(value)), out result, out error);
            case "html" or "uri" or "urid" or "csv" or "tsv" or "sh" or "base64" or "base64d":
                return NativeTable.Fail(JqValue.FromString($"@{name} is not implemented"), out result, out error);
            default:
                return NativeTable.Fail(Errors.InvalidFormat(name), out result, out error);
        }
    }
}
