using System.Text.Json;
using Devlooped;

namespace Devlooped.Tests;

public class JqModuleTests : IDisposable
{
    readonly string _tempDir;

    public JqModuleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "JqModuleTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    void WriteModule(string name, string content)
        => File.WriteAllText(Path.Combine(_tempDir, name + ".jq"), content);

    void WriteJsonModule(string name, string jsonContent)
        => File.WriteAllText(Path.Combine(_tempDir, name + ".json"), jsonContent);

    static string[] EvaluateToStrings(string expression, string inputJson, JqResolver? resolver = null)
    {
        using var document = JsonDocument.Parse(inputJson);
        return Jq.Parse(expression, resolver).Evaluate(document.RootElement)
            .Select(static e => JsonSerializer.Serialize(e))
            .ToArray();
    }

    [Fact]
    public void Include_exposes_module_function()
    {
        WriteModule("double", "def double: . * 2;");
        var resolver = new JqFileResolver(_tempDir);
        Assert.Equal(["10"], EvaluateToStrings("""include "double"; double""", "5", resolver));
    }

    [Fact]
    public void Include_exposes_multiple_module_functions()
    {
        WriteModule("math", "def double: . * 2; def triple: . * 3;");
        var resolver = new JqFileResolver(_tempDir);
        Assert.Equal(["[10,15]"], EvaluateToStrings("""include "math"; [double, triple]""", "5", resolver));
    }

    [Fact]
    public void Include_jq_extension_added_automatically()
    {
        WriteModule("utils", "def inc: . + 1;");
        var resolver = new JqFileResolver(_tempDir);
        // Path without .jq extension — should be added automatically
        Assert.Equal(["6"], EvaluateToStrings("""include "utils"; inc""", "5", resolver));
    }

    [Fact]
    public void Include_with_metadata_object_is_skipped_and_works()
    {
        WriteModule("helpers", "def negate: -. ;");
        var resolver = new JqFileResolver(_tempDir);
        // Metadata object is parsed but ignored for now
        Assert.Equal(["-5"], EvaluateToStrings("""include "helpers" {"origin": "."}; negate""", "5", resolver));
    }

    [Fact]
    public void Include_is_cached_on_repeated_parse()
    {
        var readCount = 0;
        WriteModule("counted", "def counted: . + 1;");
        var resolver = new CountingResolver(new JqFileResolver(_tempDir), () => readCount++);
        EvaluateToStrings("""include "counted"; include "counted"; counted""", "5", resolver);
        Assert.Equal(1, readCount);
    }

    [Fact]
    public void Include_nested_modules()
    {
        WriteModule("inner", "def inner: . + 10;");
        WriteModule("outer", """include "inner"; def outer: inner | . * 2;""");
        var resolver = new JqFileResolver(_tempDir);
        Assert.Equal(["30"], EvaluateToStrings("""include "outer"; outer""", "5", resolver));
    }

    [Fact]
    public void Include_missing_module_throws()
    {
        var resolver = new JqFileResolver(_tempDir);
        Assert.Throws<JqException>(() =>
            EvaluateToStrings("""include "nonexistent"; .""", "null", resolver));
    }

    [Fact]
    public void Include_without_resolver_throws()
    {
        Assert.Throws<JqException>(() =>
            EvaluateToStrings("""include "something"; .""", "null", resolver: null));
    }

    [Fact]
    public void Include_module_functions_can_use_each_other()
    {
        WriteModule("funcs", "def addtwo: . + 2; def addtwo_twice: addtwo | addtwo;");
        var resolver = new JqFileResolver(_tempDir);
        Assert.Equal(["9"], EvaluateToStrings("""include "funcs"; addtwo_twice""", "5", resolver));
    }

    [Fact]
    public void Include_module_with_parameterized_function()
    {
        WriteModule("pf", "def add($n): . + $n;");
        var resolver = new JqFileResolver(_tempDir);
        Assert.Equal(["8"], EvaluateToStrings("""include "pf"; add(3)""", "5", resolver));
    }

    [Fact]
    public void Include_module_with_extension_path()
    {
        WriteModule("ext", "def ext: . + 100;");
        var resolver = new JqFileResolver(_tempDir);
        // Explicitly specifying .jq extension should also work
        Assert.Equal(["105"], EvaluateToStrings("""include "ext.jq"; ext""", "5", resolver));
    }

