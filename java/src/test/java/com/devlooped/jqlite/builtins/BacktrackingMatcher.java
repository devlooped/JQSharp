package com.devlooped.jqlite.builtins;

import com.devlooped.jqlite.builtins.RegexSyntax.Op;
import com.devlooped.jqlite.builtins.RegexSyntax.Translation;
import java.util.Arrays;

/**
 * What the programs of {@link RegexSyntax} mean, as the simplest machine that runs them: a
 * backtracking matcher that tries each start position in turn and each alternative in priority
 * order, with the positions of the marks as they are stored. It can take time exponential in the
 * input, so it is not the engine; {@link RegexEngineTests} compares the engine with it on small
 * patterns and inputs, and gives up on a search that takes it more than a budget of steps.
 */
final class BacktrackingMatcher {
    // What the stack holds, three numbers an entry: an alternative to try (its instruction and
    // position), or the value of a slot or of a mark to put back when going back past it.
    private static final int ALTERNATIVE = 0;
    private static final int SLOT = 1;
    private static final int MARK = 2;

    private BacktrackingMatcher() {
    }

    /**
     * The first match starting at or after UTF-16 index {@code start}: its spans, as
     * {@link RegexEngine.RegexMatcher#tryMatch} gives them; null when the search is not over after
     * {@code budget} instructions.
     */
    static Boolean tryMatch(Translation program, String input, int start, int[] spans, int budget) {
        int[] steps = {budget};
        for (int at = start; at <= input.length(); at++) {
            if (run(program, input, at, spans, steps)) {
                return true;
            }
            if (steps[0] < 0) {
                return null;
            }
            if (at < input.length() && Character.isHighSurrogate(input.charAt(at))) {
                at++;
            }
        }

        return false;
    }

    // Whether the program matches from at; the spans are then in spans.
    private static boolean run(Translation program, String input, int at, int[] spans, int[] steps) {
        Op[] ops = program.ops();
        int[] marks = new int[program.marks()];
        Arrays.fill(marks, -1);
        Arrays.fill(spans, -1);
        Stack stack = new Stack();
        int pc = 0;
        int pos = at;
        while (true) {
            if (--steps[0] < 0) {
                return false;
            }
            boolean ok = true;
            int a = program.a()[pc];
            switch (ops[pc]) {
                case CHAR, SET, ANY, ANY_BUT_LINE_FEED -> {
                    int c = pos < input.length() ? input.codePointAt(pos) : -1;
                    Op op = ops[pc];
                    if (c >= 0 && (op == Op.CHAR ? c == a : op == Op.SET ? inSet(program.sets()[a], c) : op == Op.ANY || c != '\n')) {
                        pos += c > 0xFFFF ? 2 : 1;
                        pc++;
                    } else {
                        ok = false;
                    }
                }
                case SPLIT -> {
                    stack.push(ALTERNATIVE, pc + program.b()[pc], pos);
                    pc += a;
                }
                case JUMP -> pc += a;
                case SAVE -> {
                    stack.push(SLOT, a, spans[a]);
                    spans[a] = pos;
                    pc++;
                }
                case BEGIN -> {
                    ok = pos == 0;
                    pc++;
                }
                case END -> {
                    ok = pos == input.length();
                    pc++;
                }
                case WORD_BOUNDARY, NOT_WORD_BOUNDARY -> {
                    boolean boundary = (pos > 0 && isWord(input.charAt(pos - 1))) != (pos < input.length() && isWord(input.charAt(pos)));
                    ok = boundary == (ops[pc] == Op.WORD_BOUNDARY);
                    pc++;
                }
                case MARK -> {
                    stack.push(MARK, a, marks[a]);
                    marks[a] = pos;
                    pc++;
                }
                case EMPTY_EXIT -> pc += pos == marks[a] ? program.b()[pc] : 1;
                default -> {
                    return true;
                }
            }

            if (ok) {
                continue;
            }

            // Back to the last alternative, undoing what was stored since.
            while (true) {
                if (stack.size == 0) {
                    return false;
                }
                int value = stack.items[stack.size - 1];
                int index = stack.items[stack.size - 2];
                int kind = stack.items[stack.size - 3];
                stack.size -= 3;
                if (kind == ALTERNATIVE) {
                    pc = index;
                    pos = value;
                    break;
                }

                if (kind == SLOT) {
                    spans[index] = value;
                } else {
                    marks[index] = value;
                }
            }
        }
    }

    private static boolean isWord(char unit) {
        return (unit >= '0' && unit <= '9') || (unit >= 'A' && unit <= 'Z') || (unit >= 'a' && unit <= 'z') || unit == '_';
    }

    private static boolean inSet(int[] ranges, int c) {
        for (int i = 0; i < ranges.length; i += 2) {
            if (c >= ranges[i] && c <= ranges[i + 1]) {
                return true;
            }
        }

        return false;
    }

    // A growing list of ints, three per entry.
    private static final class Stack {
        int[] items = new int[48];
        int size;

        void push(int kind, int index, int value) {
            if (size + 3 > items.length) {
                items = Arrays.copyOf(items, items.length * 2);
            }
            items[size++] = kind;
            items[size++] = index;
            items[size++] = value;
        }
    }
}
