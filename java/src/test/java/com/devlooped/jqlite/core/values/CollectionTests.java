package com.devlooped.jqlite.core.values;

import static com.devlooped.jqlite.core.values.ValueHelpers.j;
import static com.devlooped.jqlite.core.values.ValueHelpers.v;
import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.devlooped.jqlite.values.JqArray;
import com.devlooped.jqlite.values.JqKind;
import com.devlooped.jqlite.values.JqObject;
import com.devlooped.jqlite.values.JqObjectBuilder;
import com.devlooped.jqlite.values.JqString;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.ValueOps;
import java.util.stream.IntStream;
import org.junit.jupiter.api.Test;

class CollectionTests {
    private static JqValue n(double number) {
        return JqValue.fromNumber(number);
    }

    @Test
    void kindsAndTruthiness() {
        assertEquals(JqKind.NULL, JqValue.NULL.kind());
        assertEquals(JqKind.FALSE, JqValue.fromBool(false).kind());
        assertEquals(JqKind.TRUE, JqValue.fromBool(true).kind());
        assertEquals(JqKind.NUMBER, n(1).kind());
        assertEquals(JqKind.STRING, JqValue.fromString("").kind());
        assertEquals(JqKind.ARRAY, JqValue.fromArray(JqArray.EMPTY).kind());
        assertEquals(JqKind.OBJECT, JqValue.fromObject(JqObject.EMPTY).kind());
        assertFalse(JqValue.NULL.isTruthy());
        assertFalse(JqValue.FALSE.isTruthy());
        assertTrue(n(0).isTruthy());
        assertTrue(JqValue.fromString("").isTruthy());
        assertEquals("boolean", JqValue.TRUE.typeName());
        assertThrows(IllegalStateException.class, () -> n(1).asString());
    }

    @Test
    void appendingTwiceToTheSameBaseGivesIndependentArrays() {
        JqArray root = JqArray.EMPTY.append(n(1)).append(n(2));
        JqArray a = root.append(n(3));
        JqArray b = root.append(n(4));
        JqArray c = a.append(n(5));
        JqArray d = a.concat(JqArray.of(n(6), n(7)));

        assertEquals("[1,2]", j(root));
        assertEquals("[1,2,3]", j(a));
        assertEquals("[1,2,4]", j(b));
        assertEquals("[1,2,3,5]", j(c));
        assertEquals("[1,2,3,6,7]", j(d));
    }

    @Test
    void slicesShareStorageButNeverChange() {
        JqArray array = v("[1,2,3,4,5]").asArray();
        JqArray middle = array.slice(1, 3);
        JqArray tail = array.slice(3, 5);
        JqArray grown = middle.append(n(9));
        JqArray longer = tail.append(n(8));

        assertEquals("[1,2,3,4,5]", j(array));
        assertEquals("[2,3]", j(middle));
        assertEquals("[2,3,9]", j(grown));
        assertEquals("[4,5,8]", j(longer));
        assertEquals("[4,5]", j(tail));
    }

    @Test
    void setItemCopiesOneLevelAndPadsWithNull() {
        JqArray array = v("[1,[2]]").asArray();
        assertEquals("[1,7]", j(array.setItem(1, n(7))));
        assertEquals("[1,[2],null,5]", j(array.setItem(3, n(5))));
        assertEquals("[1,[2],6]", j(array.setItem(2, n(6))));
        assertEquals("[1,[2]]", j(array));
        assertSame(array.get(1).asArray(), array.setItem(0, n(0)).get(1).asArray());
    }

    @Test
    void concurrentAppendsToOneBaseStayIndependent() {
        JqArray root = JqArray.EMPTY.append(n(-1));
        JqArray[] results = new JqArray[1000];
        IntStream.range(0, results.length).parallel().forEach(i -> {
            JqArray array = root.append(n(i));
            for (int k = 0; k < 10; k++) {
                array = array.append(n(i * 100 + k));
            }
            results[i] = array;
        });

        assertEquals("[-1]", j(root));
        for (int i = 0; i < results.length; i++) {
            JqArray array = results[i];
            assertEquals(12, array.count());
            assertEquals(-1, array.get(0).asNumber());
            assertEquals(i, array.get(1).asNumber());
            for (int k = 0; k < 10; k++) {
                assertEquals(i * 100 + k, array.get(2 + k).asNumber());
            }
        }
    }

