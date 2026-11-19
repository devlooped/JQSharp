using JQLite.Core.Values;
using Xunit.Sdk;

namespace JQLite.Tests.Core.Values;

static class ValueHelpers
{
    /// <summary>Reads JSON text; fails the test when it is not valid.</summary>
    public static JqValue V(string json)
        => ValueText.TryParse(json, out var value, out var error) ? value : throw new XunitException($"Bad test JSON {json}: {error.AsString()}");

    /// <summary>The canonical JSON text of a value.</summary>
    public static string J(JqValue value) => ValueText.ToJson(value);
}
