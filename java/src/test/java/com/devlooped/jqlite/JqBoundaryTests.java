package com.devlooped.jqlite;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.devlooped.jqlite.values.JqValue;
import java.time.Clock;
import java.time.Instant;
import java.time.ZoneId;
import java.time.ZoneOffset;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.stream.IntStream;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;
import org.junit.jupiter.params.provider.ValueSource;

/** The Java boundary: options, exceptions, streaming, variables and value conversion. */
class JqBoundaryTests {
    private static List<String> texts(List<JqValue> results) {
        return results.stream().map(JqJson::toJson).toList();
    }

    private static JqValue json(String text) {
        return JqJson.parse(text);
    }

    @Test
    void optionsEnvironmentIsEnvAndEnv() {
        Map<String, String> environment = new LinkedHashMap<>();
        environment.put("B", "2");
        environment.put("A", "1");
        assertEquals(List.of("{\"B\":\"2\",\"A\":\"1\"}", "\"1\""), texts(Jq.parse("$ENV, env.A", JqOptions.defaults().withEnvironment(environment)).evaluate(JqValue.NULL)));
    }

    @Test
    void withoutOptionsEnvIsTheProcessEnvironment() {
        String name = System.getenv().keySet().stream().filter(k -> k.chars().allMatch(c -> (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_') && !Character.isDigit(k.charAt(0))).findFirst().orElseThrow();
        assertEquals(List.of(JqJson.toJson(JqValue.fromString(System.getenv(name)))), texts(Jq.parse("$ENV." + name).evaluate(JqValue.NULL)));
    }

    @Test
    void optionsClockGivesNowAndTheLocalZone() {
        Clock clock = Clock.fixed(Instant.parse("2015-03-05T23:51:47Z"), ZoneOffset.ofHours(-3));
        assertEquals(List.of("1425599507", "\"2015-03-05T20:51:47 -0300\""),
            texts(Jq.parse("now, (now | strflocaltime(\"%Y-%m-%dT%H:%M:%S %z\"))", JqOptions.defaults().withClock(clock)).evaluate(JqValue.NULL)));
    }

    @Test
    void optionsClockFollowsDaylightSavingTime() {
        Clock clock = Clock.fixed(Instant.EPOCH, ZoneId.of("Europe/Berlin"));
        // January: +01:00; July: +02:00.
        assertEquals(List.of("\"+0100\"", "\"+0200\""),
            texts(Jq.parse("(1420070400, 1435708800) | strflocaltime(\"%z\")", JqOptions.defaults().withClock(clock)).evaluate(JqValue.NULL)));
    }

    @Test
    void optionsDebugAndStderrReceiveTheValues() {
        List<String> debugged = new ArrayList<>();
        List<String> written = new ArrayList<>();
        JqOptions options = JqOptions.defaults().withDebug(v -> debugged.add(JqJson.toJson(v))).withStderr(v -> written.add(JqJson.toJson(v)));
        assertEquals(List.of("1", "1", "1"), texts(Jq.parse("debug, debug(\"m\", {a: .}), stderr", options).evaluate(json("1"))));
        assertEquals(List.of("1", "\"m\"", "{\"a\":1}"), debugged);
        assertEquals(List.of("1"), written);
    }

    @Test
    void withoutHandlersDebugAndStderrOnlyOutputTheirInput() {
        assertEquals(List.of("1", "1"), texts(Jq.evaluate("debug, stderr", json("1"))));
    }

    @Test
    void haltErrorWithoutACodeExitsWith5() {
        JqHaltException error = assertThrows(JqHaltException.class, () -> Jq.evaluate("halt_error", json("\"bye\"")));
        assertEquals("bye", error.getMessage());
        assertEquals(5, error.exitCode());
    }

    @Test
    void optionsMaxDepthLimitsRecursion() {
        JqExpression expression = Jq.parse("def f: if . == 0 then 0 else (. - 1 | f) + 1 end; f", JqOptions.defaults().withMaxDepth(10));
        assertEquals(List.of("9"), texts(expression.evaluate(json("9"))));
        JqException error = assertThrows(JqException.class, () -> expression.evaluate(json("10")));
        assertEquals("Maximum call depth exceeded", error.getMessage());
    }

    @Test
    void maxDepthIsBetweenOneAndTheLimit() {
        assertEquals(JqOptions.MAX_DEPTH_LIMIT, JqOptions.defaults().withMaxDepth(JqOptions.MAX_DEPTH_LIMIT).maxDepth());
        assertThrows(IllegalArgumentException.class, () -> JqOptions.defaults().withMaxDepth(0));
        assertThrows(IllegalArgumentException.class, () -> JqOptions.defaults().withMaxDepth(JqOptions.MAX_DEPTH_LIMIT + 1));
    }

    @Test
    void callbacksAfterAHopRunOnAPoolThread() {
        List<String> threads = new ArrayList<>();
        Jq.parse("def f: if . == 0 then 0 else (. - 1 | f) | (., empty) end; f").evaluate(JqValue.fromNumber(500), result -> {
            threads.add(Thread.currentThread().getName());
            return true;
        });
        assertEquals(1, threads.size());
        assertTrue(threads.get(0).startsWith("jqlite-stack-"), threads.get(0));
    }

    static Object[][] parseErrors() {
        return new Object[][] {
            {"1 +", "Unexpected end of program at line 1, column 4", 1, 4},
            {".a |\n  foo", "foo/0 is not defined at line 2, column 3", 2, 3},
            {". as $x | break $x", "$*label-x is not defined at line 1, column 11", 1, 11},
        };
    }

    @ParameterizedTest
    @MethodSource("parseErrors")
    void parseErrorsHaveAPosition(String expression, String message, int line, int column) {
        JqParseException error = assertThrows(JqParseException.class, () -> Jq.parse(expression));
        assertEquals(message, error.getMessage());
        assertEquals(line, error.line());
        assertEquals(column, error.column());
        assertNull(error.value());
    }

    @Test
    void aStringErrorIsItsMessageAndValue() {
        JqException error = assertThrows(JqException.class, () -> Jq.evaluate("error(\"boom\")", JqValue.NULL));
        assertEquals("boom", error.getMessage());
        assertEquals("\"boom\"", JqJson.toJson(error.value()));
    }

    @Test
    void anotherErrorValueIsItsTextNotAString() {
        JqException error = assertThrows(JqException.class, () -> Jq.evaluate("error({a: [1, nan]})", JqValue.NULL));
        assertEquals("{\"a\":[1,null]} (not a string)", error.getMessage());
        assertEquals("{\"a\":[1,null]}", JqJson.toJson(error.value()));
    }

    @Test
    void aBuiltinErrorIsItsMessage() {
        JqException error = assertThrows(JqException.class, () -> Jq.evaluate(". + 1", json("\"a\"")));
        assertEquals("string (\"a\") and number (1) cannot be added", error.getMessage());
    }

    @Test
    void streamingGivesResultsAsTheyComeAndStopsWhenAsked() {
        List<Double> seen = new ArrayList<>();
        Jq.parse("range(1000000000)").evaluate(JqValue.NULL, result -> {
            seen.add(result.asNumber());
            return seen.size() < 3;
        });
        assertEquals(List.of(0.0, 1.0, 2.0), seen);
    }

    @Test
    void streamingDeliversResultsBeforeAnError() {
        List<String> seen = new ArrayList<>();
        JqException error = assertThrows(JqException.class, () -> Jq.parse("1, 2, error(\"late\"), 3").evaluate(JqValue.NULL, result -> {
            seen.add(JqJson.toJson(result));
            return true;
        }));
        assertEquals(List.of("1", "2"), seen);
        assertEquals("late", error.getMessage());
    }

    @Test
    void anExceptionOfTheCallbackPropagates() {
        assertThrows(NumberFormatException.class, () -> Jq.parse("1, 2").evaluate(JqValue.NULL, result -> {
            throw new NumberFormatException();
        }));
    }

    @Test
    void haltEndsNormally() {
        assertEquals(List.of("1"), texts(Jq.evaluate("1, halt, 2", JqValue.NULL)));
    }

    @Test
    void haltErrorCarriesTheValueAndTheCode() {
        List<String> seen = new ArrayList<>();
        JqHaltException error = assertThrows(JqHaltException.class, () -> Jq.parse("1, ({a: 1} | halt_error(5)), 2").evaluate(JqValue.NULL, result -> {
            seen.add(JqJson.toJson(result));
            return true;
        }));
        assertEquals(List.of("1"), seen);
        assertEquals(5, error.exitCode());
        assertEquals("{\"a\":1}", JqJson.toJson(error.value()));
        assertEquals("{\"a\":1} (not a string)", error.getMessage());
        assertEquals("bye", assertThrows(JqHaltException.class, () -> Jq.evaluate("\"bye\" | halt_error(0)", JqValue.NULL)).getMessage());
    }

    @ParameterizedTest
    @ValueSource(strings = {"$root", "1x", "a-b", "", "ENV"})
    void invalidVariableNamesAreRejectedBeforeEvaluation(String name) {
        assertThrows(IllegalArgumentException.class, () -> Jq.evaluate("error(\"evaluated\")", JqValue.NULL, Map.of(name, JqValue.fromNumber(1))));
    }

    @Test
    void variablesArePerEvaluation() {
        JqExpression expression = Jq.parse("$x + 1");
        assertEquals(List.of("2"), texts(expression.evaluate(JqValue.NULL, Map.of("x", JqValue.fromNumber(1)))));
        assertEquals(List.of("11"), texts(expression.evaluate(JqValue.NULL, Map.of("x", JqValue.fromNumber(10)))));
        assertEquals("$x is not defined", assertThrows(JqException.class, () -> expression.evaluate(JqValue.NULL)).getMessage());
    }

    static String[][] crossings() {
        return new String[][] {
            // Non-finite numbers come out as jq prints them; large numbers stay doubles.
            {"[nan, infinite, -infinite]", "null", "[null,1.7976931348623157e+308,-1.7976931348623157e+308]"},
            {".", "100000000000000000000000000001", "1e+29"},
            {".", "1E400", "1.7976931348623157e+308"},
            {". + 1", "9007199254740993", "9007199254740992"},
            {".", "-0", "-0"},
            // A lone surrogate becomes U+FFFD on the way in (spec §4.1).
            {". | explode", "\"a\\ud800b\"", "[97,65533,98]"},
            {"keys", "{\"\\udc00\":1}", "[\"\uFFFD\"]"},
            {"[(.[0] | keys), .[1]]", "[{\"\\udc00x\":1,\"\\udc00x\":2}, 3]", "[[\"\uFFFDx\"],3]"},
            {"tojson", "{\"b\":1,\"a\":2,\"b\":3}", "\"{\\\"b\\\":3,\\\"a\\\":2}\""},
        };
    }

    @ParameterizedTest
    @MethodSource("crossings")
    void valuesCrossTheBoundary(String expression, String input, String expected) {
        List<JqValue> results = Jq.evaluate(expression, json(input));
        assertEquals(1, results.size());
        assertEquals(expected, JqJson.toJson(results.get(0)));
    }

    @Test
    void hostStringsWithLoneSurrogatesAreSanitized() {
        assertEquals("\"a\uFFFDb\"", JqJson.toJson(Jq.evaluate(".", JqValue.fromString("a\uD800b")).get(0)));
    }

    @Test
    void deepInputAndOutputConvertWithoutRecursion() throws InterruptedException {
        String text = "[".repeat(5000) + "]".repeat(5000);
        String[] result = new String[1];
        Thread thread = new Thread(null, () -> result[0] = JqJson.toJson(Jq.evaluate(".", JqJson.parse(text)).get(0)), "deep", 256 * 1024);
        thread.start();
        thread.join();
        assertEquals(text, result[0]);
    }

    @Test
    void oneExpressionEvaluatesConcurrently() {
        JqExpression expression = Jq.parse("[.[] | . * $k] | add");
        JqValue input = json("[1,2,3]");
        String[] results = new String[64];
        IntStream.range(0, results.length).parallel().forEach(i -> results[i] = JqJson.toJson(expression.evaluate(input, Map.of("k", JqValue.fromNumber(i))).get(0)));
        for (int i = 0; i < results.length; i++) {
            assertEquals(Integer.toString(6 * i), results[i]);
        }
    }

    @Test
    void invalidJsonIsAJqException() {
        JqException error = assertThrows(JqException.class, () -> JqJson.parse("[1,"));
        assertEquals("Unfinished JSON term at EOF at line 1, column 3 (while parsing '[1,')", error.getMessage());
    }
}