    // The reference measures the bytes allocated; Java has no portable allocation counter, so this
    // checks that a long loop of appends takes linear time instead.
    @Test
    void appendingInALoopDoesNotCopyTheArray() {
        long started = System.nanoTime();
        JqArray array = JqArray.EMPTY;
        for (int i = 0; i < 100000; i++) {
            array = array.concat(JqArray.of(n(i)));
        }
        long elapsedMs = (System.nanoTime() - started) / 1_000_000;

        assertEquals(100000, array.count());
        assertEquals(99999, array.get(99999).asNumber());
        assertTrue(elapsedMs < 2000, elapsedMs + " ms");
    }

    private static JqValue s(String text) {
        return JqValue.fromString(text);
    }

    @Test
    void addingTwiceToTheSameLongStringGivesIndependentStrings() {
        String head = "x".repeat(JqString.MIN_LENGTH);
        JqValue root = JqValue.concat(s(head), s("a"));
        JqValue a = JqValue.concat(root, s("b"));
        JqValue b = JqValue.concat(root, s("c"));
        JqValue c = JqValue.concat(a, s("d"));

        assertEquals(head + "a", root.asString());
        assertEquals(head + "ab", a.asString());
        assertEquals(head + "ac", b.asString());
        assertEquals(head + "abd", c.asString());
        assertEquals(JqKind.STRING, c.kind());
        assertTrue(ValueOps.areEqual(c, s(head + "abd")));
        assertEquals(0, ValueOps.compare(s(head + "ac"), b));
    }

    @Test
    void addingAnEmptyStringReturnsTheOther() {
        assertEquals("ab", JqValue.concat(s(""), s("ab")).asString());
        assertEquals("ab", JqValue.concat(s("ab"), s("")).asString());
        assertEquals("ab", JqValue.concat(s("a"), s("b")).asString());
    }

    @Test
    void concurrentAdditionsToOneLongStringStayIndependent() {
        String head = "x".repeat(JqString.MIN_LENGTH);
        JqValue root = JqValue.concat(s(head), s("-"));
        JqValue[] results = new JqValue[1000];
        IntStream.range(0, results.length).parallel().forEach(i -> {
            JqValue text = JqValue.concat(root, s("<" + i + ">"));
            for (int k = 0; k < 10; k++) {
                text = JqValue.concat(text, s((i * 100 + k) + ","));
            }
            results[i] = text;
        });

        assertEquals(head + "-", root.asString());
        for (int i = 0; i < results.length; i++) {
            StringBuilder expected = new StringBuilder(head).append("-<").append(i).append('>');
            for (int k = 0; k < 10; k++) {
                expected.append(i * 100 + k).append(',');
            }
            assertEquals(expected.toString(), results[i].asString());
        }
    }

    // As for arrays, linear time stands for the reference's bound on the bytes allocated.
    @Test
    void addingStringsInALoopDoesNotCopyTheString() {
        long started = System.nanoTime();
        JqValue text = s("");
        JqValue ab = s("ab");
        for (int i = 0; i < 100000; i++) {
            text = JqValue.concat(text, ab);
        }
        int length = text.asString().length();
        long elapsedMs = (System.nanoTime() - started) / 1_000_000;

        assertEquals(200000, length);
        assertTrue(elapsedMs < 2000, elapsedMs + " ms");
    }

    @Test
    void addingKeysInALoopDoesNotCopyTheObject() {
        long started = System.nanoTime();
        JqObject obj = JqObject.EMPTY;
        for (int i = 0; i < 50000; i++) {
            obj = obj.merge(JqObject.EMPTY.set("k" + i, n(i)));
        }
        long elapsedMs = (System.nanoTime() - started) / 1_000_000;

        assertEquals(50000, obj.count());
        assertEquals(1234, obj.get("k1234").asNumber());
        assertTrue(elapsedMs < 2000, elapsedMs + " ms");
    }

