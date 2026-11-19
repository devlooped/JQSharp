package com.devlooped.jqlite;

import com.devlooped.jqlite.values.JqValue;

/**
 * An error of a jq expression: an uncaught runtime error, or (as {@link JqParseException}) a syntax
 * or binding error. Errors of jqlite are values inside the engine; they become exceptions only at
 * this boundary.
 */
public class JqException extends RuntimeException {
    private static final long serialVersionUID = 1L;

    private final transient JqValue value;

    /** Creates an exception with a message and no error value. */
    public JqException(String message) {
        super(message);
        this.value = null;
    }

    /** Creates an exception for a jq error value: {@code message} describes it. */
    public JqException(String message, JqValue value) {
        super(message);
        this.value = value;
    }

    /**
     * The error value of an uncaught runtime error (what {@code error(v)} raised, or the message
     * string of a builtin's error), or null. The message is the value itself when it is a string,
     * else its JSON text followed by {@code (not a string)}.
     */
    public JqValue value() {
        return value;
    }
}
