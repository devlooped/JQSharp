using System.Reflection;

namespace Devlooped;

/// <summary>
/// A <see cref="JqResolver"/> that resolves module paths to embedded resources in
/// one or more assemblies. Resource names are formed by combining an optional
/// <see cref="Prefix"/> with the module path (slashes converted to dots, <c>.jq</c>
/// extension added when absent).
/// </summary>
/// <remarks>
/// <para>
/// For example, with <c>prefix = "MyApp.Filters"</c> an <c>include "utils"</c>
/// statement resolves to the embedded resource named <c>MyApp.Filters.utils.jq</c>.
/// </para>
/// <para>
/// Nested includes (from within a module) resolve relative to the parent module's
/// canonical name, so <c>include "sub/helper"</c> inside <c>MyApp.Filters.utils.jq</c>
/// resolves to <c>MyApp.Filters.sub.helper.jq</c>.
/// </para>
/// </remarks>
/// <remarks>
/// Initializes a new instance using the given assembly and resource name prefix.
/// </remarks>
/// <param name="assembly">The assembly whose embedded resources are searched.</param>
/// <param name="prefix">
/// A dot-separated prefix prepended to every resolved resource name
/// (e.g. the default namespace of the assembly).  May be empty.
/// </param>
public class JqResourceResolver(Assembly assembly, string? prefix = default) : JqResolver
{
    readonly Assembly _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));

    /// <summary>
    /// Gets the dot-separated resource name prefix applied before the module path.
    /// May be empty. When non-empty it should NOT end with a dot.
    /// </summary>
    public string Prefix { get; } = prefix ?? string.Empty;

    /// <inheritdoc/>
    public override TextReader Resolve(string path, string? fromPath)
    {
        var resourceName = GetCanonicalPath(path, fromPath);
        var stream = _assembly.GetManifestResourceStream(resourceName)
            ?? throw new JqException(
                $"Embedded resource not found: '{resourceName}' in assembly '{_assembly.GetName().Name}'.");

        return new StreamReader(stream);
    }

    /// <summary>
    /// Returns the dot-separated embedded resource name for <paramref name="path"/>.
    /// Slashes and backslashes are converted to dots; <c>.jq</c> is appended when
    /// the path has no extension.
    /// </summary>
    public override string GetCanonicalPath(string path, string? fromPath)
    {
        var hasKnownExtension =
            path.EndsWith(".jq", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        // Resolve relative paths against the parent module's "directory" (prefix segment).
        string basePart;
        if (fromPath is not null)
        {
            // fromPath is e.g. "MyApp.Filters.utils.jq" — strip the last segment to get the "directory".
            var lastDot = fromPath.LastIndexOf('.', fromPath.Length - 4); // skip the ".jq" suffix
            basePart = lastDot >= 0 ? fromPath[..lastDot] : fromPath;
        }
        else
        {
            basePart = Prefix;
        }

        // Convert path separators to dots and combine.
        var normalized = path.Replace('/', '.').Replace('\\', '.');

        var resourceName = string.IsNullOrEmpty(basePart)
            ? normalized
            : basePart + "." + normalized;

        if (!hasKnownExtension && !resourceName.EndsWith(".jq", StringComparison.OrdinalIgnoreCase))
            resourceName += ".jq";

        return resourceName;
    }
}
