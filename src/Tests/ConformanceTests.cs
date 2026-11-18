using System.Text.Json;
using Devlooped;

namespace Devlooped.Tests;

/// <summary>
/// Runs every case of the jqlite conformance corpus (spec/tests/*.test) through <see cref="Jq"/>.
/// Cases listed in spec/tests/pending.txt are known failures of the current engine: such a case
/// must fail, and the run fails as soon as it passes, so the list only ever shrinks.
/// </summary>
public class ConformanceTests
{
    static readonly string SpecDirectory = Path.Combine(AppContext.BaseDirectory, "spec", "tests");

    // localtime.test needs a host fixed at UTC+02:00 without DST, and the current engine always
    // uses the time zone of the machine. Step 10 of the jqlite plan enables it through JqOptions.TimeProvider.
    static readonly HashSet<string> SkippedFiles = ["localtime.test"];

    static readonly Lazy<HashSet<string>> Pending = new(LoadPending);

    public static IEnumerable<object?[]> Cases()
    {
        foreach (var file in Directory.GetFiles(SpecDirectory, "*.test").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (SkippedFiles.Contains(name))
                continue;

            foreach (var test in JqTestParser.ParseFile(file))
                yield return [name, test.LineNumber, test.Program, test.Input, string.Join('\n', test.ExpectedOutputs), test.ShouldFail, test.ExpectedError];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Corpus_case(string file, int line, string program, string input, string expected, bool shouldFail, string? expectedError)
    {
        var failure = Run(program, input, expected, shouldFail, expectedError);
        var pending = Pending.Value.Contains(Key(file, program, input));

        if (pending)
            Assert.True(failure is not null, $"{file}:{line} passes but is listed in pending.txt: remove it from the list.");
        else
            Assert.True(failure is null, $"{file}:{line}: {failure}");
    }

    [Fact]
    public void Pending_cases_exist_in_corpus()
    {
        var keys = Directory.GetFiles(SpecDirectory, "*.test")
            .SelectMany(file => JqTestParser.ParseFile(file).Select(test => Key(Path.GetFileName(file), test.Program, test.Input)))
            .ToHashSet();

        var unknown = Pending.Value.Where(key => !keys.Contains(key)).ToArray();
        Assert.True(unknown.Length == 0, "pending.txt lists cases that are not in the corpus:\n" + string.Join('\n', unknown));
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
            catch (JqException e) when (string.IsNullOrEmpty(expectedError) || e.Message.Contains(expectedError, StringComparison.Ordinal))
            {
                return null;
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

        var outputs = expected.Length == 0 ? [] : expected.Split('\n');
        if (actual.Length != outputs.Length || !outputs.Zip(actual).All(static pair => JsonEquals(pair.First, pair.Second)))
            return $"expected [{string.Join(", ", outputs)}] but got [{string.Join(", ", actual)}]";

        return null;
    }

    /// <summary>Structural equality: numbers as doubles, strings by value, arrays in order, objects with the same keys in the same order.</summary>
    static bool JsonEquals(string expected, string actual)
    {
        using var left = JsonDocument.Parse(expected);
        using var right = JsonDocument.Parse(actual);
        return JsonEquals(left.RootElement, right.RootElement);
    }

    static bool JsonEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
            return false;

        switch (left.ValueKind)
        {
            case JsonValueKind.Number:
                return left.GetDouble().Equals(right.GetDouble());
            case JsonValueKind.String:
                return left.GetString() == right.GetString();
            case JsonValueKind.Array:
                return left.GetArrayLength() == right.GetArrayLength()
                    && left.EnumerateArray().Zip(right.EnumerateArray()).All(static pair => JsonEquals(pair.First, pair.Second));
            case JsonValueKind.Object:
                var leftProperties = left.EnumerateObject().ToArray();
                var rightProperties = right.EnumerateObject().ToArray();
                return leftProperties.Length == rightProperties.Length
                    && leftProperties.Zip(rightProperties).All(static pair => pair.First.Name == pair.Second.Name && JsonEquals(pair.First.Value, pair.Second.Value));
            default:
                return true;
        }
    }

    // A %%FAIL case has no input line: its key is the file name and the program.
    static string Key(string file, string program, string input)
        => input.Length == 0 ? $"{file}\t{program}" : $"{file}\t{program}\t{input}";

    static HashSet<string> LoadPending()
        => File.ReadLines(Path.Combine(SpecDirectory, "pending.txt"))
            .Where(static line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);
}