    [Fact]
    public void Import_exposes_prefixed_function()
    {
        WriteModule("double", "def double: . * 2;");
        var resolver = new JqFileResolver(_tempDir);
        Assert.Equal(["10"], EvaluateToStrings("""import "double" as m; m::double""", "5", resolver));
    }

    [Fact]
    public void Import_exposes_multiple_prefixed_functions()
    {
        WriteModule("math", "def double: . * 2; def triple: . * 3;");
        var resolver = new JqFileResolver(_tempDir);
        Assert.Equal(["[10,15]"], EvaluateToStrings("""import "math" as m; [m::double, m::triple]""", "5", resolver));
    }

    [Fact]
    public void Import_function_not_in_caller_namespace()
    {
        WriteModule("double", "def double: . * 2;");
        var resolver = new JqFileResolver(_tempDir);
        Assert.Throws<JqException>(() =>
            EvaluateToStrings("""import "double" as m; double""", "5", resolver));
    }

    [Fact]
    public void Import_with_metadata_object()
    {
        WriteModule("helpers", "def negate: -. ;");
        var resolver = new JqFileResolver(_tempDir);
        Assert.Equal(["-5"], EvaluateToStrings("""import "helpers" as h {"origin": "."}; h::negate""", "5", resolver));
    }

    [Fact]
    public void Import_module_functions_can_call_each_other()
    {
        WriteModule("funcs", "def addtwo: . + 2; def addtwo_twice: addtwo | addtwo;");
        var resolver = new JqFileResolver(_tempDir);
        Assert.Equal(["9"], EvaluateToStrings("""import "funcs" as m; m::addtwo_twice""", "5", resolver));
    }

    [Fact]
    public void Import_with_parameterized_function()
    {
        WriteModule("pf", "def add($n): . + $n;");
        var resolver = new JqFileResolver(_tempDir);
        Assert.Equal(["8"], EvaluateToStrings("""import "pf" as m; m::add(3)""", "5", resolver));
    }

    [Fact]
    public void Import_cached_on_repeated_import()
    {
        var readCount = 0;
        WriteModule("counted", "def counted: . + 1;");
        var resolver = new CountingResolver(new JqFileResolver(_tempDir), () => readCount++);
        EvaluateToStrings("""import "counted" as c1; import "counted" as c2; c1::counted""", "5", resolver);
        Assert.Equal(1, readCount);
    }

    [Fact]
    public void Import_missing_module_throws()
    {
        var resolver = new JqFileResolver(_tempDir);
        Assert.Throws<JqException>(() =>
            EvaluateToStrings("""import "nonexistent" as m; .""", "null", resolver));
    }

    [Fact]
    public void Import_without_resolver_throws()
    {
        Assert.Throws<JqException>(() =>
            EvaluateToStrings("""import "something" as m; .""", "null", resolver: null));
    }

    [Fact]
    public void Import_two_modules_different_aliases()
    {
        WriteModule("math", "def double: . * 2;");
        WriteModule("str", "def upper: ascii_upcase;");
        var resolver = new JqFileResolver(_tempDir);
        Assert.Equal(["10"], EvaluateToStrings("""import "math" as m; import "str" as s; m::double""", "5", resolver));
    }

    [Fact]
    public void Import_and_include_together()
    {
        WriteModule("bare", "def bare_fn: . + 1;");
        WriteModule("ns", "def ns_fn: . * 10;");
        var resolver = new JqFileResolver(_tempDir);
        Assert.Equal(["[6,50]"], EvaluateToStrings("""include "bare"; import "ns" as n; [bare_fn, n::ns_fn]""", "5", resolver));
    }

    [Fact]
    public void Import_nested_module_with_include()
    {
        WriteModule("inner", "def inner_fn: . + 10;");
        WriteModule("outer", """include "inner"; def outer_fn: inner_fn | . * 2;""");
        var resolver = new JqFileResolver(_tempDir);
        Assert.Equal(["30"], EvaluateToStrings("""import "outer" as o; o::outer_fn""", "5", resolver));
    }

    [Fact]
    public void Import_jq_extension_added_automatically()
    {
        WriteModule("utils", "def inc: . + 1;");
        var resolver = new JqFileResolver(_tempDir);
        Assert.Equal(["6"], EvaluateToStrings("""import "utils" as u; u::inc""", "5", resolver));
    }

