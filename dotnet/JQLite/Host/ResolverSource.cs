using JQLite.Core.Binding;
using JQLite.Core.Values;
using JQLite.Json;

namespace JQLite.Host;

/// <summary>A public <see cref="JqResolver"/> as the module source of the core (spec §9.11, §12).</summary>
sealed class ResolverSource(JqResolver resolver) : IModuleSource
{
    public bool TryResolve(string path, ModuleKind kind, JqValue? metadata, string? from, out string identity, out string error)
    {
        identity = "";
        error = "";
        try
        {
            var options = metadata is { } value ? JsonElementAdapter.ToElement(value) : default(System.Text.Json.JsonElement?);
            identity = resolver.GetCanonicalPath(path, kind == ModuleKind.Data ? JqModuleKind.Data : JqModuleKind.Code, options, from) ?? "";
        }
        catch (Exception e) when (e is JqException or IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return false;
        }

        if (identity.Length == 0)
            error = BindingMessages.ModuleNotFound(path);
        return identity.Length > 0;
    }

    public bool TryRead(string identity, out string text, out string error)
    {
        text = "";
        error = "";
        try
        {
            using var reader = resolver.Open(identity);
            text = reader.ReadToEnd();
            return true;
        }
        catch (Exception e) when (e is JqException or IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return false;
        }
    }
}
