package com.devlooped.jqlite.values;

/**
 * Strings by Unicode code point. jqlite strings are sequences of code points: length, slices,
 * ordering, explode and implode count code points, never UTF-16 units. A Java string holds them as
 * UTF-16, so a code point above U+FFFF is a surrogate pair here; every string of a {@link JqValue}
 * is well-formed (no lone surrogate, see {@link #sanitize}).
 */
public final class CodePoints {
    public static final int MAX = 0x10FFFF;
    public static final char REPLACEMENT = (char) 0xFFFD;

    private CodePoints() {
    }

    /** Replaces each lone surrogate with U+FFFD; returns {@code text} itself when it has none. */
    public static String sanitize(String text) {
        int length = text.length();
        for (int i = 0; i < length; i++) {
            char c = text.charAt(i);
            if (!Character.isSurrogate(c)) {
                continue;
            }
            if (Character.isHighSurrogate(c) && i + 1 < length && Character.isLowSurrogate(text.charAt(i + 1))) {
                i++;
                continue;
            }

            return replace(text, i);
        }

        return text;
    }

    private static String replace(String text, int from) {
        StringBuilder builder = new StringBuilder(text.length());
        builder.append(text, 0, from);
        for (int i = from; i < text.length(); i++) {
            char c = text.charAt(i);
            if (Character.isHighSurrogate(c) && i + 1 < text.length() && Character.isLowSurrogate(text.charAt(i + 1))) {
                builder.append(c).append(text.charAt(i + 1));
                i++;
            } else {
                builder.append(Character.isSurrogate(c) ? REPLACEMENT : c);
            }
        }

        return builder.toString();
    }

    /** The number of code points of a well-formed string. */
    public static int length(String text) {
        int length = text.length();
        for (int i = 0; i < text.length(); i++) {
            if (Character.isHighSurrogate(text.charAt(i))) {
                length--;
            }
        }

        return length;
    }

    /** The code points {@code start} (included) to {@code end} (excluded); 0 &lt;= start &lt;= end &lt;= length. */
    public static String substring(String text, int start, int end) {
        int from = advance(text, 0, start);
        int to = advance(text, from, end - start);
        return text.substring(from, to);
    }

    private static int advance(String text, int offset, int codePoints) {
        for (int i = 0; i < codePoints; i++) {
            offset += Character.isHighSurrogate(text.charAt(offset)) ? 2 : 1;
        }
        return offset;
    }

    /** Orders two well-formed strings by code point (the order of their UTF-8 bytes); returns -1, 0 or 1. */
    public static int compare(String a, String b) {
        int length = Math.min(a.length(), b.length());
        for (int i = 0; i < length; i++) {
            char x = a.charAt(i);
            char y = b.charAt(i);
            if (x != y) {
                return rank(x) < rank(y) ? -1 : 1;
            }
        }

        return a.length() == b.length() ? 0 : a.length() < b.length() ? -1 : 1;
    }

    // UTF-16 unit order differs from code point order in one place: a surrogate (D800-DFFF, part of
    // a code point above U+FFFF) must sort after E000-FFFF. Moving surrogates to the top fixes it
    // without decoding: at the first unit that differs, both strings are at a code point boundary.
    private static int rank(char c) {
        return c >= 0xE000 ? c - 0x800 : c >= 0xD800 ? c + 0x2000 : c;
    }

    /** The code points of a well-formed string, as numbers. */
    public static JqArray explode(String text) {
        JqValue[] items = new JqValue[length(text)];
        int count = 0;
        for (int i = 0; i < text.length(); i++) {
            int c = text.charAt(i);
            if (Character.isHighSurrogate(text.charAt(i))) {
                c = Character.toCodePoint(text.charAt(i), text.charAt(i + 1));
                i++;
            }

            items[count++] = JqValue.fromNumber(c);
        }

        return JqArray.fromOwned(items);
    }

    /** Whether {@code codePoint} is a Unicode scalar value (a code point that is not a surrogate). */
    public static boolean isScalar(int codePoint) {
        return codePoint >= 0 && codePoint <= MAX && (codePoint < 0xD800 || codePoint > 0xDFFF);
    }

    /** Appends a Unicode scalar value. */
    public static void append(StringBuilder builder, int codePoint) {
        if (codePoint < 0x10000) {
            builder.append((char) codePoint);
            return;
        }

        codePoint -= 0x10000;
        builder.append((char) (0xD800 + (codePoint >> 10)));
        builder.append((char) (0xDC00 + (codePoint & 0x3FF)));
    }

    /**
     * The number of UTF-8 bytes of the UTF-16 unit at {@code index} and of its pair, if any. The
     * number of units it covers is 2 when the result is 4, else 1 (the reference's {@code units}).
     */
    public static int utf8Length(String text, int index) {
        char c = text.charAt(index);
        boolean pair = Character.isHighSurrogate(c) && index + 1 < text.length();
        return pair ? 4 : c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
    }
}
