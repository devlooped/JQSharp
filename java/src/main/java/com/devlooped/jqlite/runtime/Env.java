package com.devlooped.jqlite.runtime;

import com.devlooped.jqlite.binding.Bound.Node;
import com.devlooped.jqlite.values.JqValue;

/**
 * One frame of the environment, linked to the frame outside it; the empty environment is null. A
 * frame holds the variables of one binding ({@link #values}), the closures of one call of a function
 * with parameters ({@link #closures}), or one label instance ({@link #label}); the resolver numbered
 * the frames, so a reference walks a known number of frames outward (Bound.java). Frames are never
 * changed once built, so they can be shared freely.
 */
public final class Env {
    /** A filter argument: the argument's node with the caller's environment (§7.7). */
    public static final class Closure {
        public final Node node;
        public final Env env;

        public Closure(Node node, Env env) {
            this.node = node;
            this.env = env;
        }
    }

    public final Env parent;
    public final JqValue[] values;
    public final Closure[] closures;
    public final long label;

    public Env(Env parent, JqValue[] values) {
        this.parent = parent;
        this.values = values;
        this.closures = null;
        this.label = 0;
    }

    public Env(Env parent, Closure[] closures) {
        this.parent = parent;
        this.values = null;
        this.closures = closures;
        this.label = 0;
    }

    public Env(Env parent, long label) {
        this.parent = parent;
        this.values = null;
        this.closures = null;
        this.label = label;
    }

    /** The environment {@code hops} frames out from {@code env}. */
    public static Env up(Env env, int hops) {
        for (; hops > 0; hops--) {
            env = env.parent;
        }
        return env;
    }
}
