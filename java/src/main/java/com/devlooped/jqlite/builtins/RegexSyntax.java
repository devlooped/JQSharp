package com.devlooped.jqlite.builtins;

import com.devlooped.jqlite.values.CodePoints;
import java.util.ArrayList;
import java.util.List;

/**
 * The regex flavor of §9.10: reads a jqlite pattern, rejects everything outside the flavor, and
 * writes an equivalent program for the engine ({@link RegexEngine}) together with the names of its
 * capturing groups in the order of their '('.
 *
 * <p>The program is made of the instructions of {@link Op}, with the meaning the spec demands
 * spelled out: {@code .}, classes and {@code \d \w \s} as sets of code points, {@code $} as the end
 * of the input, {@code \b \B} as ASCII word boundaries. The {@code i} flag is done here: it folds
 * ASCII letters only ({@link RangeSet#addAsciiCases}, the one place of the rule). No host regex
 * engine reads the flavor with one meaning, so none is used (docs/porting.md §3.3).
 *
 * <p>The limits of §11 are checked here, while the pattern is read ({@link Measure}), so that the
 * engine never meets a pattern beyond them and takes every one within.
 *
 * <p>The pattern is read in one pass, without recursion, and each instruction that jumps says how
 * far (not where to), so a piece of the program can be copied or moved as it is: a quantifier wraps
 * or repeats the instructions of the atom before it.
 */
final class RegexSyntax {
    /** The largest count of a quantifier, and of the counts of nested quantifiers multiplied. */
    static final int MAX_REPEAT = 1000;

    /** The largest size of a pattern (§9.10): its items, counted repetitions written out. */
    static final int MAX_SIZE = 10000;

    /** The most groups open at once. */
    static final int MAX_NESTING = 100;

    /** The most capturing groups. */
    static final int MAX_GROUPS = 1000;

    /** The reason of a pattern beyond MAX_SIZE, MAX_NESTING or MAX_GROUPS: a fixed text (§10.3). */
    static final String TOO_LARGE = "the pattern is too large";

    private RegexSyntax() {
    }

    /** The flags of §9.10 that change the pattern itself. */
    record Options(boolean extended, boolean dotAll, boolean ignoreCase) {
    }

    /** The instructions of a program; {@code a} and {@code b} are their operands. */
    enum Op {
        /** The code point {@code a}. */
        CHAR,

        /** A code point of the set {@code a} of the program. */
        SET,

        /** Any code point. */
        ANY,

        /** Any code point but U+000A. */
        ANY_BUT_LINE_FEED,

        /** Go on {@code a} instructions further; if that fails, {@code b} instructions further. */
        SPLIT,

        /** Go on {@code a} instructions further. */
        JUMP,

        /** Store the position in slot {@code a} (2g: the start of group g; 2g + 1: its end; group 0 is the match). */
        SAVE,

        /** The start of the input. */
        BEGIN,

        /** The end of the input. */
        END,

        /** An ASCII word boundary. */
        WORD_BOUNDARY,

        /** Not an ASCII word boundary. */
        NOT_WORD_BOUNDARY,

        /** Store the position in mark {@code a}: where an iteration of a repetition starts. */
        MARK,

        /**
         * Go on {@code b} instructions further when the position is the one of mark {@code a} (an
         * iteration that matched nothing ends its repetition); else to the next instruction.
         */
        EMPTY_EXIT,

        /** The pattern matched. */
        MATCH,
    }

    /**
     * A translated pattern: the program (an instruction and its operands {@code a}, {@code b} per
     * index), its sets of code points (sorted pairs low, high), the number of its marks, and one name
     * (or null) per capturing group.
     */
    record Translation(Op[] ops, int[] a, int[] b, int[][] sets, int marks, String[] groups) {
    }

    // What precedes a quantifier: only an atom may be repeated.
    private enum Previous {
        NONE,
        ATOM,
        QUANTIFIER,
        ANCHOR,
    }

    /** The translation, or Java null with the reason in {@code reason[0]}. */
    static Translation tryTranslate(String pattern, Options options, String[] reason) {
        return new Translator(decode(pattern), options).tryTranslate(reason);
    }

