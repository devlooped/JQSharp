package com.devlooped.jqlite.values;

import java.util.ArrayList;

/**
 * The one JSON writer, number formatter and JSON reader of jqlite.
 *
 * <p>Canonical text is compact JSON. Strings escape only {@code "}, {@code \\}, the control
 * characters below U+0020 ({@code \b \f \n \r \t}, the others a six-character escape of the unit, in lower-case hex) and U+007F, as jq
 * does. Numbers are the shortest digits that read back to the same double, laid out as
 * ECMAScript's {@code Number::toString}; NaN is {@code null} and ±Infinity are
 * ±{@code 1.7976931348623157e+308}.
 *
 * <p>Host-specific (porting guide §3.1): the shortest round-trip digits come from
 * {@link Double#toString(double)} (shortest since JDK 19), the decimal-to-double step from
 * {@link Double#parseDouble(String)} once the jqlite grammar is checked here.
 */
public final class ValueText {
    private static final String MAX_DOUBLE = "1.7976931348623157e+308";

    /** The deepest level the writer writes (§5.1, §11). */
    public static final int MAX_WRITE_DEPTH = 10001;

    private static final String TOO_DEEP = "<skipped: too deep>";

    private static final char[] HEX = "0123456789abcdef".toCharArray();

    private ValueText() {
    }

    public static String toJson(JqValue value) {
        StringBuilder builder = new StringBuilder();
        write(builder, value);
        return builder.toString();
    }

    /**
     * Writes the canonical text of {@code value}. No recursion: the containers being written are on
     * an explicit stack, so nesting depth is not limited by the host stack. A value nested deeper
     * than {@link #MAX_WRITE_DEPTH} levels (the outermost value is level 1) is written as
     * {@code <skipped: too deep>}, as jq 1.8.2 does for scalars and containers alike.
     */
    public static void write(StringBuilder builder, JqValue value) {
        ArrayList<JqValue> containers = new ArrayList<>();
        int[] indexes = new int[16];
        JqValue current = value;
        while (true) {
            // containers.size() is the number of containers around current: its level minus one.
            int depth = containers.size();
            if (depth >= MAX_WRITE_DEPTH) {
                builder.append(TOO_DEEP);
            } else if (writeOpening(builder, current)) {
                // A container with elements: write its first one next.
                containers.add(current);
                if (depth == indexes.length) {
                    indexes = java.util.Arrays.copyOf(indexes, depth * 2);
                }
                indexes[depth] = 0;
                current = element(builder, current, 0);
                continue;
            }

            // current is written: go on with the next element, closing the containers that end.
            while (true) {
                int top = containers.size() - 1;
                if (top < 0) {
                    return;
                }

                JqValue container = containers.get(top);
                int index = ++indexes[top];
                if (index < count(container)) {
                    builder.append(',');
                    current = element(builder, container, index);
                    break;
                }

                builder.append(container.kind() == JqKind.ARRAY ? ']' : '}');
                containers.remove(top);
            }
        }
    }

    // Writes a scalar or an empty container whole and returns false; writes the opening bracket
    // of a container with elements and returns true.
    private static boolean writeOpening(StringBuilder builder, JqValue value) {
        switch (value.kind()) {
            case NULL:
                builder.append("null");
                return false;
            case FALSE:
                builder.append("false");
                return false;
            case TRUE:
                builder.append("true");
                return false;
            case NUMBER:
                builder.append(formatNumber(value.asNumber()));
                return false;
            case STRING:
                writeString(builder, value.asString());
                return false;
            case ARRAY:
                builder.append('[');
                if (value.asArray().count() > 0) {
                    return true;
                }
                builder.append(']');
                return false;
            default:
                builder.append('{');
                if (value.asObject().count() > 0) {
                    return true;
                }
                builder.append('}');
                return false;
        }
    }

    private static int count(JqValue container) {
        return container.kind() == JqKind.ARRAY ? container.asArray().count() : container.asObject().count();
    }

    // The element at index; for an object, writes its key and colon first.
    private static JqValue element(StringBuilder builder, JqValue container, int index) {
        if (container.kind() == JqKind.ARRAY) {
            return container.asArray().get(index);
        }

        JqObject obj = container.asObject();
        writeString(builder, obj.keyAt(index));
        builder.append(':');
        return obj.valueAt(index);
    }

