package com.devlooped.jqlite.syntax;

/**
 * The one error a lexing, parsing or binding run returns: a message and the 1-based position of the
 * token where it was detected, in the main program or, when {@code module} is set, in the module of
 * that identity (§9.11).
 */
public record SyntaxError(String message, int line, int column, String module) {
    public SyntaxError(String message, int line, int column) {
        this(message, line, column, null);
    }
}
