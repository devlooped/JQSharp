using System.Text.Json;

namespace JQLite;

/// <summary>Whether an <c>import</c> asks for a code module (<c>.jq</c>) or a data module (<c>import "p" as $name;</c>, JSON values).</summary>
public enum JqModuleKind
{
    /// <summary>A module of jq definitions.</summary>
    Code,

    /// <summary>A sequence of JSON values, bound as an array.</summary>
    Data,
}

/// <summary>
/// Finds the modules of <c>import</c> and <c>include</c> directives and of <c>modulemeta</c>: maps a
/// path, as written, to a canonical path (the module's identity), then opens it.
/// </summary>
/// <remarks>
/// A module is read once per canonical path while an expression is parsed. <c>modulemeta</c> calls
/// the resolver while expressions are evaluated, possibly from several threads at once:
/// implementations must allow that.
/// </remarks>
public abstract class JqResolver
{
    /// <summary>
    /// The canonical path of the module <paramref name="path"/> of the given kind, imported with
    /// <paramref name="metadata"/> (the directive's metadata object, if any) from the module at
    /// <paramref name="fromPath"/> (a canonical path, or <see langword="null"/> for the expression itself);
    /// <see langword="null"/> when there is no such module. The same canonical path means the same module.
    /// </summary>
    public abstract string? GetCanonicalPath(string path, JqModuleKind kind, JsonElement? metadata, string? fromPath);

    /// <summary>Opens the module at a canonical path that <see cref="GetCanonicalPath"/> returned.</summary>
    public abstract TextReader Open(string canonicalPath);
}