    /** Writes {@code text} as a JSON string literal. */
    public static void writeString(StringBuilder builder, String text) {
        builder.append('"');
        int run = 0;
        int length = text.length();
        for (int i = 0; i < length; i++) {
            char c = text.charAt(i);
            if (c >= 0x20 && c != '"' && c != '\\' && c != 0x7F) {
                continue;
            }

            builder.append(text, run, i);
            run = i + 1;
            switch (c) {
                case '"' -> builder.append("\\\"");
                case '\\' -> builder.append("\\\\");
                case '\b' -> builder.append("\\b");
                case '\f' -> builder.append("\\f");
                case '\n' -> builder.append("\\n");
                case '\r' -> builder.append("\\r");
                case '\t' -> builder.append("\\t");
                default -> builder.append("\\u00").append(HEX[(c >> 4) & 0xF]).append(HEX[c & 0xF]);
            }
        }

        builder.append(text, run, length).append('"');
    }

    /**
     * The JSON text of {@code value}; when longer than {@code maxBytes} UTF-8 bytes, its longest
     * prefix of at most {@code keepBytes} bytes that ends on a code point boundary, then "..." and
     * the closing delimiter of the value ({@code "}, {@code ]} or <code>}</code>), as jq 1.8.2 writes
     * them.
     */
    public static String toJsonTruncated(JqValue value, int maxBytes, int keepBytes) {
        String text = toJson(value);
        if (text.length() * 3 <= maxBytes || utf8Count(text) <= maxBytes) {
            return text;
        }

        int bytes = 0;
        int end = 0;
        while (end < text.length()) {
            int size = CodePoints.utf8Length(text, end);
            if (bytes + size > keepBytes) {
                break;
            }
            bytes += size;
            end += size == 4 ? 2 : 1;
        }

        JqKind kind = value.kind();
        return text.substring(0, end) + (kind == JqKind.STRING ? "...\"" : kind == JqKind.ARRAY ? "...]" : "...}");
    }

    private static int utf8Count(String text) {
        int bytes = 0;
        for (int i = 0; i < text.length();) {
            int size = CodePoints.utf8Length(text, i);
            bytes += size;
            i += size == 4 ? 2 : 1;
        }
        return bytes;
    }

    /** The canonical text of a number: shortest round-trip digits in ECMAScript {@code Number::toString} layout. */
    public static String formatNumber(double number) {
        if (Double.isNaN(number)) {
            return "null";
        }
        if (Double.isInfinite(number)) {
            return number > 0 ? MAX_DOUBLE : "-" + MAX_DOUBLE;
        }
        if (number == 0) {
            return (Double.doubleToRawLongBits(number) < 0) ? "-0" : "0";
        }
        // Fast path: integers below 1e15 print as themselves.
        if (Math.abs(number) < 1e15 && number == (double) (long) number) {
            return Long.toString((long) number);
        }

        // Java's Double.toString gives the shortest round-trip digits (JDK 19+); only the layout changes below.
        String text = Double.toString(number);
        boolean negative = text.charAt(0) == '-';
        String body = negative ? text.substring(1) : text;
        int exponent = 0;
        int e = body.indexOf('E');
        if (e >= 0) {
            exponent = Integer.parseInt(body.substring(e + 1));
            body = body.substring(0, e);
        }

        int dot = body.indexOf('.');
        String digits = dot < 0 ? body : body.substring(0, dot) + body.substring(dot + 1);
        // n: the position of the decimal point relative to the first digit (value = 0.digits * 10^n).
        int n = (dot < 0 ? body.length() : dot) + exponent;
        int lead = 0;
        while (lead < digits.length() - 1 && digits.charAt(lead) == '0') {
            lead++;
        }
        int trail = digits.length();
        while (trail > lead + 1 && digits.charAt(trail - 1) == '0') {
            trail--;
        }
        digits = digits.substring(lead, trail);
        n -= lead;
        if (digits.length() == 2) {
            // Java gives two digits where one reads back too but two are closer (Double.MIN_VALUE is
            // 4.9E-324): the shortest is one digit then.
            double magnitude = Math.abs(number);
            char low = digits.charAt(0);
            boolean lowFits = Double.parseDouble(low + "E" + (n - 1)) == magnitude;
            boolean highFits = low == '9'
                ? Double.parseDouble("1E" + n) == magnitude
                : Double.parseDouble((char) (low + 1) + "E" + (n - 1)) == magnitude;
            if (lowFits && (!highFits || digits.charAt(1) < '5')) {
                digits = String.valueOf(low);
            } else if (highFits) {
                digits = low == '9' ? "1" : String.valueOf((char) (low + 1));
                n += low == '9' ? 1 : 0;
            }
        }
        int k = digits.length();

        StringBuilder builder = new StringBuilder(32);
        if (negative) {
            builder.append('-');
        }
        if (k <= n && n <= 21) {
            builder.append(digits).repeat('0', n - k);
        } else if (0 < n && n <= 21) {
            builder.append(digits, 0, n).append('.').append(digits, n, k);
        } else if (-6 < n && n <= 0) {
            builder.append("0.").repeat('0', -n).append(digits);
        } else {
            builder.append(digits.charAt(0));
            if (k > 1) {
                builder.append('.').append(digits, 1, k);
            }
            builder.append('e').append(n - 1 < 0 ? '-' : '+').append(Math.abs(n - 1));
        }

        return builder.toString();
    }

