using System.Text;

namespace JQLite.Tests.Core.Builtins;

/// <summary>
/// Random patterns of the flavor of §9.10 and random inputs for them, from a seed: the same seed gives
/// the same cases on every run.
/// </summary>
sealed class RegexFuzz(uint seed)
{
    static readonly string[] Atoms = ["a", "a", "b", "b", "c", ".", "[ab]", "[^a]", @"\w", @"\s"];
    static readonly string[] Anchors = ["^", "$", @"\b", @"\B"];
    static readonly string[] Quantifiers = ["*", "+", "?", "*?", "+?", "??", "{2}", "{0,2}", "{1,}", "{1,2}?", "{2,}?"];
    static readonly string[] Letters = ["a", "a", "b", "b", "c", " ", "\U0001F600"];

    uint state = seed;

    /// <summary>A number 0 .. n - 1 (a linear congruential generator, exact in 32 bits).</summary>
    public int Next(int n)
    {
        state = unchecked(state * 1664525 + 1013904223);
        return (int)((ulong)state * (ulong)n >> 32);
    }

    /// <summary>A pattern: alternatives of sequences of atoms, groups and anchors, with quantifiers; <paramref name="depth"/> bounds the nesting of groups.</summary>
    public string Pattern(int depth)
    {
        var text = new StringBuilder();
        var alternatives = 1 + (Next(4) == 0 ? 1 + Next(2) : 0);
        for (var a = 0; a < alternatives; a++)
        {
            if (a > 0)
                text.Append('|');
            var items = Next(4);
            for (var i = 0; i < items; i++)
            {
                var kind = Next(10);
                if (kind == 0)
                {
                    text.Append(Anchors[Next(Anchors.Length)]);
                    continue;
                }

                if (kind <= 3 && depth > 0)
                    text.Append(Next(3) == 0 ? "(?:" : "(").Append(Pattern(depth - 1)).Append(')');
                else
                    text.Append(Atoms[Next(Atoms.Length)]);
                if (Next(2) == 0)
                    text.Append(Quantifiers[Next(Quantifiers.Length)]);
            }
        }

        return text.ToString();
    }

    /// <summary>An input of at most <paramref name="length"/> code points.</summary>
    public string Input(int length)
    {
        var text = new StringBuilder();
        for (var n = Next(length + 1); n > 0; n--)
            text.Append(Letters[Next(Letters.Length)]);
        return text.ToString();
    }
}
