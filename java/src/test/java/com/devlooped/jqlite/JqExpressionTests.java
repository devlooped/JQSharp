package com.devlooped.jqlite;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertThrows;

import com.devlooped.jqlite.values.JqValue;
import java.io.StringReader;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;

class JqExpressionTests {
    private static List<String> evaluateToStrings(String expression, String inputJson) {
        return Jq.evaluate(expression, JqJson.parse(inputJson)).stream().map(JqJson::toJson).toList();
    }

    @Test
    void parseReturnsNonNullExpression() {
        assertNotNull(Jq.parse("."));
    }

    // As in jq, a program of nothing (or of definitions only) is '.'.
    @ParameterizedTest
    @ValueSource(strings = {"", "   ", "# only a comment", "def f: 1;"})
    void anEmptyExpressionIsTheIdentity(String expression) {
        assertEquals(List.of("[1]"), evaluateToStrings(expression, "[1]"));
    }

    @Test
    void parseThrowsForNullArguments() {
        assertThrows(NullPointerException.class, () -> Jq.parse(null));
        assertThrows(NullPointerException.class, () -> Jq.parse(".", (JqOptions) null));
        assertThrows(NullPointerException.class, () -> Jq.parse(".", (JqResolver) null));
    }

    @Test
    void parseThrowsForInvalidExpression() {
        assertThrows(JqParseException.class, () -> Jq.parse("[invalid!!!"));
    }

    @Test
    void parsedExpressionEvaluatesCorrectly() {
        assertEquals(List.of("\"Alice\""), Jq.parse(".name").evaluate(JqJson.parse("{\"name\":\"Alice\"}")).stream().map(JqJson::toJson).toList());
    }

    @Test
    void parsedExpressionCanBeReusedAcrossInputs() {
        JqExpression expression = Jq.parse(".x + .y");
        assertEquals(3, expression.evaluate(JqJson.parse("{\"x\":1,\"y\":2}")).get(0).asNumber());
        assertEquals(30, expression.evaluate(JqJson.parse("{\"x\":10,\"y\":20}")).get(0).asNumber());
        assertEquals(300, expression.evaluate(JqJson.parse("{\"x\":100,\"y\":200}")).get(0).asNumber());
    }

    @Test
    void parsedExpressionMatchesEvaluateConvenienceMethod() {
        JqValue input = JqJson.parse("[1,2,3]");
        assertEquals(Jq.evaluate("map(. * 2)", input).stream().map(JqJson::toJson).toList(),
            Jq.parse("map(. * 2)").evaluate(input).stream().map(JqJson::toJson).toList());
    }

    @Test
    void parsedExpressionHandlesMultipleOutputs() {
        assertEquals(List.of(10.0, 20.0, 30.0), Jq.parse(".[]").evaluate(JqJson.parse("[10, 20, 30]")).stream().map(JqValue::asNumber).toList());
    }

    @Test
    void uncaughtErrorThrowsJqException() {
        assertThrows(JqException.class, () -> evaluateToStrings("\"hello\" | error", "null"));
    }

    // Programs on several lines: the one-line programs of spec/tests cannot carry them.
    @Test
    void leadingCommentLineIsIgnored() {
        assertEquals(List.of("42"), evaluateToStrings("# comment\n.foo\n", "{\"foo\":42}"));
    }

    @Test
    void commentBeforePipeDoesNotBreakNextLine() {
        assertEquals(List.of("99"), evaluateToStrings(".foo # pick foo\n| .bar\n", "{\"foo\":{\"bar\":99}}"));
    }

    @Test
    void oddTrailingBackslashesContinueCommentToNextLine() {
        assertEquals(List.of("42"), evaluateToStrings("# comment \\\\\\\n# still comment\n.foo\n", "{\"foo\":42}"));
    }

    @Test
    void evenTrailingBackslashesDoNotContinueComment() {
        assertEquals(List.of("42"), evaluateToStrings("# comment \\\\\\\\\n.foo\n", "{\"foo\":42}"));
    }

    // JSON Lines: each value evaluated alone, the results in order, halt ending only its value.
    @Test
    void jsonLinesAreEvaluatedOneByOne() throws Exception {
        List<String> results = new ArrayList<>();
        Jq.evaluateLines(Jq.parse(".a, (if .a == 2 then halt else empty end), .a * 10"), new StringReader("{\"a\":1}\n\n{\"a\":2}\n{\"a\":3}\n"), Map.of(), result -> {
            results.add(JqJson.toJson(result));
            return true;
        });
        assertEquals(List.of("1", "10", "2", "3", "30"), results);
    }

    private static List<String> lines(String expression, String jsonl) throws Exception {
        List<String> results = new ArrayList<>();
        Jq.evaluateLines(Jq.parse(expression), new StringReader(jsonl), Map.of(), result -> {
            results.add(JqJson.toJson(result));
            return true;
        });
        return results;
    }

    @Test
    void jsonLinesExtractAFieldFromEachValue() throws Exception {
        assertEquals(List.of("\"Alice\"", "\"Bob\"", "\"Charlie\""),
            lines(".name", "{\"name\":\"Alice\",\"age\":30}\n{\"name\":\"Bob\",\"age\":25}\n{\"name\":\"Charlie\",\"age\":35}"));
    }

    @Test
    void jsonLinesTransformEachValue() throws Exception {
        assertEquals(List.of("2", "4", "6"), lines(".x * 2", "{\"x\":1}\n{\"x\":2}\n{\"x\":3}"));
    }

    @Test
    void jsonLinesExpandArraysAcrossValues() throws Exception {
        assertEquals(List.of("1", "2", "3", "4"), lines(".items[]", "{\"items\":[1,2]}\n{\"items\":[3,4]}"));
    }

    @Test
    void jsonLinesFilterValuesWithSelect() throws Exception {
        assertEquals(List.of("\"Alice\"", "\"Charlie\""),
            lines("select(.active) | .name", "{\"name\":\"Alice\",\"active\":true}\n{\"name\":\"Bob\",\"active\":false}\n{\"name\":\"Charlie\",\"active\":true}"));
    }

    @Test
    void jsonLinesEvaluateEachValueAlone() throws Exception {
        assertEquals(List.of("1", "3"), lines("if . == 2 then halt else . end", "1\n2\n3"));
    }

    @Test
    void jsonLinesStopWhenAsked() throws Exception {
        List<String> results = new ArrayList<>();
        Jq.evaluateLines(Jq.parse(".[]"), new StringReader("[1,2]\n[3,4]\n"), Map.of(), result -> {
            results.add(JqJson.toJson(result));
            return results.size() < 3;
        });
        assertEquals(List.of("1", "2", "3"), results);
    }
}