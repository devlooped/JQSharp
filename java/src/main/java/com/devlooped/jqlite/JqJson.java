package com.devlooped.jqlite;

import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;
import com.devlooped.jqlite.values.ValueText;

/**
 * Host values from and to JSON text. Java has no standard ordered JSON value, so the host works
 * with the engine's own immutable {@link JqValue} (build one with {@code JqValue.fromNumber},
 * {@code JqArray.of}, {@code JqObjectBuilder}, or read it from text here). The reader and the writer
 * are jqlite's (spec §5): object keys keep their order, the text written is canonical (NaN as
 * {@code null}, infinities as ±1.7976931348623157e+308).
 */
public final class JqJson {
    private JqJson() {
    }

    /** Reads exactly one JSON value. */
    public static JqValue parse(String json) {
        Result value = ValueText.tryParse(json);
        if (value instanceof Result.Err err) {
            throw new JqException(err.error().asString(), err.error());
        }
        return (JqValue) value;
    }

    /** The canonical (compact) JSON text of a value. */
    public static String toJson(JqValue value) {
        return ValueText.toJson(value);
    }
}
