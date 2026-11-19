package com.devlooped.jqlite;

import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;

/** Reads the cases of a corpus file (spec/tests/README.md). */
public final class JqTestParser {
    private JqTestParser() {
    }

    public record JqTestCase(String program, String input, List<String> expectedOutputs, boolean shouldFail, String expectedError, int lineNumber) {
    }

    /** The directory spec/tests, found from the working directory up. */
    public static Path specTests() {
        Path directory = Path.of("").toAbsolutePath();
        while (directory != null) {
            Path candidate = directory.resolve("spec").resolve("tests");
            if (Files.isDirectory(candidate)) {
                return candidate;
            }
            directory = directory.getParent();
        }
        throw new IllegalStateException("spec/tests not found");
    }

    public static List<JqTestCase> parseFile(Path file) {
        String text;
        try {
            text = Files.readString(file, StandardCharsets.UTF_8);
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }

        // Lines end at LF (a CR before it is part of the line ending); a lone CR is part of the line.
        String[] raw = text.split("\n", -1);
        String[] lines = new String[raw.length];
        for (int i = 0; i < raw.length; i++) {
            lines[i] = raw[i].endsWith("\r") ? raw[i].substring(0, raw[i].length() - 1) : raw[i];
        }

        List<JqTestCase> cases = new ArrayList<>();
        int index = 0;
        while (index < lines.length) {
            if (isSeparator(lines[index])) {
                index++;
                continue;
            }

            if (isFailMarker(lines[index])) {
                // "%%FAIL IGNORE MSG": the program must not parse, whatever the message.
                boolean ignoreMessage = lines[index].contains("IGNORE MSG");
                index++;
                while (index < lines.length && isSeparator(lines[index])) {
                    index++;
                }
                if (index >= lines.length) {
                    break;
                }

                String program = lines[index];
                int lineNumber = index + 1;
                index++;
                List<String> errorLines = new ArrayList<>();
                while (index < lines.length && !isSeparator(lines[index]) && !isFailMarker(lines[index])) {
                    errorLines.add(lines[index]);
                    index++;
                }

                cases.add(new JqTestCase(program, "", List.of(), true, ignoreMessage ? null : String.join("\n", errorLines), lineNumber));
                continue;
            }

            String program = lines[index];
            int lineNumber = index + 1;
            index++;
            if (index >= lines.length) {
                throw new IllegalStateException("Missing input line for test program at line " + lineNumber);
            }

            String input = lines[index];
            index++;
            List<String> expected = new ArrayList<>();
            while (index < lines.length && !isSeparator(lines[index]) && !isFailMarker(lines[index])) {
                expected.add(lines[index]);
                index++;
            }

            cases.add(new JqTestCase(program, input, expected, false, null, lineNumber));
        }

        return cases;
    }

    private static boolean isFailMarker(String line) {
        return line.stripLeading().startsWith("%%FAIL");
    }

    private static boolean isSeparator(String line) {
        String trimmed = line.strip();
        return trimmed.isEmpty() || trimmed.startsWith("#");
    }
}
