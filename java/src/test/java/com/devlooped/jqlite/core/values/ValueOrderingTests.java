package com.devlooped.jqlite.core.values;

import static com.devlooped.jqlite.core.values.ValueHelpers.j;
import static com.devlooped.jqlite.core.values.ValueHelpers.v;
import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.devlooped.jqlite.values.CodePoints;
import com.devlooped.jqlite.values.JqArray;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.ValueOps;
import java.util.Random;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

class ValueOrderingTests {
    // In jq's order; no two are equal.
    private static final String[] ORDERED = {
        "null", "false", "true", "-1e300", "-1", "0", "0.5", "1", "1e300",
        "\"\"", "\"A\"", "\"a\"", "\"ab\"", "\"b\"", "\"é\"", "\"\uE000\"", "\"\uFFFF\"", "\"😀\"",
        "[]", "[null]", "[1]", "[1,2]", "[1,3]", "[2]", "[\"a\"]", "[[]]",
        "{}", "{\"a\":1}", "{\"a\":2}", "{\"a\":[]}", "{\"a\":1,\"b\":1}", "{\"a\":2,\"b\":1}", "{\"a\":1,\"c\":0}",
        "{\"b\":0}", "{\"é\":0}", "{\"😀\":0}",
    };

    @Test
    void everyPairIsOrdered() {
        for (int i = 0; i < ORDERED.length; i++) {
            JqValue a = v(ORDERED[i]);
            assertEquals(0, ValueOps.compare(a, v(ORDERED[i])));
            assertTrue(ValueOps.areEqual(a, v(ORDERED[i])), ORDERED[i]);
            for (int k = i + 1; k < ORDERED.length; k++) {
                JqValue b = v(ORDERED[k]);
                assertEquals(-1, ValueOps.compare(a, b), ORDERED[i] + " < " + ORDERED[k]);
                assertEquals(1, ValueOps.compare(b, a), ORDERED[k] + " > " + ORDERED[i]);
                assertFalse(ValueOps.areEqual(a, b), ORDERED[i] + " != " + ORDERED[k]);
            }
        }
    }

    @Test
    void deepValuesCompare() {
        JqValue a = v("[".repeat(10000) + "]".repeat(10000));
        JqValue b = v("[".repeat(10000) + "1" + "]".repeat(10000));
        assertTrue(ValueOps.areEqual(a, v(j(a))));
        assertEquals(-1, ValueOps.compare(a, b));
    }

    @Test
    void objectsOrderByKeySetsFirst() {
        assertEquals(-1, ValueOps.compare(v("{\"a\":2,\"b\":1}"), v("{\"a\":1,\"c\":0}")));
    }

    @Test
    void objectKeyOrderDoesNotMatter() {
        JqValue a = v("{\"a\":1,\"b\":[1,{\"x\":2,\"y\":3}]}");
        JqValue b = v("{\"b\":[1,{\"y\":3,\"x\":2}],\"a\":1}");
        assertTrue(ValueOps.areEqual(a, b));
        assertEquals(0, ValueOps.compare(a, b));
    }

    @Test
    void stringsOrderByCodePointNotUtf16Unit() {
        // U+E000 is one unit 0xE000; U+1F600 is the pair D83D DE00: ordinal UTF-16 puts the pair first.
        assertTrue("😀".compareTo("\uE000") < 0);
        assertEquals(-1, CodePoints.compare("\uE000", "😀"));
        assertEquals(1, CodePoints.compare("a😀", "a\uFFFD"));
        assertEquals(-1, CodePoints.compare("😀", "😁"));
        assertEquals(-1, CodePoints.compare("a", "a😀"));
    }

    @Test
    void nanSortsBelowNumbersComparesEqualToNanButIsNeverEqual() {
        JqValue nan = JqValue.fromNumber(Double.NaN);
        assertEquals(-1, ValueOps.compare(nan, JqValue.fromNumber(Double.NEGATIVE_INFINITY)));
        assertEquals(1, ValueOps.compare(JqValue.fromNumber(-1e300), nan));
        assertEquals(1, ValueOps.compare(nan, JqValue.TRUE));
        assertEquals(-1, ValueOps.compare(nan, JqValue.fromString("")));
        // A total order (spec §4.3, §13).
        assertEquals(0, ValueOps.compare(nan, nan));
        assertEquals(0, ValueOps.compare(v("[nan,{\"a\":nan}]"), v("[nan,{\"a\":nan}]")));
        // == stays IEEE: nan == nan is false, also inside containers.
        assertFalse(ValueOps.areEqual(nan, nan));
        assertFalse(ValueOps.areEqual(v("[nan]"), v("[nan]")));
        assertEquals(-1, ValueOps.compare(v("[nan]"), v("[0]")));
    }

    @Test
    void sortKeepsNansTogetherAndStable() {
        JqArray items = v("[1,nan,\"a\",-1,nan,null]").asArray();
        assertArrayEquals(new int[] {5, 1, 4, 3, 0, 2}, ValueOps.sortOrder(items));
    }

    static String[][] equal() {
        return new String[][] {
            {"1", "1.0"}, {"-0", "0"}, {"1e2", "100"}, {"null", "null"},
            {"[1,[2,{\"a\":null}]]", "[1,[2,{\"a\":null}]]"}, {"\"😀\"", "\"\\ud83d\\ude00\""},
        };
    }

    @ParameterizedTest
    @MethodSource("equal")
    void equalValues(String a, String b) {
        assertTrue(ValueOps.areEqual(v(a), v(b)));
        assertEquals(0, ValueOps.compare(v(a), v(b)));
    }

    static String[][] unequal() {
        return new String[][] {
            {"1", "\"1\""}, {"false", "null"}, {"[1,2]", "[2,1]"}, {"{\"a\":1}", "{\"a\":1,\"b\":null}"}, {"{\"a\":1}", "{\"b\":1}"},
        };
    }

    @ParameterizedTest
    @MethodSource("unequal")
    void unequalValues(String a, String b) {
        assertFalse(ValueOps.areEqual(v(a), v(b)));
    }

    @Test
    void sortOrdersEveryKind() {
        JqValue[] shuffled = new JqValue[ORDERED.length];
        StringBuilder expected = new StringBuilder("[");
        for (int i = 0; i < ORDERED.length; i++) {
            shuffled[ORDERED.length - 1 - i] = v(ORDERED[i]);
            expected.append(i > 0 ? "," : "").append(j(v(ORDERED[i])));
        }
        JqArray sorted = ValueOps.sort(JqArray.of(shuffled));
        assertEquals(expected.append(']').toString(), j(sorted));
    }

    @Test
    void sortOrderIsStable() {
        assertArrayEquals(new int[] {4, 1, 3, 0, 2, 5}, ValueOps.sortOrder(v("[2,1,2,1,0,2]").asArray()));
        assertEquals(0, ValueOps.sortOrder(JqArray.EMPTY).length);
        assertArrayEquals(new int[] {0}, ValueOps.sortOrder(v("[7]").asArray()));
    }

    @Test
    void sortMatchesAReferenceSortOnRandomInput() {
        Random random = new Random(7);
        JqValue[] items = new JqValue[1000];
        for (int i = 0; i < items.length; i++) {
            items[i] = JqValue.fromNumber(random.nextInt(100));
        }

        int[] order = ValueOps.sortOrder(JqArray.of(items));
        for (int i = 1; i < order.length; i++) {
            int c = ValueOps.compare(items[order[i - 1]], items[order[i]]);
            assertTrue(c < 0 || (c == 0 && order[i - 1] < order[i]));
        }
    }
}
