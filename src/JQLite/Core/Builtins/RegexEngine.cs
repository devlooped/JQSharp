using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace JQLite.Core.Builtins;

/// <summary>
/// The host-specific part of §9.10, the only file of the core that uses a host regex engine: it
/// compiles a pattern that <see cref="RegexSyntax"/> translated and finds the next match from a
/// position. A port plugs its engine in here (Go <c>regexp</c>, Rust <c>regex</c>, JS <c>RegExp</c>
/// with the <c>u</c> and <c>d</c> flags, Java <c>java.util.regex</c>, Python <c>re</c>); the dialect,
/// the translation and the natives are portable code beside it.
/// </summary>
/// <remarks>
/// .NET runs the pattern with <see cref="RegexOptions.NonBacktracking"/> (linear time in the input)
/// and works on UTF-16 units, which the translation accounts for. The one exception is a pattern
/// with <c>\b</c> or <c>\B</c>: the dialect's word boundaries are ASCII, .NET's are Unicode, and the
/// ASCII form needs lookaround, which the linear-time engine does not have; such a pattern runs on
/// the backtracking engine and gives up the linear-time guarantee (it is validated by the dialect
/// first, so it has no backreferences or other constructs beyond the lookaround of the boundary).
/// Case-insensitivity is never the engine's: the translation does it. Compiled patterns are cached
/// per process in a thread-safe map that is emptied when it reaches <see cref="CacheLimit"/> entries.
/// </remarks>
static class RegexEngine
{
    const int CacheLimit = 256;

    static readonly ConcurrentDictionary<string, HostPattern> cache = new();

    /// <summary>
    /// A compiled pattern, or false with the engine's reason; <paramref name="lookaround"/> (a
    /// property of the pattern, so not part of the cache key) selects the backtracking engine.
    /// </summary>
    public static bool TryCompile(string pattern, bool lookaround, out HostPattern compiled, out string reason)
    {
        reason = "";
        if (cache.TryGetValue(pattern, out compiled!))
            return true;

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant | (lookaround ? RegexOptions.None : RegexOptions.NonBacktracking));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            // The boundary of the host engine: its exception becomes the dialect's error.
            compiled = null!;
            reason = e.Message;
            return false;
        }

        if (cache.Count >= CacheLimit)
            cache.Clear();
        compiled = new HostPattern(regex);
        cache[pattern] = compiled;
        return true;
    }

    /// <summary>
    /// The first match starting at or after UTF-16 index <paramref name="start"/> (anchors and word
    /// boundaries see the whole input). <paramref name="spans"/> receives start and end of the match,
    /// then of each group (-1, -1 for a group that did not participate): 2 · (groups + 1) entries.
    /// </summary>
    public static bool TryMatch(HostPattern compiled, string input, int start, int[] spans)
    {
        var match = compiled.Regex.Match(input, start);
        if (!match.Success)
            return false;
        for (var g = 0; g < spans.Length / 2; g++)
        {
            var group = match.Groups[g];
            spans[2 * g] = group.Success ? group.Index : -1;
            spans[2 * g + 1] = group.Success ? group.Index + group.Length : -1;
        }

        return true;
    }
}

/// <summary>A pattern compiled by the host engine.</summary>
sealed class HostPattern(Regex regex)
{
    public Regex Regex { get; } = regex;
}