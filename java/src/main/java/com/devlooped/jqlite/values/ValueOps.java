package com.devlooped.jqlite.values;

import java.util.ArrayList;

/**
 * The one implementation of equality, ordering, arithmetic, slicing and sorting of values, with
 * jq 1.8 semantics.
 *
 * <p>An operation that can fail in jq returns a {@link Result}: the value on success, or an
 * {@link Result.Err} with the jqlite error value (the message string). Nothing here throws as part
 * of jqlite semantics.
 */
public final class ValueOps {
    // The longest string a repeat may build (the reference's limit, below Java's array limit).
    private static final int MAX_STRING_LENGTH = 0x3FFFFFDF;

    private ValueOps() {
    }

    /**
     * jq's {@code ==}: same kind and same content. Numbers compare as doubles, so {@code nan == nan}
     * is false and so is {@code [nan] == [nan]}; objects are equal whatever their key order.
     */
    public static boolean areEqual(JqValue a, JqValue b) {
        return walk(a, b, true) == 0;
    }

    /**
     * jq's total order; returns -1, 0 or 1. Kinds first: null &lt; false &lt; true &lt; numbers &lt;
     * strings &lt; arrays &lt; objects. Strings by code point; arrays lexicographically; objects by
     * their sorted key lists, then by their values in that key order. NaN is below every other
     * number and equal to NaN, so the order is total (jq 1.8 has {@code nan < nan}; spec §13).
     */
    public static int compare(JqValue a, JqValue b) {
        return walk(a, b, false);
    }

    // A pair of containers being walked: children 0 to count - 1 are compared in turn, then tail
    // (the length comparison of two arrays) decides.
    private static final class Frame {
        JqValue a;
        JqValue b;
        int count;
        int index;
        int tail;
        String[] keys;

        void clear() {
            a = null;
            b = null;
            count = 0;
            index = 0;
            tail = 0;
            keys = null;
        }
    }

    /**
     * Compares (equality false) or tests equality (equality true, any non-zero result means
     * "different") without recursion: the containers being compared are on an explicit stack, so
     * nesting depth is not limited by the host stack.
     */
    private static int walk(JqValue a, JqValue b, boolean equality) {
        // Scalars need no frame: most comparisons of a program are of numbers and strings.
        JqKind kind = a.kind();
        if (kind == b.kind() && kind.compareTo(JqKind.ARRAY) < 0) {
            return scalar(a, b, kind, equality);
        }

        Frame frame = new Frame();
        int c = shallow(a, b, equality, frame);
        if (c != 0 || frame.count == 0) {
            return c != 0 ? c : frame.tail;
        }

        ArrayList<Frame> stack = new ArrayList<>();
        stack.add(frame);
        JqValue[] pair = new JqValue[2];
        if (!child(frame, equality, pair)) {
            return 1;
        }

        while (true) {
            frame = new Frame();
            c = shallow(pair[0], pair[1], equality, frame);
            if (c != 0) {
                return c;
            }
            if (frame.count > 0) {
                stack.add(frame);
                if (!child(frame, equality, pair)) {
                    return 1;
                }
                continue;
            }

            if (frame.tail != 0) {
                return frame.tail;
            }

            // The pair is equal: go on with the next pair, leaving the containers that end.
            while (true) {
                if (stack.isEmpty()) {
                    return 0;
                }

                Frame top = stack.get(stack.size() - 1);
                top.index++;
                if (top.index < top.count) {
                    if (!child(top, equality, pair)) {
                        return 1;
                    }
                    break;
                }

                stack.remove(stack.size() - 1);
                if (top.tail != 0) {
                    return top.tail;
                }
            }
        }
    }

