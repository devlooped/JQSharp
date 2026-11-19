using System.Diagnostics;
using System.Reflection;
using JQLite.Core.Builtins;
using static JQLite.Core.Builtins.RegexSyntax;
using static JQLite.Tests.Core.Runtime.Evaluate;

namespace JQLite.Tests.Core.Builtins;

/// <summary>
/// The regex engine: what it finds is what the simplest matcher of the same programs finds
/// (<see cref="BacktrackingMatcher"/>), in time linear in the input and with memory the size of the
/// pattern. The corpus shows what the programs mean (spec §9.10).
/// </summary>
public class RegexEngineTests
{
    static readonly Options Plain = new(Extended: false, DotAll: false, IgnoreCase: false);

    [Theory]
    [InlineData(1u)]
    [InlineData(7u)]
    [InlineData(12345u)]
    public void Random_patterns_match_as_the_backtracking_matcher_does(uint seed)
    {
        var fuzz = new RegexFuzz(seed);
        var compared = 0;
        for (var n = 0; n < 20000; n++)
        {
            var source = fuzz.Pattern(3);
            Assert.True(TryTranslate(source, Plain with { DotAll = fuzz.Next(2) == 0 }, out var translation, out var reason), $"{source}: {reason}");
            var matcher = new RegexMatcher(new RegexProgram(translation));
            var width = 2 * (translation.Groups.Length + 1);
            for (var k = 0; k < 4; k++)
            {
                var text = fuzz.Input(7);
                for (var start = 0; start <= text.Length; start++)
                {
                    // A search starts between code points.
                    if (start < text.Length && char.IsLowSurrogate(text[start]))
                        continue;
                    var expected = new int[width];
                    if (BacktrackingMatcher.TryMatch(translation, text, start, expected, 200000) is not { } found)
                        continue;
                    var where = $"{source} on {text} from {start}";
                    var spans = new int[width];
                    Assert.True(matcher.TryMatch(text, start, spans) == found, where);
                    if (found)
                        Assert.True(expected.SequenceEqual(spans), $"{where}: [{string.Join(", ", spans)}], expected [{string.Join(", ", expected)}]");
                    // Only the match; only whether there is one.
                    var match = new int[2];
                    Assert.True(matcher.TryMatch(text, start, match) == found, where);
                    if (found)
                        Assert.True(expected.Take(2).SequenceEqual(match), where);
                    Assert.True(matcher.TryMatch(text, start, []) == found, where);
                    compared++;
                }
            }
        }

        Assert.True(compared > 300000, compared.ToString());
    }

