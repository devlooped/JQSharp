namespace Devlooped;

/// <summary>
/// Resolves module paths to their jq source content for <c>include</c> statements.
/// Inspired by <see cref="System.Xml.XmlUrlResolver"/>.
/// </summary>
public abstract class JqResolver
{
    /// <summary>
    /// Resolves a relative module path to a <see cref="TextReader"/> for reading its content.
    /// </summary>
    /// <param name="path">The relative path as specified in the <c>include</c> statement.</param>
    /// <param name="fromPath">
    /// The canonical path of the module that contains the <c>include</c> statement,
    /// or <see langword="null"/> if the <c>include</c> is in the top-level expression.
    /// </param>
    /// <returns>A <see cref="TextReader"/> over the module source.</returns>
    /// <exception cref="JqException">Thrown when the module cannot be resolved.</exception>
    public abstract TextReader Resolve(string path, string? fromPath);

    /// <summary>
    /// Returns a canonical (cache) key for a resolved module path.
    /// The default implementation returns <paramref name="path"/> unchanged; override
    /// to return absolute paths or other stable identifiers for caching.
    /// </summary>
    public virtual string GetCanonicalPath(string path, string? fromPath) => path;
}
