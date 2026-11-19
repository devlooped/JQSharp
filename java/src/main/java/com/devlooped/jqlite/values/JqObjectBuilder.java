package com.devlooped.jqlite.values;

import java.util.Arrays;
import java.util.HashMap;

/**
 * A private, mutable object under construction (object literals, {@code *}, the JSON reader,
 * merges that replace keys). {@link #toObject} hands its storage to the new {@link JqObject}
 * without copying; the builder must not be used afterwards.
 */
public final class JqObjectBuilder {
    private String[] keys;
    private JqValue[] values;
    private int count;
    private HashMap<String, Integer> index;
    private boolean built;

    public JqObjectBuilder() {
        this(4);
    }

    public JqObjectBuilder(int capacity) {
        keys = new String[Math.max(capacity, 1)];
        values = new JqValue[keys.length];
    }

    /** Starts from the entries of {@code source}, with room for 4 more. */
    public JqObjectBuilder(JqObject source) {
        this(source, 4);
    }

    /** Starts from the entries of {@code source}, with room for {@code extra} more. */
    public JqObjectBuilder(JqObject source, int extra) {
        this(source.count() + extra);
        for (int i = 0; i < source.count(); i++) {
            keys[i] = source.keyAt(i);
            values[i] = source.valueAt(i);
        }

        count = source.count();
        if (count > JqObject.LINEAR_LIMIT) {
            buildIndex();
        }
    }

    public int count() {
        return count;
    }

    /** The value of {@code key}, or Java null when absent. */
    public JqValue get(String key) {
        int i = indexOf(key);
        return i >= 0 ? values[i] : null;
    }

    /** Sets {@code key}: an existing key keeps its position, a new key goes last. */
    public void set(String key, JqValue value) {
        ensureNotBuilt();
        int i = indexOf(key);
        if (i >= 0) {
            values[i] = value;
            return;
        }

        if (count == keys.length) {
            keys = Arrays.copyOf(keys, count * 2);
            values = Arrays.copyOf(values, count * 2);
        }

        keys[count] = key;
        values[count] = value;
        count++;
        if (index != null) {
            index.put(key, count - 1);
        } else if (count > JqObject.LINEAR_LIMIT) {
            buildIndex();
        }
    }

    public boolean remove(String key) {
        ensureNotBuilt();
        int i = indexOf(key);
        if (i < 0) {
            return false;
        }

        System.arraycopy(keys, i + 1, keys, i, count - i - 1);
        System.arraycopy(values, i + 1, values, i, count - i - 1);
        count--;
        keys[count] = null;
        values[count] = null;
        index = null;
        if (count > JqObject.LINEAR_LIMIT) {
            buildIndex();
        }
        return true;
    }

    public JqObject toObject() {
        ensureNotBuilt();
        built = true;
        return JqObject.fromOwned(keys, values, count, index);
    }

    private int indexOf(String key) {
        if (index != null) {
            Integer i = index.get(key);
            return i != null ? i : -1;
        }

        for (int i = 0; i < count; i++) {
            if (keys[i].equals(key)) {
                return i;
            }
        }

        return -1;
    }

    private void buildIndex() {
        index = new HashMap<>(count * 2);
        for (int i = 0; i < count; i++) {
            index.put(keys[i], i);
        }
    }

    private void ensureNotBuilt() {
        if (built) {
            throw new IllegalStateException("The builder was already turned into an object.");
        }
    }
}
