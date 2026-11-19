package com.devlooped.jqlite;

import com.devlooped.jqlite.values.JqValue;
import java.time.Clock;
import java.time.ZoneOffset;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.stream.Stream;
import org.junit.jupiter.api.DynamicTest;
import org.junit.jupiter.api.TestFactory;

/**
 * Runs every case of the jqlite conformance corpus (spec/tests/*.test), by the rules of
 * spec/tests/README.md: through the public API ({@link Jq#parse(String, JqOptions)},
 * {@link JqExpression}), and through the core with every prelude builtin bound to its definition, to
 * check that the natives that override some change nothing. Every case must pass both ways.
 */
class ConformanceTests {
    // The host of spec §14.2: no environment variables, a zone at UTC+02:00 without DST or
    // abbreviation, the default depth, and the module files of modules/.
    static final JqOptions OPTIONS = JqOptions.defaults()
        .withEnvironment(Map.of())
        .withClock(Clock.system(ZoneOffset.ofHours(2)))
        .withMaxDepth(1000)
        .withResolver(new JqFileResolver(CoreConformanceTests.SPEC.resolve("modules")));

    @TestFactory
    Stream<DynamicTest> corpusCase() {
        return CoreConformanceTests.cases(ConformanceTests::run);
    }

    @TestFactory
    Stream<DynamicTest> corpusCaseWithoutNativeOverrides() {
        return CoreConformanceTests.cases(CoreConformanceTests::runCore);
    }

    private static String run(JqTestParser.JqTestCase test) {
        JqExpression expression;
        try {
            expression = Jq.parse(test.program(), OPTIONS);
        } catch (JqParseException e) {
            return test.shouldFail() ? CoreConformanceTests.missingFixedText(test.expectedError(), e.getMessage())
                : e.getMessage() + " at " + e.line() + ":" + e.column();
        }

        if (test.shouldFail()) {
            return "the program parses and binds but must not";
        }

        JqValue input = JqJson.parse(test.input());
        List<String> outputs = new ArrayList<>();
        try {
            expression.evaluate(input, result -> {
                outputs.add(JqJson.toJson(result));
                return true;
            });
        } catch (JqException e) {
            return e.getClass().getSimpleName() + " " + e.getMessage() + " after " + outputs;
        }

        return CoreConformanceTests.compare(test.expectedOutputs(), outputs);
    }
}