    [Fact]
    public void Import_with_explicit_jq_extension()
    {
        WriteModule("ext", "def ext: . + 100;");
        var resolver = new JqFileResolver(_tempDir);
        Assert.Equal(["105"], EvaluateToStrings("""import "ext.jq" as e; e::ext""", "5", resolver));
    }

    [Fact]
    public void Import_json_data_as_variable()
    {
        WriteJsonModule("config", """{"key":"value"}""");
        var resolver = new JqFileResolver(_tempDir);
        Assert.Equal(["""{"key":"value"}"""], EvaluateToStrings("""import "config" as $cfg; $cfg::cfg""", "null", resolver));
    }

    [Fact]
    public void Import_json_data_nested_access()
    {
        WriteJsonModule("config", """{"key":"value"}""");
        var resolver = new JqFileResolver(_tempDir);
        Assert.Equal(["\"value\""], EvaluateToStrings("""import "config" as $cfg; $cfg::cfg.key""", "null", resolver));
    }

    [Fact]
    public void Import_json_data_array()
    {
        WriteJsonModule("data", "[1,2,3]");
        var resolver = new JqFileResolver(_tempDir);
        Assert.Equal(["3"], EvaluateToStrings("""import "data" as $d; $d::d | length""", "null", resolver));
    }

    [Fact]
    public void Import_json_data_scalar()
    {
        WriteJsonModule("num", "42");
        var resolver = new JqFileResolver(_tempDir);
        Assert.Equal(["42"], EvaluateToStrings("""import "num" as $n; $n::n""", "null", resolver));
    }

    [Fact]
    public void Import_json_data_missing_file_throws()
    {
        var resolver = new JqFileResolver(_tempDir);
        Assert.Throws<JqException>(() =>
            EvaluateToStrings("""import "missing" as $m; $m::m""", "null", resolver));
    }

    [Fact]
    public void Import_json_data_invalid_json_throws()
    {
        File.WriteAllText(Path.Combine(_tempDir, "bad.json"), "not valid json {{{");
        var resolver = new JqFileResolver(_tempDir);
        Assert.Throws<JqException>(() =>
            EvaluateToStrings("""import "bad" as $b; $b::b""", "null", resolver));
    }

    [Fact]
    public void Import_json_data_without_resolver_throws()
    {
        Assert.Throws<JqException>(() =>
            EvaluateToStrings("""import "data" as $d; $d::d""", "null", resolver: null));
    }

    [Fact]
    public void Include_resource_resolver_loads_embedded_module()
    {
        // The Tests assembly embeds suite/jq.test but not .jq files.
        // Use a custom in-memory resolver to simulate an assembly resource.
        var resolver = new InMemoryResolver(new Dictionary<string, string>
        {
            ["myns.greet.jq"] = """def greet: "hello " + .;""",
        });
        Assert.Equal(["\"hello world\""], EvaluateToStrings("""include "greet"; greet""", "\"world\"", resolver));
    }

    // Helper resolver that wraps another and counts Resolve() calls.
    sealed class CountingResolver(JqResolver inner, Action onResolve) : JqResolver
    {
        public override TextReader Resolve(string path, string? fromPath)
        {
            onResolve();
            return inner.Resolve(path, fromPath);
        }

        public override string GetCanonicalPath(string path, string? fromPath)
            => inner.GetCanonicalPath(path, fromPath);
    }

    // In-memory resolver keyed by canonical name, used to simulate JqResourceResolver.
    sealed class InMemoryResolver(Dictionary<string, string> modules) : JqResolver
    {
        public override TextReader Resolve(string path, string? fromPath)
        {
            var key = GetCanonicalPath(path, fromPath);
            if (!modules.TryGetValue(key, out var content))
                throw new JqException($"Module not found: '{key}'");
            return new StringReader(content);
        }

        public override string GetCanonicalPath(string path, string? fromPath)
        {
            // Mimic JqResourceResolver: prefix "myns", dots for separators, .jq suffix.
            var basePart = fromPath is not null
                ? fromPath[..fromPath.LastIndexOf('.', fromPath.Length - 4)]
                : "myns";
            var normalized = path.Replace('/', '.').Replace('\\', '.');
            var name = basePart + "." + normalized;
            if (!name.EndsWith(".jq", StringComparison.OrdinalIgnoreCase))
                name += ".jq";
            return name;
        }
    }
}
