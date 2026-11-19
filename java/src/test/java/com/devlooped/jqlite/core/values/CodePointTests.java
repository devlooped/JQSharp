package com.devlooped.jqlite.core.values;

import static com.devlooped.jqlite.core.values.ValueHelpers.text;
import static com.devlooped.jqlite.core.values.ValueHelpers.v;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertSame;

import com.devlooped.jqlite.values.CodePoints;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.ValueOps;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

class CodePointTests {
    static Object[][] lengths() {
        return new Object[][] {{"", 0}, {"abc", 3}, {"aé😀b", 4}, {"😀😀", 2}};
    }

    @ParameterizedTest
    @MethodSource("lengths")
    void lengthCountsCodePoints(String text, int length) {
        assertEquals(length, CodePoints.length(text));
    }

    static String[][] slices() {
        return new String[][] {
            {"\"aé😀b\"", "2", "3", "\"😀\""},
            {"\"aé😀b\"", "-2", "null", "\"😀b\""},
            {"\"aé😀b\"", "null", "2", "\"aé\""},
            {"\"aé😀b\"", "1.5", "2.5", "\"é😀\""},
            {"\"aé😀b\"", "3", "1", "\"\""},
            {"\"aé😀b\"", "-100", "100", "\"aé😀b\""},
            {"\"aé😀b\"", "nan", "nan", "\"aé😀b\""},
            {"[1,2,3,4]", "1", "3", "[2,3]"},
            {"[1,2,3,4]", "-1", "null", "[4]"},
            {"[1,2,3,4]", "null", "-1", "[1,2,3]"},
            {"[1,2,3,4]", "0.2", "0.2", "[1]"},
            {"[1,2,3,4]", "5", "10", "[]"},
            {"null", "1", "2", "null"},
            {"[0,1,2,3,4,5,6,7,8,9]", "1.2", "3.5", "[1,2,3]"},
            {"\"abcdefghi\"", "-2", "null", "\"hi\""},
            {"null", "\"a\"", "{}", "null"},
            {"1", "1", "2", "error: Cannot index number with object ({\"start\":1,\"end\":2})"},
            {"1", "\"a\"", "2", "error: Cannot index number with object ({\"start\":\"a\",\"end\":2})"},
            {"{}", "1", "null", "error: Cannot index object with object ({\"start\":1,\"end\":null})"},
            {"[1]", "\"a\"", "null", "error: Array/string slice indices must be integers"},
            {"\"ab\"", "0", "true", "error: Array/string slice indices must be integers"},
        };
    }

    @ParameterizedTest
    @MethodSource("slices")
    void slicesWork(String input, String start, String end, String expected) {
        assertEquals(expected, text(ValueOps.trySlice(v(input), v(start), v(end))));
    }

    static String[][] indexSlices() {
        return new String[][] {
            {"[1,2,3]", "{\"start\":1,\"end\":null}", "[2,3]"},
            {"[1,2,3]", "{\"start\":1,\"end\":2,\"x\":0}", "[2]"},
            {"\"abc\"", "{\"start\":null,\"end\":null}", "\"abc\""},
            {"\"abc\"", "{\"start\":1}", "error: Array/string slice indices must be integers"},
            {"[1,2,3]", "{\"end\":2}", "error: Array/string slice indices must be integers"},
            {"[1,2,3]", "{\"start\":\"a\",\"end\":2}", "error: Array/string slice indices must be integers"},
            {"null", "{\"start\":1}", "null"},
            {"null", "{\"a\":1}", "null"},
            {"1", "{\"start\":1}", "error: Cannot index number with object ({\"start\":1})"},
            {"{\"a\":1}", "{\"start\":1}", "error: Cannot index object with object ({\"start\":1})"},
        };
    }

    // jq 1.8.2: .[{"start":…,"end":…}].
    @ParameterizedTest
    @MethodSource("indexSlices")
    void slicesByObjectKey(String input, String key, String expected) {
        assertEquals(expected, text(ValueOps.tryIndexSlice(v(input), v(key))));
    }

    static String[][] explodes() {
        return new String[][] {
            {"\"aé😀b\"", "[97,233,128512,98]"},
            {"\"\"", "[]"},
            {"1", "error: explode input must be a string"},
            {"[\"a\"]", "error: explode input must be a string"},
        };
    }

    @ParameterizedTest
    @MethodSource("explodes")
    void explode(String input, String expected) {
        assertEquals(expected, text(ValueOps.tryExplode(v(input))));
    }

    static String[][] implodes() {
        return new String[][] {
            {"[97,233,128512,98]", "\"aé😀b\""},
            {"[]", "\"\""},
            {"[65.9]", "\"A\""},
            {"[1114111]", "\"" + new String(Character.toChars(0x10FFFF)) + "\""},
            // jq 1.8.2: [-0.5, 97, 1114112, 55296, -1, 97.9] | implode | explode is [0,97,65533,65533,65533,97].
            {"[-0.5,97,1114112,55296,-1,97.9]", "\"\\u0000a\uFFFD\uFFFD\uFFFDa\""},
            {"[57343,57344,1e300,-1e300]", "\"\uFFFD\uE000\uFFFD\uFFFD\""},
            {"[\"a\"]", "error: string (\"a\") can't be imploded, unicode codepoint needs to be numeric"},
            {"[1,[1]]", "error: array ([1]) can't be imploded, unicode codepoint needs to be numeric"},
            {"[nan]", "error: number (null) can't be imploded, unicode codepoint needs to be numeric"},
            {"[null]", "error: null (null) can't be imploded, unicode codepoint needs to be numeric"},
            {"\"a\"", "error: implode input must be an array"},
            {"{}", "error: implode input must be an array"},
        };
    }

    @ParameterizedTest
    @MethodSource("implodes")
    void implode(String input, String expected) {
        assertEquals(expected, text(ValueOps.tryImplode(v(input))));
    }

    @Test
    void loneSurrogatesBecomeReplacementCharacters() {
        char high = (char) 0xD83D;
        char low = (char) 0xDE00;
        char replacement = (char) 0xFFFD;
        assertEquals("a" + replacement + "b", JqValue.fromString("a" + high + "b").asString());
        assertEquals("" + replacement, JqValue.fromString("" + low).asString());
        assertEquals("" + replacement, JqValue.fromString("" + high).asString());
        assertEquals("" + high + low + replacement, JqValue.fromString("" + high + low + high).asString());
        assertEquals("" + replacement + replacement, JqValue.fromString("" + low + high).asString());
    }

    @Test
    void wellFormedStringsAreNotCopied() {
        String text = "aé😀b";
        assertSame(text, JqValue.fromString(text).asString());
    }
}