    @Test
    void objectKeepsInsertionOrder() {
        JqObject obj = JqObject.EMPTY.set("b", n(1)).set("a", n(2)).set("c", n(3));
        assertEquals("{\"b\":1,\"a\":2,\"c\":3}", j(obj));
        assertEquals("{\"b\":1,\"a\":9,\"c\":3}", j(obj.set("a", n(9))));
        assertEquals("{\"b\":1,\"c\":3}", j(obj.remove("a")));
        assertEquals("{\"b\":1,\"c\":3,\"a\":4}", j(obj.remove("a").set("a", n(4))));
        assertSame(obj, obj.remove("x"));
        assertEquals("{\"b\":1,\"a\":2,\"c\":3}", j(obj));
        assertArrayEquals(new String[] {"a", "b", "c"}, obj.sortedKeys());
    }

    @Test
    void objectMergeKeepsInsertionOrder() {
        JqObject left = v("{\"z\":1,\"y\":2,\"x\":3}").asObject();
        assertEquals("{\"z\":1,\"y\":5,\"x\":3,\"w\":4}", j(left.merge(v("{\"w\":4,\"y\":5}").asObject())));
        assertEquals("{\"z\":1,\"y\":2,\"x\":3,\"a\":0,\"b\":1}", j(left.merge(v("{\"a\":0,\"b\":1}").asObject())));
        assertEquals("{\"z\":1,\"y\":2,\"x\":3}", j(left));
    }

    @Test
    void versionsOfALargeObjectSeeOnlyTheirKeys() {
        JqObject[] versions = new JqObject[21];
        versions[0] = JqObject.EMPTY;
        for (int i = 1; i <= 20; i++) {
            versions[i] = versions[i - 1].set("k" + i, n(i));
        }

        // A branch from an older version takes a slot already used by a newer one: it copies.
        JqObject branch = versions[10].set("k15", n(-15));

        for (int i = 1; i <= 20; i++) {
            assertEquals(i, versions[i].count());
            assertTrue(versions[i].containsKey("k" + i));
            assertEquals(i < 20, !versions[i].containsKey("k20"));
            assertFalse(versions[i].containsKey("k" + (i + 1)));
        }

        assertEquals(-15, branch.get("k15").asNumber());
        assertEquals(15, versions[15].get("k15").asNumber());
        assertFalse(versions[12].containsKey("k15"));
        assertEquals(10, branch.indexOf("k15"));
    }

    @Test
    void concurrentKeyAddsToOneBaseStayIndependent() {
        JqObject base = JqObject.EMPTY;
        for (int i = 0; i < 20; i++) {
            base = base.set("base" + i, n(i));
        }
        JqObject root = base;

        JqObject[] results = new JqObject[500];
        IntStream.range(0, results.length).parallel().forEach(i -> {
            JqObject obj = root.set("own" + i, n(i));
            for (int k = 0; k < 5; k++) {
                obj = obj.set("own" + i + "_" + k, n(k));
            }
            results[i] = obj;
        });

        assertEquals(20, root.count());
        for (int i = 0; i < results.length; i++) {
            JqObject obj = results[i];
            assertEquals(26, obj.count());
            assertEquals("own" + i, obj.keyAt(20));
            assertEquals(i, obj.get("own" + i).asNumber());
            assertFalse(obj.containsKey("own" + ((i + 1) % results.length)));
            assertFalse(root.containsKey("own" + i));
        }
    }

    @Test
    void builderReplacesInPlaceAndRemoves() {
        JqObjectBuilder builder = new JqObjectBuilder();
        for (int i = 0; i < 12; i++) {
            builder.set("k" + i, n(i));
        }
        builder.set("k3", n(30));
        assertTrue(builder.remove("k0"));
        assertFalse(builder.remove("missing"));
        JqObject obj = builder.toObject();

        assertEquals(11, obj.count());
        assertEquals("k1", obj.keyAt(0));
        assertEquals(30, obj.get("k3").asNumber());
        assertFalse(obj.containsKey("k0"));
        assertThrows(IllegalStateException.class, () -> builder.set("x", n(1)));
    }
}
