package com.devlooped.jqlite;

import com.devlooped.jqlite.binding.Resolver;
import com.devlooped.jqlite.runtime.Evaluation.EvaluationOptions;
import com.devlooped.jqlite.runtime.Evaluation.IClock;
import com.devlooped.jqlite.runtime.Evaluation.Outcome;
import com.devlooped.jqlite.runtime.Evaluation.OutcomeKind;
import com.devlooped.jqlite.runtime.Interpreter;
import com.devlooped.jqlite.values.JqKind;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;
import com.devlooped.jqlite.values.ValueText;
import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.regex.Pattern;
import java.util.stream.Stream;
import org.junit.jupiter.api.Assertions;
import org.junit.jupiter.api.DynamicTest;

/** The parts of the conformance runner shared by the two ways the corpus runs (spec/tests/README.md). */
final class CoreConformanceTests {
    private CoreConformanceTests() {
    }

    static final Path SPEC = JqTestParser.specTests();
    static final DirectoryModules CORPUS_MODULES = new DirectoryModules(SPEC.resolve("modules"));

    // The message lines of a %%FAIL case that are fixed texts of spec §10.2 must be contained in
    // the error; the other lines are jq's own wording and are not compared (spec §14.1).
    private static final Pattern FIXED_TEXT = Pattern.compile(
        "^(\\S+/\\d+ is not defined|\\$\\*label-\\S+ is not defined|\\$\\S+::\\S+ is not defined|Cannot use .+ as object key|May need parentheses around object key expression|Module metadata must be constant|Module metadata must be an object|Import path must be constant|Invalid escape)$");

    /** UTC+02:00 without DST or abbreviation. */
    static final IClock FIXED_ZONE = new IClock() {
        @Override
        public double now() {
            return System.currentTimeMillis() / 1000.0;
        }

        @Override
        public int offset(double seconds) {
            return 7200;
        }

        @Override
        public String abbreviation(double seconds) {
            return null;
        }
    };

    /** Runs a case: Java null when it passes, else what failed. */
    @FunctionalInterface
    interface CaseRunner {
        String run(JqTestParser.JqTestCase test);
    }

    static Stream<DynamicTest> cases(CaseRunner runner) {
        List<DynamicTest> tests = new ArrayList<>();
        try (Stream<Path> files = Files.list(SPEC)) {
            for (Path file : files.filter(f -> f.toString().endsWith(".test")).sorted().toList()) {
                for (JqTestParser.JqTestCase test : JqTestParser.parseFile(file)) {
                    String name = file.getFileName() + ":" + test.lineNumber();
                    tests.add(DynamicTest.dynamicTest(name, () -> {
                        String failure;
                        try {
                            failure = runner.run(test);
                        } catch (RuntimeException | StackOverflowError e) {
                            throw new AssertionError(name + ": " + test.program() + " => " + e, e);
                        }
                        Assertions.assertNull(failure, () -> name + ": " + test.program() + " => " + failure);
                    }));
                }
            }
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
        return tests.stream();
    }

    /** The core without the native overrides of prelude builtins. */
    static String runCore(JqTestParser.JqTestCase test) {
        Resolver.Compiled compiled = Resolver.tryCompile(test.program(), false, CORPUS_MODULES);
        if (compiled.program() == null) {
            return test.shouldFail() ? missingFixedText(test.expectedError(), compiled.error().message())
                : compiled.error().message() + " at " + compiled.error().line() + ":" + compiled.error().column();
        }
        if (test.shouldFail()) {
            return "the program parses and binds but must not";
        }

        Result input = ValueText.tryParse(test.input());
        if (input instanceof Result.Err err) {
            return "bad input: " + err.error();
        }
        List<String> outputs = new ArrayList<>();
        EvaluationOptions options = new EvaluationOptions();
        options.clock = FIXED_ZONE;
        Outcome outcome = Interpreter.run(compiled.program(), (JqValue) input, options, output -> {
            outputs.add(ValueText.toJson(output));
            return true;
        });

        if (outcome.kind() == OutcomeKind.ERROR) {
            return "error " + ValueText.toJson(outcome.value()) + " after " + outputs;
        }
        if (outcome.kind() == OutcomeKind.HALT && (outcome.hasValue() || outcome.code() != 0)) {
            return "halt_error " + outcome.code() + " after " + outputs;
        }
        return compare(test.expectedOutputs(), outputs);
    }

    // The outputs, as text, against the expected lines: each read back and compared structurally (spec §14.2).
    static String compare(List<String> expected, List<String> actual) {
        boolean same = expected.size() == actual.size();
        for (int i = 0; same && i < expected.size(); i++) {
            same = textEquals(expected.get(i), actual.get(i));
        }
        return same ? null : "expected " + expected + " but got " + actual;
    }

    private static boolean textEquals(String expected, String actual) {
        Result left = ValueText.tryParse(expected);
        Result right = ValueText.tryParse(actual);
        return left instanceof JqValue a && right instanceof JqValue b && same(a, b);
    }

    // Numbers as doubles (-0 equals 0), strings by code points, arrays in order, objects with the
    // same keys in the same order.
    static boolean same(JqValue left, JqValue right) {
        if (left.kind() != right.kind()) {
            return false;
        }
        switch (left.kind()) {
            case NUMBER:
                return left.asNumber() == right.asNumber();
            case STRING:
                return left.asString().equals(right.asString());
            case ARRAY: {
                var a = left.asArray();
                var b = right.asArray();
                if (a.count() != b.count()) {
                    return false;
                }
                for (int i = 0; i < a.count(); i++) {
                    if (!same(a.get(i), b.get(i))) {
                        return false;
                    }
                }
                return true;
            }
            case OBJECT: {
                var x = left.asObject();
                var y = right.asObject();
                if (x.count() != y.count()) {
                    return false;
                }
                for (int i = 0; i < x.count(); i++) {
                    if (!x.keyAt(i).equals(y.keyAt(i)) || !same(x.valueAt(i), y.valueAt(i))) {
                        return false;
                    }
                }
                return true;
            }
            default:
                return left.kind() == JqKind.NULL || left.kind() == right.kind();
        }
    }

    static String missingFixedText(String expectedError, String message) {
        for (String line : (expectedError == null ? "" : expectedError).split("\n", -1)) {
            if (FIXED_TEXT.matcher(line).matches() && !message.contains(line)) {
                return "expected an error containing '" + line + "', got '" + message + "'";
            }
        }

        return null;
    }
}
