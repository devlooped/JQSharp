package com.devlooped.jqlite.runtime;

import com.devlooped.jqlite.values.JqValue;

/**
 * Why evaluation stops besides reaching its end: evaluation returns null to go on, or one of these,
 * which every construct passes up unchanged except the one that handles it (§6.2).
 *
 * <p>A signal is an object, and its identity matters: an error-handling construct recognizes the
 * signals that came back from its consumer by identity (see Interpreter.Consumer), so a construct
 * that passes a signal up must pass the same object; signals are compared with {@code ==}.
 */
public final class Signal {
    public enum SignalKind {
        /** A jqlite error (§6.2); {@link Signal#value} is the error value. Caught by {@code try}, {@code ?} and {@code ?//}. */
        ERROR,

        /** {@code break} to the label instance {@link Signal#label}; handled by that label only. */
        BREAK,

        /** {@code halt} or {@code halt_error}: stops the whole evaluation; handled by the host only. */
        HALT,

        /** The host stopped the stream of outputs (not an error); handled by the host only. */
        STOP,
    }

    /** The one {@link SignalKind#STOP} signal. */
    public static final Signal STOP = new Signal(SignalKind.STOP, JqValue.NULL, false, 0, 0);

    public final SignalKind kind;

    /** The error value, or the halt value when {@link #hasValue}. */
    public final JqValue value;

    /** For {@link SignalKind#HALT}: whether a value goes with the exit code ({@code halt_error}). */
    public final boolean hasValue;

    /** For {@link SignalKind#BREAK}: the label instance. */
    public final long label;

    /** For {@link SignalKind#HALT}: the exit code. */
    public final int code;

    private Signal(SignalKind kind, JqValue value, boolean hasValue, long label, int code) {
        this.kind = kind;
        this.value = value;
        this.hasValue = hasValue;
        this.label = label;
        this.code = code;
    }

    public static Signal error(JqValue value) {
        return new Signal(SignalKind.ERROR, value, true, 0, 0);
    }

    public static Signal breakTo(long label) {
        return new Signal(SignalKind.BREAK, JqValue.NULL, false, label, 0);
    }

    /** A halt; {@code value} is Java null for {@code halt}. */
    public static Signal halt(JqValue value, int code) {
        return new Signal(SignalKind.HALT, value != null ? value : JqValue.NULL, value != null, 0, code);
    }

    public boolean isError() {
        return kind == SignalKind.ERROR;
    }
}