    // Compares what can be compared without looking at children. A non-zero result decides. With
    // zero, frame.count > 0 means the children must be compared, and frame.tail is the result
    // once they are all equal.
    private static int shallow(JqValue a, JqValue b, boolean equality, Frame frame) {
        frame.clear();
        JqKind kind = a.kind();
        JqKind other = b.kind();
        if (kind != other) {
            return kind.compareTo(other) < 0 ? -1 : 1;
        }

        switch (kind) {
            case NUMBER, STRING:
                return scalar(a, b, kind, equality);
            case ARRAY: {
                int p = a.asArray().count();
                int q = b.asArray().count();
                if (equality && p != q) {
                    return 1;
                }
                frame.a = a;
                frame.b = b;
                frame.count = Math.min(p, q);
                frame.tail = p == q ? 0 : p < q ? -1 : 1;
                return 0;
            }
            case OBJECT: {
                JqObject m = a.asObject();
                JqObject n = b.asObject();
                if (equality) {
                    // Same size, then each key of a found in b with an equal value: no sorting.
                    if (m.count() != n.count()) {
                        return 1;
                    }
                    frame.a = a;
                    frame.b = b;
                    frame.count = m.count();
                    return 0;
                }

                String[] keys = m.sortedKeys();
                String[] others = n.sortedKeys();
                int length = Math.min(keys.length, others.length);
                for (int i = 0; i < length; i++) {
                    int c = CodePoints.compare(keys[i], others[i]);
                    if (c != 0) {
                        return c;
                    }
                }

                if (keys.length != others.length) {
                    return keys.length < others.length ? -1 : 1;
                }
                frame.a = a;
                frame.b = b;
                frame.count = keys.length;
                frame.keys = keys;
                return 0;
            }
            default:
                return 0;
        }
    }

    // Two scalars of the same kind.
    private static int scalar(JqValue a, JqValue b, JqKind kind, boolean equality) {
        if (kind == JqKind.NUMBER) {
            double x = a.asNumber();
            double y = b.asNumber();
            if (equality) {
                return x == y ? 0 : 1;
            }
            if (Double.isNaN(x)) {
                return Double.isNaN(y) ? 0 : -1;
            }
            if (Double.isNaN(y)) {
                return 1;
            }
            return x < y ? -1 : x == y ? 0 : 1;
        }
        if (kind == JqKind.STRING) {
            return equality
                ? a.asString().equals(b.asString()) ? 0 : 1
                : CodePoints.compare(a.asString(), b.asString());
        }
        return 0;
    }

    // The children at frame.index, into pair; false when b lacks a key of a (equality of objects only).
    private static boolean child(Frame frame, boolean equality, JqValue[] pair) {
        if (frame.a.kind() == JqKind.ARRAY) {
            pair[0] = frame.a.asArray().get(frame.index);
            pair[1] = frame.b.asArray().get(frame.index);
            return true;
        }

        JqObject x = frame.a.asObject();
        JqObject y = frame.b.asObject();
        if (equality) {
            pair[0] = x.valueAt(frame.index);
            pair[1] = y.get(x.keyAt(frame.index));
            return pair[1] != null;
        }

        String key = frame.keys[frame.index];
        pair[0] = x.get(key);
        pair[1] = y.get(key);
        return true;
    }

    /** jq's {@code +}: null is the identity; numbers add; strings and arrays concatenate; objects merge (right wins, insertion order kept). */
    public static Result tryAdd(JqValue a, JqValue b) {
        JqKind kind = a.kind();
        if (kind == JqKind.NULL) {
            return b;
        }

        if (b.kind() == JqKind.NULL) {
            return a;
        }

        if (kind == b.kind()) {
            switch (kind) {
                case NUMBER:
                    return JqValue.fromNumber(a.asNumber() + b.asNumber());
                case STRING:
                    return JqValue.fromString(a.asString().concat(b.asString()));
                case ARRAY:
                    return a.asArray().concat(b.asArray());
                case OBJECT:
                    return a.asObject().merge(b.asObject());
                default:
                    break;
            }
        }

        return Result.err(Errors.cannotBe(a, b, Errors.ADDED));
    }

