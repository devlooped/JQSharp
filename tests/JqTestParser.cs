namespace Devlooped.Tests;

public record JqTestCase(
    string Program,
    string Input,
    string[] ExpectedOutputs,
    bool ShouldFail,
    string? ExpectedError,
    int LineNumber);

public static class JqTestParser
{
    public static IEnumerable<JqTestCase> ParseFile(string filePath)
    {
        if (filePath is null)
            throw new ArgumentNullException(nameof(filePath), "File path cannot be null.");

        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be empty or whitespace.", nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Test file was not found: '{filePath}'.", filePath);

        var lines = File.ReadAllLines(filePath);
        var cases = new List<JqTestCase>();
        var index = 0;

        while (index < lines.Length)
        {
            if (IsSeparator(lines[index]))
            {
                index++;
                continue;
            }

            if (lines[index].Trim() == "%%FAIL")
            {
                index++;
                SkipSeparators(lines, ref index);

                if (index >= lines.Length)
                    break;

                var program = lines[index];
                var lineNumber = index + 1;
                index++;

                var errorLines = new List<string>();
                while (index < lines.Length)
                {
                    if (IsSeparator(lines[index]) || lines[index].Trim() == "%%FAIL")
                        break;

                    errorLines.Add(lines[index]);
                    index++;
                }

                cases.Add(new JqTestCase(
                    program,
                    string.Empty,
                    Array.Empty<string>(),
                    true,
                    string.Join('\n', errorLines),
                    lineNumber));

                continue;
            }

            var normalProgram = lines[index];
            var normalLineNumber = index + 1;
            index++;

            if (index >= lines.Length)
                throw new InvalidDataException($"Missing input line for test program at line {normalLineNumber}.");

            var input = lines[index];
            index++;

            var expectedOutputs = new List<string>();
            while (index < lines.Length)
            {
                if (IsSeparator(lines[index]) || lines[index].Trim() == "%%FAIL")
                    break;

                expectedOutputs.Add(lines[index]);
                index++;
            }

            cases.Add(new JqTestCase(
                normalProgram,
                input,
                expectedOutputs.ToArray(),
                false,
                null,
                normalLineNumber));
        }

        return cases;
    }

    static void SkipSeparators(string[] lines, ref int index)
    {
        while (index < lines.Length && IsSeparator(lines[index]))
            index++;
    }

    static bool IsSeparator(string line)
    {
        var trimmed = line.Trim();
        return string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#');
    }
}
