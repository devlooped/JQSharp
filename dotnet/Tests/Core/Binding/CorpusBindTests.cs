using JQLite.Core.Binding;
using JQLite.Core.Syntax;

namespace JQLite.Tests.Core.Binding;

/// <summary>
/// The conformance corpus as binding tests: every program of spec/tests/*.test binds, and every
/// %%FAIL case fails to parse or to bind, with the fixed message of spec §10.2 it names.
/// </summary>
public class CorpusBindTests
{
    static readonly string SpecDirectory = Path.Combine(AppContext.BaseDirectory, "spec", "tests");

    // The module files of modules.test (spec/tests/README.md).
    static readonly DirectoryModules Modules = new(Path.Combine(SpecDirectory, "modules"));

    public static IEnumerable<object[]> Files()
        => Directory.GetFiles(SpecDirectory, "*.test").Order(StringComparer.Ordinal).Select(file => new object[] { Path.GetFileName(file) });

    [Theory]
    [MemberData(nameof(Files))]
    public void Every_program_of_the_corpus_binds(string file)
    {
        var failures = new List<string>();
        foreach (var test in JqTestParser.ParseFile(Path.Combine(SpecDirectory, file)))
        {
            if (!Parser.TryParse(test.Program, out var program, out var error))
            {
                if (!test.ShouldFail)
                    failures.Add($"{file}:{test.LineNumber}: '{test.Program}' does not parse: {error.Message}");
                continue;
            }

            var binds = Resolver.TryBind(program, out _, out error, modules: Modules);
            if (test.ShouldFail && binds)
                failures.Add($"{file}:{test.LineNumber}: '{test.Program}' binds but is a %%FAIL case");
            else if (!test.ShouldFail && !binds)
                failures.Add($"{file}:{test.LineNumber}: '{test.Program}': {error!.Message} at {error.Line}:{error.Column}");
            else if (test.ShouldFail && test.ExpectedError is { } expected && !error!.Message.Contains(expected))
                failures.Add($"{file}:{test.LineNumber}: '{test.Program}': '{error.Message}' does not contain '{expected}'");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Some_failure_of_the_corpus_is_a_binding_error()
    {
        var bindingFailures = Directory.GetFiles(SpecDirectory, "*.test")
            .SelectMany(JqTestParser.ParseFile)
            .Where(test => test.ShouldFail && Parser.TryParse(test.Program, out _, out _))
            .Select(test => test.Program);
        Assert.Contains(". as $foo | break $foo", bindingFailures);
    }
}