    // An open group, or the whole pattern: where it starts, where its current alternative starts, the
    // jumps of its earlier alternatives to its end, and whether it can match nothing (for the loops
    // around it): before is whether every item of the current alternative but the last can, last
    // whether the last can (null: no item), any whether an earlier alternative can.
    private static final class Frame {
        private boolean before = true;

        final int start;
        final int group;
        int alternative;
        final List<Integer> jumps = new ArrayList<>();
        Boolean last;
        boolean any;

        Frame(int start, int group) {
            this.start = start;
            this.group = group;
            this.alternative = start;
        }

        void add(boolean nullable) {
            before = before && !Boolean.FALSE.equals(last);
            last = nullable;
        }

        // Ends the current alternative.
        void close() {
            any = any || before && !Boolean.FALSE.equals(last);
            before = true;
            last = null;
        }
    }

    private static final class Translator {
        private final int[] points;
        private final Options options;
        private final List<Op> ops = new ArrayList<>();
        private final List<Integer> a = new ArrayList<>();
        private final List<Integer> b = new ArrayList<>();
        private final List<int[]> sets = new ArrayList<>();
        private final List<String> groups = new ArrayList<>();
        private final List<Frame> frames = new ArrayList<>();
        private final Measure measure = new Measure();
        private int marks;
        private int i;
        private Previous previous = Previous.NONE;
        // Where the instructions of the last atom start.
        private int atom;
        private final String[] reason = {""};

        Translator(int[] points, Options options) {
            this.points = points;
            this.options = options;
        }

        Translation tryTranslate(String[] failure) {
            if (!translate()) {
                failure[0] = reason[0];
                return null;
            }
            int[] first = new int[a.size()];
            int[] second = new int[b.size()];
            for (int k = 0; k < first.length; k++) {
                first[k] = a.get(k);
                second[k] = b.get(k);
            }
            return new Translation(ops.toArray(new Op[0]), first, second, sets.toArray(new int[0][]), marks, groups.toArray(new String[0]));
        }

        private boolean translate() {
            emit(Op.SAVE, 0, 0);
            Frame frame = new Frame(ops.size(), 0);
            frames.add(frame);
            while (i < points.length) {
                int c = points[i];
                if (options.extended() && (isExtendedSpace(c) || c == '#')) {
                    i = skipExtended(points, i);
                    continue;
                }

                switch (c) {
                    case '\\' -> {
                        if (!tryEscape()) {
                            return false;
                        }
                        if (!measure.add(1)) {
                            return fail(TOO_LARGE);
                        }
                    }
                    case '[' -> {
                        int[] items = {0};
                        if (!tryClass(items)) {
                            return false;
                        }
                        if (!measure.add(items[0])) {
                            return fail(TOO_LARGE);
                        }
                    }
                    case '.' -> {
                        startAtom();
                        emit(options.dotAll() ? Op.ANY : Op.ANY_BUT_LINE_FEED, 0, 0);
                        if (!measure.add(1)) {
                            return fail(TOO_LARGE);
                        }
                        i++;
                    }
                    case '^' -> {
                        anchor(Op.BEGIN);
                        if (!measure.add(1)) {
                            return fail(TOO_LARGE);
                        }
                        i++;
                    }
                    case '$' -> {
                        anchor(Op.END);
                        if (!measure.add(1)) {
                            return fail(TOO_LARGE);
                        }
                        i++;
                    }
                    case '|' -> {
                        if (!measure.add(1)) {
                            return fail(TOO_LARGE);
                        }
                        // The alternative so far, or the rest: it ends with a jump to the end of the group.
                        insert(frame.alternative, Op.SPLIT, 1, ops.size() - frame.alternative + 2);
                        frame.jumps.add(ops.size());
                        emit(Op.JUMP, 0, 0);
                        frame.alternative = ops.size();
                        frame.close();
                        previous = Previous.NONE;
                        i++;
                    }
                    case '(' -> {
                        if (measure.depth() == MAX_NESTING) {
                            return fail(TOO_LARGE);
                        }
                        int start = ops.size();
                        if (!tryGroup()) {
                            return false;
                        }
                        if (!measure.open()) {
                            return fail(TOO_LARGE);
                        }
                        frame = new Frame(start, ops.size() > start ? groups.size() : 0);
                        frame.alternative = ops.size();
                        frames.add(frame);
                        previous = Previous.NONE;
                    }
                    case ')' -> {
                        if (frames.size() == 1) {
                            return fail("unmatched close parenthesis");
                        }
                        measure.close();
                        end(frame);
                        if (frame.group > 0) {
                            emit(Op.SAVE, 2 * frame.group + 1, 0);
                        }
                        frames.remove(frames.size() - 1);
                        Frame group = frame;
                        frame = frames.get(frames.size() - 1);
                        frame.add(group.any);
                        atom = group.start;
                        previous = Previous.ATOM;
                        i++;
                    }
                    case '*', '+', '?', '{' -> {
                        if (previous != Previous.ATOM) {
                            return fail(previous == Previous.QUANTIFIER ? "nested quantifier" : "quantifier without an operand");
                        }
                        if (!tryQuantifier(frame)) {
                            return false;
                        }
                        previous = Previous.QUANTIFIER;
                    }
                    case ']', '}' -> {
                        return fail("unescaped '" + (char) c + "'");
                    }
                    default -> {
                        literal(c);
                        if (!measure.add(1)) {
                            return fail(TOO_LARGE);
                        }
                        i++;
                    }
                }
            }

            if (frames.size() > 1) {
                return fail("missing close parenthesis");
            }
            end(frame);
            emit(Op.SAVE, 1, 0);
            emit(Op.MATCH, 0, 0);
            return true;
        }

