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
