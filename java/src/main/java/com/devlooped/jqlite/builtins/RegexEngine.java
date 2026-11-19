package com.devlooped.jqlite.builtins;

import com.devlooped.jqlite.builtins.RegexSyntax.Op;
import com.devlooped.jqlite.builtins.RegexSyntax.Options;
import com.devlooped.jqlite.builtins.RegexSyntax.Translation;
import java.util.Arrays;
import java.util.concurrent.ConcurrentHashMap;

/**
 * The engine of §9.10: compiles a pattern through {@link RegexSyntax} and finds the next match from
 * a position. It is portable code, no host regex engine: none reads the flavor with one meaning
 * (docs/porting.md §3.3).
 *
 * <p>The matcher reads the input once, from the start position on, and follows every way the
 * program can match at the same time (a Pike machine): a thread is an instruction that waits for a
 * code point, with the captures of the way that led to it. The threads are kept in the order a
 * backtracking matcher would try them (a greedy quantifier takes all it can first, a lazy one as
 * little, the left alternative first), and a state reached a second time at the same position is
 * dropped: the first way to it has priority and the same future. So the time is the length of the
 * input times the size of the program, and the memory depends on the program alone. The
 * translation bounds the program (§11), so nothing is refused here: within those limits a program
 * has at most 50,000 instructions, a hundred levels and 2,002 capture slots per thread. A search for
 * the leftmost match does not start again at each position: a new thread starts at each position,
 * behind the ones already running, until a match is found. Nothing recurses.
 *
 * <p>A loop whose body can match nothing ends after an iteration that matched nothing
 * ({@link Op#MARK} and {@link Op#EMPTY_EXIT}). Whether an iteration has matched something yet
 * belongs to the state of a thread, as its level: of the loops with a mark around its instruction,
 * how many have, counted from the outermost (if an outer iteration has matched nothing, the inner
 * ones it contains have not either).
 *
 * <p>The input is read by code point: a match never splits a surrogate pair, and the positions are
 * UTF-16 indices. Compiled programs are immutable and cached per process in a thread-safe map that
 * is emptied when it reaches {@link #CACHE_LIMIT} entries; the memory of a search is a
 * {@link RegexMatcher} of its own.
 */
final class RegexEngine {
    private static final int CACHE_LIMIT = 256;

    private static final ConcurrentHashMap<String, RegexProgram> CACHE = new ConcurrentHashMap<>();

    private RegexEngine() {
    }

    /** The program of {@code pattern} under {@code options}, or Java null with why the pattern is not of the flavor or beyond its limits in {@code reason[0]}. */
    static RegexProgram tryCompile(String pattern, Options options, String[] reason) {
        String key = (options.extended() ? "x" : "-") + (options.dotAll() ? "m" : "-") + (options.ignoreCase() ? "i" : "-") + pattern;
        RegexProgram program = CACHE.get(key);
        if (program != null) {
            return program;
        }
        Translation translation = RegexSyntax.tryTranslate(pattern, options, reason);
        if (translation == null) {
            return null;
        }
        program = new RegexProgram(translation);
        if (CACHE.size() >= CACHE_LIMIT) {
            CACHE.clear();
        }
        CACHE.put(key, program);
        return program;
    }

    /** A translated pattern ready to run: immutable, shared by the searches of every thread. */
    static final class RegexProgram {
        /** One name (or null) per capturing group. */
        final String[] groups;

        /** The capture slots of a thread: 2 · (groups + 1). */
        final int slots;

        /** The states of the program: its instructions, each at every level it can have. */
        final int states;

        /** The instructions that read a code point. */
        final int readers;

        final Op[] ops;
        final int[] a;
        final int[] b;
        final int[][] sets;

        /** Per instruction: the loops with a mark around it. */
        final int[] depth;

        /** Per instruction: its first state (the one of level 0). */
        final int[] base;

        /** The UTF-16 unit a match must start with, or -1. */
        final int literal;

        /** Whether a match can only start at the start of the input. */
        final boolean anchored;

        RegexProgram(Translation translation) {
            Op[] ops = translation.ops();
            this.ops = ops;
            a = translation.a();
            b = translation.b();
            sets = translation.sets();
            groups = translation.groups();
            slots = 2 * (translation.groups().length + 1);
            depth = new int[ops.length];
            base = new int[ops.length];
            int level = 0;
            int count = 0;
            int reading = 0;
            for (int pc = 0; pc < ops.length; pc++) {
                depth[pc] = level;
                base[pc] = count;
                count += level + 1;
                if (ops[pc] == Op.MARK) {
                    level++;
                } else if (ops[pc] == Op.EMPTY_EXIT) {
                    level--;
                } else if (isReader(ops[pc])) {
                    reading++;
                }
            }

            states = count;
            readers = reading;
            // The program starts with the instruction that stores the start of the match: what follows
            // it is the first thing every match does.
            literal = ops[1] == Op.CHAR && translation.a()[1] <= 0xFFFF ? translation.a()[1] : -1;
            anchored = ops[1] == Op.BEGIN;
        }
    }

