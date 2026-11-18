using System.Text.Json;
using System.Text.RegularExpressions;
using Devlooped;
using Devlooped.Core.Binding;
using Devlooped.Core.Values;
using EvaluationOptions = Devlooped.Core.Runtime.EvaluationOptions;
using IClock = Devlooped.Core.Runtime.IClock;
using Interpreter = Devlooped.Core.Runtime.Interpreter;
using OutcomeKind = Devlooped.Core.Runtime.OutcomeKind;

namespace Devlooped.Tests;

/// <summary>
/// Runs every case of the jqlite conformance corpus (spec/tests/*.test) twice, by the rules of
/// spec/tests/README.md: through <see cref="Jq"/> (the current engine) and through the new core
/// (parse, bind, evaluate on <see cref="JqValue"/>). The core must pass every case. Cases listed
/// in spec/tests/pending.txt are known failures of the current engine: such a case must fail, and
/// the run fails as soon as it passes, so the list only ever shrinks.
/// </summary>
public partial class ConformanceTests
{
    static readonly string SpecDirectory = Path.Combine(AppContext.BaseDirectory, "spec", "tests");

    // localtime.test needs a host fixed at UTC+02:00 without DST (spec §14.2): the core gets it
    // from its clock, the current engine always uses the time zone of the machine (step 10
    // enables it through JqOptions.TimeProvider), so it skips the file.
    static readonly HashSet<string> SkippedByCurrentEngine = ["localtime.test"];

    // The module files of modules.test, for the core (spec §14.2).
    static readonly DirectoryModules CorpusModules = new(Path.Combine(SpecDirectory, "modules"));

    static readonly Lazy<HashSet<string>> Pending = new(() => LoadPending("pending.txt"));

    public static IEnumerable<object?[]> Cases(bool core)
    {
        foreach (var file in Directory.GetFiles(SpecDirectory, "*.test").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (!core && SkippedByCurrentEngine.Contains(name))
                continue;

            foreach (var test in JqTestParser.ParseFile(file))
                yield return [name, test.LineNumber, test.Program, test.Input, string.Join('\n', test.ExpectedOutputs), test.ShouldFail, test.ExpectedError];
        }
    }

    [Theory]
    [MemberData(nameof(Cases), false)]
    public void Corpus_case(string file, int line, string program, string input, string expected, bool shouldFail, string? expectedError)
    {
        // The current engine's resolvers do not follow the module rule of README.md; its module
        // tests stay in JqModuleTests until step 10.
        if (file == "modules.test")
            return;
        Check("pending.txt", Pending.Value, file, line, program, input, Run(program, input, expected, shouldFail, expectedError));
    }

    [Theory]
    [MemberData(nameof(Cases), true)]
    public void Core_corpus_case(string file, int line, string program, string input, string expected, bool shouldFail, string? expectedError)
        => Pass(file, line, RunCore(program, input, expected, shouldFail, expectedError, nativeOverrides: true));

    // The same with every prelude builtin bound to its definition: the natives that override some
    // (Native.OverridesPrelude) must change nothing.
    [Theory]
    [MemberData(nameof(Cases), true)]
    public void Core_corpus_case_without_native_overrides(string file, int line, string program, string input, string expected, bool shouldFail, string? expectedError)
        => Pass(file, line, RunCore(program, input, expected, shouldFail, expectedError, nativeOverrides: false));

    static void Pass(string file, int line, string? failure) => Assert.True(failure is null, $"{file}:{line}: {failure}");

    static void Check(string list, HashSet<string> pending, string file, int line, string program, string input, string? failure)
    {
        if (pending.Contains(Key(file, program, input)))
            Assert.True(failure is not null, $"{file}:{line} passes but is listed in {list}: remove it from the list.");
        else
            Assert.True(failure is null, $"{file}:{line}: {failure}");
    }

    [Fact]
    public void Pending_cases_exist_in_corpus()
    {
        const string list = "pending.txt";
        var keys = Directory.GetFiles(SpecDirectory, "*.test")
            .SelectMany(file => JqTestParser.ParseFile(file).Select(test => Key(Path.GetFileName(file), test.Program, test.Input)))
            .ToHashSet();

        var unknown = LoadPending(list).Where(key => !keys.Contains(key)).ToArray();
        Assert.True(unknown.Length == 0, $"{list} lists cases that are not in the corpus:\n" + string.Join('\n', unknown));
    }

    static string? Run(string program, string input, string expected, bool shouldFail, string? expectedError)
    {
        if (shouldFail)
        {
            try
            {
                Jq.Parse(program);
                return "the program parses but must not";
            }
            catch (JqException e)
            {
                return MissingFixedText(expectedError, e.Message);
            }
            catch (Exception e)
            {
                return $"expected a parse error '{expectedError}', got {e.GetType().Name}: {e.Message}";
            }
        }

        string[] actual;
        try
        {
            using var document = JsonDocument.Parse(input);
            actual = [.. Jq.Evaluate(program, document.RootElement).Select(static e => e.GetRawText())];
        }
        catch (Exception e)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }

