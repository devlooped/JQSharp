using System.Text;
using Devlooped.Core.Syntax;
using Xunit.Sdk;

namespace Devlooped.Tests.Core.Syntax;

/// <summary>
/// The nesting limit of spec §11, and programs that must not exhaust the host stack. Each parse runs
/// on a thread with a 1 MB stack (the default of a Windows main thread), so the result does not
/// depend on the stack size of the test runner's threads.
/// </summary>
public class ParserLimitTests
{
    const int Limit = Parser.MaxNesting;

    static string Repeat(string text, int count) => new StringBuilder().Insert(0, text, count).ToString();

    static (Program? Program, SyntaxError? Error) ParseOnSmallStack(string source)
    {
        Program? program = null;
        SyntaxError? error = null;
        var thread = new Thread(() => Parser.TryParse(source, out program, out error), 1024 * 1024);
        thread.Start();
        thread.Join();
        return (program, error);
    }

    static Program Parses(string source)
    {
        var (program, error) = ParseOnSmallStack(source);
        return program ?? throw new XunitException($"{error!.Message} at {error.Line}:{error.Column}");
    }

    static string Fails(string source)
    {
        var (program, error) = ParseOnSmallStack(source);
        Assert.Null(program);
        return $"{error!.Message} @{error.Line}:{error.Column}";
    }

    public static TheoryData<string, string, string, int> Nestings => new()
    {
        // name, opening repeated, closing repeated (the innermost term is "1"), offset in the
        // opening of the token that opens the level
        { "parentheses", "(", ")", 0 },
        { "arrays", "[", "]", 0 },
        { "objects", "{a:", "}", 0 },
        { "indexes", ".[", "]", 1 },
        { "calls", "f(", ")", 1 },
        { "interpolations", "\"\\(", ")\"", 0 },
        { "if", "if . then ", " end", 0 },
        { "try", "try ", "", 0 },
        { "reduce", "reduce . as $x (", ";.)", 0 },
        { "foreach", "foreach . as $x (", ";.)", 0 },
        { "definitions", "def f: ", "; f", 0 },
        { "object keys", "{(", "):1}", 0 },
    };

    [Theory]
    [MemberData(nameof(Nestings))]
    public void Nesting_up_to_the_limit_parses(string name, string open, string close, int offset)
    {
        _ = (name, offset);
        var count = Levels(open);
        Parses(Repeat(open, count) + Innermost(open) + Repeat(close, count));
    }

    // "{(" opens two levels (the object and the parentheses).
    static int Levels(string open) => open == "{(" ? Limit / 2 : Limit;

    // A parenthesized key must not be a number.
    static string Innermost(string open) => open == "{(" ? "\"a\"" : "1";

    [Theory]
    [MemberData(nameof(Nestings))]
    public void Nesting_beyond_the_limit_fails_at_the_opening_token(string name, string open, string close, int offset)
    {
        _ = name;
        var count = Levels(open) + 1;
        var source = Repeat(open, count) + Innermost(open) + Repeat(close, count);
        // The level beyond the limit is opened by the token at offset in the last opening.
        var column = ((count - 1) * open.Length) + offset + 1;
        Assert.Equal($"Program too deeply nested @1:{column}", Fails(source));
    }

    [Fact]
    public void Nested_patterns_count_too()
    {
        Parses(". as " + Repeat("[", Limit) + "$x" + Repeat("]", Limit) + " | $x");
        Assert.Equal($"Program too deeply nested @1:{Limit + 6}", Fails(". as " + Repeat("[", Limit + 1) + "$x" + Repeat("]", Limit + 1) + " | $x"));
    }

    [Fact]
    public void Nested_metadata_counts_too()
        => Assert.Equal($"Program too deeply nested @1:{Limit + 11}", Fails("module {a: " + Repeat("[", Limit) + Repeat("]", Limit) + "}; 1"));

    [Fact]
    public void Nested_metadata_within_the_limit_parses()
        => Parses("module {a: " + Repeat("[", Limit - 1) + Repeat("]", Limit - 1) + "}; 1");

    [Fact]
    public void Mixed_nesting_counts_every_kind()
        => Assert.Equal($"Program too deeply nested @1:{(Limit / 2 * 2) + 1}", Fails(Repeat("([", (Limit / 2) + 1) + "1" + Repeat("])", (Limit / 2) + 1)));

    // Sequences of any length parse without recursing per element: prefix, 10,000 elements, suffix.
    [Theory]
    [InlineData("", "1 + ", "1")]
    [InlineData("", "1 * ", "1")]
    [InlineData("", "1 // ", "1")]
    [InlineData("", "1 and ", "1")]
    [InlineData("", "1 == 1 or ", "1")]
    [InlineData("", "1 | ", "1")]
    [InlineData("", "1, ", "1")]
    [InlineData("", "1 as $x | ", "$x")]
    [InlineData("", "label $x | ", "1")]
    [InlineData("", "def f: 1; ", "f")]
    [InlineData("(", "def f: 1; ", "f)")]
    [InlineData("[1, ", "def f: 1; ", "f]")]
    [InlineData("", "-", "1")]
    [InlineData("", "1 + -", "1")]
    [InlineData("", ".a", "")]
    [InlineData("", ".[0]", "")]
    [InlineData("", ".a?", "")]
    [InlineData(". as $a", " ?// $a", " | 1")]
    [InlineData("{a: ", "1 | ", "1}")]
    [InlineData("{", "a: 1, ", "}")]
    [InlineData("\"", "\\(1)", "\"")]
    [InlineData("f(", "1; ", "1)")]
    public void Long_sequences_do_not_recurse(string prefix, string element, string suffix)
        => Parses(prefix + Repeat(element, 10_000) + suffix);

    [Fact]
    public void Long_elif_chains_do_not_recurse()
        => Parses("if . then 1 " + Repeat("elif . then 1 ", 10_000) + "else 2 end");

    [Fact]
    public void A_long_chain_keeps_its_associativity()
    {
        var program = Parses(Repeat("1 - ", 10_000) + "1");
        // Left-associative: the left spine is 10,000 subtractions deep.
        var depth = 0;
        Node node = program.Body!;
        while (node is Binary binary)
        {
            Assert.IsType<Literal>(binary.Right);
            node = binary.Left;
            depth++;
        }

        Assert.Equal(10_000, depth);

        program = Parses(Repeat("1 // ", 10_000) + "1");
        depth = 0;
        node = program.Body!;
        while (node is Alternative alternative)
        {
            Assert.IsType<Literal>(alternative.Left);
            node = alternative.Right;
            depth++;
        }

        Assert.Equal(10_000, depth);
    }

    [Fact]
    public void A_long_pipe_of_bindings_nests_to_the_right()
    {
        var program = Parses(Repeat("1 as $x | ", 10_000) + "$x");
        var depth = 0;
        var node = program.Body!;
        while (node is Bind bind)
        {
            node = bind.Body;
            depth++;
        }

        Assert.Equal(10_000, depth);
        Assert.IsType<Variable>(node);
    }

    [Fact]
    public void Many_top_level_definitions_are_a_list()
    {
        var program = Parses(Repeat("def f: 1; ", 10_000) + "f");
        Assert.Equal(10_000, program.Definitions.Length);
        Assert.IsType<Call>(program.Body);
    }
}