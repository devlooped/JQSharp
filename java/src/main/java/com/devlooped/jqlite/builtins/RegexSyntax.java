package com.devlooped.jqlite.builtins;

import com.devlooped.jqlite.values.CodePoints;
import java.util.ArrayList;
import java.util.List;

/**
 * The regex dialect of §9.10: reads a jqlite pattern, rejects everything outside the dialect, and
 * writes an equivalent pattern for the host engine ({@link RegexEngine}) together with the names of
 * its capturing groups in the order of their '('.
 *
 * <p>The output spells out the meaning the spec demands so that no engine default leaks in. The
 * engine (RegexEngine) works on code points, so (unlike the reference, whose engine works on UTF-16
 * units) every code point is written as {@code \x{H}} and classes are written directly, astral
 * ranges included: {@code .} is the class of every code point but U+000A (or of all of them with
 * {@code m}/{@code p}), {@code \d \w \s} are ASCII classes, {@code $} is {@code \z}, {@code \b \B}
 * stay as they are (the engine's are ASCII word boundaries), named groups are plain groups. The {@code i} flag is done here, never by the engine: it folds ASCII
 * letters only ({@link RangeSet#addAsciiCases}, the one place of the rule).
 */
final class RegexSyntax {
    static final int MAX_REPEAT = 1000;

    private RegexSyntax() {
    }

    /** The flags of §9.10 that change the pattern itself. */
    record Options(boolean extended, boolean dotAll, boolean ignoreCase) {
    }

    /** A translated pattern: the engine's pattern, and one name (or null) per capturing group. */
    record Translation(String pattern, String[] groups) {
    }

    // What precedes a quantifier: only an atom may be repeated.
    private enum Previous {
        NONE,
        ATOM,
        QUANTIFIER,
        ANCHOR,
    }

    // The state of one translation.
    private static final class State {
        final int[] points;
        final StringBuilder output;
        final Options options;
        int i;
        Previous previous = Previous.NONE;
        String reason = "";

        State(int[] points, StringBuilder output, Options options) {
            this.points = points;
            this.output = output;
            this.options = options;
        }

        boolean fail(String text) {
            reason = text;
            return false;
        }
    }

    /** The translation, or Java null with the reason in {@code reason[0]}. */
    static Translation tryTranslate(String pattern, Options options, String[] reason) {
        int[] points = new int[CodePoints.length(pattern)];
        for (int i = 0, n = 0; i < pattern.length(); n++) {
            points[n] = pattern.codePointAt(i);
            i += Character.charCount(points[n]);
        }
        State state = new State(points, new StringBuilder(pattern.length() * 6), options);
        List<String> groups = new ArrayList<>();
        int open = 0;
        StringBuilder output = state.output;
        while (state.i < points.length) {
            int c = points[state.i];
            if (options.extended && (isExtendedSpace(c) || c == '#')) {
                state.i = skipExtended(points, state.i);
                continue;
            }

            boolean ok = true;
            switch (c) {
                case '\\' -> ok = tryEscape(state);
                case '[' -> {
                    ok = tryClass(state);
                    state.previous = Previous.ATOM;
                }
                case '.' -> {
                    output.append(options.dotAll ? "[\\x{0}-\\x{10FFFF}]" : "[^\\x{A}]");
                    state.previous = Previous.ATOM;
                    state.i++;
                }
                case '^' -> {
                    output.append('^');
                    state.previous = Previous.ANCHOR;
                    state.i++;
                }
                case '$' -> {
                    output.append("\\z");
                    state.previous = Previous.ANCHOR;
                    state.i++;
                }
                case '|' -> {
                    output.append('|');
                    state.previous = Previous.NONE;
                    state.i++;
                }
                case '(' -> {
                    ok = tryGroup(state, groups);
                    open++;
                    state.previous = Previous.NONE;
                }
                case ')' -> {
                    if (open == 0) {
                        ok = state.fail("unmatched close parenthesis");
                    } else {
                        open--;
                        output.append(')');
                        state.previous = Previous.ATOM;
                        state.i++;
                    }
                }
                case '*', '+', '?', '{' -> {
                    if (state.previous != Previous.ATOM) {
                        ok = state.fail(state.previous == Previous.QUANTIFIER ? "nested quantifier" : "quantifier without an operand");
                    } else {
                        ok = tryQuantifier(state);
                        state.previous = Previous.QUANTIFIER;
                    }
                }
                case ']', '}' -> ok = state.fail("unescaped '" + (char) c + "'");
                default -> {
                    literal(output, c, options);
                    state.previous = Previous.ATOM;
                    state.i++;
                }
            }

            if (!ok) {
                reason[0] = state.reason;
                return null;
            }
        }

        if (open > 0) {
            reason[0] = "missing close parenthesis";
            return null;
        }
        return new Translation(output.toString(), groups.toArray(new String[0]));
    }