    /** jq's {@code -}: numbers subtract; arrays drop every element equal to one of the right-hand array. */
    public static Result trySubtract(JqValue a, JqValue b) {
        JqKind kind = a.kind();
        if (kind == JqKind.NUMBER && b.kind() == JqKind.NUMBER) {
            return JqValue.fromNumber(a.asNumber() - b.asNumber());
        }

        if (kind == JqKind.ARRAY && b.kind() == JqKind.ARRAY) {
            JqArray items = a.asArray();
            JqArray remove = b.asArray();
            JqArray kept = JqArray.EMPTY;
            for (int i = 0; i < items.count(); i++) {
                boolean found = false;
                for (int j = 0; j < remove.count() && !found; j++) {
                    found = areEqual(items.get(i), remove.get(j));
                }
                if (!found) {
                    kept = kept.append(items.get(i));
                }
            }

            return kept;
        }

        return Result.err(Errors.cannotBe(a, b, Errors.SUBTRACTED));
    }

    /** jq's unary {@code -}: IEEE negation of a number ({@code 0 | -.} is -0). */
    public static Result tryNegate(JqValue value) {
        if (value.kind() != JqKind.NUMBER) {
            return Result.err(Errors.cannotNegate(value));
        }
        return JqValue.fromNumber(-value.asNumber());
    }

    /**
     * jq's {@code *}: numbers multiply; a string and a number (either order) repeat the string;
     * objects merge recursively.
     */
    public static Result tryMultiply(JqValue a, JqValue b) {
        JqKind kind = a.kind();
        JqKind other = b.kind();
        if (kind == JqKind.NUMBER && other == JqKind.NUMBER) {
            return JqValue.fromNumber(a.asNumber() * b.asNumber());
        }

        if ((kind == JqKind.STRING && other == JqKind.NUMBER) || (kind == JqKind.NUMBER && other == JqKind.STRING)) {
            String text = kind == JqKind.STRING ? a.asString() : b.asString();
            double times = kind == JqKind.STRING ? b.asNumber() : a.asNumber();
            return tryRepeat(text, times);
        }

        if (kind == JqKind.OBJECT && other == JqKind.OBJECT) {
            return deepMerge(a.asObject(), b.asObject());
        }

        return Result.err(Errors.cannotBe(a, b, Errors.MULTIPLIED));
    }

    // s * n: null for a NaN or negative n; otherwise s repeated trunc(n) times ("" below 1).
    private static Result tryRepeat(String text, double times) {
        if (Double.isNaN(times) || times < 0) {
            return JqValue.NULL;
        }

        int count = times >= Integer.MAX_VALUE ? Integer.MAX_VALUE : (int) times;
        if (count == 0 || text.isEmpty()) {
            return JqValue.fromString("");
        }

        // The spec's limit is 2^31 - 1 UTF-8 bytes; the host's string length limit may be hit first.
        if ((long) utf8ByteCount(text) * count > Integer.MAX_VALUE || (long) text.length() * count > MAX_STRING_LENGTH) {
            return Result.err(Errors.repeatTooLong());
        }

        return JqValue.fromString(text.repeat(count));
    }

    private static int utf8ByteCount(String text) {
        int bytes = 0;
        for (int i = 0; i < text.length(); i++) {
            int size = CodePoints.utf8Length(text, i);
            bytes += size;
            if (size == 4) {
                i++;
            }
        }
        return bytes;
    }

    // a * b on objects: like a + b, except that a key whose two values are objects gets their merge.
    // The objects being merged are on an explicit stack, so nesting depth is not limited by the host stack.
    private static JqObject deepMerge(JqObject a, JqObject b) {
        ArrayList<Merge> stack = new ArrayList<>();
        stack.add(new Merge(a, b));
        while (true) {
            Merge top = stack.get(stack.size() - 1);
            if (top.next < top.right.count()) {
                String key = top.right.keyAt(top.next);
                JqValue value = top.right.valueAt(top.next++);
                JqValue existing = value.kind() == JqKind.OBJECT ? top.result.get(key) : null;
                if (existing != null && existing.kind() == JqKind.OBJECT) {
                    top.key = key;
                    stack.add(new Merge(existing.asObject(), value.asObject()));
                } else {
                    top.result.set(key, value);
                }

                continue;
            }

            JqObject merged = top.result.toObject();
            stack.remove(stack.size() - 1);
            if (stack.isEmpty()) {
                return merged;
            }
            Merge parent = stack.get(stack.size() - 1);
            parent.result.set(parent.key, merged);
        }
    }

