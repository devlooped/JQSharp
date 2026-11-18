using System.Text.Json;
using Devlooped;

namespace Devlooped.Tests;

/// <summary>
/// The public resolvers (<see cref="JqFileResolver"/>, <see cref="JqResourceResolver"/>, a custom
/// <see cref="JqResolver"/>) through <see cref="Jq.Parse"/>. What modules mean is the corpus's
/// (spec/tests/modules.test).
/// </summary>
public sealed class JqModuleTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "JqModuleTests_" + Guid.NewGuid().ToString("N"));

    public JqModuleTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    void Write(string name, string content)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    static string[] Run(string expression, JqResolver? resolver, string input = "null")
    {
        using var document = JsonDocument.Parse(input);
        return [.. Jq.Parse(expression, new JqOptions { Resolver = resolver }).Evaluate(document.RootElement).Select(static e => e.GetRawText())];
    }

    [Fact]
    public void File_resolver_adds_the_extension_of_the_kind()
    {
        Write("utils.jq", "def inc: . + 1;");
        Write("config.json", """{"key":"value"} 2""");
        Assert.Equal(["6", """[{"key":"value"},2]"""], Run("""include "utils"; import "config" as $c; inc, $c""", new JqFileResolver(directory), "5"));
    }

    [Fact]
    public void File_resolver_keeps_an_explicit_extension()
    {
        Write("ext.jq", "def ext: . + 100;");
        Assert.Equal(["105", "105"], Run("""include "ext.jq"; import "ext.jq" as e; ext, e::ext""", new JqFileResolver(directory), "5"));
    }

    [Fact]
    public void File_resolver_resolves_from_the_importing_module_directory()
    {
        Write("lib/outer.jq", """include "inner"; def outer: inner * 2;""");
        Write("lib/inner.jq", "def inner: . + 10;");
        Assert.Equal(["30"], Run("""include "lib/outer"; outer""", new JqFileResolver(directory), "5"));
    }

    [Fact]
    public void A_missing_module_is_a_parse_error()
    {
        var error = Assert.Throws<JqParseException>(() => Run("""include "nonexistent"; .""", new JqFileResolver(directory)));
        Assert.Equal("module not found: nonexistent at line 1, column 1", error.Message);
        Assert.Equal((1, 1), (error.Line, error.Column));
    }

    [Fact]
    public void Without_a_resolver_no_module_exists()
    {
        Assert.Throws<JqParseException>(() => Run("""import "something" as m; .""", null));
        Assert.Throws<JqParseException>(() => Run("""import "data" as $d; $d""", null));
        Assert.Equal(["\"module not found: x\""], Run("""try ("x" | modulemeta) catch .""", null));
    }

    [Fact]
    public void An_error_in_a_module_names_it()
    {
        Write("bad.jq", "def ok: 1;\ndef broken: (1;");
        var error = Assert.Throws<JqParseException>(() => Run("""import "bad" as b; 1""", new JqFileResolver(directory)));
        Assert.Equal((2, 15), (error.Line, error.Column));
        Assert.Contains(Path.Combine(directory, "bad.jq"), error.Message);
    }

    [Fact]
    public void Data_modules_bind_an_array_of_their_values()
    {
        Write("config.json", """{"key":"value"}""");
        Assert.Equal(["""[{"key":"value"}]""", "\"value\""], Run("""import "config" as $cfg; $cfg::cfg, $cfg[0].key""", new JqFileResolver(directory)));
    }

    [Fact]
    public void Invalid_data_is_a_parse_error()
    {
        Write("bad.json", "not valid json {{{");
        Assert.Throws<JqParseException>(() => Run("""import "bad" as $b; $b""", new JqFileResolver(directory)));
    }

    [Fact]
    public void Each_module_is_read_once_per_parse()
    {
        Write("counted.jq", "def counted: . + 1;");
        var resolver = new CountingResolver(new JqFileResolver(directory));
        Assert.Equal(["6"], Run("""include "counted"; import "counted" as c1; import "counted" as c2; c1::counted""", resolver, "5"));
        Assert.Equal(1, resolver.Opens);
    }

    [Fact]
    public void Modulemeta_resolves_through_the_resolver_at_run_time()
    {
        Write("meta.jq", """module {"version": "1.0"}; def double: . * 2;""");
        var expression = Jq.Parse("""modulemeta""", new JqOptions { Resolver = new JqFileResolver(directory) });
        using var input = JsonDocument.Parse("\"meta\"");
        var results = new string[16];
        Parallel.For(0, results.Length, i => results[i] = expression.Evaluate(input.RootElement)[0].GetRawText());
        Assert.All(results, result => Assert.Equal("""{"version":"1.0","deps":[],"defs":["double/0"]}""", result));
    }

    [Fact]
    public void Resource_resolver_finds_embedded_modules()
    {
        var resolver = new JqResourceResolver(typeof(JqModuleTests).Assembly, "Devlooped.Tests.Modules");
        Assert.Equal(["\"hello world!\""], Run("""include "greet"; greet""", resolver, "\"world\""));
    }

    [Fact]
    public void Resource_resolver_reports_missing_modules()
        => Assert.Throws<JqParseException>(() => Run("""include "missing"; .""", new JqResourceResolver(typeof(JqModuleTests).Assembly, "Devlooped.Tests.Modules")));

    [Fact]
    public void A_custom_resolver_names_and_opens_modules()
    {
        var resolver = new MemoryResolver(new Dictionary<string, string>
        {
            ["mem:greet.jq"] = """include "punctuation"; def greet: "hello " + . + mark;""",
            ["mem:punctuation.jq"] = """def mark: "!";""",
        });
        Assert.Equal(["\"hello world!\""], Run("""include "greet"; greet""", resolver, "\"world\""));
    }

    // Counts the modules opened.
    sealed class CountingResolver(JqResolver inner) : JqResolver
    {
        int opens;

        public int Opens => opens;

        public override string? GetCanonicalPath(string path, JqModuleKind kind, JsonElement? metadata, string? fromPath)
            => inner.GetCanonicalPath(path, kind, metadata, fromPath);

        public override TextReader Open(string canonicalPath)
        {
            Interlocked.Increment(ref opens);
            return inner.Open(canonicalPath);
        }
    }

    // Modules in a dictionary, by "mem:" and the path with its extension.
    sealed class MemoryResolver(Dictionary<string, string> modules) : JqResolver
    {
        public override string? GetCanonicalPath(string path, JqModuleKind kind, JsonElement? metadata, string? fromPath)
        {
            var name = "mem:" + path + (kind == JqModuleKind.Code ? ".jq" : ".json");
            return modules.ContainsKey(name) ? name : null;
        }

        public override TextReader Open(string canonicalPath) => new StringReader(modules[canonicalPath]);
    }
}
