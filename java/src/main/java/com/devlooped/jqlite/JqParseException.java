package com.devlooped.jqlite;

/** A syntax or binding error, thrown by {@link Jq#parse} before any evaluation. */
public final class JqParseException extends JqException {
    private static final long serialVersionUID = 1L;

    private final int line;
    private final int column;

    JqParseException(String message, int line, int column) {
        super(message);
        this.line = line;
        this.column = column;
    }

    /** The 1-based line of the error, in the expression or in the module the message names. */
    public int line() {
        return line;
    }

    /** The 1-based column of the error, counting code points. */
    public int column() {
        return column;
    }
}