        // The end of a group or of the pattern: the jumps of its alternatives arrive here.
        private void end(Frame frame) {
            for (int jump : frame.jumps) {
                a.set(jump, ops.size() - jump);
            }
            frame.close();
        }

        private void emit(Op op, int first, int second) {
            ops.add(op);
            a.add(first);
            b.add(second);
        }

        private void insert(int at, Op op, int first, int second) {
            ops.add(at, op);
            a.add(at, first);
            b.add(at, second);
        }

        // An atom starts here: a quantifier after it repeats the instructions from here on.
        private void startAtom() {
            atom = ops.size();
            previous = Previous.ATOM;
            frames.get(frames.size() - 1).add(false);
        }

        private void anchor(Op op) {
            emit(op, 0, 0);
            previous = Previous.ANCHOR;
            frames.get(frames.size() - 1).add(true);
        }

        // \x outside a class: a literal, a shorthand class or an anchor.
        private boolean tryEscape() {
            if (i + 1 == points.length) {
                return fail("trailing backslash");
            }
            int c = points[i + 1];
            switch (c) {
                case 'b', 'B' -> {
                    anchor(c == 'b' ? Op.WORD_BOUNDARY : Op.NOT_WORD_BOUNDARY);
                    i += 2;
                    return true;
                }
                case 'd', 'w', 's', 'D', 'W', 'S' -> {
                    RangeSet set = new RangeSet();
                    set.addShorthand(c);
                    startAtom();
                    addClass(set, false);
                    i += 2;
                    return true;
                }
                default -> {
                    // A literal, below.
                }
            }

            int[] at = {i};
            int literal = tryEscapedLiteral(points, at, reason);
            if (literal < 0) {
                return false;
            }
            i = at[0];
            literal(literal);
            return true;
        }

