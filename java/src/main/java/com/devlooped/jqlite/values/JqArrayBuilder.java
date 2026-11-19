package com.devlooped.jqlite.values;

import java.util.Arrays;

/**
 * A private, mutable array under construction, the array counterpart of {@link JqObjectBuilder}.
 * {@link #toArray} hands its storage to the new {@link JqArray} without copying; the builder must
 * not be used afterwards.
 */
public final class JqArrayBuilder {
    private JqValue[] items;
    private int count;
    private boolean built;

    /** Starts from the elements of {@code source} (one level copied). */
    public JqArrayBuilder(JqArray source) {
        items = new JqValue[Math.max(4, source.count())];
        source.copyTo(items, 0);
        count = source.count();
    }

    public int count() {
        return count;
    }

    /** The element at {@code index}, 0 &lt;= index &lt; count. */
    public JqValue get(int index) {
        if (index < 0 || index >= count) {
            throw new IndexOutOfBoundsException(index);
        }
        return items[index];
    }

    /** Sets the element at {@code index} (&gt;= 0); an index past the end pads with nulls, as jq does. */
    public void set(int index, JqValue value) {
        if (built) {
            throw new IllegalStateException("The builder was already turned into an array.");
        }
        if (index >= items.length) {
            items = Arrays.copyOf(items, Math.max(index + 1, items.length * 2));
        }
        if (index > count) {
            Arrays.fill(items, count, index, JqValue.NULL);
        }
        items[index] = value;
        if (index >= count) {
            count = index + 1;
        }
    }

    public JqArray toArray() {
        if (built) {
            throw new IllegalStateException("The builder was already turned into an array.");
        }
        built = true;
        return JqArray.fromOwned(items, count);
    }
}