    /**
     * Reads the numeric text of §5.3 ({@code tonumber}, and the numbers of JSON text): an optional
     * sign, then digits with an optional fraction ({@code 1.} and {@code .5} included) and exponent,
     * or, in any ASCII case, {@code nan} or {@code snan} (NaN), {@code inf} or {@code infinity}. No
     * white space. Returns the number, or Java null when the text is not one.
     */
    public static Double tryParseNumber(String text) {
        int i = 0;
        boolean negative = false;
        int length = text.length();
        if (i < length && (text.charAt(i) == '+' || text.charAt(i) == '-')) {
            negative = text.charAt(i) == '-';
            i++;
        }

        int wordLength = length - i;
        if ((wordLength == 3 && text.regionMatches(true, i, "nan", 0, 3)) || (wordLength == 4 && text.regionMatches(true, i, "snan", 0, 4))) {
            return Double.NaN;
        }

        if ((wordLength == 3 && text.regionMatches(true, i, "inf", 0, 3)) || (wordLength == 8 && text.regionMatches(true, i, "infinity", 0, 8))) {
            return negative ? Double.NEGATIVE_INFINITY : Double.POSITIVE_INFINITY;
        }

        int digits = 0;
        for (; i < length && isDigit(text.charAt(i)); i++) {
            digits++;
        }
        if (i < length && text.charAt(i) == '.') {
            for (i++; i < length && isDigit(text.charAt(i)); i++) {
                digits++;
            }
        }

        if (digits == 0) {
            return null;
        }

        if (i < length && (text.charAt(i) == 'e' || text.charAt(i) == 'E')) {
            i++;
            if (i < length && (text.charAt(i) == '+' || text.charAt(i) == '-')) {
                i++;
            }
            int exponentDigits = 0;
            for (; i < length && isDigit(text.charAt(i)); i++) {
                exponentDigits++;
            }
            if (exponentDigits == 0) {
                return null;
            }
        }

        if (i != length) {
            return null;
        }

        // The grammar is checked above; the host parser only converts (out of range gives ±Infinity or 0).
        return Double.parseDouble(text);
    }

    private static boolean isDigit(char c) {
        return c >= '0' && c <= '9';
    }

    /**
     * Reads exactly one JSON value from {@code text} (§5.3; jq's {@code fromjson}). On failure the
     * error is the whole message, {@code <reason> (while parsing '<text>')}, the reason with the
     * line and column where reading stopped (except for {@code Expected JSON value} and
     * {@code Unexpected extra JSON values}, as in jq).
     */
    public static Result tryParse(String text) {
        return new JsonReader(text, false).read();
    }

    /**
     * Reads a sequence of JSON values separated by white space (a data module, §9.11) as one array;
     * no value at all is the empty array. Errors as {@link #tryParse}.
     */
    public static Result tryParseSequence(String text) {
        return new JsonReader(text, true).read();
    }

    /**
     * A JSON reader without recursion (an explicit stack), so deep input cannot exhaust the host
     * stack. Like jq, a literal is any run of characters up to white space, a quote or one of
     * <code>[]{},:</code>; a raw control character inside a string is an error, and every lone
     * surrogate (escaped or raw) becomes U+FFFD.
     */
    private static final class JsonReader {
        private static final int MAX_DEPTH = 10000;

        private static final class Frame {
            final boolean isObject;
            JqArray array = JqArray.EMPTY;
            final JqObjectBuilder object;
            String key = "";

            Frame(boolean isObject) {
                this.isObject = isObject;
                this.object = isObject ? new JqObjectBuilder() : null;
            }

