package com.devlooped.jqlite.core.values;

import static com.devlooped.jqlite.core.values.ValueHelpers.j;
import static com.devlooped.jqlite.core.values.ValueHelpers.v;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.devlooped.jqlite.values.Errors;
import com.devlooped.jqlite.values.JqArray;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;
import com.devlooped.jqlite.values.Utf8;
import com.devlooped.jqlite.values.ValueText;
import java.util.Locale;
import java.util.Random;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;
import org.junit.jupiter.params.provider.ValueSource;

class ValueTextTests {
    static Object[][] numbers() {
        return new Object[][] {
            {0d, "0"}, {1d, "1"}, {-1d, "-1"}, {1.5, "1.5"}, {-1.5, "-1.5"}, {0.5, "0.5"}, {100d, "100"},
            {1e15, "1000000000000000"}, {1e20, "100000000000000000000"}, {123456789012345680000d, "123456789012345680000"},
            {1e21, "1e+21"}, {-1e21, "-1e+21"}, {1.5e300, "1.5e+300"}, {1e-6, "0.000001"}, {1.5e-6, "0.0000015"},
            {1e-7, "1e-7"}, {1.23e-18, "1.23e-18"}, {0.1 + 0.2, "0.30000000000000004"}, {5e-324, "5e-324"},
            {1.7976931348623157e308, "1.7976931348623157e+308"}, {9007199254740993d, "9007199254740992"}, {123.456, "123.456"},
            {Double.NaN, "null"}, {Double.POSITIVE_INFINITY, "1.7976931348623157e+308"}, {Double.NEGATIVE_INFINITY, "-1.7976931348623157e+308"},
        };
    }

    @ParameterizedTest
    @MethodSource("numbers")
    void numberText(double number, String expected) {
        assertEquals(expected, ValueText.formatNumber(number));
    }

    @Test
    void negativeZeroText() {
        assertEquals("-0", ValueText.formatNumber(-0.0));
    }

    @Test
    void numberTextRoundTrips() {
        Random random = new Random(42);
        for (int i = 0; i < 100000; i++) {
            double number = Double.longBitsToDouble(random.nextLong());
            if (Double.isNaN(number) || Double.isInfinite(number)) {
                continue;
            }
            String text = ValueText.formatNumber(number);
            Double back = ValueText.tryParseNumber(text);
            assertNotNull(back, text);
            assertEquals(number, back, text);
        }
    }

    // The digits are the shortest that read back (one digit where Java's Double.toString gives two).
    @Test
    void numberTextIsShortest() {
        assertEquals("5e-324", ValueText.formatNumber(Double.MIN_VALUE));
        assertEquals("-5e-324", ValueText.formatNumber(-Double.MIN_VALUE));
        assertEquals("1e-323", ValueText.formatNumber(2 * Double.MIN_VALUE));
        assertEquals("1e+23", ValueText.formatNumber(1e23));
        assertEquals("2e+22", ValueText.formatNumber(2e22));
    }

    static String[][] escapes() {
        return new String[][] {
            {"é<", "\"é<\""},
            {"a\"b\\c/d", "\"a\\\"b\\\\c/d\""},
            {"\b\f\n\r\t", "\"\\b\\f\\n\\r\\t\""},
            {"\u0000\u0001\u001f", "\"\\u0000\\u0001\\u001f\""},
            {"\u007f\u0080", "\"\\u007f\u0080\""},
            {"😀", "\"😀\""},
            {"\u2028", "\"\u2028\""},
        };
    }

    @ParameterizedTest
    @MethodSource("escapes")
    void stringEscaping(String text, String expected) {
        assertEquals(expected, j(JqValue.fromString(text)));
    }

    @ParameterizedTest
    @ValueSource(strings = {"null", "true", "false", "[]", "{}", "\"\"", "[1,2.5,\"x\",null,true,false,{},[[]]]",
        "{\"b\":1,\"a\":[1,{\"z\":null,\"y\":\"é\"}],\"c\":{\"d\":[]}}", "-0"})
    void readerRoundTrips(String json) {
        assertEquals(json, j(v(json)));
    }