    // Whether an instruction reads a code point: Char, Set, Any, AnyButLineFeed come first in Op.
    static boolean isReader(Op op) {
        return op.ordinal() <= Op.ANY_BUT_LINE_FEED.ordinal();
    }

    /**
     * The memory of the searches of one caller over one program: one mark per state, a stack of at
     * most two entries per state, and two lists of threads of at most one thread per instruction that
     * reads a code point. Made at the first search and used again by the next ones; never shared
     * between threads.
     */
    static final class RegexMatcher {
        private static final int[] NONE = new int[0];

        private final RegexProgram program;
        // Per state: the generation that reached it last. A generation is one position of one search.
        private int[] seen = NONE;
        private int generation;
        // Pairs: an instruction and its level, still to follow; or -1 - slot and the value to put back.
        private int[] stack = NONE;
        // The captures of the thread being followed.
        private int[] work = NONE;
        private Threads current;
        private Threads next;

        RegexMatcher(RegexProgram program) {
            this.program = program;
        }

        RegexProgram program() {
            return program;
        }

        /**
         * The first match starting at or after UTF-16 index {@code start} (anchors and word boundaries
         * see the whole input). {@code spans} receives start and end of the match, then of each group
         * (-1, -1 for a group that did not participate): 2 · (groups + 1) entries. A shorter
         * {@code spans} receives the first entries only; an empty one asks only whether there is a
         * match.
         */
        boolean tryMatch(String input, int start, int[] spans) {
            Op[] ops = program.ops;
            int[] a = program.a;
            int[][] sets = program.sets;
            int[] depth = program.depth;
            int length = input.length();
            int width = Math.min(spans.length, program.slots);
            if (seen.length == 0) {
                seen = new int[program.states];
                stack = new int[2 * program.states];
                work = new int[program.slots];
                int capacity = Math.min(8, program.readers + 1);
                current = new Threads(capacity, program.slots);
                next = new Threads(capacity, program.slots);
            }

            current.count = 0;
            boolean matched = false;
            int pos = start;
            advance();
            while (true) {
                // The threads at pos are in current. Until a match is found, one more starts here,
                // behind them: a match that starts earlier has priority.
                if (!matched) {
                    if (current.count == 0) {
                        if (program.anchored && pos > 0) {
                            return false;
                        }
                        if (program.literal >= 0) {
                            int at = input.indexOf(program.literal, pos);
                            if (at < 0) {
                                return false;
                            }
                            if (at != pos) {
                                pos = at;
                                advance();
                            }
                        }
                    }

                    for (int slot = 0; slot < width; slot++) {
                        work[slot] = -1;
                    }
                    if (follow(current, 0, 0, input, pos, width, spans)) {
                        if (width == 0) {
                            return true;
                        }
                        matched = true;
                    }
                }

                if (pos >= length) {
                    return matched;
                }
                int c = input.charAt(pos);
                int size = 1;
                if (Character.isHighSurrogate(input.charAt(pos)) && pos + 1 < length && Character.isLowSurrogate(input.charAt(pos + 1))) {
                    c = Character.toCodePoint(input.charAt(pos), input.charAt(pos + 1));
                    size = 2;
                }

                if (current.count == 0) {
                    if (matched) {
                        return true;
                    }
                    pos += size;
                    advance();
                    continue;
                }

                // Every thread reads the code point at pos: the ones that accept it go on after it.
                advance();
                next.count = 0;
                for (int i = 0; i < current.count; i++) {
                    int pc = current.pcs[i];
                    Op op = ops[pc];
                    if (op == Op.CHAR ? a[pc] != c : op == Op.SET ? !inSet(sets[a[pc]], c) : op == Op.ANY_BUT_LINE_FEED && c == '\n') {
                        continue;
                    }
                    System.arraycopy(current.captures, i * program.slots, work, 0, width);
                    // It has read a code point: every loop around it has matched something.
                    if (follow(next, pc + 1, depth[pc + 1], input, pos + size, width, spans)) {
                        if (width == 0) {
                            return true;
                        }
                        // The threads behind this one have a lower priority than the match.
                        matched = true;
                        break;
                    }
                }

                pos += size;
                Threads swap = current;
                current = next;
                next = swap;
            }
        }

        // The next position of the search: no state has been reached at it yet.
        private void advance() {
            if (generation == Integer.MAX_VALUE) {
                Arrays.fill(seen, 0);
                generation = 0;
            }

            generation++;
        }

