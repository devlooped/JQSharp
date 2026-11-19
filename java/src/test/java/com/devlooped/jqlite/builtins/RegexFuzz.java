package com.devlooped.jqlite.builtins;

/**
 * Random patterns of the flavor of §9.10 and random inputs for them, from a seed: the same seed gives
 * the same cases on every run (and the same as the reference's).
 */
final class RegexFuzz {
    private static final String[] ATOMS = {"a", "a", "b", "b", "c", ".", "[ab]", "[^a]", "\\w", "\\s"};
    private static final String[] ANCHORS = {"^", "$", "\\b", "\\B"};
    private static final String[] QUANTIFIERS = {"*", "+", "?", "*?", "+?", "??", "{2}", "{0,2}", "{1,}", "{1,2}?", "{2,}?"};
    private static final String[] LETTERS = {"a", "a", "b", "b", "c", " ", "😀"};

    // An unsigned 32-bit number in an int.
    private int state;

    RegexFuzz(int seed) {
        state = seed;
    }

    /** A number 0 .. n - 1 (a linear congruential generator, exact in 32 bits). */
    int next(int n) {
        state = state * 1664525 + 1013904223;
        return (int) (((state & 0xFFFFFFFFL) * n) >>> 32);
    }

    /** A pattern: alternatives of sequences of atoms, groups and anchors, with quantifiers; {@code depth} bounds the nesting of groups. */
    String pattern(int depth) {
        StringBuilder text = new StringBuilder();
        int alternatives = 1 + (next(4) == 0 ? 1 + next(2) : 0);
        for (int a = 0; a < alternatives; a++) {
            if (a > 0) {
                text.append('|');
            }
            int items = next(4);
            for (int i = 0; i < items; i++) {
                int kind = next(10);
                if (kind == 0) {
                    text.append(ANCHORS[next(ANCHORS.length)]);
                    continue;
                }

                if (kind <= 3 && depth > 0) {
                    text.append(next(3) == 0 ? "(?:" : "(").append(pattern(depth - 1)).append(')');
                } else {
                    text.append(ATOMS[next(ATOMS.length)]);
                }
                if (next(2) == 0) {
                    text.append(QUANTIFIERS[next(QUANTIFIERS.length)]);
                }
            }
        }

        return text.toString();
    }

    /** An input of at most {@code length} code points. */
    String input(int length) {
        StringBuilder text = new StringBuilder();
        for (int n = next(length + 1); n > 0; n--) {
            text.append(LETTERS[next(LETTERS.length)]);
        }
        return text.toString();
    }
}