    // \x outside a class: a literal, a shorthand class or an anchor.
    private static boolean tryEscape(State state) {
        int[] points = state.points;
        if (state.i + 1 == points.length) {
            return state.fail("trailing backslash");
        }
        int c = points[state.i + 1];
        switch (c) {
            case 'b', 'B' -> {
                state.output.append(c == 'b' ? "\\b" : "\\B");
                state.previous = Previous.ANCHOR;
                state.i += 2;
                return true;
            }
            case 'd', 'w', 's', 'D', 'W', 'S' -> {
                RangeSet set = new RangeSet();
                set.addShorthand(c);
                writeClass(state.output, set, false);
                state.previous = Previous.ATOM;
                state.i += 2;
                return true;
            }
            default -> {
                // A literal, below.
            }
        }

        int literal = tryEscapedLiteral(state);
        if (literal < 0) {
            return false;
        }
        literal(state.output, literal, state.options);
        state.previous = Previous.ATOM;
        return true;
    }

    // The escapes that stand for one code point: the metacharacters, \t \n \r \f \v and \xHH; -1 on error.
    private static int tryEscapedLiteral(State state) {
        int[] points = state.points;
        int i = state.i;
        if (i + 1 == points.length) {
            state.fail("trailing backslash");
            return -1;
        }
        int c = points[i + 1];
        int literal;
        switch (c) {
            case '\\', '.', '*', '+', '?', '(', ')', '[', ']', '{', '}', '|', '^', '$', '/', '-' -> literal = c;
            case 't' -> literal = '\t';
            case 'n' -> literal = '\n';
            case 'r' -> literal = '\r';
            case 'f' -> literal = '\f';
            case 'v' -> literal = 0x0B;
            case 'x' -> {
                int high = i + 2 < points.length ? hexValue(points[i + 2]) : -1;
                int low = i + 3 < points.length ? hexValue(points[i + 3]) : -1;
                if (high < 0 || low < 0) {
                    state.fail("\\x must be followed by two hex digits");
                    return -1;
                }
                state.i += 4;
                return high * 16 + low;
            }
            default -> {
                state.fail("invalid escape \\" + text(c));
                return -1;
            }
        }

        state.i += 2;
        return literal;
    }

    // ( (?: or (?<name>
    private static boolean tryGroup(State state, List<String> groups) {
        int[] points = state.points;
        int i = state.i;
        if (i + 1 < points.length && points[i + 1] == '?') {
            if (i + 2 < points.length && points[i + 2] == ':') {
                state.output.append("(?:");
                state.i += 3;
                return true;
            }

            if (i + 2 < points.length && points[i + 2] == '<') {
                int start = i + 3;
                int end = start;
                while (end < points.length && points[end] != '>') {
                    end++;
                }
                if (end == points.length || !isGroupName(points, start, end)) {
                    return state.fail("invalid group name");
                }
                String name = text(points, start, end);
                if (groups.contains(name)) {
                    return state.fail("duplicate group name " + name);
                }
                groups.add(name);
                state.output.append('(');
                state.i = end + 1;
                return true;
            }

            return state.fail("unsupported group syntax (?");
        }

        groups.add(null);
        state.output.append('(');
        state.i++;
        return true;
    }

