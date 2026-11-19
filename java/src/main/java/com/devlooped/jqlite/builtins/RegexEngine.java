package com.devlooped.jqlite.builtins;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.concurrent.ConcurrentHashMap;

/**
 * The regex engine of §9.10: compiles a pattern that {@link RegexSyntax} translated and finds the
 * next match from a position.
 *
 * <p>Java: the port does not use {@code java.util.regex}, a backtracking engine that recurses once
 * per repetition of a group on the caller's thread, so that {@code ("ab" * 20000) | test("^(a|b)*$")}
 * exhausts the host stack (spec §11 forbids that). The dialect has no backreferences and no
 * lookaround (its {@code \b} and {@code \B} are conditions on one position), so this file is a small
 * linear matcher instead: the pattern compiles to a Thompson automaton, run by a Pike VM over the
 * code points of the input, with the threads in priority order so that the match and its groups are
 * those of a backtracking engine (leftmost, then the first alternative, greedy or lazy repetition).
 * Time is O(input × pattern), memory O(pattern), and nothing recurses: the pattern is read with an
 * explicit stack of groups, and the closure over the empty transitions with an explicit stack of
 * states. Offsets are UTF-16 units of the Java string; the input is walked by code point, so a match
 * never splits a surrogate pair. A port whose host engine is linear (Go, Rust) or does not recurse
 * per repetition plugs it in here instead.
 *
 * <p>The syntax read is the one RegexSyntax writes, nothing more: {@code \x{H}}, classes
 * {@code [..]} and {@code [^..]} of {@code \x{H}} and ranges, {@code (}, {@code (?:}, {@code )},
 * {@code |}, the quantifiers {@code * + ? {n} {n,} {n,m}} and their lazy forms, {@code ^},
 * {@code \z}, {@code \b}, {@code \B}. Compiled patterns are cached per process in a thread-safe map
 * that is emptied when it reaches {@link #CACHE_LIMIT} entries.
 */
final class RegexEngine {
    private static final int CACHE_LIMIT = 256;

    /** The largest automaton (in states) a pattern may compile to; {@code (x{1000}){1000}} is beyond. */
    static final int MAX_STATES = 1_000_000;

    private static final ConcurrentHashMap<String, HostPattern> CACHE = new ConcurrentHashMap<>();

    // The operations of the automaton.
    private static final int CHAR = 0;      // a = class index: consume one code point of the class
    private static final int SPLIT = 1;     // a, b = next states, a preferred
    private static final int JUMP = 2;      // a = next state
    private static final int SAVE = 3;      // a = slot
    private static final int BEGIN = 4;     // the start of the input
    private static final int END = 5;       // the end of the input
    private static final int BOUNDARY = 6;  // an ASCII word boundary
    private static final int INSIDE = 7;    // not an ASCII word boundary
    private static final int MATCH = 8;

    private RegexEngine() {
    }

    /** A compiled pattern: the automaton (op, a, b per state), its classes, and its number of groups. */
    static final class HostPattern {
        final int[] code;
        final int[][] classes;
        final boolean[] negated;
        final int groups;

        HostPattern(int[] code, int[][] classes, boolean[] negated, int groups) {
            this.code = code;
            this.classes = classes;
            this.negated = negated;
            this.groups = groups;
        }
    }

    /** A compiled pattern applied to one input, with the scratch space of the search. */
    static final class HostMatcher {
        final HostPattern pattern;
        final String input;
        int[] current;
        int[][] currentSlots;
        int[] next;
        int[][] nextSlots;
        final int[] seen;
        int generation;
        final int[] stack;
        final int[][] stackSlots;

        HostMatcher(HostPattern pattern, String input) {
            this.pattern = pattern;
            this.input = input;
            int states = pattern.code.length / 3;
            current = new int[states];
            currentSlots = new int[states][];
            next = new int[states];
            nextSlots = new int[states][];
            seen = new int[states];
            stack = new int[states + 1];
            stackSlots = new int[states + 1][];
        }
    }

    /** A compiled pattern, or Java null with the reason in {@code reason[0]}. */
    static HostPattern tryCompile(String pattern, String[] reason) {
        HostPattern compiled = CACHE.get(pattern);
        if (compiled != null) {
            return compiled;
        }

        compiled = new Compiler(pattern).compile(reason);
        if (compiled == null) {
            return null;
        }
        if (CACHE.size() >= CACHE_LIMIT) {
            CACHE.clear();
        }
        CACHE.put(pattern, compiled);
        return compiled;
    }

    /** The pattern ready to search {@code input}. */
    static HostMatcher open(HostPattern compiled, String input) {
        return new HostMatcher(compiled, input);
    }