        // Follows a thread from an instruction at pos, with the captures in work, through the
        // instructions that read nothing: each reading instruction it reaches first becomes a thread
        // of threads, in priority order. True when it reached the end of the program: the match is
        // then in spans, and what it had still to follow has a lower priority.
        private boolean follow(Threads threads, int pc, int level, String input, int pos, int width, int[] spans) {
            Op[] ops = program.ops;
            int[] a = program.a;
            int[] b = program.b;
            int[] depth = program.depth;
            int[] states = program.base;
            int slots = program.slots;
            int top = 0;
            while (true) {
                Op op = ops[pc];
                // What an instruction that reads does next does not depend on the level.
                int state = isReader(op) ? states[pc] : states[pc] + level;
                boolean alive = seen[state] != generation;
                if (alive) {
                    seen[state] = generation;
                    switch (op) {
                        case CHAR, SET, ANY, ANY_BUT_LINE_FEED -> {
                            int index = threads.count;
                            if (index == threads.pcs.length) {
                                threads.grow();
                            }
                            threads.pcs[index] = pc;
                            System.arraycopy(work, 0, threads.captures, index * slots, width);
                            threads.count = index + 1;
                            alive = false;
                        }
                        case SPLIT -> {
                            stack[top++] = pc + b[pc];
                            stack[top++] = level;
                            pc += a[pc];
                        }
                        case JUMP -> pc += a[pc];
                        case SAVE -> {
                            int slot = a[pc];
                            if (slot < width) {
                                stack[top++] = -1 - slot;
                                stack[top++] = work[slot];
                                work[slot] = pos;
                            }

                            pc++;
                        }
                        case BEGIN -> {
                            if (pos == 0) {
                                pc++;
                            } else {
                                alive = false;
                            }
                        }
                        case END -> {
                            if (pos == input.length()) {
                                pc++;
                            } else {
                                alive = false;
                            }
                        }
                        case WORD_BOUNDARY, NOT_WORD_BOUNDARY -> {
                            boolean boundary = (pos > 0 && isWord(input.charAt(pos - 1))) != (pos < input.length() && isWord(input.charAt(pos)));
                            if (boundary == (op == Op.WORD_BOUNDARY)) {
                                pc++;
                            } else {
                                alive = false;
                            }
                        }
                        case MARK -> {
                            // An iteration starts: it has matched nothing yet, so the level stays.
                            pc++;
                        }
                        case EMPTY_EXIT -> {
                            // The iteration of the innermost loop ends: it matched something if the level counts it.
                            int inner = depth[pc];
                            pc += level < inner ? b[pc] : 1;
                            if (level == inner) {
                                level = inner - 1;
                            }
                        }
                        default -> {
                            System.arraycopy(work, 0, spans, 0, width);
                            return true;
                        }
                    }
                }

                if (alive) {
                    continue;
                }

                // This way ended: back to the last one left aside, putting back the captures stored since.
                while (true) {
                    if (top == 0) {
                        return false;
                    }
                    int value = stack[--top];
                    int entry = stack[--top];
                    if (entry >= 0) {
                        pc = entry;
                        level = value;
                        break;
                    }

                    work[-1 - entry] = value;
                }
            }
        }

        // The ASCII word characters of §9.10: a UTF-16 unit of a code point above U+FFFF is not one.
        private static boolean isWord(char unit) {
            return (unit >= '0' && unit <= '9') || (unit >= 'A' && unit <= 'Z') || (unit >= 'a' && unit <= 'z') || unit == '_';
        }

        // Whether a code point is in a set: sorted pairs low, high.
        private static boolean inSet(int[] ranges, int c) {
            if (ranges.length <= 8) {
                for (int i = 0; i < ranges.length; i += 2) {
                    if (c < ranges[i]) {
                        return false;
                    }
                    if (c <= ranges[i + 1]) {
                        return true;
                    }
                }

                return false;
            }

            int low = 0;
            int high = ranges.length / 2 - 1;
            while (low <= high) {
                int middle = (low + high) >>> 1;
                if (c < ranges[2 * middle]) {
                    high = middle - 1;
                } else if (c > ranges[2 * middle + 1]) {
                    low = middle + 1;
                } else {
                    return true;
                }
            }

            return false;
        }

        // The threads at one position, in priority order: the instruction of each and its captures.
        private static final class Threads {
            private final int slots;
            private int[] pcs;
            private int[] captures;
            private int count;

            Threads(int capacity, int slots) {
                this.slots = slots;
                pcs = new int[capacity];
                captures = new int[capacity * slots];
            }

            // Room for one more thread: there are never more than the program has reading instructions.
            void grow() {
                pcs = Arrays.copyOf(pcs, pcs.length * 2);
                captures = Arrays.copyOf(captures, pcs.length * slots);
            }
        }
    }
}
