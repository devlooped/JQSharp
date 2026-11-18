using System.Reflection;
using System.Text.Json;

namespace Devlooped;

/// <summary>
/// A <see cref="JqResolver"/> over the embedded resources of an assembly. A resource name is the
/// <see cref="Prefix"/> and the path with <c>/</c> and <c>\</c> turned into dots, plus <c>.jq</c> (code)
/// or <c>.json</c> (data) unless the path ends with one; a directive in a module is relative to that
/// module's name without its last segment. Safe to use from several threads.
/// </summary>
/// <remarks>With the prefix <c>MyApp.Filters</c>, <c>include "utils"</c> is <c>MyApp.Filters.utils.jq</c>, and <c>include "sub/helper"</c> in it is <c>MyApp.Filters.sub.helper.jq</c>.</remarks>
/// <param name="assembly">The assembly whose embedded resources hold the modules.</param>
/// <param name="prefix">The dot-separated prefix of the resource names (often the assembly's default namespace), without a final dot; may be empty.</param>
public class JqResourceResolver(Assembly assembly, string? prefix = default) : JqResolver
{
    readonly Assembly assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));

    /// <summary>The dot-separated prefix of the resource names; may be empty.</summary>
    public string Prefix { get; } = prefix ?? string.Empty;

    /// <inheritdoc/>
    public override string? GetCanonicalPath(string path, JqModuleKind kind, JsonElement? metadata, string? fromPath)
    {
        // A module's "directory" is its name without the last segment and the extension.
        var directory = Prefix;
        if (fromPath is not null)
        {
            var extension = fromPath.LastIndexOf('.');
            var lastDot = extension > 0 ? fromPath.LastIndexOf('.', extension - 1) : -1;
            directory = lastDot >= 0 ? fromPath[..lastDot] : "";
        }

        var normalized = path.Replace('/', '.').Replace('\\', '.');
        var name = directory.Length == 0 ? normalized : directory + "." + normalized;
        var suffix = kind == JqModuleKind.Data ? ".json" : ".jq";
        if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            name += suffix;
        return assembly.GetManifestResourceInfo(name) is null ? null : name;
    }

    /// <inheritdoc/>
    public override TextReader Open(string canonicalPath)
        => new StreamReader(assembly.GetManifestResourceStream(canonicalPath)
            ?? throw new JqException($"module not found: {canonicalPath}"));
}
