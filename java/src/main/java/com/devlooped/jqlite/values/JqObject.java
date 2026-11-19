package com.devlooped.jqlite.values;

import java.util.Arrays;
import java.util.HashMap;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * An immutable map from string keys to values that keeps the insertion order of its keys.
 *
 * <p>Storage is a shared buffer of entries plus a count, as for {@link JqArray}. Adding a new key to
 * the latest version of a buffer claims the next slot with one compare-and-swap: amortized O(1), so
 * {@code reduce ... (.; . + {($k): $v})} and {@code setpath} on new keys do not copy the object.
 * The keys of a buffer are all distinct (a slot is only claimed for a key the version lacks), so
 * one index (key to slot) serves every version: a version with {@code count} entries owns the key
 * when its slot is below {@code count}.
 *
 * <p>Replacing or removing a key copies one level. Up to {@link #LINEAR_LIMIT} entries, lookup scans
 * the keys; above, it uses the index, built lazily and extended under a lock because versions on
 * other threads share it.
 */
public final class JqObject implements JqValue {
    static final int LINEAR_LIMIT = 8;

    private static final class Buffer {
        final String[] keys;
        final JqValue[] values;

        // Slots [0, used) are claimed; only their claimer writes them, before publishing.
        final AtomicInteger used;

        // Key to slot for slots [0, indexed); guarded by synchronized (this).
        HashMap<String, Integer> index;
        int indexed;

        Buffer(String[] keys, JqValue[] values, int used, HashMap<String, Integer> index) {
            this.keys = keys;
            this.values = values;
            this.used = new AtomicInteger(used);
            this.index = index;
            this.indexed = index == null ? 0 : index.size();
        }
    }

    public static final JqObject EMPTY = new JqObject(new Buffer(new String[0], new JqValue[0], 0, null), 0);

    private final Buffer buffer;
    private final int count;

    // The keys in code point order, computed on first use. A benign race: two threads may both
    // compute the same array, and either result is correct.
    private volatile String[] sortedKeys;

    private JqObject(Buffer buffer, int count) {
        this.buffer = buffer;
        this.count = count;
    }

    /** Creates an object over arrays the caller gives up; keys must be distinct; {@code index}, if any, maps exactly the first {@code count} keys. */
    static JqObject fromOwned(String[] keys, JqValue[] values, int count, HashMap<String, Integer> index) {
        return count == 0 ? EMPTY : new JqObject(new Buffer(keys, values, count, index), count);
    }

    @Override
    public JqKind kind() {
        return JqKind.OBJECT;
    }

    @Override
    public JqObject asObject() {
        return this;
    }

    public int count() {
        return count;
    }

    /** The key at {@code index} in insertion order. */
    public String keyAt(int index) {
        if (index < 0 || index >= count) {
            throw new IndexOutOfBoundsException(index);
        }
        return buffer.keys[index];
    }

    /** The value at {@code index} in insertion order. */
    public JqValue valueAt(int index) {
        if (index < 0 || index >= count) {
            throw new IndexOutOfBoundsException(index);
        }
        return buffer.values[index];
    }

    public boolean containsKey(String key) {
        return indexOf(key) >= 0;
    }

    /** The value of {@code key}, or Java null when absent. */
    public JqValue get(String key) {
        int index = indexOf(key);
        return index >= 0 ? buffer.values[index] : null;
    }

    /** The position of {@code key} in insertion order, or -1. */
    public int indexOf(String key) {
        if (count <= LINEAR_LIMIT) {
            for (int i = 0; i < count; i++) {
                if (buffer.keys[i].equals(key)) {
                    return i;
                }
            }

            return -1;
        }

        synchronized (buffer) {
            if (buffer.index == null) {
                buffer.index = new HashMap<>();
            }
            HashMap<String, Integer> index = buffer.index;
            // Slots below this version's count are written (it was published after them).
            for (; buffer.indexed < count; buffer.indexed++) {
                index.put(buffer.keys[buffer.indexed], buffer.indexed);
            }
            Integer slot = index.get(key);
            return slot != null && slot < count ? slot : -1;
        }
    }

    /** A copy with {@code key} set: an existing key keeps its position, a new key goes last. */
    public JqObject set(String key, JqValue value) {
        int index = indexOf(key);
        if (index < 0) {
            return add(key, value);
        }

        String[] keys = Arrays.copyOf(buffer.keys, count);
        JqValue[] values = Arrays.copyOf(buffer.values, count);
        values[index] = value;
        return fromOwned(keys, values, count, null);
    }

    // Appends a key this version does not have.
    private JqObject add(String key, JqValue value) {
        if (count < buffer.keys.length && buffer.used.compareAndSet(count, count + 1)) {
            buffer.keys[count] = key;
            buffer.values[count] = value;
            return new JqObject(buffer, count + 1);
        }

        int capacity = Math.max(4, (count + 1) * 2);
        String[] keys = new String[capacity];
        JqValue[] values = new JqValue[capacity];
        System.arraycopy(buffer.keys, 0, keys, 0, count);
        System.arraycopy(buffer.values, 0, values, 0, count);
        keys[count] = key;
        values[count] = value;
        return fromOwned(keys, values, count + 1, null);
    }

    /** A copy without {@code key}; the other keys keep their order. */
    public JqObject remove(String key) {
        int index = indexOf(key);
        if (index < 0) {
            return this;
        }

        String[] keys = new String[count - 1];
        JqValue[] values = new JqValue[count - 1];
        System.arraycopy(buffer.keys, 0, keys, 0, index);
        System.arraycopy(buffer.values, 0, values, 0, index);
        System.arraycopy(buffer.keys, index + 1, keys, index, count - index - 1);
        System.arraycopy(buffer.values, index + 1, values, index, count - index - 1);
        return fromOwned(keys, values, count - 1, null);
    }

    /** jq's {@code +} on objects: the keys of {@code other} win; existing keys keep their position, new ones follow in {@code other}'s order. */
    public JqObject merge(JqObject other) {
        if (other.count == 0) {
            return this;
        }
        if (count == 0) {
            return other;
        }

        boolean overlaps = false;
        for (int i = 0; i < other.count && !overlaps; i++) {
            overlaps = indexOf(other.buffer.keys[i]) >= 0;
        }

        if (!overlaps) {
            // Only new keys: append them, in place when this is the latest version.
            JqObject result = this;
            for (int i = 0; i < other.count; i++) {
                result = result.add(other.buffer.keys[i], other.buffer.values[i]);
            }
            return result;
        }

        JqObjectBuilder builder = new JqObjectBuilder(this, other.count);
        for (int i = 0; i < other.count; i++) {
            builder.set(other.buffer.keys[i], other.buffer.values[i]);
        }
        return builder.toObject();
    }

    /** The keys in code point order (jq's {@code keys}); computed once per object. Callers must not write the array. */
    public String[] sortedKeys() {
        String[] keys = sortedKeys;
        if (keys == null) {
            keys = Arrays.copyOf(buffer.keys, count);
            Arrays.sort(keys, CodePoints::compare);
            sortedKeys = keys;
        }

        return keys;
    }

    @Override
    public String toString() {
        return ValueText.toJson(this);
    }
}