    /**
     * The first match starting at or after UTF-16 index {@code start} (anchors and word boundaries
     * see the whole input). {@code spans} receives start and end of the match, then of each group
     * (-1, -1 for a group that did not participate): 2 · (groups + 1) entries.
     */
    static boolean tryMatch(HostMatcher search, int start, int[] spans) {
        HostPattern pattern = search.pattern;
        String input = search.input;
        int length = input.length();
        int slots = 2 * (pattern.groups + 1);
        int[] matched = null;
        int count = 0;
        int listGeneration = ++search.generation;
        for (int at = start; ; ) {
            // A thread that starts here, below every running one, until something matched.
            if (matched == null) {
                int[] seed = new int[slots];
                Arrays.fill(seed, -1);
                seed[0] = at;
                count = close(search, search.current, search.currentSlots, count, 0, seed, at, listGeneration);
            }
            if (count == 0) {
                if (matched != null || at >= length) {
                    break;
                }
                at += Character.charCount(input.codePointAt(at));
                listGeneration = ++search.generation;
                continue;
            }

            int c = at < length ? input.codePointAt(at) : -1;
            int nextAt = c < 0 ? at : at + Character.charCount(c);
            int nextGeneration = ++search.generation;
            int nextCount = 0;
            for (int i = 0; i < count; i++) {
                int state = search.current[i];
                int op = pattern.code[3 * state];
                if (op == MATCH) {
                    // The threads below this one lose.
                    matched = search.currentSlots[i].clone();
                    matched[1] = at;
                    break;
                }
                if (op == CHAR && c >= 0 && contains(pattern, pattern.code[3 * state + 1], c)) {
                    nextCount = close(search, search.next, search.nextSlots, nextCount, state + 1, search.currentSlots[i], nextAt, nextGeneration);
                }
            }

            int[] swap = search.current;
            search.current = search.next;
            search.next = swap;
            int[][] swapSlots = search.currentSlots;
            search.currentSlots = search.nextSlots;
            search.nextSlots = swapSlots;
            Arrays.fill(search.nextSlots, 0, count, null);
            count = nextCount;
            listGeneration = nextGeneration;
            if (c < 0 || (count == 0 && matched != null)) {
                break;
            }
            at = nextAt;
        }

        if (matched == null) {
            return false;
        }
        System.arraycopy(matched, 0, spans, 0, Math.min(spans.length, matched.length));
        return true;
    }

    // Adds the state and every state its empty transitions reach, in priority order, to the list;
    // the conditions are checked at position at. Returns the new count. No recursion: an explicit
    // stack, the preferred branch first. generation marks the states already in this list.
    private static int close(HostMatcher search, int[] list, int[][] listSlots, int count, int state, int[] slots, int at, int generation) {
        HostPattern pattern = search.pattern;
        int[] code = pattern.code;
        int[] stack = search.stack;
        int[][] stackSlots = search.stackSlots;
        int[] seen = search.seen;
        int top = 0;
        stack[top] = state;
        stackSlots[top++] = slots;
        while (top > 0) {
            int s = stack[--top];
            int[] threadSlots = stackSlots[top];
            stackSlots[top] = null;
            while (seen[s] != generation) {
                seen[s] = generation;
                int op = code[3 * s];
                if (op == SPLIT) {
                    stack[top] = code[3 * s + 2];
                    stackSlots[top++] = threadSlots;
                    s = code[3 * s + 1];
                } else if (op == JUMP) {
                    s = code[3 * s + 1];
                } else if (op == SAVE) {
                    threadSlots = threadSlots.clone();
                    threadSlots[code[3 * s + 1]] = at;
                    s++;
                } else if (op == BEGIN || op == END || op == BOUNDARY || op == INSIDE) {
                    if (!holds(op, search.input, at)) {
                        break;
                    }
                    s++;
                } else {
                    list[count] = s;
                    listSlots[count++] = threadSlots;
                    break;
                }
            }
        }
        return count;
    }

    private static boolean holds(int op, String input, int at) {
        return switch (op) {
            case BEGIN -> at == 0;
            case END -> at == input.length();
            default -> {
                boolean before = at > 0 && isWord(input.charAt(at - 1));
                boolean after = at < input.length() && isWord(input.charAt(at));
                yield (before != after) == (op == BOUNDARY);
            }
        };
    }

    private static boolean isWord(char c) {
        return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';
    }

    private static boolean contains(HostPattern pattern, int index, int c) {
        int[] ranges = pattern.classes[index];
        int low = 0;
        int high = ranges.length / 2 - 1;
        boolean found = false;
        while (low <= high) {
            int middle = (low + high) >>> 1;
            if (c < ranges[2 * middle]) {
                high = middle - 1;
            } else if (c > ranges[2 * middle + 1]) {
                low = middle + 1;
            } else {
                found = true;
                break;
            }
        }
        return found != pattern.negated[index];
    }

