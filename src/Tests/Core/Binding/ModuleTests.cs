using Devlooped.Core.Binding;
using Devlooped.Core.Syntax;
using Devlooped.Core.Values;
using Devlooped.Tests.Core.Runtime;

namespace Devlooped.Tests.Core.Binding;

/// <summary>The modules group (spec §9.11) beyond the corpus: errors and where they are, loading once, limits, modulemeta.</summary>
public class ModuleTests
{
    static readonly DirectoryModules Corpus = new(Path.Combine(AppContext.BaseDirectory, "spec", "tests", "modules"));

    static string Error(string program, IModuleSource? modules)
    {
        Assert.False(Resolver.TryCompile(program, out _, out var error, modules: modules));
        return $"{error.Message} @{error.Module ?? "main"}:{error.Line}:{error.Column}";
    }

    static string Run(string program, IModuleSource modules, string input = "null")
    {
        Assert.True(Resolver.TryCompile(program, out var bound, out var error, modules: modules), error?.Message);
        return string.Join(", ", Evaluate.Run(bound, Evaluate.Json(input)));
    }

    [Theory]
    [InlineData("import \"double\" as d; import \"nope\" as n; 1", "module not found: nope @main:1:23")]
    [InlineData("import \"broken\" as b; 1", "Expected ')' but found ';' @broken.jq:1:15")]
    [InlineData("import \"usesmain\" as u; 1", "mainf/0 is not defined @usesmain.jq:1:15")]
    [InlineData("import \"cycle1\" as c; 1", "circular import of cycle1.jq @cycle2.jq:1:1")]
    [InlineData("import \"self\" as s; 1", "circular import of self.jq @self.jq:1:1")]
    [InlineData("include \"config\"; 1", "module not found: config @main:1:1")]
    [InlineData("import \"a\" as a; a::zz", "a::zz/0 is not defined @main:1:18")]
    [InlineData("import \"values\" as $d; $d::x", "$d::x is not defined @main:1:24")]
    public void Binding_errors_say_where_they_are(string program, string expected)
        => Assert.Equal(expected, Error(program, Corpus));

    [Fact]
    public void Bad_data_is_a_binding_error()
        => Assert.StartsWith("error loading data file bad.json: Unfinished JSON term at EOF", Error("import \"bad\" as $b; $b", Corpus));

    [Fact]
    public void Without_a_source_no_module_exists()
    {
        Assert.Equal("module not found: a @main:1:1", Error("import \"a\" as a; 1", null));
        Assert.Equal("\"module not found: a\"", string.Join(", ", Evaluate.Run("try (\"a\" | modulemeta) catch .")));
    }

    [Fact]
    public void Each_module_is_read_once()
    {
        var modules = new DirectoryModules(Path.Combine(AppContext.BaseDirectory, "spec", "tests", "modules"));
        Assert.Equal("2", Run("import \"one\" as o; import \"one\" as p; include \"one\"; o::k + p::k", modules));
        Assert.Equal(1, modules.Reads);
    }

    [Fact]
    public void A_long_import_chain_is_no_recursion()
    {
        // m0 imports m1, …, m999 imports nothing; bound on a 1 MB stack.
        var sources = new Dictionary<string, string>();
        for (var i = 0; i < 1000; i++)
            sources[$"m{i}.jq"] = i == 999 ? "def f: 999;" : $"import \"m{i + 1}\" as n; def f: n::f; def g: {i};";
        var modules = new MemoryModules(sources);
        string? result = null;
        var thread = new Thread(() => result = Run("import \"m0\" as m; m::f, m::g", modules), 1024 * 1024);
        thread.Start();
        thread.Join();
        Assert.Equal("999, 0", result);
    }

    [Fact]
    public void Modulemeta_uses_what_the_program_loaded()
    {
        var sources = new Dictionary<string, string> { ["x.jq"] = "def a: 1;" };
        var modules = new MemoryModules(sources);
        Assert.True(Resolver.TryCompile("import \"x\" as x; [\"x\", \"y\"][] | modulemeta | .defs", out var bound, out _, modules: modules));
        sources["x.jq"] = "def changed: 1;";
        sources["y.jq"] = "def b: 1; def c: 2;";
        // x as the program loaded it; y, which it does not import, from its source now.
        Assert.Equal(["[\"a/0\"]", "[\"b/0\",\"c/0\"]"], Evaluate.Run(bound, JqValue.Null));
    }

    [Fact]
    public void Modules_share_the_host_variables()
    {
        var modules = new MemoryModules(new Dictionary<string, string> { ["v.jq"] = "def v: $x;" });
        Assert.True(Resolver.TryCompile("import \"v\" as v; v::v, $x", out var bound, out _, modules: modules));
        var options = new Devlooped.Core.Runtime.EvaluationOptions { Variables = new Dictionary<string, JqValue> { ["x"] = JqValue.FromNumber(7) } };
        Assert.Equal(["7", "7"], Evaluate.Run(bound, JqValue.Null, options));
    }

    [Fact]
    public void Qualified_aliases_nest()
    {
        var modules = new MemoryModules(new Dictionary<string, string> { ["a.jq"] = "def f: 1;" });
        Assert.Equal("1", Run("import \"a\" as x::y; x::y::f", modules));
    }

    [Fact]
    public void A_module_obeys_the_nesting_limit_of_its_file()
    {
        var deep = new string('[', Parser.MaxNesting + 1) + new string(']', Parser.MaxNesting + 1);
        var modules = new MemoryModules(new Dictionary<string, string> { ["deep.jq"] = $"def f: {deep};" });
        // def opens the first level, so the 1000th bracket is one too many.
        Assert.Equal("Program too deeply nested @deep.jq:1:1007", Error("import \"deep\" as d; 1", modules));
    }
}