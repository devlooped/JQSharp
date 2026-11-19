package com.devlooped.jqlite.core.syntax;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.devlooped.jqlite.JqTestParser;
import com.devlooped.jqlite.syntax.Ast;
import com.devlooped.jqlite.syntax.Parser;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.stream.Stream;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

/**
 * The conformance corpus and the prelude as parser tests: every program of spec/tests/*.test parses,
 * except the %%FAIL cases that fail for a syntax reason; spec/prelude.jq parses as a module.
 */
class CorpusParseTests {
    static final Path SPEC = JqTestParser.specTests();

    // %%FAIL cases whose program is syntactically valid: they fail when names are bound.
    static final Set<String> BINDING_FAILURES = Set.of(". as $foo | break $foo", "input", "[inputs]");

    static Stream<String> files() throws IOException {
        try (Stream<Path> files = Files.list(SPEC)) {
            return files.map(f -> f.getFileName().toString()).filter(f -> f.endsWith(".test")).sorted().toList().stream();
        }
    }

    @ParameterizedTest
    @MethodSource("files")
    void everyProgramOfTheCorpusParses(String file) {
        List<String> failures = new ArrayList<>();
        for (JqTestParser.JqTestCase test : JqTestParser.parseFile(SPEC.resolve(file))) {
            Parser.Parsed parsed = Parser.tryParse(test.program());
            boolean parses = parsed.program() != null;
            // The failures of modules.test are all binding errors: missing modules, cycles, names.
            if (test.shouldFail() && !BINDING_FAILURES.contains(test.program()) && !file.equals("modules.test")) {
                if (parses) {
                    failures.add(file + ":" + test.lineNumber() + ": '" + test.program() + "' parses but is a %%FAIL case");
                }
            } else if (!parses) {
                failures.add(file + ":" + test.lineNumber() + ": '" + test.program() + "': " + parsed.error());
            }
        }

        assertTrue(failures.isEmpty(), String.join("\n", failures));
    }

    @Test
    void theBindingFailuresAreInTheCorpus() throws IOException {
        Set<String> programs = new HashSet<>();
        for (String file : files().toList()) {
            for (JqTestParser.JqTestCase test : JqTestParser.parseFile(SPEC.resolve(file))) {
                if (test.shouldFail()) {
                    programs.add(test.program());
                }
            }
        }
        assertTrue(programs.containsAll(BINDING_FAILURES));
    }

    @Test
    void thePreludeParsesAsAModule() throws IOException {
        String source = Files.readString(SPEC.getParent().resolve("prelude.jq"), StandardCharsets.UTF_8);
        Parser.Parsed parsed = Parser.tryParseModule(source);
        assertNotNull(parsed.program(), String.valueOf(parsed.error()));
        assertNull(parsed.program().body());
        assertEquals(102, parsed.program().definitions().size());
        assertTrue(parsed.program().definitions().stream().anyMatch(d -> d.name().equals("_modify") && d.params().size() == 2));

        // And as a program of definitions only, whose body is '.'.
        parsed = Parser.tryParse(source);
        assertNotNull(parsed.program());
        assertInstanceOf(Ast.Identity.class, parsed.program().body());
    }
}
