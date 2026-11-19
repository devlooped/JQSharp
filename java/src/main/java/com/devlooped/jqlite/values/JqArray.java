package com.devlooped.jqlite.values;

import java.util.Arrays;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * An immutable sequence of values.
 *
 * <p>Storage is a shared buffer plus a window ({@code start}, {@code count}) on it, as a Go slice.
 * A slot of a buffer is written once, before any array that covers it exists, and never again, so
 * every {@link JqArray} is immutable however many arrays share its buffer.
 *
 * <p>Appending ({@link #append}, {@link #concat}) to the array whose window ends where the buffer's
 * used part ends (its latest version) claims the next free slots with one compare-and-swap on
 * {@code used} and writes there: amortized O(1), so {@code reduce range(n) as $i ([]; . + [$i])}
 * is O(n). When the slots are already taken (an older version, or another thread won the claim) or
 * the buffer is full, the window is copied to a new buffer with twice the room. Replacing one
 * element copies one level. A slice shares the buffer and keeps all of it alive, as in Go.
 */
public final class JqArray implements JqValue {
    private static final class Buffer {
        final JqValue[] items;

        // Slots [0, used) are claimed. Only the thread that claimed a slot writes it, before it
        // publishes the array that covers it.
        final AtomicInteger used;

        Buffer(JqValue[] items, int used) {
            this.items = items;
            this.used = new AtomicInteger(used);
        }
    }

    public static final JqArray EMPTY = new JqArray(new Buffer(new JqValue[0], 0), 0, 0);

    private final Buffer buffer;
    private final int start;
    private final int count;

    private JqArray(Buffer buffer, int start, int count) {
        this.buffer = buffer;
        this.start = start;
        this.count = count;
    }

    /** Creates an array with a copy of {@code items}. */
    public static JqArray of(JqValue... items) {
        if (items.length == 0) {
            return EMPTY;
        }
        return fromOwned(items.clone());
    }

    /** Creates an array over {@code items} without copying: the caller gives up the array and never writes it again. */
    public static JqArray fromOwned(JqValue[] items) {
        return items.length == 0 ? EMPTY : new JqArray(new Buffer(items, items.length), 0, items.length);
    }

    /** Creates an array over the first {@code count} items without copying; the rest of the storage is room to append. */
    public static JqArray fromOwned(JqValue[] items, int count) {
        return count == 0 ? EMPTY : new JqArray(new Buffer(items, count), 0, count);
    }

    @Override
    public JqKind kind() {
        return JqKind.ARRAY;
    }

    @Override
    public JqArray asArray() {
        return this;
    }

    public int count() {
        return count;
    }

    /** The element at {@code index}, 0 &lt;= index &lt; count. */
    public JqValue get(int index) {
        if (index < 0 || index >= count) {
            throw new IndexOutOfBoundsException(index);
        }
        return buffer.items[start + index];
    }

    public JqArray append(JqValue value) {
        int end = start + count;
        if (end < buffer.items.length && buffer.used.compareAndSet(end, end + 1)) {
            buffer.items[end] = value;
            return new JqArray(buffer, start, count + 1);
        }

        JqValue[] items = new JqValue[grow(count + 1)];
        System.arraycopy(buffer.items, start, items, 0, count);
        items[count] = value;
        return new JqArray(new Buffer(items, count + 1), 0, count + 1);
    }

    /** This array followed by {@code other} (jq's {@code +} on arrays). */
    public JqArray concat(JqArray other) {
        if (other.count == 0) {
            return this;
        }
        if (count == 0) {
            return other;
        }

        int end = start + count;
        int total = count + other.count;
        if (end + other.count <= buffer.items.length && buffer.used.compareAndSet(end, end + other.count)) {
            System.arraycopy(other.buffer.items, other.start, buffer.items, end, other.count);
            return new JqArray(buffer, start, total);
        }

        JqValue[] items = new JqValue[grow(total)];
        System.arraycopy(buffer.items, start, items, 0, count);
        System.arraycopy(other.buffer.items, other.start, items, count, other.count);
        return new JqArray(new Buffer(items, total), 0, total);
    }

    /** The elements {@code from} (included) to {@code to} (excluded), sharing this array's buffer; 0 &lt;= from &lt;= to &lt;= count. */
    public JqArray slice(int from, int to) {
        if (from < 0 || from > count || to < from || to > count) {
            throw new IndexOutOfBoundsException(from);
        }
        if (from == 0 && to == count) {
            return this;
        }
        return to == from ? EMPTY : new JqArray(buffer, start + from, to - from);
    }

    /** A copy with {@code value} at {@code index} (index &gt;= 0); an index past the end pads with nulls, as jq does. */
    public JqArray setItem(int index, JqValue value) {
        if (index < 0) {
            throw new IndexOutOfBoundsException(index);
        }
        if (index == count) {
            return append(value);
        }

        JqValue[] items = new JqValue[Math.max(count, index + 1)];
        System.arraycopy(buffer.items, start, items, 0, count);
        if (index > count) {
            Arrays.fill(items, count, index, JqValue.NULL);
        }
        items[index] = value;
        return fromOwned(items);
    }

    public void copyTo(JqValue[] target, int targetIndex) {
        System.arraycopy(buffer.items, start, target, targetIndex, count);
    }

    public JqValue[] toArray() {
        JqValue[] items = new JqValue[count];
        copyTo(items, 0);
        return items;
    }

    private static int grow(int needed) {
        return Math.max(4, needed * 2);
    }

    @Override
    public String toString() {
        return ValueText.toJson(this);
    }
}