        // ( (?: or (?<name>: a capturing group starts with the instruction that stores its start.
        private boolean tryGroup() {
            if (i + 1 < points.length && points[i + 1] == '?') {
                if (i + 2 < points.length && points[i + 2] == ':') {
                    i += 3;
                    return true;
                }

                if (i + 2 < points.length && points[i + 2] == '<') {
                    int start = i + 3;
                    int end = start;
                    while (end < points.length && points[end] != '>') {
                        end++;
                    }
                    if (end == points.length || !isGroupName(points, start, end)) {
                        return fail("invalid group name");
                    }
                    String name = text(points, start, end);
                    if (groups.contains(name)) {
                        return fail("duplicate group name " + name);
                    }
                    if (groups.size() == MAX_GROUPS) {
                        return fail(TOO_LARGE);
                    }
                    groups.add(name);
                    emit(Op.SAVE, 2 * groups.size(), 0);
                    i = end + 1;
                    return true;
                }

                return fail("unsupported group syntax (?");
            }

            if (groups.size() == MAX_GROUPS) {
                return fail(TOO_LARGE);
            }
            groups.add(null);
            emit(Op.SAVE, 2 * groups.size(), 0);
            i++;
            return true;
        }

        // * + ? {n} {n,} {n,m}, each optionally followed by ? (lazy).
        private boolean tryQuantifier(Frame frame) {
            int c = points[i];
            int min;
            // -1: no maximum.
            int max;
            // The count of §9.10: how many times the operand is written out.
            int count = 1;
            if (c == '{') {
                int[] at = {i + 1};
                min = tryNumber(points, at);
                if (min < 0) {
                    return fail("'{' is not a quantifier");
                }
                max = min;
                boolean unbounded = false;
                if (at[0] < points.length && points[at[0]] == ',') {
                    at[0]++;
                    if (at[0] < points.length && points[at[0]] == '}') {
                        unbounded = true;
                    } else {
                        max = tryNumber(points, at);
                        if (max < 0) {
                            return fail("'{' is not a quantifier");
                        }
                    }
                }

                if (at[0] == points.length || points[at[0]] != '}') {
                    return fail("'{' is not a quantifier");
                }
                if (min > MAX_REPEAT || (!unbounded && (max > MAX_REPEAT || max < min))) {
                    return fail("invalid repetition count");
                }
                count = Math.max(unbounded ? min : max, 1);
                if (!measure.multiply(count)) {
                    return fail("invalid repetition count");
                }
                if (unbounded) {
                    max = -1;
                }
                i = at[0] + 1;
            } else {
                min = c == '+' ? 1 : 0;
                max = c == '?' ? 1 : -1;
                i++;
            }

            if (!measure.repeat(count)) {
                return fail(TOO_LARGE);
            }

            if (options.extended()) {
                while (i < points.length && (isExtendedSpace(points[i]) || points[i] == '#')) {
                    i = skipExtended(points, i);
                }
            }

            boolean lazy = false;
            if (i < points.length && points[i] == '?') {
                lazy = true;
                i++;
            }

            return repeat(frame, min, max, lazy);
        }

        // Repeats the last atom: its instructions min times, then a loop (no maximum) or max - min
        // optional copies. Each copy is an iteration. When the atom can match nothing and there can be
        // several iterations, each marks where it starts, and one that matched nothing ends the
        // repetition, whatever its counts (§9.10): "ca" | match("(c??){2}a") captures "" at 1.
        private boolean repeat(Frame frame, int min, int max, boolean lazy) {
            int start = atom;
            int length = ops.size() - start;
            boolean nullable = !Boolean.FALSE.equals(frame.last);
            boolean marked = nullable && (max < 0 || max > 1);
            int iteration = length + (marked ? 2 : 0);
            int size = min * iteration + (max < 0 ? iteration + 2 : (max - min) * (iteration + 1));
            Body body = new Body(ops, a, b, start, length, marked ? marks++ : -1, start + size);
            ops.subList(start, start + length).clear();
            a.subList(start, start + length).clear();
            b.subList(start, start + length).clear();
            for (int n = 0; n < min; n++) {
                copy(body);
            }
            if (max < 0) {
                // again: split (iteration, end); iteration; jump again; end:
                int again = ops.size();
                emit(Op.SPLIT, lazy ? body.end - again : 1, lazy ? 1 : body.end - again);
                copy(body);
                emit(Op.JUMP, again - ops.size(), 0);
            } else {
                // split (iteration, end); iteration; split (iteration, end); iteration; …; end:
                for (int n = max - min; n > 0; n--) {
                    int exit = body.end - ops.size();
                    emit(Op.SPLIT, lazy ? exit : 1, lazy ? 1 : exit);
                    copy(body);
                }
            }

            frame.last = min == 0 || nullable;
            return true;
        }

