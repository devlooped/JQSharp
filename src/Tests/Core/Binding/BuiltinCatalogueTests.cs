using System.Text.RegularExpressions;
using Devlooped.Core.Binding;
using Devlooped.Core.Builtins;

namespace Devlooped.Tests.Core.Binding;

/// <summary>
/// The native catalogue and the embedded prelude against spec §9: the prelude binds completely,
/// and together they are exactly the builtins the spec names, natives where the spec says N and
/// prelude definitions where it says P. The lists are read from the spec, so spec and code cannot drift.
/// </summary>
public partial class BuiltinCatalogueTests
{
    [Fact]
    public void The_embedded_prelude_is_spec_prelude_jq()
    {
        using var stream = typeof(Prelude).Assembly.GetManifestResourceStream(Prelude.ResourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        Assert.Equal(File.ReadAllText(SpecFile("prelude.jq")), reader.ReadToEnd());
    }

    [Fact]
    public void The_prelude_binds_against_the_natives()
    {
        var prelude = Prelude.Instance;
        Assert.Equal(91, prelude.Definitions.Length);
        // Nested definitions (recurse's r, walk's w) are functions of the prelude too.
        Assert.True(prelude.Functions.Length > prelude.Definitions.Length);
        Assert.Equal(prelude.Definitions.Length, prelude.Definitions.Select(Signature).Distinct().Count());
    }

    [Fact]
    public void A_prelude_cannot_use_host_variables()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Prelude.Load("def f: $x;"));
        Assert.Contains("$x is not defined at 1:8", error.Message);
    }

    [Fact]
    public void The_catalogue_has_unique_signatures_and_indices()
    {
        Assert.Equal(Natives.All.Length, Natives.All.Select(Signature).Distinct().Count());
        for (var i = 0; i < Natives.All.Length; i++)
        {
            Assert.Equal(i, Natives.All[i].Index);
            Assert.Same(Natives.All[i], Natives.Find(Natives.All[i].Name, Natives.All[i].Arity));
        }

        Assert.Null(Natives.Find("map", 1));
    }

    [Fact]
    public void Natives_and_prelude_are_the_builtins_of_the_spec()
    {
        var spec = SpecBuiltins();
        var natives = Natives.All.Where(native => !native.OverridesPrelude).Select(Signature).ToHashSet();
        var prelude = Prelude.Instance.Definitions.Select(Signature).ToHashSet();
        // An override implements a prelude builtin natively: it stays a prelude builtin of §9.
        Assert.Subset(prelude, Natives.All.Where(native => native.OverridesPrelude).Select(Signature).ToHashSet());

        Assert.Empty(natives.Intersect(prelude));
        Assert.Equal(spec.All.Order(StringComparer.Ordinal), natives.Union(prelude).Order(StringComparer.Ordinal));
        Assert.Subset(natives, spec.Native);
        Assert.Subset(prelude, spec.Prelude);
        Assert.Equal(spec.Modules.Order(StringComparer.Ordinal), Natives.All.Where(native => native.Group == NativeGroup.Modules).Select(Signature).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Internal_builtins_start_with_an_underscore()
    {
        Assert.Equal(["_group_by_impl/1", "_max_by_impl/1", "_min_by_impl/1", "_sort_by_impl/1", "_strindices/1"],
            Natives.All.Where(native => native.IsInternal && !native.OverridesPrelude).Select(Signature).Order(StringComparer.Ordinal));
        Assert.Equal(["_assign/2", "_flatten/1", "_modify/2"],
            Prelude.Instance.Definitions.Where(function => function.Name.StartsWith('_')).Select(Signature).Order(StringComparer.Ordinal));
    }

    static string Signature(Native native) => $"{native.Name}/{native.Arity}";

    static string Signature(Function function) => $"{function.Name}/{function.Arity}";

    record SpecLists(HashSet<string> All, HashSet<string> Native, HashSet<string> Prelude, HashSet<string> Modules);

    // Section 9 of spec/jqlite.md: every `name/n` in its text; the library functions of §9.8, listed
    // by name with "(one input)"; and the functions §9.8 defines by formula as `name(x):`. Table rows
    // whose K column says N or P give the kind of the signatures of their first cell.
    static SpecLists SpecBuiltins()
    {
        var text = File.ReadAllText(SpecFile("jqlite.md")).Replace("\r\n", "\n");
        var section = Section(text, "## 9. ", "## 10. ");
        var all = Signatures().Matches(section).Select(match => match.Groups[1].Value).ToHashSet();

        var library = LibraryList().Match(section);
        Assert.True(library.Success, "The library functions of §9.8 were not found");
        foreach (var name in library.Groups[1].Value.Split(' '))
            all.Add(name + "/0");
        foreach (Match formula in Formulas().Matches(Section(section, "### 9.8 ", "### 9.9 ")))
            all.Add(formula.Groups[1].Value + "/0");

        var native = new HashSet<string>();
        var prelude = new HashSet<string>();
        foreach (Match row in KindRows().Matches(section))
        {
            var kind = row.Groups[2].Value == "N" ? native : prelude;
            foreach (Match signature in Signatures().Matches(row.Groups[1].Value))
                kind.Add(signature.Groups[1].Value);
        }

        // §9.8 says "All natives" for its section, except abs/0, which it gives as prelude.
        foreach (var signature in Signatures().Matches(Section(section, "### 9.8 ", "### 9.9 ")).Select(match => match.Groups[1].Value)
            .Concat(library.Groups[1].Value.Split(' ').Select(name => name + "/0")))
            (signature == "abs/0" ? prelude : native).Add(signature);

        var modules = Signatures().Matches(Section(section, "### 9.11 ", "### 9.12 ")).Select(match => match.Groups[1].Value).ToHashSet();
        native.UnionWith(modules);
        return new SpecLists(all, native, prelude, modules);
    }

    static string Section(string text, string start, string end)
    {
        var from = text.IndexOf("\n" + start, StringComparison.Ordinal);
        var to = text.IndexOf("\n" + end, from + 1, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, $"Section '{start}' not found in the spec");
        return text[from..to];
    }

    [GeneratedRegex(@"`([A-Za-z_][A-Za-z0-9_]*/[0-9]+)`")]
    private static partial Regex Signatures();

    [GeneratedRegex(@"\*\*Library functions\*\*.*?`([a-z0-9 ]+)` \(one input\)")]
    private static partial Regex LibraryList();

    [GeneratedRegex(@"^\s*([a-z][a-z0-9]*)\(x\):", RegexOptions.Multiline)]
    private static partial Regex Formulas();

    [GeneratedRegex(@"^\| ([^|]*) \| ([NP]) \|", RegexOptions.Multiline)]
    private static partial Regex KindRows();

    static string SpecFile(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "spec", name);
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException($"spec/{name} not found above " + AppContext.BaseDirectory);
    }
}