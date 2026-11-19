package com.devlooped.jqlite.values;

/**
 * An immutable jqlite value: null, false, true, a number (IEEE-754 double, NaN and ±Infinity
 * included), a string, an array or an object. Java's sealed interface is the tagged union of the
 * porting guide (§2.1): {@link JNull} and the two {@link JBool} are singletons, {@link JqArray},
 * {@link JqObject} and {@link JqString} (a string made by {@code +}) are values themselves.
 */
public sealed interface JqValue extends Result permits JqValue.JNull, JqValue.JBool, JqValue.JNumber, JqValue.JString, JqString, JqArray, JqObject {

    JqValue NULL = JNull.INSTANCE;
    JqValue FALSE = JBool.FALSE_VALUE;
    JqValue TRUE = JBool.TRUE_VALUE;

    JqKind kind();

    static JqValue fromBool(boolean value) {
        return value ? TRUE : FALSE;
    }

    static JqValue fromNumber(double value) {
        return new JNumber(value);
    }

    /** Creates a string value; a lone surrogate becomes U+FFFD. */
    static JqValue fromString(String value) {
        return new JString(value);
    }

    /**
     * jq's {@code +} on two strings. A long result is a {@link JqString}, which the next
     * concatenation extends in place; both sides are well-formed, so it is not sanitized again.
     */
    static JqValue concat(JqValue left, JqValue right) {
        String tail = right.asString();
        if (tail.isEmpty()) {
            return left;
        }
        if (left instanceof JqString grown) {
            return grown.append(tail);
        }

        String head = left.asString();
        if (head.isEmpty()) {
            return right;
        }
        return head.length() + tail.length() < JqString.MIN_LENGTH
            ? new JString(head.concat(tail))
            : JqString.concat(head, tail);
    }

    static JqValue fromArray(JqArray value) {
        return value;
    }

    static JqValue fromObject(JqObject value) {
        return value;
    }

    /** False for null and false, true for every other value. */
    default boolean isTruthy() {
        return true;
    }

    /** The name jq's {@code type} gives the kind: false and true are both "boolean". */
    default String typeName() {
        return switch (kind()) {
            case NULL -> "null";
            case FALSE, TRUE -> "boolean";
            case NUMBER -> "number";
            case STRING -> "string";
            case ARRAY -> "array";
            case OBJECT -> "object";
        };
    }

    // The as* accessors throw only on a programming error (asking a value for the wrong kind),
    // never as part of jqlite semantics: callers check kind() first.
    default double asNumber() {
        throw wrongKind(JqKind.NUMBER);
    }

    default String asString() {
        throw wrongKind(JqKind.STRING);
    }

    default JqArray asArray() {
        throw wrongKind(JqKind.ARRAY);
    }

    default JqObject asObject() {
        throw wrongKind(JqKind.OBJECT);
    }

    private IllegalStateException wrongKind(JqKind expected) {
        return new IllegalStateException("A " + kind() + " value is not a " + expected + ".");
    }

    /** null. */
    final class JNull implements JqValue {
        static final JNull INSTANCE = new JNull();

        private JNull() {
        }

        @Override
        public JqKind kind() {
            return JqKind.NULL;
        }

        @Override
        public boolean isTruthy() {
            return false;
        }

        @Override
        public String toString() {
            return "null";
        }
    }

    /** false or true. */
    final class JBool implements JqValue {
        static final JBool FALSE_VALUE = new JBool(false);
        static final JBool TRUE_VALUE = new JBool(true);

        private final boolean value;

        private JBool(boolean value) {
            this.value = value;
        }

        @Override
        public JqKind kind() {
            return value ? JqKind.TRUE : JqKind.FALSE;
        }

        @Override
        public boolean isTruthy() {
            return value;
        }

        @Override
        public String toString() {
            return value ? "true" : "false";
        }
    }

    /** A number. */
    record JNumber(double value) implements JqValue {
        @Override
        public JqKind kind() {
            return JqKind.NUMBER;
        }

        @Override
        public double asNumber() {
            return value;
        }

        @Override
        public String toString() {
            return ValueText.formatNumber(value);
        }
    }

    /** A string; every string of a value is well-formed (lone surrogates become U+FFFD). */
    record JString(String value) implements JqValue {
        public JString {
            value = CodePoints.sanitize(value);
        }

        @Override
        public JqKind kind() {
            return JqKind.STRING;
        }

        @Override
        public String asString() {
            return value;
        }

        @Override
        public String toString() {
            return ValueText.toJson(this);
        }
    }
}