    /**
     * Reads the translated pattern into the automaton, without recursion: each open group is a frame
     * holding its finished alternatives and the pieces of the current one. A piece is a fragment of
     * code whose jumps are relative to the state that holds them, so pieces are concatenated,
     * wrapped and repeated by copying.
     */
    private static final class Compiler {
        private final String text;
        private int pos;
        private final List<int[]> classes = new ArrayList<>();
        private final List<Boolean> negated = new ArrayList<>();
        private int groups;
        private String failure;

        // A fragment: op, a, b per state; a and b of SPLIT and JUMP are relative.
        private static final class Fragment {
            int[] code = new int[24];
            int size;

            void add(int op, int a, int b) {
                if (size + 3 > code.length) {
                    code = Arrays.copyOf(code, code.length * 2);
                }
                code[size++] = op;
                code[size++] = a;
                code[size++] = b;
            }

            void append(Fragment other) {
                if (size + other.size > code.length) {
                    code = Arrays.copyOf(code, Math.max(code.length * 2, size + other.size));
                }
                System.arraycopy(other.code, 0, code, size, other.size);
                size += other.size;
            }

            int states() {
                return size / 3;
            }
        }

        private static final class Frame {
            final int capture;
            final List<Fragment> alternatives = new ArrayList<>();
            Fragment current = new Fragment();
            // The current alternative's last piece, the one a quantifier applies to.
            Fragment last;

            Frame(int capture) {
                this.capture = capture;
            }

            void flush() {
                if (last != null) {
                    current.append(last);
                    last = null;
                }
            }
        }

        Compiler(String text) {
            this.text = text;
        }

        HostPattern compile(String[] reason) {
            List<Frame> frames = new ArrayList<>();
            frames.add(new Frame(-1));
            while (pos < text.length() && failure == null) {
                Frame frame = frames.get(frames.size() - 1);
                char c = text.charAt(pos);
                switch (c) {
                    case '(' -> {
                        frame.flush();
                        int capture = -1;
                        if (text.startsWith("(?:", pos)) {
                            pos += 3;
                        } else {
                            capture = ++groups;
                            pos++;
                        }
                        frames.add(new Frame(capture));
                    }
                    case ')' -> {
                        pos++;
                        frames.remove(frames.size() - 1);
                        Fragment body = alternation(frame);
                        if (frame.capture > 0) {
                            Fragment group = new Fragment();
                            group.add(SAVE, 2 * frame.capture, 0);
                            group.append(body);
                            group.add(SAVE, 2 * frame.capture + 1, 0);
                            body = group;
                        }
                        Frame parent = frames.get(frames.size() - 1);
                        parent.last = body;
                    }
                    case '|' -> {
                        pos++;
                        frame.flush();
                        frame.alternatives.add(frame.current);
                        frame.current = new Fragment();
                    }
                    case '*', '+', '?', '{' -> quantifier(frame);
                    case '^' -> {
                        pos++;
                        frame.flush();
                        frame.last = single(BEGIN, 0);
                    }
                    case '[' -> {
                        frame.flush();
                        frame.last = single(CHAR, readClass());
                    }
                    case '\\' -> {
                        frame.flush();
                        frame.last = escape();
                    }
                    default -> failure = "unexpected '" + c + "' in the translated pattern";
                }
                if (frame.current.size > 3 * MAX_STATES) {
                    failure = "the pattern is too large";
                }
            }

            if (failure == null && frames.size() != 1) {
                failure = "missing close parenthesis";
            }
            if (failure != null) {
                reason[0] = failure;
                return null;
            }

            Fragment body = alternation(frames.get(0));
            Fragment program = new Fragment();
            program.append(body);
            program.add(MATCH, 0, 0);
            if (program.states() > MAX_STATES) {
                reason[0] = "the pattern is too large";
                return null;
            }

            // Relative jumps to absolute states.
            int[] code = Arrays.copyOf(program.code, program.size);
            for (int s = 0; s < code.length / 3; s++) {
                int op = code[3 * s];
                if (op == SPLIT) {
                    code[3 * s + 1] += s;
                    code[3 * s + 2] += s;
                } else if (op == JUMP) {
                    code[3 * s + 1] += s;
                }
            }
            boolean[] flags = new boolean[negated.size()];
            for (int i = 0; i < flags.length; i++) {
                flags[i] = negated.get(i);
            }
            return new HostPattern(code, classes.toArray(new int[0][]), flags, groups);
        }

        private static Fragment single(int op, int a) {
            Fragment fragment = new Fragment();
            fragment.add(op, a, 0);
            return fragment;
        }

