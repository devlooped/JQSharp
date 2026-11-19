package com.devlooped.jqlite.core.values;

import static com.devlooped.jqlite.core.values.ValueHelpers.j;
import static com.devlooped.jqlite.core.values.ValueHelpers.text;
import static com.devlooped.jqlite.core.values.ValueHelpers.v;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;

import com.devlooped.jqlite.values.JqObject;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;
import com.devlooped.jqlite.values.ValueOps;
import com.devlooped.jqlite.values.ValueText;
import java.util.ArrayList;
import java.util.List;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

class ValueArithmeticTests {
    // The result as JSON text, or "error: <message>".
    private static String apply(String op, String a, String b) {
        Result result = switch (op) {
            case "+" -> ValueOps.tryAdd(v(a), v(b));
            case "-" -> ValueOps.trySubtract(v(a), v(b));
            case "*" -> ValueOps.tryMultiply(v(a), v(b));
            case "/" -> ValueOps.tryDivide(v(a), v(b));
            case "%" -> ValueOps.tryModulo(v(a), v(b));
            default -> throw new IllegalArgumentException(op);
        };
        return text(result);
    }

    static String[][] adds() {
        return new String[][] {
            {"null", "1", "1"},
            {"1", "null", "1"},
            {"null", "null", "null"},
            {"null", "{\"a\":1}", "{\"a\":1}"},
            {"1", "2", "3"},
            {"0.1", "0.2", "0.30000000000000004"},
            {"1e308", "1e308", "1.7976931348623157e+308"},
            {"\"a\"", "\"b\"", "\"ab\""},
            {"[1]", "[2,[3]]", "[1,2,[3]]"},
            {"[]", "[]", "[]"},
            {"{\"a\":1,\"b\":2}", "{\"c\":3,\"a\":4}", "{\"a\":4,\"b\":2,\"c\":3}"},
            {"{\"b\":1}", "{\"a\":2}", "{\"b\":1,\"a\":2}"},
            {"1", "\"a\"", "error: number (1) and string (\"a\") cannot be added"},
            {"{}", "[]", "error: object ({}) and array ([]) cannot be added"},
            {"true", "true", "error: boolean (true) and boolean (true) cannot be added"},
            {"false", "1", "error: boolean (false) and number (1) cannot be added"},
            {"{\"a\":\"very long string\"}", "1", "error: object ({\"a\":\"very long string\"}) and number (1) cannot be added"},
            {"[1,2,3,4,5,6,7,8,9,10,11,12,13]", "1", "error: array ([1,2,3,4,5,6,7,8,9,10,11,...]) and number (1) cannot be added"},
        };
    }

    @ParameterizedTest
    @MethodSource("adds")
    void add(String a, String b, String expected) {
        assertEquals(expected, apply("+", a, b));
    }

    static String[][] subtracts() {
        return new String[][] {
            {"5", "3", "2"},
            {"[1,2,3,1,[1]]", "[1,[1]]", "[2,3]"},
            {"[1,2]", "[]", "[1,2]"},
            {"[{\"a\":1,\"b\":2}]", "[{\"b\":2,\"a\":1}]", "[]"},
            {"\"a\"", "\"a\"", "error: string (\"a\") and string (\"a\") cannot be subtracted"},
            {"null", "1", "error: null (null) and number (1) cannot be subtracted"},
            {"{}", "{}", "error: object ({}) and object ({}) cannot be subtracted"},
        };
    }

    @ParameterizedTest
    @MethodSource("subtracts")
    void subtract(String a, String b, String expected) {
        assertEquals(expected, apply("-", a, b));
    }

    static String[][] multiplies() {
        return new String[][] {
            {"2", "3", "6"},
            {"\"ab\"", "3", "\"ababab\""},
            {"3", "\"ab\"", "\"ababab\""},
            {"\"ab\"", "1", "\"ab\""},
            {"\"ab\"", "1.9", "\"ab\""},
            {"\"ab\"", "1.5", "\"ab\""},
            {"\"x\"", "0", "\"\""},
            {"\"ab\"", "0.5", "\"\""},
            {"\"ab\"", "-0", "\"\""},
            {"0", "\"ab\"", "\"\""},
            {"\"ab\"", "-0.5", "null"},
            {"\"ab\"", "-1", "null"},
            {"\"ab\"", "nan", "null"},
            {"\"\"", "5", "\"\""},
            {"\"\"", "1e300", "\"\""},
            {"\"é\"", "1.5e9", "error: Repeat string result too long"},
            {"\"ab\"", "1e9", "error: Repeat string result too long"},
            {"{\"a\":{\"b\":1},\"c\":1}", "{\"a\":{\"d\":2},\"c\":{\"x\":1}}", "{\"a\":{\"b\":1,\"d\":2},\"c\":{\"x\":1}}"},
            {"{\"a\":{\"b\":{\"c\":1}}}", "{\"a\":{\"b\":{\"d\":2}},\"e\":3}", "{\"a\":{\"b\":{\"c\":1,\"d\":2}},\"e\":3}"},
            {"{\"a\":{\"b\":1}}", "{\"a\":1}", "{\"a\":1}"},
            {"[1]", "2", "error: array ([1]) and number (2) cannot be multiplied"},
            {"\"a\"", "\"b\"", "error: string (\"a\") and string (\"b\") cannot be multiplied"},
            {"null", "2", "error: null (null) and number (2) cannot be multiplied"},
        };
    }

