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
/// Runs every case of the jqlite conformance corpus (spec/tests/*.test), by the rules of
/// spec/tests/README.md: through the public API (<see cref="Jq.Parse"/>, <see cref="JqExpression"/>),
/// and through the core with every prelude builtin bound to its definition, to check that the
/// natives that override some change nothing. Every case must pass both ways.
/// </summary>
public partial class ConformanceTests
{
    static readonly string SpecDirectory = Path.Combine(AppContext.BaseDirectory, "spec", "tests");

    // The host of spec §14.2: no environment variables, a zone at UTC+02:00 without DST or
    // abbreviation, the default depth, and the module files of modules/.
    static readonly JqOptions Options = new()
    {
        Environment = new Dictionary<string, string>(),
        TimeProvider = new FixedZone(),
        MaxDepth = 1000,
        Resolver = new JqFileResolver(Path.Combine(SpecDirectory, "modules")),
    };

    static readonly DirectoryModules CorpusModules = new(Path.Combine(SpecDirectory, "modules"));

    public static IEnumerable<object?[]> Cases()
    {
        foreach (var file in Directory.GetFiles(SpecDirectory, "*.test").Order(StringComparer.Ordinal))
        {
            foreach (var test in JqTestParser.ParseFile(file))
                yield return [Path.GetFileName(file), test.LineNumber, test.Program, test.Input, string.Join('\n', test.ExpectedOutputs), test.ShouldFail, test.ExpectedError];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Corpus_case(string file, int line, string program, string input, string expected, bool shouldFail, string? expectedError)
        => Pass(file, line, Run(program, input, expected, shouldFail, expectedError));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Corpus_case_without_native_overrides(string file, int line, string program, string input, string expected, bool shouldFail, string? expectedError)
        => Pass(file, line, RunCore(program, input, expected, shouldFail, expectedError));

    static void Pass(string file, int line, string? failure) => Assert.True(failure is null, $"{file}:{line}: {failure}");

    static string? Run(string program, string input, string expected, bool shouldFail, string? expectedError)
    {
        JqExpression expression;
        try
        {
            expression = Jq.Parse(program, Options);
        }
        catch (JqParseException e)
        {
            return shouldFail ? MissingFixedText(expectedError, e.Message) : $"{e.Message} at {e.Line}:{e.Column}";
        }

        if (shouldFail)
            return "the program parses and binds but must not";

        using var document = JsonDocument.Parse(input);
        var outputs = new List<string>();
        try
        {
            expression.Evaluate(document.RootElement, result =>
            {
                outputs.Add(result.GetRawText());
                return true;
            });
        }
        catch (JqException e)
        {
            return $"{e.GetType().Name} {e.Message} after [{string.Join(", ", outputs)}]";
        }

        return Compare(expected, [.. outputs]);
    }

    static string? RunCore(string program, string input, string expected, bool shouldFail, string? expectedError)
    {
        if (!Resolver.TryCompile(program, out var bound, out var error, nativeOverrides: false, CorpusModules))
            return shouldFail ? MissingFixedText(expectedError, error.Message) : $"{error.Message} at {error.Line}:{error.Column}";
        if (shouldFail)
            return "the program parses and binds but must not";

        if (!ValueText.TryParse(input, out var value, out var inputError))
            return $"bad input: {inputError}";
        var outputs = new List<string>();
        var options = new EvaluationOptions { MaxDepth = 1000, Clock = FixedZoneClock.Instance };
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

    // The outputs, as text, against the expected lines: each read back and compared structurally (spec §14.2).
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

    // UTC+02:00 without DST or abbreviation, for the public API.
    sealed class FixedZone : TimeProvider
    {
        static readonly TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone("jqlite+02", TimeSpan.FromHours(2), "UTC+02", "UTC+02");

        public override TimeZoneInfo LocalTimeZone => zone;
    }

    // The same, for the core.
    sealed class FixedZoneClock : IClock
    {
        public static readonly FixedZoneClock Instance = new();

        public double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        public int Offset(double seconds) => 7200;

        public string? Abbreviation(double seconds) => null;
    }
}
