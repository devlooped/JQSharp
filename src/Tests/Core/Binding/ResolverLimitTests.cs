using System.Text;
using Devlooped.Core.Binding;
using Devlooped.Core.Syntax;
using Xunit.Sdk;

namespace Devlooped.Tests.Core.Binding;

using Bind = Devlooped.Core.Binding.Bind;
using Variable = Devlooped.Core.Binding.Variable;

/// <summary>
/// Binding does not exhaust the host stack on what the parser accepts: nesting up to the limit of
/// spec §11, and sequences of any length, which the parser builds as deep spines. Each program is
/// parsed and bound on a thread with a 1 MB stack (the default of a Windows main thread), as in
/// ParserLimitTests.
/// </summary>
public class ResolverLimitTests
{
    const int Limit = Parser.MaxNesting;

    static string Repeat(string text, int count) => new StringBuilder().Insert(0, text, count).ToString();

    static BoundProgram Binds(string source)
    {
        BoundProgram? bound = null;
        SyntaxError? error = null;
        var thread = new Thread(() => Resolver.TryCompile(source, out bound, out error), 1024 * 1024);
        thread.Start();
        thread.Join();
        return bound ?? throw new XunitException($"{error!.Message} at {error.Line}:{error.Column}");
    }

    // Opening and closing repeated Limit times around "1"; each opening is one level of nesting
    // and costs the binder the most frames that level can (a construct, then a sequence inside it).
    [Theory]
    [InlineData("(", ")")]
    [InlineData("[", "]")]
    [InlineData("{a: ", "}")]
    [InlineData("{(\"a\" + ", "): 1}")]
    [InlineData(".[", "]")]
    [InlineData("f(", ")")]
    [InlineData("\"\\(", ")\"")]
    [InlineData("if . then ", " end")]
    [InlineData("try ", "")]
    [InlineData("reduce . as $x (", ";.)")]
    [InlineData("foreach . as $x (", ";.)")]
    [InlineData("def f: ", "; f")]
    [InlineData("def f(g): ", "; f(.)")]
    [InlineData("[1 + ", "]")]
    [InlineData("[1, 2 as $x | ", "]")]
    [InlineData("(1 | ", ")")]
    [InlineData(".[1, ", "]")]
    [InlineData("{a: 1 | 1 + ", "}")]
    [InlineData("if 1, 1 as $x | ", " then . end")]
    [InlineData("try -", "")]
    [InlineData("f(label $x | ", ")")]
    [InlineData(". as [$x] ?// {(\"a\" + ", "): $y} | 1")]
    public void Nesting_up_to_the_limit_binds(string open, string close)
    {
        var levels = open.Contains("{(") ? Limit / 2 : Limit;
        var source = "def f: 1; def f(g): 1; " + Repeat(open, levels) + "1" + Repeat(close, levels);
        Binds(source);
    }

    // The sequences of ParserLimitTests: prefix, 10,000 elements, suffix.
    [Theory]
    [InlineData("", "1 + ", "1")]
    [InlineData("", "1 * ", "1")]
    [InlineData("", "1 // ", "1")]
    [InlineData("", "1 and ", "1")]
    [InlineData("", "1 == 1 or ", "1")]
    [InlineData("", "1 | ", "1")]
    [InlineData("", "1, ", "1")]
    [InlineData("", "1 as $x | ", "$x")]
    [InlineData("", "label $x | ", "break $x")]
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
    [InlineData("[", "1, 1 as $x | ", "$x]")]
    [InlineData("", "1, def f: 1; ", "f")]
    [InlineData("", "1, label $x | ", "1")]
    [InlineData("", "1 // 1 | ", "1")]
    [InlineData("", "1 as $x | label $y | def f: $x; ", "f")]
    [InlineData("", "1 | 1, 1 | ", "1")]
    [InlineData("", "def f(g): g; ", "f(.)")]
    [InlineData("", "reduce . as $x (.; .) | ", "1")]
    public void Long_sequences_do_not_recurse(string prefix, string element, string suffix)
        => Binds(prefix + Repeat(element, 10_000) + suffix);

    [Fact]
    public void Long_elif_chains_do_not_recurse()
        => Binds("if . then 1 " + Repeat("elif . then 1 ", 10_000) + "else 2 end");

    [Fact]
    public void A_variable_ten_thousand_frames_out()
    {
        var program = Binds("0 as $a | " + Repeat("1 as $x | ", 10_000) + "$a");
        var node = program.Body;
        var depth = 0;
        while (node is Bind bind)
        {
            node = bind.Body;
            depth++;
        }

        Assert.Equal(10_001, depth);
        Assert.Equal(new Variable(10_000, 0), node);
    }

    [Fact]
    public void Many_definitions_in_a_row()
    {
        var program = Binds(Repeat("def f: 1; ", 10_000) + "f");
        Assert.Equal(Prelude.Instance.Functions.Length + 10_000, program.Functions.Length);
        var call = Assert.IsType<CallFunction>(program.Body);
        Assert.Equal(program.Functions.Length - 1, call.Function);
        Assert.Equal(0, call.Hops);
    }

    [Fact]
    public void Many_parameters_and_arguments()
    {
        var program = Binds("def f(" + Repeat("a; ", 10_000) + "b): b; f(" + Repeat("1; ", 10_000) + "1)");
        Assert.Equal(10_001, Assert.IsType<CallFunction>(program.Body).Arguments.Length);
        Assert.Equal(new CallParameter(0, 10_000), program.Functions[^1].Body);
    }
}