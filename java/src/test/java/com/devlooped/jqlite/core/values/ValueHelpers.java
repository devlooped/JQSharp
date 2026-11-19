package com.devlooped.jqlite.core.values;

import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;
import com.devlooped.jqlite.values.ValueText;
import org.junit.jupiter.api.Assertions;

/** Reads and writes test values. */
public final class ValueHelpers {
    private ValueHelpers() {
    }

    /** Reads JSON text; fails the test when it is not valid. */
    public static JqValue v(String json) {
        Result value = ValueText.tryParse(json);
        if (value instanceof Result.Err err) {
            Assertions.fail("Bad test JSON " + json + ": " + err.error().asString());
        }
        return (JqValue) value;
    }

    /** The canonical JSON text of a value. */
    public static String j(JqValue value) {
        return ValueText.toJson(value);
    }

    /** A result as JSON text, or "error: message". */
    public static String text(Result result) {
        return result instanceof Result.Err err ? "error: " + err.error().asString() : j((JqValue) result);
    }
}