            void add(JqValue value) {
                if (object != null) {
                    object.set(key, value);
                } else {
                    array = array.append(value);
                }
            }

            JqValue toValue() {
                return object != null ? object.toObject() : array;
            }
        }

        private final String text;
        private final int length;
        private int pos;
        private JqValue failure;
        // The values read so far, when reading a sequence.
        private final ArrayList<JqValue> values;
        // The value of the last literal or string read.
        private JqValue scalar;
        private String string;

        JsonReader(String text, boolean sequence) {
            this.text = text;
            this.length = text.length();
            this.values = sequence ? new ArrayList<>() : null;
        }

        Result read() {
            JqValue result = readDocument();
            return result != null ? result : Result.err(failure);
        }

        private JqValue readDocument() {
            if (length > 0 && text.charAt(0) == (char) 0xFEFF) { // a byte order mark
                pos = 1;
            }
            skipSpace();
            if (pos == length && values != null) {
                return JqArray.EMPTY;
            }

            if (pos == length) {
                return failWithoutPosition(Errors.EXPECTED_VALUE);
            }

            ArrayList<Frame> stack = new ArrayList<>();
            while (true) {
                // A value starts at pos (white space skipped).
                if (pos == length) {
                    return fail(Errors.UNFINISHED_AT_EOF);
                }

                JqValue current;
                char c = text.charAt(pos);
                if (c == '[' || c == '{') {
                    if (stack.size() == MAX_DEPTH) {
                        return fail(Errors.DEPTH_LIMIT, pos + 1);
                    }
                    pos++;
                    skipSpace();
                    Frame frame = new Frame(c == '{');
                    if (pos < length && text.charAt(pos) == (c == '{' ? '}' : ']')) {
                        pos++;
                        current = frame.toValue();
                    } else {
                        stack.add(frame);
                        if (frame.isObject && !readKey(frame)) {
                            return null;
                        }
                        continue;
                    }
                } else if (c == '"') {
                    pos++;
                    if (!readString()) {
                        return null;
                    }
                    current = JqValue.fromString(string);
                } else if (c == ']' || c == '}') {
                    if (stack.isEmpty()) {
                        return fail(Errors.unmatched(c), pos + 1);
                    }
                    return fail(stack.get(stack.size() - 1).isObject ? Errors.KEY_VALUE_PAIRS : Errors.EXPECTED_ARRAY_ELEMENT, pos + 1);
                } else if (c == ',' || c == ':') {
                    return fail(Errors.expectedValueBefore(c), pos + 1);
                } else if (!readLiteral()) {
                    return null;
                } else {
                    current = scalar;
                }

                // Attach the value to its container; close containers as they end.
                while (true) {
                    skipSpace();
                    if (stack.isEmpty() && values != null) {
                        // A sequence goes on with the next value, if any.
                        values.add(current);
                        if (pos < length) {
                            break;
                        }
                        return JqArray.fromOwned(values.toArray(new JqValue[0]));
                    }

                    if (stack.isEmpty()) {
                        if (pos != length) {
                            return failWithoutPosition(Errors.EXTRA_VALUES);
                        }
                        return current;
                    }

                    Frame top = stack.get(stack.size() - 1);
                    top.add(current);
                    if (pos == length) {
                        return fail(Errors.UNFINISHED_AT_EOF);
                    }

                    char d = text.charAt(pos++);
                    if (d == (top.isObject ? '}' : ']')) {
                        stack.remove(stack.size() - 1);
                        current = top.toValue();
                        continue;
                    }

                    if (d != ',') {
                        return fail(d == ']' || d == '}' ? Errors.unmatched(d) : Errors.EXPECTED_SEPARATOR, pos);
                    }

                    skipSpace();
                    if (top.isObject && !readKey(top)) {
                        return null;
                    }
                    break;
                }
            }
        }

        // Reads `"key" :` and the white space after it.
        private boolean readKey(Frame frame) {
            if (pos == length) {
                return fail(Errors.UNFINISHED_AT_EOF) != null;
            }
            if (text.charAt(pos) != '"') {
                return fail(Errors.KEYS_MUST_BE_STRINGS, pos + 1) != null;
            }
            pos++;
            if (!readString()) {
                return false;
            }
            String key = string;
            skipSpace();
            if (pos == length) {
                return fail(Errors.UNFINISHED_AT_EOF) != null;
            }
            if (text.charAt(pos) != ':') {
                return fail(Errors.KEY_VALUE_PAIRS, pos + 1) != null;
            }
            pos++;
            skipSpace();
            frame.key = CodePoints.sanitize(key);
            return true;
        }

