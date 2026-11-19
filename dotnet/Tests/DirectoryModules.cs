using JQLite.Core.Binding;
using JQLite.Core.Values;

namespace JQLite.Tests;

/// <summary>
/// A module source over a directory, with the rule of spec/tests/README.md: the path <c>p</c>
/// imported from a file names <c>p.jq</c> (code) or <c>p.json</c> (data) in that file's directory,
/// the main program's directory being the root. Identities are paths relative to the root.
/// </summary>
sealed class DirectoryModules(string root) : IModuleSource
{
    int reads;

    /// <summary>How many module files were read.</summary>
    public int Reads => reads;

    public bool TryResolve(string path, ModuleKind kind, JqValue? metadata, string? from, out string identity, out string error)
    {
        var directory = from is null ? "" : System.IO.Path.GetDirectoryName(from) ?? "";
        identity = System.IO.Path.Combine(directory, path + (kind == ModuleKind.Code ? ".jq" : ".json")).Replace('\\', '/');
        error = File.Exists(System.IO.Path.Combine(root, identity)) ? "" : $"module not found: {path}";
        return error.Length == 0;
    }

    public bool TryRead(string identity, out string text, out string error)
    {
        Interlocked.Increment(ref reads);
        text = File.ReadAllText(System.IO.Path.Combine(root, identity));
        error = "";
        return true;
    }
}

/// <summary>A module source over a dictionary of identities and texts; a path is its identity without the extension.</summary>
sealed class MemoryModules(IReadOnlyDictionary<string, string> modules) : IModuleSource
{
    public bool TryResolve(string path, ModuleKind kind, JqValue? metadata, string? from, out string identity, out string error)
    {
        identity = path + (kind == ModuleKind.Code ? ".jq" : ".json");
        error = modules.ContainsKey(identity) ? "" : $"module not found: {path}";
        return error.Length == 0;
    }

    public bool TryRead(string identity, out string text, out string error)
    {
        error = "";
        return modules.TryGetValue(identity, out text!);
    }
}
