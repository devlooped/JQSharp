package com.devlooped.jqlite.runtime;

import com.devlooped.jqlite.values.JqArray;
import com.devlooped.jqlite.values.JqValue;

/**
 * Where a value came from, in path mode (§8.1): the keys from the root input, as an immutable list
 * linked from the last key, so appending is O(1) and shares the prefix.
 *
 * <p>Evaluation takes and emits a {@code Path} that may be Java null: null in value mode (no path
 * is ever built), and in path mode either a tracked path ({@link #ROOT} and what is appended to it)
 * or {@link #UNTRACKED}, for an output that has no path (a literal, a variable, an arithmetic result).
 */
public final class Path {
    /** The empty path: the root input in path mode. */
    public static final Path ROOT = new Path(null, JqValue.NULL, 0);

    /** An output in path mode that has no path. */
    public static final Path UNTRACKED = new Path(null, JqValue.NULL, -1);

    private final Path parent;
    private final JqValue key;
    private final int length;

    private Path(Path parent, JqValue key, int length) {
        this.parent = parent;
        this.key = key;
        this.length = length;
    }

    public boolean isTracked() {
        return length >= 0;
    }

    /** The path of an output without one: null in value mode, else {@link #UNTRACKED}. */
    public static Path of(Path mode) {
        return mode == null ? null : UNTRACKED;
    }

    /** This path followed by {@code next}; this path must be tracked. */
    public Path append(JqValue next) {
        return new Path(this, next, length + 1);
    }

    /** This path followed by the elements of {@code keys}; this path must be tracked. */
    public Path append(JqArray keys) {
        Path path = this;
        for (int i = 0; i < keys.count(); i++) {
            path = path.append(keys.get(i));
        }
        return path;
    }

    /** The keys as a jqlite array, first key first; this path must be tracked. */
    public JqValue toValue() {
        JqValue[] keys = new JqValue[length];
        Path path = this;
        for (int i = length - 1; i >= 0; i--) {
            keys[i] = path.key;
            path = path.parent;
        }

        return JqArray.fromOwned(keys);
    }
}