    @ParameterizedTest
    @MethodSource("multiplies")
    void multiply(String a, String b, String expected) {
        assertEquals(expected, apply("*", a, b));
    }

    static String[][] divides() {
        return new String[][] {
            {"6", "4", "1.5"},
            {"-1", "3", "-0.3333333333333333"},
            {"1", "0", "error: number (1) and number (0) cannot be divided because the divisor is zero"},
            {"0", "-0", "error: number (0) and number (-0) cannot be divided because the divisor is zero"},
            {"\"a,b,c\"", "\",\"", "[\"a\",\"b\",\"c\"]"},
            {"\"a,b,\"", "\",\"", "[\"a\",\"b\",\"\"]"},
            {"\"a, b\"", "\", \"", "[\"a\",\"b\"]"},
            {"\"abc\"", "\"x\"", "[\"abc\"]"},
            {"\"\"", "\",\"", "[]"},
            {"\"aé😀\"", "\"\"", "[\"a\",\"é\",\"😀\"]"},
            {"[]", "1", "error: array ([]) and number (1) cannot be divided"},
            {"\"a\"", "1", "error: string (\"a\") and number (1) cannot be divided"},
        };
    }

    @ParameterizedTest
    @MethodSource("divides")
    void divide(String a, String b, String expected) {
        assertEquals(expected, apply("/", a, b));
    }

    static String[][] modulos() {
        return new String[][] {
            {"5", "3", "2"},
            {"-5", "3", "-2"},
            {"5", "-3", "2"},
            {"5.9", "3.9", "2"},
            {"5", "-1", "0"},
            {"-9223372036854775808", "-1", "0"},
            {"1e300", "7", "0"},
            {"nan", "1", "null"},
            {"1", "nan", "null"},
            {"1e300", "-1e300", "9223372036854776000"},
            {"-1e300", "1e300", "-1"},
            {"1", "0", "error: number (1) and number (0) cannot be divided (remainder) because the divisor is zero"},
            {"5", "0.5", "error: number (5) and number (0.5) cannot be divided (remainder) because the divisor is zero"},
            {"\"a\"", "1", "error: string (\"a\") and number (1) cannot be divided (remainder)"},
            {"1", "\"a\"", "error: number (1) and string (\"a\") cannot be divided (remainder)"},
        };
    }

    @ParameterizedTest
    @MethodSource("modulos")
    void modulo(String a, String b, String expected) {
        assertEquals(expected, apply("%", a, b));
    }

    @Test
    void remainderOfInfinities() {
        // [(infinite, -infinite) % (1, -1, infinite)] is [0,0,0,0,0,-1] (spec §4.4).
        List<String> results = new ArrayList<>();
        for (double a : new double[] {Double.POSITIVE_INFINITY, Double.NEGATIVE_INFINITY}) {
            for (double b : new double[] {1, -1, Double.POSITIVE_INFINITY}) {
                Result result = ValueOps.tryModulo(JqValue.fromNumber(a), JqValue.fromNumber(b));
                assertInstanceOf(JqValue.class, result);
                results.add(j((JqValue) result));
            }
        }

        assertEquals(List.of("0", "0", "0", "0", "0", "-1"), results);
    }

    static String[][] negations() {
        return new String[][] {
            {"1", "-1"},
            {"0", "-0"},
            {"-0", "0"},
            {"nan", "null"},
            {"null", "error: null (null) cannot be negated"},
            {"\"a\"", "error: string (\"a\") cannot be negated"},
            {"\"very-long-long-long-long-string\"", "error: string (\"very-long-long-long-long...\") cannot be negated"},
        };
    }

    @ParameterizedTest
    @MethodSource("negations")
    void negation(String a, String expected) {
        assertEquals(expected, text(ValueOps.tryNegate(v(a))));
    }

    @Test
    void operandsNeverChange() {
        JqValue a = v("{\"a\":1,\"b\":{\"c\":2}}");
        JqValue b = v("{\"b\":{\"d\":3},\"e\":4}");
        assertFalse(ValueOps.tryAdd(a, b) instanceof Result.Err);
        assertFalse(ValueOps.tryMultiply(a, b) instanceof Result.Err);
        assertEquals("{\"a\":1,\"b\":{\"c\":2}}", j(a));
        assertEquals("{\"b\":{\"d\":3},\"e\":4}", j(b));
    }

    private static JqValue nest(String key, int depth) {
        JqValue value = JqObject.EMPTY.set(key, JqValue.fromNumber(depth));
        for (int i = 0; i < depth; i++) {
            value = JqObject.EMPTY.set("a", value);
        }
        return value;
    }

    @Test
    void deepMergeOfTenThousandLevelsDoesNotRecurse() throws InterruptedException {
        // {"a":{"a":…{"x":1}}} * {"a":{"a":…{"y":2}}}, 10,000 levels, on a 1 MB stack.
        Result[] result = new Result[1];
        Thread thread = new Thread(null, () -> result[0] = ValueOps.tryMultiply(nest("x", 10_000), nest("y", 10_000)), "deep", 1024 * 1024);
        thread.start();
        thread.join();
        assertInstanceOf(JqValue.class, result[0]);
        JqValue value = (JqValue) result[0];
        for (int i = 0; i < 10_000; i++) {
            value = value.asObject().valueAt(0);
        }
        assertEquals("{\"x\":10000,\"y\":10000}", ValueText.toJson(value));
    }
}