        // One iteration of a repetition: the atom, between its mark and the exit of an empty iteration.
        private void copy(Body body) {
            if (body.mark >= 0) {
                emit(Op.MARK, body.mark, 0);
            }
            for (int k = 0; k < body.ops.length; k++) {
                emit(body.ops[k], body.a[k], body.b[k]);
            }
            if (body.mark >= 0) {
                emit(Op.EMPTY_EXIT, body.mark, body.end - ops.size());
            }
        }

        // [...] or [^...]: literals, escapes, ranges of literals and shorthands; items[0] is their number.
        private boolean tryClass(int[] items) {
            items[0] = 0;
            int[] at = {i + 1};
            boolean negated = at[0] < points.length && points[at[0]] == '^';
            if (negated) {
                at[0]++;
            }
            int first = at[0];
            RangeSet set = new RangeSet();
            int previousLiteral = -1;
            while (true) {
                if (at[0] == points.length) {
                    return fail("missing ']'");
                }
                int c = points[at[0]];
                if (c == ']') {
                    break;
                }
                if (c == '[') {
                    return fail("unescaped '[' in a class");
                }

                if (c == '-' && at[0] != first && at[0] + 1 < points.length && points[at[0] + 1] != ']') {
                    // A range: the item before must be a single literal, the one after a literal too.
                    if (previousLiteral < 0) {
                        return fail("'-' must be first, last, escaped or between two literals");
                    }
                    at[0]++;
                    int high = tryClassLiteral(points, at, reason);
                    if (high < 0) {
                        return false;
                    }
                    if (high < previousLiteral) {
                        return fail("range out of order");
                    }
                    set.add(previousLiteral, high);
                    previousLiteral = -1;
                    continue;
                }

                if (c == '\\' && at[0] + 1 < points.length && isShorthand(points[at[0] + 1])) {
                    set.addShorthand(points[at[0] + 1]);
                    previousLiteral = -1;
                    items[0]++;
                    at[0] += 2;
                    continue;
                }

                int literal = tryClassLiteral(points, at, reason);
                if (literal < 0) {
                    return false;
                }
                set.add(literal, literal);
                previousLiteral = literal;
                items[0]++;
            }

            if (at[0] == first) {
                return fail("empty class");
            }
            // The i flag folds before a negation applies: [^a] matches neither a nor A.
            if (options.ignoreCase()) {
                set.addAsciiCases();
            }
            startAtom();
            addClass(set, negated);
            i = at[0] + 1;
            return true;
        }

        // A class as a set of the program: a negated class is its complement.
        private void addClass(RangeSet set, boolean negated) {
            List<int[]> ranges = (negated ? set.complement() : set).ranges();
            int[] pairs = new int[2 * ranges.size()];
            for (int k = 0; k < ranges.size(); k++) {
                pairs[2 * k] = ranges.get(k)[0];
                pairs[2 * k + 1] = ranges.get(k)[1];
            }

            sets.add(pairs);
            emit(Op.SET, sets.size() - 1, 0);
        }

        // A code point; under the i flag an ASCII letter becomes the class of its two cases.
        private void literal(int c) {
            startAtom();
            if (options.ignoreCase() && ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))) {
                RangeSet set = new RangeSet();
                set.add(c, c);
                set.addAsciiCases();
                addClass(set, false);
                return;
            }