    // One pair of objects being merged: the result so far, the right side's next key, and the key
    // whose values are being merged below.
    private static final class Merge {
        final JqObjectBuilder result;
        final JqObject right;
        int next;
        String key = "";

        Merge(JqObject left, JqObject right) {
            this.result = new JqObjectBuilder(left, right.count());
            this.right = right;
        }
    }

    /** jq's {@code /}: numbers divide (a zero divisor is an error, {@code 0/0} too); strings split on the right-hand string. */
    public static Result tryDivide(JqValue a, JqValue b) {
        JqKind kind = a.kind();
        if (kind == JqKind.NUMBER && b.kind() == JqKind.NUMBER) {
            double divisor = b.asNumber();
            if (divisor == 0) {
                return Result.err(Errors.divisorIsZero(a, b, Errors.DIVIDED));
            }
            return JqValue.fromNumber(a.asNumber() / divisor);
        }

        if (kind == JqKind.STRING && b.kind() == JqKind.STRING) {
            return split(a.asString(), b.asString());
        }

        return Result.err(Errors.cannotBe(a, b, Errors.DIVIDED));
    }

    /**
     * {@code split/1} and {@code /} on strings: the empty string gives []; an empty separator splits
     * into code points.
     */
    public static JqArray split(String text, String separator) {
        JqArray parts = JqArray.EMPTY;
        if (text.isEmpty()) {
            return parts;
        }

        if (separator.isEmpty()) {
            for (int i = 0; i < text.length(); i++) {
                int units = Character.isHighSurrogate(text.charAt(i)) ? 2 : 1;
                parts = parts.append(JqValue.fromString(text.substring(i, i + units)));
                i += units - 1;
            }

            return parts;
        }

        int start = 0;
        int found;
        while ((found = text.indexOf(separator, start)) >= 0) {
            parts = parts.append(JqValue.fromString(text.substring(start, found)));
            start = found + separator.length();
        }

        return parts.append(JqValue.fromString(text.substring(start)));
    }

    /**
     * jq's {@code %}: both numbers truncate to integers (saturating at the 64-bit range), the sign
     * follows the dividend; NaN gives NaN; a divisor that truncates to 0 is an error.
     */
    public static Result tryModulo(JqValue a, JqValue b) {
        if (a.kind() != JqKind.NUMBER || b.kind() != JqKind.NUMBER) {
            return Result.err(Errors.cannotBe(a, b, Errors.REMAINDER));
        }

        double x = a.asNumber();
        double y = b.asNumber();
        if (Double.isNaN(x) || Double.isNaN(y)) {
            return JqValue.fromNumber(Double.NaN);
        }

        long divisor = toInteger(y);
        if (divisor == 0) {
            return Result.err(Errors.divisorIsZero(a, b, Errors.REMAINDER));
        }

        // -1 divides everything, and Long.MIN_VALUE % -1 overflows.
        return JqValue.fromNumber(divisor == -1 ? 0 : toInteger(x) % divisor);
    }

    // Truncation toward zero, clamped to [-2^63, 2^63 - 1] (±Infinity clamp to the ends).
    private static long toInteger(double value) {
        return value <= Long.MIN_VALUE ? Long.MIN_VALUE : value >= Long.MAX_VALUE ? Long.MAX_VALUE : (long) value;
    }

    /**
     * {@code .[key]} (§7.6): an object by a string (absent: null), an array by a number (NaN and out
     * of range: null; negative counts from the end) or by an array (the indices where it occurs), an
     * array or a string by a slice object, null by a string, a number or an object (null); anything
     * else is {@code Cannot index T with T (D)}.
     */
    public static Result tryIndex(JqValue target, JqValue key) {
        JqKind kind = target.kind();
        switch (key.kind()) {
            case STRING:
                if (kind == JqKind.OBJECT) {
                    JqValue value = target.asObject().get(key.asString());
                    return value != null ? value : JqValue.NULL;
                }
                if (kind == JqKind.NULL) {
                    return JqValue.NULL;
                }
                break;
            case NUMBER:
                if (kind == JqKind.ARRAY) {
                    return element(target.asArray(), key.asNumber());
                }
                if (kind == JqKind.NULL) {
                    return JqValue.NULL;
                }
                break;
            case ARRAY:
                if (kind == JqKind.ARRAY) {
                    return indices(target.asArray(), key.asArray());
                }
                break;
            case OBJECT:
                if (kind == JqKind.ARRAY || kind == JqKind.STRING || kind == JqKind.NULL) {
                    return tryIndexSlice(target, key);
                }
                break;
            default:
                break;
        }

        return Result.err(Errors.cannotIndex(target, key));
    }

