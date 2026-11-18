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

        // Lines end at LF (a CR before it is part of the line ending); a lone CR is part of the line.
        var lines = File.ReadAllText(filePath).Split('\n').Select(static line => line.EndsWith('\r') ? line[..^1] : line).ToArray();
        var cases = new List<JqTestCase>();
        var index = 0;

        while (index < lines.Length)
        {
            if (IsSeparator(lines[index]))
            {
                index++;
                continue;
            }

            if (IsFailMarker(lines[index]))
            {
                // "%%FAIL IGNORE MSG": the program must not parse, whatever the message.
                var ignoreMessage = lines[index].Contains("IGNORE MSG", StringComparison.Ordinal);
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
                    if (IsSeparator(lines[index]) || IsFailMarker(lines[index]))
                        break;

                    errorLines.Add(lines[index]);
                    index++;
                }

                cases.Add(new JqTestCase(
                    program,
                    string.Empty,
                    Array.Empty<string>(),
                    true,
                    ignoreMessage ? null : string.Join('\n', errorLines),
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
                if (IsSeparator(lines[index]) || IsFailMarker(lines[index]))
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

    static bool IsFailMarker(string line) => line.TrimStart().StartsWith("%%FAIL", StringComparison.Ordinal);

    static bool IsSeparator(string line)
    {
        var trimmed = line.Trim();
        return string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#');
    }
}