        return Compare(expected, actual);
    }

    static string? RunCore(string program, string input, string expected, bool shouldFail, string? expectedError, bool nativeOverrides)
    {
        if (!Resolver.TryCompile(program, out var bound, out var error, nativeOverrides, CorpusModules))
            return shouldFail ? MissingFixedText(expectedError, error.Message) : $"{error.Message} at {error.Line}:{error.Column}";
        if (shouldFail)
            return "the program parses and binds but must not";

        if (!ValueText.TryParse(input, out var value, out var inputError))
            return $"bad input: {inputError}";
        var outputs = new List<string>();
        var options = new EvaluationOptions { Environment = JqValue.FromObject(JqObject.Empty), MaxDepth = 1000, Clock = FixedZoneClock.Instance };
        var outcome = Interpreter.Run(bound, value, options, output =>
        {
            outputs.Add(ValueText.ToJson(output));
            return true;
        });

        if (outcome.Kind == OutcomeKind.Error)
            return $"error {ValueText.ToJson(outcome.Value)} after [{string.Join(", ", outputs)}]";
        if (outcome.Kind == OutcomeKind.Halt && (outcome.HasValue || outcome.Code != 0))
            return $"halt_error {outcome.Code} after [{string.Join(", ", outputs)}]";
        return Compare(expected, [.. outputs]);
    }

    // The outputs, as canonical text, against the expected lines: each read back and compared
    // structurally (spec §14.2).
    static string? Compare(string expected, string[] actual)
    {
        var outputs = expected.Length == 0 ? [] : expected.Split('\n');
        if (actual.Length != outputs.Length || !outputs.Zip(actual).All(static pair => TextEquals(pair.First, pair.Second)))
            return $"expected [{string.Join(", ", outputs)}] but got [{string.Join(", ", actual)}]";
        return null;
    }

    static bool TextEquals(string expected, string actual)
        => ValueText.TryParse(expected, out var left, out _) && ValueText.TryParse(actual, out var right, out _) && Same(left, right);

    // Numbers as doubles (-0 equals 0), strings by code points, arrays in order, objects with the
    // same keys in the same order.
    static bool Same(JqValue left, JqValue right)
    {
        if (left.Kind != right.Kind)
            return false;
        switch (left.Kind)
        {
            case JqKind.Number:
                return left.AsNumber() == right.AsNumber();
            case JqKind.String:
                return left.AsString() == right.AsString();
            case JqKind.Array:
                var a = left.AsArray();
                var b = right.AsArray();
                return a.Count == b.Count && Enumerable.Range(0, a.Count).All(i => Same(a[i], b[i]));
            case JqKind.Object:
                var x = left.AsObject();
                var y = right.AsObject();
                return x.Count == y.Count && Enumerable.Range(0, x.Count).All(i => x.KeyAt(i) == y.KeyAt(i) && Same(x.ValueAt(i), y.ValueAt(i)));
            default:
                return true;
        }
    }

    // The message lines of a %%FAIL case that are fixed texts of spec §10.2 must be contained in
    // the error; the other lines are jq's own wording and are not compared (spec §14.1).
    static string? MissingFixedText(string? expectedError, string message)
    {
        foreach (var line in (expectedError ?? "").Split('\n'))
        {
            if (FixedText().IsMatch(line) && !message.Contains(line, StringComparison.Ordinal))
                return $"expected an error containing '{line}', got '{message}'";
        }

        return null;
    }

    [GeneratedRegex(@"^(\S+/\d+ is not defined|\$\*label-\S+ is not defined|\$\S+::\S+ is not defined|Cannot use .+ as object key|May need parentheses around object key expression|Module metadata must be constant|Module metadata must be an object|Import path must be constant|Invalid escape)$")]
    private static partial Regex FixedText();

    // A %%FAIL case has no input line: its key is the file name and the program.
    static string Key(string file, string program, string input)
        => input.Length == 0 ? $"{file}\t{program}" : $"{file}\t{program}\t{input}";

    // The host of spec §14.2: the real time, and a zone at UTC+02:00 without DST or abbreviation.
    sealed class FixedZoneClock : IClock
    {
        public static readonly FixedZoneClock Instance = new();

        public double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        public int Offset(double seconds) => 7200;

        public string? Abbreviation(double seconds) => null;
    }

    // Lines end at LF, as in the corpus: a program may hold a lone CR.
    static HashSet<string> LoadPending(string list)
        => File.ReadAllText(Path.Combine(SpecDirectory, list)).Split('\n')
            .Select(static line => line.EndsWith('\r') ? line[..^1] : line)
            .Where(static line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);
}