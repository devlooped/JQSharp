package com.devlooped.jqlite.values;

/**
 * The outcome of an operation that jq can fail (porting guide §2.2): a {@link JqValue} on success
 * (every value is its own successful result, so success allocates nothing), or an {@link Err}
 * holding the jqlite error value (usually the message string). Nothing here is thrown.
 */
public sealed interface Result permits JqValue, Result.Err {

    /** A failed operation: {@code error} is the jqlite error value. */
    record Err(JqValue error) implements Result {
    }

    static Result err(JqValue error) {
        return new Err(error);
    }
}
