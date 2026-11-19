package com.devlooped.jqlite.values;

import java.util.concurrent.atomic.AtomicInteger;

/**
 * An immutable string made by jq's {@code +} on strings, which later appends extend in place.
 *
 * <p>Storage is a shared buffer of UTF-16 units plus a length, as {@link JqArray} is without its
 * start. A unit of a buffer is written once, before any string that covers it exists, and never
 * again, so every {@link JqString} is immutable however many strings share its buffer.
 *
 * <p>Appending ({@link #append}) to the string whose length is the buffer's used part (its latest
 * version) claims the next free units with one compare-and-swap on {@code used} and writes there:
 * amortized O(length of the appended text), so {@code reduce range(n) as $i (""; . + "ab")}, and
 * with it {@code add} and {@code join}, is O(n). When the units are already taken (an older
 * version, or another thread won the claim) or the buffer is full, the string is copied to a new
 * buffer with twice the room. {@link #asString} builds the Java string once per version, when a
 * reader needs it.
 */
public final class JqString implements JqValue {
    private static final class Buffer {
        final char[] units;

        // Units [0, used) are claimed. Only the thread that claimed a unit writes it, before it
        // publishes the string that covers it.
        final AtomicInteger used;

        Buffer(char[] units, int used) {
            this.units = units;
            this.used = new AtomicInteger(used);
        }
    }

    /**
     * The shortest concatenation that gets a buffer: shorter ones are plain Java strings, whose
     * copies cost little and which need no second copy for {@link #asString}.
     */
    public static final int MIN_LENGTH = 256;

    private final Buffer buffer;
    private final int length;

    // The Java string of this version, built on first read. Two threads may both build it: they
    // build equal strings, and either may be kept (a String is safely published).
    private String text;

    private JqString(Buffer buffer, int length) {
        this.buffer = buffer;
        this.length = length;
    }

    /** {@code left} followed by {@code right}, in a new buffer with room to append. */
    static JqString concat(String left, String right) {
        int total = left.length() + right.length();
        char[] units = new char[grow(total)];
        left.getChars(0, left.length(), units, 0);
        right.getChars(0, right.length(), units, left.length());
        return new JqString(new Buffer(units, total), total);
    }

    @Override
    public JqKind kind() {
        return JqKind.STRING;
    }

    /** The Java string, built once per version. */
    @Override
    public String asString() {
        String built = text;
        if (built == null) {
            built = new String(buffer.units, 0, length);
            text = built;
        }
        return built;
    }

    /** The number of UTF-16 units. */
    public int length() {
        return length;
    }

    /** This string followed by {@code right}. */
    JqString append(String right) {
        int total = length + right.length();
        if (total <= buffer.units.length && buffer.used.compareAndSet(length, total)) {
            right.getChars(0, right.length(), buffer.units, length);
            return new JqString(buffer, total);
        }

        char[] units = new char[grow(total)];
        System.arraycopy(buffer.units, 0, units, 0, length);
        right.getChars(0, right.length(), units, length);
        return new JqString(new Buffer(units, total), total);
    }

    @Override
    public String toString() {
        return ValueText.toJson(this);
    }

    // Twice the room, within the longest string the JVM holds; past it the allocation fails, as
    // String.concat does.
    private static int grow(int needed) {
        return (int) Math.max(needed, Math.min(needed * 2L, ValueOps.MAX_STRING_LENGTH));
    }
}