    static String[][] readerValues() {
        return new String[][] {
            {" \t\r\n[ 1 , { \"a\" : 2 } ] \n", "[1,{\"a\":2}]"},
            {"\uFEFF1", "1"},
            {"1.0", "1"},
            {"1E2", "100"},
            {"1e-2", "0.01"},
            {"1.", "1"},
            {".5", "0.5"},
            {"+1", "1"},
            {"007", "7"},
            {"1e1000", "1.7976931348623157e+308"},
            {"-1e1000", "-1.7976931348623157e+308"},
            {"1e-1000", "0"},
            {"nan", "null"},
            {"NaN", "null"},
            {"[-nan]", "[null]"},
            {"Infinity", "1.7976931348623157e+308"},
            {"-Infinity", "-1.7976931348623157e+308"},
            {"\"\\u00e9\\/\\\"\"", "\"é/\\\"\""},
            {"\"\\ud83d\\ude00\"", "\"😀\""},
            {"\"\\ud800\"", "\"\uFFFD\""},
            {"\"\\udc00x\"", "\"\uFFFDx\""},
            {"\"\\ud800\\ud800\"", "\"\uFFFD\uFFFD\""},
            {"\"\\ud800\\u0041\"", "\"\uFFFDA\""},
            {"[nAn,SNaN,-snan,+inf,-INF,iNfInItY]", "[null,null,null,1.7976931348623157e+308,-1.7976931348623157e+308,1.7976931348623157e+308]"},
            {"[1,nan]", "[1,null]"},
            {"{\"a\":1,\"b\":2,\"a\":3}", "{\"a\":3,\"b\":2}"},
            {"\"a\u007Fb\"", "\"a\\u007fb\""},
            {"{\"a\":1,\"a\":2}", "{\"a\":2}"},
            {"{\"\\ud800\":1}", "{\"\uFFFD\":1}"},
        };
    }

    @ParameterizedTest
    @MethodSource("readerValues")
    void readerValuesWork(String json, String expected) {
        assertEquals(expected, j(v(json)));
    }

    // The reason (implementation-defined, §5.3); every message ends with " (while parsing '<input>')".
    static String[][] readerFailures() {
        return new String[][] {
            {"", "Expected JSON value"},
            {"   ", "Expected JSON value"},
            {"1 2", "Unexpected extra JSON values"},
            {"{", "Unfinished JSON term at EOF at line 1, column 1"},
            {"[1,2", "Unfinished JSON term at EOF at line 1, column 4"},
            {"\"abc", "Unfinished JSON term at EOF at line 1, column 4"},
            {"[1 2]", "Expected separator between values at line 1, column 4"},
            {"[1,\n2 3]", "Expected separator between values at line 2, column 3"},
            {"tru", "Invalid literal at line 1, column 3"},
            {"nul", "Invalid literal at line 1, column 3"},
            {"Null", "Invalid literal at line 1, column 4"},
            {"NaN1", "Invalid literal at line 1, column 4"},
            {"nanx", "Invalid literal at line 1, column 4"},
            {"[1x]", "Invalid numeric literal at line 1, column 3"},
            {"1e", "Invalid numeric literal at line 1, column 2"},
            {"'a'", "Invalid numeric literal at line 1, column 3"},
            {"\"\\q\"", "Invalid escape at line 1, column 3"},
            {"\"\\u12G4\"", "Invalid \\uXXXX escape at line 1, column 6"},
            {"\"a\nb\"", "Invalid string: control characters from U+0000 through U+001F must be escaped at line 2, column 0"},
            {"\"a\u0001\"", "Invalid string: control characters from U+0000 through U+001F must be escaped at line 1, column 3"},
            {"[\"\t\"]", "Invalid string: control characters from U+0000 through U+001F must be escaped at line 1, column 3"},
            {"{1:2}", "Object keys must be strings at line 1, column 2"},
            {"{\"a\" 1}", "Objects must consist of key:value pairs at line 1, column 6"},
            {"[1,]", "Expected another array element at line 1, column 4"},
            {"]", "Unmatched ']' at line 1, column 1"},
            {"[1}", "Unmatched '}' at line 1, column 3"},
            {",", "Expected value before ',' at line 1, column 1"},
        };
    }

    @ParameterizedTest
    @MethodSource("readerFailures")
    void readerFailuresWork(String json, String reason) {
        Result result = ValueText.tryParse(json);
        assertInstanceOf(Result.Err.class, result);
        assertEquals(reason + " (while parsing '" + json + "')", ((Result.Err) result).error().asString());
    }

    @Test
    void readerDepthLimit() {
        String ok = "[".repeat(10000) + "]".repeat(10000);
        assertEquals(ok, j(v(ok)));

        Result result = ValueText.tryParse("[".repeat(10001));
        assertInstanceOf(Result.Err.class, result);
        assertTrue(((Result.Err) result).error().asString().startsWith("Exceeds depth limit for parsing"));
    }

