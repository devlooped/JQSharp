namespace Devlooped;

/// <summary>
/// A <see cref="JqResolver"/> that resolves module paths against the file system.
/// Relative paths are resolved from the directory of the including module (or
/// <see cref="BaseDirectory"/> for top-level includes). The <c>.jq</c> extension
/// is appended automatically when the path has no extension.
/// </summary>
/// <remarks>
/// Initializes a new instance of <see cref="JqFileResolver"/> rooted at
/// <paramref name="baseDirectory"/>.
/// </remarks>
public class JqFileResolver(string baseDirectory) : JqResolver
{
    /// <summary>
    /// Gets the base directory used to resolve top-level (non-relative) includes.
    /// </summary>
    public string BaseDirectory { get; } = Path.GetFullPath(baseDirectory);

    /// <inheritdoc/>
    public override TextReader Resolve(string path, string? fromPath)
    {
        var fullPath = GetCanonicalPath(path, fromPath);
        if (!File.Exists(fullPath))
            throw new JqException($"Module not found: '{path}' (resolved to '{fullPath}')");

        return new StreamReader(fullPath);
    }

    /// <summary>
    /// Returns the absolute file path for <paramref name="path"/>, appending <c>.jq</c>
    /// when the path has no extension. Resolution is relative to <paramref name="fromPath"/>'s
    /// directory (if provided) or <see cref="BaseDirectory"/>.
    /// </summary>
    public override string GetCanonicalPath(string path, string? fromPath)
    {
        var searchDir = fromPath is not null
            ? Path.GetDirectoryName(fromPath) ?? BaseDirectory
            : BaseDirectory;

        var candidate = Path.GetFullPath(Path.Combine(searchDir, path));

        if (!Path.HasExtension(candidate))
            candidate += ".jq";

        return candidate;
    }
}