    // Patterns a backtracking matcher takes exponential or quadratic time for, and long inputs.
    [Theory]
    [InlineData("\"a\" * 28 + \"c\" | test(\"^(a*)*b$\")", "false")]
    [InlineData("\"a\" * 5000 + \"c\" | test(\"^(a*)*b$\")", "false")]
    [InlineData("\"ab\" * 2000000 | test(\"^(a|b)*$\")", "true")]
    [InlineData("\"a\" * 200000 | test(\"a*b\")", "false")]
    [InlineData("\"a\" * 200000 | [match(\"a*b\")] | length", "0")]
    [InlineData("\"x\" * 100000 | test(\"(x+x+)+y\")", "false")]
    [InlineData("\"a\" * 40 | test(\"(a?){40}a{40}\")", "true")]
    [InlineData("\"ab\" * 2000000 | match(\"^(a|b)*$\") | .captures[0].offset", "3999999")]
    [InlineData("\"a\" * 100000 | [match(\"(a|aa)*?$\"; \"g\") | .length]", "[100000,0]")]
    public void Linear_time(string program, string expected)
    {
        var clock = Stopwatch.StartNew();
        Assert.Equal(expected, Line(program));
        Assert.True(clock.ElapsedMilliseconds < 2000, $"{clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void The_memory_of_a_search_is_the_size_of_the_pattern()
    {
        // The threads are kept in arrays that grow with their number: after a long input they hold
        // no more than one thread per instruction that reads a code point.
        Assert.True(RegexEngine.TryCompile("^(?:(a)|(b)|[ab]c?)*$", Plain, out var program, out _));
        Assert.Equal(4, program.Readers);
        Assert.Equal(6, program.Slots);
        var matcher = new RegexMatcher(program);
        var spans = new int[6];
        Assert.True(matcher.TryMatch(string.Concat(Enumerable.Repeat("ab", 500000)), 0, spans));
        Assert.Equal([0, 1000000, 999998, 999999, 999999, 1000000], spans);
        var threads = Field(matcher, "current")!;
        var pcs = (int[])threads.GetType().GetProperty("Pcs")!.GetValue(threads)!;
        var captures = (int[])threads.GetType().GetProperty("Captures")!.GetValue(threads)!;
        Assert.True(pcs.Length <= 8, pcs.Length.ToString());
        Assert.Equal(pcs.Length * 6, captures.Length);
        Assert.Equal(program.States, ((int[])Field(matcher, "seen")!).Length);
        Assert.Equal(2 * program.States, ((int[])Field(matcher, "stack")!).Length);
    }

    [Fact]
    public void The_largest_patterns_of_spec_11_are_programs_the_matcher_runs()
    {
        // The limits are the translation's, and the corpus has their boundaries. What is the engine's:
        // within them a program has at most five instructions per unit of the pattern's size, and a
        // state is an instruction at a level (nested loops that can match nothing have levels).
        static RegexProgram Compiled(string source)
        {
            Assert.True(RegexEngine.TryCompile(source, Plain, out var program, out var reason), reason);
            return program;
        }

        static string Nested(int levels, string inner) => string.Concat(Enumerable.Repeat("(?:", levels)) + inner + string.Concat(Enumerable.Repeat(")*", levels));

        Assert.Equal(new[] { 10000, 100, 1000 }, new[] { MaxSize, MaxNesting, MaxGroups });
        // The most instructions for a size: an empty group repeated, each copy optional and with its mark.
        Assert.True(TryTranslate(string.Concat(Enumerable.Repeat("(){0,1000}", 9)) + "(){0,990}", Plain, out var copies, out _));
        Assert.Equal(5 * (MaxSize - 10) + 3, copies.Ops.Length);
        Assert.True(new RegexMatcher(new RegexProgram(copies)).TryMatch("a", 0, new int[2]));
        // The most states: every instruction inside a hundred loops that can match nothing.
        var deep = Compiled(Nested(MaxNesting, string.Concat(Enumerable.Repeat("a?", 4900))));
        Assert.InRange(deep.States, 1_000_001, 1_999_999);
        Assert.True(new RegexMatcher(deep).TryMatch(new string('a', 100) + "b", 0, new int[2]));
        Assert.Equal("[300,0]", Line($"\"a\" * 300 | [match(\"{Nested(MaxNesting, "a*")}\"; \"g\") | .length]"));
        // The most capture slots: a thousand groups, each thread with its own.
        var groups = Compiled(string.Concat(Enumerable.Repeat("(a)", MaxGroups)));
        Assert.Equal(2 * (MaxGroups + 1), groups.Slots);
        var spans = new int[groups.Slots];
        Assert.True(new RegexMatcher(groups).TryMatch(new string('a', MaxGroups + 5), 0, spans));
        Assert.Equal(new[] { 0, 1000, 0, 1, 999, 1000 }, spans[..4].Concat(spans[^2..]).ToArray());
        // Past a limit the translation fails, with the fixed reason.
        Assert.False(RegexEngine.TryCompile(string.Concat(Enumerable.Repeat("(a)", MaxGroups + 1)), Plain, out _, out var failure));
        Assert.Equal("the pattern is too large", failure);
    }

    static object? Field(object target, string name)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
}
