package com.devlooped.jqlite;

import com.devlooped.jqlite.values.JqValue;

/**
 * {@code halt_error} ended the evaluation: {@link #value} is the value it was given and
 * {@link #exitCode} its code. ({@code halt} ends an evaluation normally, without an exception.)
 */
public final class JqHaltException extends JqException {
    private static final long serialVersionUID = 1L;

    private final int exitCode;

    JqHaltException(String message, JqValue value, int exitCode) {
        super(message, value);
        this.exitCode = exitCode;
    }

    /** The exit code given to {@code halt_error}. */
    public int exitCode() {
        return exitCode;
    }
}