    // .[n] on an array: NaN and out of range give null, a negative index counts from the end.
    private static JqValue element(JqArray array, double index) {
        if (Double.isNaN(index)) {
            return JqValue.NULL;
        }
        double i = index < 0 ? Math.ceil(index) : Math.floor(index);
        if (i < 0) {
            i += array.count();
        }
        return i >= 0 && i < array.count() ? array.get((int) i) : JqValue.NULL;
    }

    // The positions at which needle occurs in items as a contiguous sub-array; none for an empty needle.
    private static JqArray indices(JqArray items, JqArray needle) {
        JqArray found = JqArray.EMPTY;
        for (int start = 0; needle.count() > 0 && start + needle.count() <= items.count(); start++) {
            int i = 0;
            while (i < needle.count() && areEqual(items.get(start + i), needle.get(i))) {
                i++;
            }
            if (i == needle.count()) {
                found = found.append(JqValue.fromNumber(start));
            }
        }

        return found;
    }

    /**
     * {@code .[start:end]} (§7.6) on an array, a string (by code point) or null (gives null). A null
     * or NaN start is 0, a null or NaN end the length; a negative bound counts from the end; both
     * are clamped, then the start rounds down and the end up.
     */
    public static Result trySlice(JqValue input, JqValue start, JqValue end) {
        JqKind kind = input.kind();
        if (kind != JqKind.NULL && kind != JqKind.ARRAY && kind != JqKind.STRING) {
            // The key in the message is the slice object that `.[s:e]` stands for.
            JqObject key = JqObject.EMPTY.set("start", start).set("end", end);
            return Result.err(Errors.cannotIndex(input, key));
        }

        return trySliceBounds(input, start, end);
    }

    /**
     * {@code .[k]} with an object key: on an array or a string the key is a slice when it has both
     * {@code start} and {@code end} (other keys are ignored, as in jq), else the error
     * {@code Array/string slice indices must be integers}; null input gives null whatever the key;
     * any other input is {@code Cannot index T with object (D)}.
     */
    public static Result tryIndexSlice(JqValue input, JqValue key) {
        JqKind kind = input.kind();
        if (kind == JqKind.NULL) {
            return JqValue.NULL;
        }
        if (kind != JqKind.ARRAY && kind != JqKind.STRING) {
            return Result.err(Errors.cannotIndex(input, key));
        }

        JqObject slice = key.asObject();
        JqValue start = slice.get("start");
        JqValue end = slice.get("end");
        if (start == null || end == null) {
            return Result.err(Errors.sliceIndicesNotIntegers());
        }
        return trySliceBounds(input, start, end);
    }

    // Slices an array, a string or null (gives null).
    private static Result trySliceBounds(JqValue input, JqValue start, JqValue end) {
        JqKind kind = input.kind();
        if (kind == JqKind.NULL) {
            return JqValue.NULL;
        }

        String text = kind == JqKind.STRING ? input.asString() : null;
        int length = text == null ? input.asArray().count() : CodePoints.length(text);
        int[] range = new int[2];
        JqValue error = trySliceRange(length, start, end, range);
        if (error != null) {
            return Result.err(error);
        }

        return text == null
            ? input.asArray().slice(range[0], range[1])
            : JqValue.fromString(CodePoints.substring(text, range[0], range[1]));
    }