    // Arrays nested levels deep around inner.
    private static JqValue nest(int levels, JqValue inner) {
        JqValue value = inner;
        for (int i = 0; i < levels; i++) {
            value = JqArray.of(value);
        }
        return value;
    }

    @Test
    void writerSkipsValuesDeeperThan10001Levels() {
        String skipped = "<skipped: too deep>";
        // jq 1.8.2: reduce range(n) as $i (x; [.]) | tojson.
        assertEquals("[".repeat(10000) + "1" + "]".repeat(10000), j(nest(10000, JqValue.fromNumber(1))));
        assertEquals("[".repeat(10001) + skipped + "]".repeat(10001), j(nest(10001, JqValue.fromNumber(1))));
        assertEquals("[".repeat(10001) + "]".repeat(10001), j(nest(10000, JqArray.EMPTY)));
        assertEquals("[".repeat(10001) + skipped + "]".repeat(10001), j(nest(10001, JqArray.EMPTY)));
        assertEquals("[".repeat(10001) + skipped + "," + skipped + "]".repeat(10001), j(nest(10000, v("[1,[2]]"))));
    }

    static Object[][] numberReadings() {
        return new Object[][] {
            {"12", 12d}, {"-1.5e3", -1500d}, {"0.1", 0.1}, {".5", 0.5}, {"5.", 5d}, {"+3", 3d},
            {"inf", Double.POSITIVE_INFINITY}, {"-INFINITY", Double.NEGATIVE_INFINITY},
        };
    }

    @ParameterizedTest
    @MethodSource("numberReadings")
    void numberReading(String text, double expected) {
        assertEquals(expected, ValueText.tryParseNumber(text));
    }

    @ParameterizedTest
    @ValueSource(strings = {"", " 1", "1 ", "0x10", ".", "-", "1e+", "1,5", "abc", "1d", "1f", "0x1p3", "Infinityx"})
    void numberReadingFailures(String text) {
        assertNull(ValueText.tryParseNumber(text));
    }

    @ParameterizedTest
    @ValueSource(strings = {"nan", "NaN", "-nan", "+NAN", "snan", "-sNaN"})
    void nanWordsReadAsNan(String text) {
        Double number = ValueText.tryParseNumber(text);
        assertNotNull(number);
        assertTrue(Double.isNaN(number));
    }

    @Test
    void textIsCultureInvariant() {
        Locale culture = Locale.getDefault();
        try {
            Locale.setDefault(Locale.GERMANY);
            assertEquals("1.5", ValueText.formatNumber(1.5));
            assertEquals("1.5e+300", ValueText.formatNumber(1.5e300));
            assertEquals("[1.5]", j(v("[1.5]")));
        } finally {
            Locale.setDefault(culture);
        }
    }

    static String[][] dumps() {
        return new String[][] {
            {"[1,2]", "[1,2]"},
            {"[1,2,3,4,5,6,7,8,9,10,11,12,13]", "[1,2,3,4,5,6,7,8,9,10,11,...]"},
            {"{\"aaaaaaaaaaaaaaaaaaaaaaaaaa\":1}", "{\"aaaaaaaaaaaaaaaaaaaaaaa...}"},
            // A cut value ends with "..." and its own closing delimiter, not those of what it contains.
            {"\"very-long-long-long-long-string\"", "\"very-long-long-long-long...\""},
            {"[\"very-long-long-long-long-string\"]", "[\"very-long-long-long-lon...]"},
            // 27 characters: 29 bytes with the quotes, not cut; one more is cut.
            {"\"abcdefghijklmnopqrstuvwxyz0\"", "\"abcdefghijklmnopqrstuvwxyz0\""},
            {"\"abcdefghijklmnopqrstuvwxyz01\"", "\"abcdefghijklmnopqrstuvwx...\""},
            // Two-byte é: the quote and 12 of them make 25 bytes.
            {"\"ééééééééééééééééééé\"", "\"éééééééééééé...\""},
            {"\"a😀😀😀😀😀😀😀\"", "\"a😀😀😀😀😀...\""},
        };
    }

    @ParameterizedTest
    @MethodSource("dumps")
    void errorDump(String json, String expected) {
        String dump = Errors.dump(v(json));
        assertEquals(expected, dump);
        assertTrue(Utf8.byteCount(dump) <= 29);
    }
}
