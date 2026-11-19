package com.devlooped.jqlite;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

import com.devlooped.jqlite.values.JqValue;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import org.junit.jupiter.api.Test;

class JqVariableTests {
    private static List<String> evaluateToStrings(String expression, String inputJson) {
        return Jq.evaluate(expression, JqJson.parse(inputJson)).stream().map(JqJson::toJson).toList();
    }

    // Depends on the environment of the test process, so it cannot be a case of spec/tests.
    @Test
    void envHasKeys() {
        assertEquals(List.of("true"), evaluateToStrings("($ENV | keys | length) > 0", "null"));
    }

    @Test
    void externalVariableIsAvailableDuringEvaluation() {
        Map<String, JqValue> variables = Map.of("root", JqJson.parse("{\"id\":42}"));
        List<JqValue> results = Jq.evaluate("{name: .name, id: $root.id}", JqJson.parse("{\"name\":\"Alice\"}"), variables);
        assertEquals(List.of("{\"name\":\"Alice\",\"id\":42}"), results.stream().map(JqJson::toJson).toList());
    }

    @Test
    void externalVariableCanBeUsedFromParsedExpression() {
        JqExpression expression = Jq.parse("$greeting");
        List<JqValue> results = expression.evaluate(JqValue.NULL, Map.of("greeting", JqValue.fromString("hello")));
        assertEquals(List.of("hello"), results.stream().map(JqValue::asString).toList());
    }

    @Test
    void externalVariableMissingAtEvaluationThrows() {
        JqExpression expression = Jq.parse("$root");
        JqException exception = assertThrows(JqException.class, () -> expression.evaluate(JqValue.NULL));
        assertEquals("$root is not defined", exception.getMessage());
    }

    @Test
    void externalVariableNameMustNotStartWithDollar() {
        assertThrows(IllegalArgumentException.class, () -> Jq.evaluate("$root", JqValue.NULL, Map.of("$root", JqValue.fromNumber(1))));
    }

    @Test
    void externalVariableNameMustBeValidIdentifier() {
        assertThrows(IllegalArgumentException.class, () -> Jq.evaluate(".", JqValue.NULL, Map.of("not-valid", JqValue.fromNumber(1))));
    }

    @Test
    void externalVariableIsShadowedByAsBinding() {
        List<JqValue> results = Jq.evaluate("2 as $x | $x", JqValue.NULL, Map.of("x", JqValue.fromNumber(1)));
        assertEquals(List.of(2.0), results.stream().map(JqValue::asNumber).toList());
    }

    @Test
    void multipleExternalVariablesCanBeBound() {
        Map<String, JqValue> variables = new LinkedHashMap<>();
        variables.put("a", JqValue.fromString("A"));
        variables.put("b", JqValue.fromString("B"));
        assertEquals(List.of("[\"A\",\"B\"]"), Jq.evaluate("[$a, $b]", JqValue.NULL, variables).stream().map(JqJson::toJson).toList());
    }
}
