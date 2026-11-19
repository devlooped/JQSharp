package com.devlooped.jqlite.values;

import java.util.Arrays;

/**
 * UTF-8 encoding and decoding as plain loops over code points and bytes, for the formats that
 * work on bytes (§9.9: {@code @uri}, {@code @urid}, {@code @base64}, {@code @base64d}).
 */
public final class Utf8 {
    private Utf8() {
    }

    /** A growable list of bytes (the reference's {@code List<byte>}). */
    public static final class Bytes {
        private byte[] data = new byte[16];
        private int count;

        public int count() {
            return count;
        }

        /** The byte at {@code index}, 0 to 255. */
        public int get(int index) {
            return data[index] & 0xFF;
        }

        public void add(int value) {
            if (count == data.length) {
                data = Arrays.copyOf(data, count * 2);
            }
            data[count++] = (byte) value;
        }

        public void clear() {
            count = 0;
        }
    }

    /** The number of UTF-8 bytes of a well-formed string. */
    public static int byteCount(String text) {
        int bytes = 0;
        for (int i = 0; i < text.length(); i++) {
            int size = CodePoints.utf8Length(text, i);
            bytes += size;
            if (size == 4) {
                i++;
            }
        }
        return bytes;
    }

    /** Appends the UTF-8 bytes of a well-formed string. */
    public static void encode(String text, Bytes bytes) {
        for (int i = 0; i < text.length(); i++) {
            int c = text.charAt(i);
            if (Character.isHighSurrogate(text.charAt(i))) {
                c = 0x10000 + ((c - 0xD800) << 10) + (text.charAt(i + 1) - 0xDC00);
                i++;
            }

            encode(c, bytes);
        }
    }

    /** Appends the UTF-8 bytes of a Unicode scalar value. */
    public static void encode(int codePoint, Bytes bytes) {
        if (codePoint < 0x80) {
            bytes.add(codePoint);
        } else if (codePoint < 0x800) {
            bytes.add(0xC0 | (codePoint >> 6));
            bytes.add(0x80 | (codePoint & 0x3F));
        } else if (codePoint < 0x10000) {
            bytes.add(0xE0 | (codePoint >> 12));
            bytes.add(0x80 | ((codePoint >> 6) & 0x3F));
            bytes.add(0x80 | (codePoint & 0x3F));
        } else {
            bytes.add(0xF0 | (codePoint >> 18));
            bytes.add(0x80 | ((codePoint >> 12) & 0x3F));
            bytes.add(0x80 | ((codePoint >> 6) & 0x3F));
            bytes.add(0x80 | (codePoint & 0x3F));
        }
    }

    /** The text of well-formed UTF-8 bytes; Java null when any sequence is ill-formed. */
    public static String tryDecode(Bytes bytes) {
        StringBuilder builder = new StringBuilder(bytes.count());
        int[] codePoint = new int[1];
        for (int i = 0; i < bytes.count();) {
            i = next(bytes, i, codePoint);
            if (codePoint[0] < 0) {
                return null;
            }

            CodePoints.append(builder, codePoint[0]);
        }

        return builder.toString();
    }

    /** The text of UTF-8 bytes, each ill-formed sequence (as {@link #next} delimits it) replaced by U+FFFD. */
    public static String decodeReplacing(Bytes bytes) {
        StringBuilder builder = new StringBuilder(bytes.count());
        int[] codePoint = new int[1];
        for (int i = 0; i < bytes.count();) {
            i = next(bytes, i, codePoint);
            CodePoints.append(builder, codePoint[0] < 0 ? CodePoints.REPLACEMENT : codePoint[0]);
        }

        return builder.toString();
    }

    /**
     * Decodes the sequence at {@code index} and returns the index after it; {@code codePoint[0]} is
     * -1 when the sequence is ill-formed, and the index is then the end of its maximal ill-formed
     * subpart (Unicode §3.9, §4.1 of the spec): the bytes up to the first one that cannot continue a
     * well-formed sequence, at least one byte. The second byte's range depends on the first (E0:
     * A0-BF, ED: 80-9F, F0: 90-BF, F4: 80-8F), which rules out overlong forms, surrogates and code
     * points above U+10FFFF.
     */
    private static int next(Bytes bytes, int index, int[] codePoint) {
        int first = bytes.get(index);
        codePoint[0] = -1;
        if (first < 0x80) {
            codePoint[0] = first;
            return index + 1;
        }

        int length = first >= 0xC2 && first <= 0xDF ? 2 : first >= 0xE0 && first <= 0xEF ? 3 : first >= 0xF0 && first <= 0xF4 ? 4 : 0;
        if (length == 0) {
            return index + 1;
        }

        int low = first == 0xE0 ? 0xA0 : first == 0xF0 ? 0x90 : 0x80;
        int high = first == 0xED ? 0x9F : first == 0xF4 ? 0x8F : 0xBF;
        int value = first & (0x7F >> length);
        for (int i = 1; i < length; i++) {
            if (index + i == bytes.count()) {
                return index + i;
            }
            int next = bytes.get(index + i);
            if (next < low || next > high) {
                return index + i;
            }
            value = (value << 6) | (next & 0x3F);
            low = 0x80;
            high = 0xBF;
        }

        codePoint[0] = value;
        return index + length;
    }
}