    // * + ? {n} {n,} {n,m}, each optionally followed by ? (lazy).
    private static boolean tryQuantifier(State state) {
        int[] points = state.points;
        int c = points[state.i];
        if (c == '{') {
            int[] at = {state.i + 1};
            int min = tryNumber(points, at);
            if (min < 0) {
                return state.fail("'{' is not a quantifier");
            }
            int max = min;
            boolean unbounded = false;
            if (at[0] < points.length && points[at[0]] == ',') {
                at[0]++;
                if (at[0] < points.length && points[at[0]] == '}') {
                    unbounded = true;
                } else {
                    max = tryNumber(points, at);
                    if (max < 0) {
                        return state.fail("'{' is not a quantifier");
                    }
                }
            }

            if (at[0] == points.length || points[at[0]] != '}') {
                return state.fail("'{' is not a quantifier");
            }
            if (min > MAX_REPEAT || (!unbounded && (max > MAX_REPEAT || max < min))) {
                return state.fail("invalid repetition count");
            }
            state.output.append('{').append(min);
            if (unbounded) {
                state.output.append(',');
            } else if (max != min) {
                state.output.append(',').append(max);
            }
            state.output.append('}');
            state.i = at[0] + 1;
        } else {
            state.output.append((char) c);
            state.i++;
        }

        if (state.options.extended) {
            while (state.i < points.length && (isExtendedSpace(points[state.i]) || points[state.i] == '#')) {
                state.i = skipExtended(points, state.i);
            }
        }

        if (state.i < points.length && points[state.i] == '?') {
            state.output.append('?');
            state.i++;
        }

        return true;
    }

    // Digits at at[0], as a number capped at MAX_REPEAT + 1; -1 when there are none.
    private static int tryNumber(int[] points, int[] at) {
        int value = 0;
        int start = at[0];
        while (at[0] < points.length && points[at[0]] >= '0' && points[at[0]] <= '9') {
            value = Math.min(value * 10 + (points[at[0]] - '0'), MAX_REPEAT + 1);
            at[0]++;
        }

        return at[0] > start ? value : -1;
    }

    // [...] or [^...]: literals, escapes, ranges of literals and shorthands.
    private static boolean tryClass(State state) {
        int[] points = state.points;
        int at = state.i + 1;
        boolean negated = at < points.length && points[at] == '^';
        if (negated) {
            at++;
        }
        int first = at;
        RangeSet set = new RangeSet();
        int previousLiteral = -1;
        while (true) {
            if (at == points.length) {
                return state.fail("missing ']'");
            }
            int c = points[at];
            if (c == ']') {
                break;
            }
            if (c == '[') {
                return state.fail("unescaped '[' in a class");
            }

            if (c == '-' && at != first && at + 1 < points.length && points[at + 1] != ']') {
                // A range: the item before must be a single literal, the one after a literal too.
                if (previousLiteral < 0) {
                    return state.fail("'-' must be first, last, escaped or between two literals");
                }
                at++;
                state.i = at;
                int high = tryClassLiteral(state);
                if (high < 0) {
                    return false;
                }
                at = state.i;
                if (high < previousLiteral) {
                    return state.fail("range out of order");
                }
                set.add(previousLiteral, high);
                previousLiteral = -1;
                continue;
            }

            if (c == '\\' && at + 1 < points.length && "dwsDWS".indexOf(points[at + 1]) >= 0) {
                set.addShorthand(points[at + 1]);
                previousLiteral = -1;
                at += 2;
                continue;
            }

            state.i = at;
            int literal = tryClassLiteral(state);
            if (literal < 0) {
                return false;
            }
            at = state.i;
            set.add(literal, literal);
            previousLiteral = literal;
        }

        if (at == first) {
            return state.fail("empty class");
        }
        // The i flag folds before a negation applies: [^a] matches neither a nor A.
        if (state.options.ignoreCase) {
            set.addAsciiCases();
        }
        writeClass(state.output, set, negated);
        state.i = at + 1;
        return true;
    }

    // A literal of a class at state.i; -1 on error.
    private static int tryClassLiteral(State state) {
        int literal = state.points[state.i];
        if (literal == '\\') {
            return tryEscapedLiteral(state);
        }
        if (literal == '[' || literal == ']') {
            state.fail("unescaped '" + (char) literal + "' in a class");
            return -1;
        }
        state.i++;
        return literal;
    }

    // A class of code points, written directly (the engine matches code points); an empty set (a
    // negated class of everything) is a class no code point is in.
    private static void writeClass(StringBuilder output, RangeSet set, boolean negated) {
        List<int[]> ranges = set.ranges();
        if (ranges.isEmpty()) {
            output.append(negated ? "[\\x{0}-\\x{10FFFF}]" : "[^\\x{0}-\\x{10FFFF}]");
            return;
        }
        output.append(negated ? "[^" : "[");
        for (int[] range : ranges) {
            unit(output, range[0]);
            if (range[1] > range[0]) {
                output.append('-');
                unit(output, range[1]);
            }
        }
        output.append(']');
    }

