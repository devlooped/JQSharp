using System.Text.Json;

namespace JQLite;

/// <summary>
/// A <see cref="JqResolver"/> over the file system: a path is relative to the directory of the
/// importing module, or to <see cref="BaseDirectory"/> for the expression itself, and gets the
/// extension <c>.jq</c> (code) or <c>.json</c> (data) when it has none. The canonical path is the
/// full file path. Safe to use from several threads.
/// </summary>
/// <param name="baseDirectory">The directory of the expression's own directives.</param>
public class JqFileResolver(string baseDirectory) : JqResolver
{
    /// <summary>The directory that the expression's own directives are relative to.</summary>
    public string BaseDirectory { get; } = Path.GetFullPath(baseDirectory);

    /// <inheritdoc/>
    public override string? GetCanonicalPath(string path, JqModuleKind kind, JsonElement? metadata, string? fromPath)
    {
        var directory = fromPath is null ? BaseDirectory : Path.GetDirectoryName(fromPath) ?? BaseDirectory;
        var file = Path.GetFullPath(Path.Combine(directory, path));
        if (!Path.HasExtension(file))
            file += kind == JqModuleKind.Data ? ".json" : ".jq";
        return File.Exists(file) ? file : null;
    }

    /// <inheritdoc/>
    public override TextReader Open(string canonicalPath) => new StreamReader(canonicalPath);
}