    /**
     * The positions [{@code range[0]}, {@code range[1]}) that the bounds of a slice select in a
     * sequence of {@code length} items (§7.6): null or NaN start is 0, null or NaN end the length,
     * negative bounds count from the end, both are clamped, the start rounds down and the end up.
     * Returns Java null, or the error for bounds that are not numbers or null:
     * {@code Array/string slice indices must be integers}.
     */
    public static JqValue trySliceRange(int length, JqValue start, JqValue end, int[] range) {
        range[0] = 0;
        range[1] = 0;
        JqKind s = start.kind();
        JqKind e = end.kind();
        if ((s != JqKind.NULL && s != JqKind.NUMBER) || (e != JqKind.NULL && e != JqKind.NUMBER)) {
            return Errors.sliceIndicesNotIntegers();
        }

        double from = s == JqKind.NULL ? 0 : start.asNumber();
        double to = e == JqKind.NULL ? length : end.asNumber();
        if (Double.isNaN(from)) {
            from = 0;
        }
        if (Double.isNaN(to)) {
            to = length;
        }
        if (from < 0) {
            from += length;
        }
        if (to < 0) {
            to += length;
        }
        int first = (int) Math.floor(Math.clamp(from, 0, length));
        int last = (int) Math.ceil(Math.clamp(to, first, length));
        range[0] = first;
        range[1] = last;
        return null;
    }

    /** jq's {@code explode}: the code points of a string. */
    public static Result tryExplode(JqValue input) {
        if (input.kind() != JqKind.STRING) {
            return Result.err(Errors.explodeNotString());
        }
        return CodePoints.explode(input.asString());
    }

    /**
     * jq's {@code implode}: an array of numbers to the string of the code points {@code trunc(n)};
     * one outside [0, 0x10FFFF] or a surrogate becomes U+FFFD ({@code -0.5} truncates to U+0000).
     * A non-number or NaN element is an error that describes the element.
     */
    public static Result tryImplode(JqValue input) {
        if (input.kind() != JqKind.ARRAY) {
            return Result.err(Errors.implodeNotArray());
        }

        JqArray codes = input.asArray();
        StringBuilder builder = new StringBuilder(codes.count());
        for (int i = 0; i < codes.count(); i++) {
            JqValue code = codes.get(i);
            if (code.kind() != JqKind.NUMBER || Double.isNaN(code.asNumber())) {
                return Result.err(Errors.codepointNotNumeric(code));
            }
            double n = code.asNumber();
            double number = n < 0 ? Math.ceil(n) : Math.floor(n);
            boolean scalar = number >= 0 && number <= CodePoints.MAX && CodePoints.isScalar((int) number);
            CodePoints.append(builder, scalar ? (int) number : CodePoints.REPLACEMENT);
        }

        return JqValue.fromString(builder.toString());
    }

    /** A stably sorted copy of {@code items} (jq's {@code sort}). */
    public static JqArray sort(JqArray items) {
        int[] order = sortOrder(items);
        JqValue[] sorted = new JqValue[order.length];
        for (int i = 0; i < order.length; i++) {
            sorted[i] = items.get(order[i]);
        }
        return JqArray.fromOwned(sorted);
    }

    /**
     * The positions of {@code keys} in sorted order, equal keys in their original order (a stable
     * sort, for {@code sort_by}, {@code group_by} and {@code unique_by}): a bottom-up merge sort.
     */
    public static int[] sortOrder(JqArray keys) {
        int n = keys.count();
        int[] order = new int[n];
        for (int i = 0; i < n; i++) {
            order[i] = i;
        }
        int[] merged = new int[n];
        for (int width = 1; width < n; width *= 2) {
            for (int low = 0; low < n; low += 2 * width) {
                int middle = Math.min(low + width, n);
                int high = Math.min(low + 2 * width, n);
                int left = low;
                int right = middle;
                int target = low;
                while (left < middle && right < high) {
                    // Take the left one unless the right one is strictly smaller: stable.
                    merged[target++] = compare(keys.get(order[left]), keys.get(order[right])) <= 0 ? order[left++] : order[right++];
                }

                while (left < middle) {
                    merged[target++] = order[left++];
                }
                while (right < high) {
                    merged[target++] = order[right++];
                }
            }

            int[] swap = order;
            order = merged;
            merged = swap;
        }

        return order;
    }
}
