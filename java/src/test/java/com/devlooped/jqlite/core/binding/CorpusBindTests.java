package com.devlooped.jqlite.core.binding;

import static org.junit.jupiter.api.Assertions.assertTrue;

import com.devlooped.jqlite.DirectoryModules;
import com.devlooped.jqlite.JqTestParser;
import com.devlooped.jqlite.binding.Resolver;
import com.devlooped.jqlite.syntax.Parser;
import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.stream.Stream;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

/**
 * The conformance corpus as binding tests: every program of spec/tests/*.test binds, and every
 * %%FAIL case fails to parse or to bind, with the fixed message of spec §10.2 it names.
 */
class CorpusBindTests {
    static final Path SPEC = JqTestParser.specTests();
    static final DirectoryModules MODULES = new DirectoryModules(SPEC.resolve("modules"));

    static Stream<String> files() throws IOException {
        try (Stream<Path> files = Files.list(SPEC)) {
            return files.map(f -> f.getFileName().toString()).filter(f -> f.endsWith(".test")).sorted().toList().stream();
        }
    }

    @ParameterizedTest
    @MethodSource("files")
    void everyProgramOfTheCorpusBinds(String file) {
        List<String> failures = new ArrayList<>();
        for (JqTestParser.JqTestCase test : JqTestParser.parseFile(SPEC.resolve(file))) {
            Parser.Parsed parsed = Parser.tryParse(test.program());
            if (parsed.program() == null) {
                if (!test.shouldFail()) {
                    failures.add(file + ":" + test.lineNumber() + ": '" + test.program() + "' does not parse: " + parsed.error().message());
                }
                continue;
            }

            Resolver.Compiled bound = Resolver.tryBind(parsed.program(), true, MODULES);
            boolean binds = bound.program() != null;
            if (test.shouldFail() && binds) {
                failures.add(file + ":" + test.lineNumber() + ": '" + test.program() + "' binds but is a %%FAIL case");
            } else if (!test.shouldFail() && !binds) {
                failures.add(file + ":" + test.lineNumber() + ": '" + test.program() + "': " + bound.error());
            } else if (test.shouldFail() && test.expectedError() != null && !bound.error().message().contains(test.expectedError())) {
                failures.add(file + ":" + test.lineNumber() + ": '" + test.program() + "': '" + bound.error().message() + "' does not contain '" + test.expectedError() + "'");
            }
        }

        assertTrue(failures.isEmpty(), String.join("\n", failures));
    }

    @Test
    void someFailureOfTheCorpusIsABindingError() throws IOException {
        List<String> bindingFailures = new ArrayList<>();
        for (String file : files().toList()) {
            for (JqTestParser.JqTestCase test : JqTestParser.parseFile(SPEC.resolve(file))) {
                if (test.shouldFail() && Parser.tryParse(test.program()).program() != null) {
                    bindingFailures.add(test.program());
                }
            }
        }
        assertTrue(bindingFailures.contains(". as $foo | break $foo"));
    }
}