    // A code point; under the i flag an ASCII letter becomes the class of its two cases.
    private static void literal(StringBuilder output, int c, Options options) {
        if (options.ignoreCase && ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))) {
            RangeSet set = new RangeSet();
            set.add(c, c);
            set.addAsciiCases();
            writeClass(output, set, false);
            return;
        }

        unit(output, c);
    }

    private static void unit(StringBuilder output, int codePoint) {
        output.append("\\x{").append(Integer.toHexString(codePoint)).append('}');
    }

    // With the x flag: whitespace and a # comment up to the next line feed.
    private static int skipExtended(int[] points, int i) {
        if (points[i] != '#') {
            return i + 1;
        }
        while (i < points.length && points[i] != '\n') {
            i++;
        }
        return i;
    }

    private static boolean isExtendedSpace(int c) {
        return (c >= 0x09 && c <= 0x0D) || c == 0x20;
    }

    private static boolean isGroupName(int[] points, int start, int end) {
        if (end == start || (points[start] >= '0' && points[start] <= '9')) {
            return false;
        }
        for (int i = start; i < end; i++) {
            int c = points[i];
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')) {
                return false;
            }
        }

        return true;
    }

    private static int hexValue(int c) {
        return c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
    }

    private static String text(int c) {
        StringBuilder builder = new StringBuilder();
        CodePoints.append(builder, c);
        return builder.toString();
    }

    private static String text(int[] points, int start, int end) {
        StringBuilder builder = new StringBuilder();
        for (int i = start; i < end; i++) {
            CodePoints.append(builder, points[i]);
        }
        return builder.toString();
    }

    /** A set of code points as sorted, disjoint, non-adjacent ranges ({low, high} pairs). */
    static final class RangeSet {
        private final List<int[]> ranges = new ArrayList<>();
        private boolean normalized = true;

        List<int[]> ranges() {
            normalize();
            return ranges;
        }

        void add(int low, int high) {
            ranges.add(new int[] {low, high});
            normalized = false;
        }

        // The ASCII classes of §9.10 and their complements.
        void addShorthand(int c) {
            RangeSet set = new RangeSet();
            switch (c) {
                case 'd', 'D' -> set.add('0', '9');
                case 'w', 'W' -> {
                    set.add('0', '9');
                    set.add('A', 'Z');
                    set.add('a', 'z');
                    set.add('_', '_');
                }
                default -> {
                    set.add('\t', '\r');
                    set.add(' ', ' ');
                }
            }

            for (int[] range : c == 'D' || c == 'W' || c == 'S' ? set.complement().ranges() : set.ranges()) {
                add(range[0], range[1]);
            }
        }

        /**
         * The i flag of §9.10: adds the other case of every ASCII letter in the set (A-Z and a-z, 32
         * apart); no other code point folds.
         */
        void addAsciiCases() {
            List<int[]> added = new ArrayList<>();
            for (int[] range : ranges()) {
                int upperLow = Math.max(range[0], 'A');
                int upperHigh = Math.min(range[1], 'Z');
                if (upperLow <= upperHigh) {
                    added.add(new int[] {upperLow + 32, upperHigh + 32});
                }
                int lowerLow = Math.max(range[0], 'a');
                int lowerHigh = Math.min(range[1], 'z');
                if (lowerLow <= lowerHigh) {
                    added.add(new int[] {lowerLow - 32, lowerHigh - 32});
                }
            }

            for (int[] range : added) {
                add(range[0], range[1]);
            }
        }

        RangeSet complement() {
            RangeSet complement = new RangeSet();
            int next = 0;
            for (int[] range : ranges()) {
                if (range[0] > next) {
                    complement.add(next, range[0] - 1);
                }
                next = range[1] + 1;
            }

            if (next <= CodePoints.MAX) {
                complement.add(next, CodePoints.MAX);
            }
            return complement;
        }

        private void normalize() {
            if (normalized) {
                return;
            }
            ranges.sort((a, b) -> Integer.compare(a[0], b[0]));
            List<int[]> merged = new ArrayList<>(ranges.size());
            for (int[] range : ranges) {
                if (!merged.isEmpty() && range[0] <= merged.get(merged.size() - 1)[1] + 1) {
                    int[] last = merged.get(merged.size() - 1);
                    merged.set(merged.size() - 1, new int[] {last[0], Math.max(last[1], range[1])});
                } else {
                    merged.add(range);
                }
            }

            ranges.clear();
            ranges.addAll(merged);
            normalized = true;
        }
    }
}