            emit(Op.CHAR, c, 0);
        }

        private boolean fail(String text) {
            reason[0] = text;
            return false;
        }
    }

    // The instructions of a repeated atom; its mark (-1 when its iterations are not marked) and
    // the index where the repetition ends.
    private static final class Body {
        final Op[] ops;
        final int[] a;
        final int[] b;
        final int mark;
        final int end;

        Body(List<Op> ops, List<Integer> a, List<Integer> b, int start, int length, int mark, int end) {
            this.ops = new Op[length];
            this.a = new int[length];
            this.b = new int[length];
            for (int k = 0; k < length; k++) {
                this.ops[k] = ops.get(start + k);
                this.a[k] = a.get(start + k);
                this.b[k] = b.get(start + k);
            }
            this.mark = mark;
            this.end = end;
        }
    }

    // The escapes that stand for one code point: the metacharacters, \t \n \r \f \v and \xHH; the
    // literal, at at[0] past it, or -1 with the reason in reason[0].
    private static int tryEscapedLiteral(int[] points, int[] at, String[] reason) {
        int i = at[0];
        if (i + 1 == points.length) {
            return fail("trailing backslash", reason);
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
                    return fail("\\x must be followed by two hex digits", reason);
                }
                at[0] = i + 4;
                return high * 16 + low;
            }
            default -> {
                return fail("invalid escape \\" + text(c), reason);
            }
        }

        at[0] = i + 2;
        return literal;
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

    // A literal of a class at at[0]; -1 with the reason in reason[0].
    private static int tryClassLiteral(int[] points, int[] at, String[] reason) {
        int literal = points[at[0]];
        if (literal == '\\') {
            return tryEscapedLiteral(points, at, reason);
        }
        if (literal == '[' || literal == ']') {
            return fail("unescaped '" + (char) literal + "' in a class", reason);
        }
        at[0]++;
        return literal;
    }

    private static boolean isShorthand(int c) {
        return c == 'd' || c == 'w' || c == 's' || c == 'D' || c == 'W' || c == 'S';
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

    private static int[] decode(String text) {
        int[] points = new int[CodePoints.length(text)];
        for (int i = 0, n = 0; i < text.length(); n++) {
            if (Character.isHighSurrogate(text.charAt(i)) && i + 1 < text.length()) {
                points[n] = Character.toCodePoint(text.charAt(i), text.charAt(i + 1));
                i += 2;
            } else {
                points[n] = text.charAt(i);
                i++;
            }
        }

        return points;
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

    private static int fail(String text, String[] reason) {
        reason[0] = text;
        return -1;
    }

    /**
     * The limits of §11 on the part of a pattern read so far: its size, the groups open, and the
     * counts of the counted quantifiers nested in one another.
     */
    private static final class Measure {
        // Per open group: the size when it opened; and, with one more for the pattern itself, the
        // largest product of the counts nested in it so far.
        private final int[] starts = new int[MAX_NESTING];
        private final int[] products = new int[MAX_NESTING + 1];
        private int depth;
        private int size;
        // The last item, the operand of a quantifier: its size and the largest product of the counts nested in it.
        private int atom;
        private int atomProduct;

        Measure() {
            products[0] = 1;
        }

        /** The groups open. */
        int depth() {
            return depth;
        }

        /** An item of this size (a literal, a class, an anchor, a '|'); false when the pattern is too large. */
        boolean add(int itemSize) {
            // A class may have more items than an int holds once added to the size.
            itemSize = Math.min(itemSize, MAX_SIZE + 1);
            atom = itemSize;
            atomProduct = 1;
            size += itemSize;
            return size <= MAX_SIZE;
        }

        /** A group opens: it counts 1; false when the pattern is too large. */
        boolean open() {
            starts[depth] = size;
            depth++;
            products[depth] = 1;
            return add(1);
        }

        /** A group closes: it is the last item, with all it holds. */
        void close() {
            atom = size - starts[depth - 1];
            atomProduct = products[depth];
            depth--;
            products[depth] = Math.max(products[depth], atomProduct);
        }

        /** A counted quantifier on the last item; false when the nested counts multiply above MAX_REPEAT. */
        boolean multiply(int count) {
            int product = count * atomProduct;
            products[depth] = Math.max(products[depth], product);
            return product <= MAX_REPEAT;
        }

        /** A quantifier on the last item: it counts 1, and the item once more for each count above 1; false when the pattern is too large. */
        boolean repeat(int count) {
            size += atom * (count - 1) + 1;
            return size <= MAX_SIZE;
        }
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
            ranges.sort((x, y) -> Integer.compare(x[0], y[0]));
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