        // The alternatives of a finished frame, the first preferred.
        private static Fragment alternation(Frame frame) {
            frame.flush();
            List<Fragment> all = frame.alternatives;
            all.add(frame.current);
            Fragment result = all.get(all.size() - 1);
            for (int i = all.size() - 2; i >= 0; i--) {
                Fragment first = all.get(i);
                // SPLIT +1, +k; first; JUMP +rest+1; rest
                Fragment combined = new Fragment();
                combined.add(SPLIT, 1, first.states() + 2);
                combined.append(first);
                combined.add(JUMP, result.states() + 1, 0);
                combined.append(result);
                result = combined;
            }
            return result;
        }

        private Fragment escape() {
            if (text.startsWith("\\x{", pos)) {
                int end = text.indexOf('}', pos);
                int c = Integer.parseInt(text.substring(pos + 3, end), 16);
                pos = end + 1;
                classes.add(new int[] {c, c});
                negated.add(false);
                return single(CHAR, classes.size() - 1);
            }
            char e = pos + 1 < text.length() ? text.charAt(pos + 1) : 0;
            pos += 2;
            return switch (e) {
                case 'z' -> single(END, 0);
                case 'b' -> single(BOUNDARY, 0);
                case 'B' -> single(INSIDE, 0);
                default -> {
                    failure = "unexpected escape in the translated pattern";
                    yield new Fragment();
                }
            };
        }

        // [..] or [^..] of \x{H} and \x{H}-\x{H}: the index of the class.
        private int readClass() {
            pos++;
            boolean negate = pos < text.length() && text.charAt(pos) == '^';
            if (negate) {
                pos++;
            }
            List<int[]> ranges = new ArrayList<>();
            while (pos < text.length() && text.charAt(pos) != ']') {
                int low = codePoint();
                int high = low;
                if (pos < text.length() && text.charAt(pos) == '-') {
                    pos++;
                    high = codePoint();
                }
                ranges.add(new int[] {low, high});
            }
            pos++;
            ranges.sort((x, y) -> Integer.compare(x[0], y[0]));
            int[] flat = new int[ranges.size() * 2];
            int n = 0;
            for (int[] range : ranges) {
                if (n > 0 && range[0] <= flat[n - 1] + 1) {
                    flat[n - 1] = Math.max(flat[n - 1], range[1]);
                } else {
                    flat[n++] = range[0];
                    flat[n++] = range[1];
                }
            }
            classes.add(Arrays.copyOf(flat, n));
            negated.add(negate);
            return classes.size() - 1;
        }

        private int codePoint() {
            if (!text.startsWith("\\x{", pos)) {
                failure = "unexpected class item in the translated pattern";
                pos = text.length();
                return 0;
            }
            int end = text.indexOf('}', pos);
            int c = Integer.parseInt(text.substring(pos + 3, end), 16);
            pos = end + 1;
            return c;
        }

        // * + ? {n} {n,} {n,m}, each maybe lazy, on the frame's last piece.
        private void quantifier(Frame frame) {
            int min;
            int max;
            char c = text.charAt(pos++);
            if (c == '{') {
                int end = text.indexOf('}', pos);
                String body = text.substring(pos, end);
                pos = end + 1;
                int comma = body.indexOf(',');
                min = Integer.parseInt(comma < 0 ? body : body.substring(0, comma));
                max = comma < 0 ? min : comma == body.length() - 1 ? -1 : Integer.parseInt(body.substring(comma + 1));
            } else {
                min = c == '+' ? 1 : 0;
                max = c == '?' ? 1 : -1;
            }
            boolean greedy = true;
            if (pos < text.length() && text.charAt(pos) == '?') {
                greedy = false;
                pos++;
            }
            Fragment atom = frame.last == null ? new Fragment() : frame.last;
            long size = (long) atom.states() * (max < 0 ? min + 1 : Math.max(max, 1)) + 2L * (max < 0 ? 1 : max);
            if (size > MAX_STATES) {
                failure = "the pattern is too large";
                return;
            }
            Fragment result = new Fragment();
            for (int i = 0; i < min; i++) {
                result.append(atom);
            }
            if (max < 0) {
                // L: SPLIT body, out; body; JUMP L
                result.add(SPLIT, greedy ? 1 : atom.states() + 2, greedy ? atom.states() + 2 : 1);
                result.append(atom);
                result.add(JUMP, -(atom.states() + 1), 0);
            } else {
                // (atom (atom (…)?)?)?: each optional copy jumps to the end when it is skipped.
                int optional = max - min;
                int each = atom.states() + 1;
                for (int i = 0; i < optional; i++) {
                    int toEnd = (optional - i) * each;
                    result.add(SPLIT, greedy ? 1 : toEnd, greedy ? toEnd : 1);
                    result.append(atom);
                }
            }
            frame.last = result;
        }
    }
}