        // Reads the rest of a string after its opening quote into `string`. Escapes decode to
        // UTF-16 units as they are; JqValue.fromString then turns lone surrogates into U+FFFD.
        private boolean readString() {
            StringBuilder builder = null;
            int run = pos;
            while (true) {
                if (pos == length) {
                    return fail(Errors.UNFINISHED_AT_EOF) != null;
                }
                char c = text.charAt(pos);
                if (c == '"') {
                    if (builder == null) {
                        string = text.substring(run, pos);
                    } else {
                        builder.append(text, run, pos);
                        string = builder.toString();
                    }
                    pos++;
                    return true;
                }

                if (c < 0x20) {
                    return fail(Errors.CONTROL_CHARACTER, pos + 1) != null;
                }

                if (c != '\\') {
                    pos++;
                    continue;
                }

                if (builder == null) {
                    builder = new StringBuilder();
                }
                builder.append(text, run, pos);
                pos++;
                if (pos == length) {
                    return fail(Errors.UNFINISHED_AT_EOF) != null;
                }
                char e = text.charAt(pos++);
                switch (e) {
                    case '"', '\\', '/' -> builder.append(e);
                    case 'b' -> builder.append('\b');
                    case 'f' -> builder.append('\f');
                    case 'n' -> builder.append('\n');
                    case 'r' -> builder.append('\r');
                    case 't' -> builder.append('\t');
                    case 'u' -> {
                        int unit = 0;
                        for (int i = 0; i < 4; i++) {
                            if (pos == length) {
                                return fail(Errors.UNFINISHED_AT_EOF) != null;
                            }
                            int digit = hexValue(text.charAt(pos++));
                            if (digit < 0) {
                                return fail(Errors.INVALID_UNICODE_ESCAPE, pos) != null;
                            }
                            unit = unit * 16 + digit;
                        }

                        builder.append((char) unit);
                    }
                    default -> {
                        return fail(Errors.INVALID_ESCAPE, pos) != null;
                    }
                }

                run = pos;
            }
        }

        private boolean readLiteral() {
            int start = pos;
            while (pos < length && !isDelimiter(text.charAt(pos))) {
                pos++;
            }
            String token = text.substring(start, pos);
            switch (token) {
                case "null":
                    scalar = JqValue.NULL;
                    return true;
                case "true":
                    scalar = JqValue.TRUE;
                    return true;
                case "false":
                    scalar = JqValue.FALSE;
                    return true;
                default:
                    break;
            }

            Double number = tryParseNumber(token);
            if (number == null) {
                char first = token.charAt(0);
                boolean letter = (first >= 'a' && first <= 'z') || (first >= 'A' && first <= 'Z');
                return fail(letter ? Errors.INVALID_LITERAL : Errors.INVALID_NUMERIC_LITERAL, pos) != null;
            }
            scalar = JqValue.fromNumber(number);
            return true;
        }

        private static boolean isDelimiter(char c) {
            return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '[' || c == ']' || c == '{' || c == '}' || c == ',' || c == ':' || c == '"';
        }

        private static int hexValue(char c) {
            if (c >= '0' && c <= '9') {
                return c - '0';
            }
            if (c >= 'a' && c <= 'f') {
                return c - 'a' + 10;
            }
            if (c >= 'A' && c <= 'F') {
                return c - 'A' + 10;
            }
            return -1;
        }

        private void skipSpace() {
            while (pos < length) {
                char c = text.charAt(pos);
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r') {
                    break;
                }
                pos++;
            }
        }

        // Fails; returns Java null, the "no value" of readDocument.
        private JqValue fail(String message) {
            return fail(message, length);
        }

        // consumed: how many characters were read when the error was found; the column counts
        // them on their line, as jq does ("{" fails at line 1, column 1).
        private JqValue fail(String message, int consumed) {
            int line = 1;
            int lineStart = 0;
            for (int i = 0; i < consumed && i < length; i++) {
                if (text.charAt(i) == '\n') {
                    line++;
                    lineStart = i + 1;
                }
            }

            return failWithoutPosition(Errors.atPosition(message, line, consumed - lineStart));
        }

        private JqValue failWithoutPosition(String reason) {
            failure = Errors.whileParsing(reason, text);
            return null;
        }
    }
}
