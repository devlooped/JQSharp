using JQLite.Core.Syntax;

namespace JQLite.Tests.Core.Syntax;

/// <summary>
/// The conformance corpus and the prelude as parser tests: every program of spec/tests/*.test
/// parses, except the %%FAIL cases that fail for a syntax reason; spec/prelude.jq parses as a module.
/// </summary>
public class CorpusParseTests
{
    static readonly string SpecDirectory = Path.Combine(AppContext.BaseDirectory, "spec", "tests");

    // %%FAIL cases whose program is syntactically valid: they fail when names are bound (step 5).
    static readonly HashSet<string> BindingFailures =
    [
        ". as $foo | break $foo",
        "input",
        "[inputs]",
    ];

    public static IEnumerable<object[]> Files()
        => Directory.GetFiles(SpecDirectory, "*.test").Order(StringComparer.Ordinal).Select(file => new object[] { Path.GetFileName(file) });

    [Theory]
    [MemberData(nameof(Files))]
    public void Every_program_of_the_corpus_parses(string file)
    {
        var failures = new List<string>();
        foreach (var test in JqTestParser.ParseFile(Path.Combine(SpecDirectory, file)))
        {
            var parses = Parser.TryParse(test.Program, out _, out var error);
            // The failures of modules.test are all binding errors: missing modules, cycles, names.
            if (test.ShouldFail && !BindingFailures.Contains(test.Program) && file != "modules.test")
            {
                if (parses)
                    failures.Add($"{file}:{test.LineNumber}: '{test.Program}' parses but is a %%FAIL case");
            }
            else if (!parses)
            {
                failures.Add($"{file}:{test.LineNumber}: '{test.Program}': {error!.Message} at {error.Line}:{error.Column}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void The_binding_failures_are_in_the_corpus()
    {
        var programs = Directory.GetFiles(SpecDirectory, "*.test")
            .SelectMany(JqTestParser.ParseFile)
            .Where(test => test.ShouldFail)
            .Select(test => test.Program)
            .ToHashSet();
        Assert.Subset(programs, BindingFailures);
    }

    [Fact]
    public void The_prelude_parses_as_a_module()
    {
        var source = File.ReadAllText(FindPrelude());
        Assert.True(Parser.TryParseModule(source, out var program, out var error), error is null ? null : $"{error.Message} at {error.Line}:{error.Column}");
        Assert.Null(program.Body);
        Assert.Equal(102, program.Definitions.Length);
        Assert.Contains(program.Definitions, definition => definition.Name == "_modify" && definition.Params.Length == 2);

        // And as a program of definitions only, whose body is '.'.
        Assert.True(Parser.TryParse(source, out program, out _));
        Assert.IsType<Identity>(program.Body);
    }

    static string FindPrelude()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "spec", "prelude.jq");
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException("spec/prelude.jq not found above " + AppContext.BaseDirectory);
    }
}