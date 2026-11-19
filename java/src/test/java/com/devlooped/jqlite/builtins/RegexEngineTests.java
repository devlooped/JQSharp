package com.devlooped.jqlite.builtins;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.devlooped.jqlite.builtins.RegexEngine.RegexMatcher;
import com.devlooped.jqlite.builtins.RegexEngine.RegexProgram;
import com.devlooped.jqlite.builtins.RegexSyntax.Options;
import com.devlooped.jqlite.builtins.RegexSyntax.Translation;
import com.devlooped.jqlite.core.runtime.Evaluate;
import java.util.Arrays;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;
import org.junit.jupiter.params.provider.ValueSource;

/**
 * The regex engine: what it finds is what the simplest matcher of the same programs finds
 * ({@link BacktrackingMatcher}), in time linear in the input and with memory the size of the
 * pattern. The corpus shows what the programs mean (spec §9.10). In the engine's package, for its
 * package-private types.
 */
class RegexEngineTests {
    private static final Options PLAIN = new Options(false, false, false);

    @ParameterizedTest
    @ValueSource(ints = {1, 7, 12345})
    void randomPatternsMatchAsTheBacktrackingMatcherDoes(int seed) {
        RegexFuzz fuzz = new RegexFuzz(seed);
        int compared = 0;
        String[] reason = {""};
        for (int n = 0; n < 20000; n++) {
            String source = fuzz.pattern(3);
            Translation translation = RegexSyntax.tryTranslate(source, new Options(false, fuzz.next(2) == 0, false), reason);
            assertNotNull(translation, () -> source + ": " + reason[0]);
            RegexMatcher matcher = new RegexMatcher(new RegexProgram(translation));
            int width = 2 * (translation.groups().length + 1);
            for (int k = 0; k < 4; k++) {
                String text = fuzz.input(7);
                for (int start = 0; start <= text.length(); start++) {
                    // A search starts between code points.
                    if (start < text.length() && Character.isLowSurrogate(text.charAt(start))) {
                        continue;
                    }
                    int[] expected = new int[width];
                    Boolean found = BacktrackingMatcher.tryMatch(translation, text, start, expected, 200000);
                    if (found == null) {
                        continue;
                    }
                    String where = source + " on " + text + " from " + start;
                    int[] spans = new int[width];
                    assertEquals(found, matcher.tryMatch(text, start, spans), where);
                    if (found) {
                        assertArrayEquals(expected, spans, where);
                    }
                    // Only the match; only whether there is one.
                    int[] match = new int[2];
                    assertEquals(found, matcher.tryMatch(text, start, match), where);
                    if (found) {
                        assertArrayEquals(Arrays.copyOf(expected, 2), match, where);
                    }
                    assertEquals(found, matcher.tryMatch(text, start, new int[0]), where);
                    compared++;
                }
            }
        }

        assertTrue(compared > 300000, String.valueOf(compared));
    }

    // Patterns a backtracking matcher takes exponential or quadratic time for, and long inputs.
    static String[][] linear() {
        return new String[][] {
            {"\"a\" * 28 + \"c\" | test(\"^(a*)*b$\")", "false"},
            {"\"a\" * 5000 + \"c\" | test(\"^(a*)*b$\")", "false"},
            {"\"ab\" * 2000000 | test(\"^(a|b)*$\")", "true"},
            {"\"a\" * 200000 | test(\"a*b\")", "false"},
            {"\"a\" * 200000 | [match(\"a*b\")] | length", "0"},
            {"\"x\" * 100000 | test(\"(x+x+)+y\")", "false"},
            {"\"a\" * 40 | test(\"(a?){40}a{40}\")", "true"},
            {"\"ab\" * 2000000 | match(\"^(a|b)*$\") | .captures[0].offset", "3999999"},
            {"\"a\" * 100000 | [match(\"(a|aa)*?$\"; \"g\") | .length]", "[100000,0]"},
            // Java: the search loop and the prelude's sub and gsub on long inputs, without the host stack.
            {"\"ab\" * 100000 | [match(\"(?<x>a)(b)\"; \"g\")] | length", "100000"},
            {"\"ab\" * 100000 | sub(\"(a|b)+?\"; \"x\")", "\"x" + "b" + "ab".repeat(99999) + "\""},
            // gsub is quadratic in the number of matches in the prelude (the reference too): a shorter input.
            {"\"ab\" * 5000 | gsub(\"a\"; \"\") | length", "5000"},
            {"\"x\" * 20000 | test(\"(x?){100}y\")", "false"},
        };
    }

    // Java: on a 256 KB thread, as nothing recurses (spec §11: no operation exhausts the host stack).
    @ParameterizedTest
    @MethodSource("linear")
    void linearTime(String program, String expected) {
        long started = System.nanoTime();
        assertEquals(expected, Evaluate.onStack(256 * 1024, program, "null", null));
        long elapsed = (System.nanoTime() - started) / 1_000_000;
        assertTrue(elapsed < 2000, elapsed + " ms");
    }

    @Test
    void theMemoryOfASearchIsTheSizeOfThePattern() throws ReflectiveOperationException {
        // The threads are kept in arrays that grow with their number: after a long input they hold
        // no more than one thread per instruction that reads a code point.
        RegexProgram program = RegexEngine.tryCompile("^(?:(a)|(b)|[ab]c?)*$", PLAIN, new String[1]);
        assertNotNull(program);
        assertEquals(4, program.readers);
        assertEquals(6, program.slots);
        RegexMatcher matcher = new RegexMatcher(program);
        int[] spans = new int[6];
        assertTrue(matcher.tryMatch("ab".repeat(500000), 0, spans));
        assertArrayEquals(new int[] {0, 1000000, 999998, 999999, 999999, 1000000}, spans);
        Object threads = field(matcher, "current");
        int[] pcs = (int[]) field(threads, "pcs");
        int[] captures = (int[]) field(threads, "captures");
        assertTrue(pcs.length <= 8, String.valueOf(pcs.length));
        assertEquals(pcs.length * 6, captures.length);
        assertEquals(program.states, ((int[]) field(matcher, "seen")).length);
        assertEquals(2 * program.states, ((int[]) field(matcher, "stack")).length);
    }

    @Test
    void theLargestPatternsOfSpec11AreProgramsTheMatcherRuns() {
        // The limits are the translation's, and the corpus has their boundaries. What is the engine's:
        // within them a program has at most five instructions per unit of the pattern's size, and a
        // state is an instruction at a level (nested loops that can match nothing have levels).
        assertArrayEquals(new int[] {10000, 100, 1000}, new int[] {RegexSyntax.MAX_SIZE, RegexSyntax.MAX_NESTING, RegexSyntax.MAX_GROUPS});
        // The most instructions for a size: an empty group repeated, each copy optional and with its mark.
        String[] reason = {""};
        Translation copies = RegexSyntax.tryTranslate("(){0,1000}".repeat(9) + "(){0,990}", PLAIN, reason);
        assertNotNull(copies, reason[0]);
        assertEquals(5 * (RegexSyntax.MAX_SIZE - 10) + 3, copies.ops().length);
        assertTrue(new RegexMatcher(new RegexProgram(copies)).tryMatch("a", 0, new int[2]));
        // The most states: every instruction inside a hundred loops that can match nothing.
        RegexProgram deep = compiled(nested(RegexSyntax.MAX_NESTING, "a?".repeat(4900)));
        assertTrue(deep.states > 1_000_000 && deep.states < 2_000_000, String.valueOf(deep.states));
        assertTrue(new RegexMatcher(deep).tryMatch("a".repeat(100) + "b", 0, new int[2]));
        assertEquals("[300,0]", Evaluate.line("\"a\" * 300 | [match(\"" + nested(RegexSyntax.MAX_NESTING, "a*") + "\"; \"g\") | .length]"));
        // The most capture slots: a thousand groups, each thread with its own.
        RegexProgram groups = compiled("(a)".repeat(RegexSyntax.MAX_GROUPS));
        assertEquals(2 * (RegexSyntax.MAX_GROUPS + 1), groups.slots);
        int[] spans = new int[groups.slots];
        assertTrue(new RegexMatcher(groups).tryMatch("a".repeat(RegexSyntax.MAX_GROUPS + 5), 0, spans));
        assertArrayEquals(new int[] {0, 1000, 0, 1, 999, 1000}, new int[] {spans[0], spans[1], spans[2], spans[3], spans[spans.length - 2], spans[spans.length - 1]});
        // Past a limit the translation fails, with the fixed reason.
        assertNull(RegexEngine.tryCompile("(a)".repeat(RegexSyntax.MAX_GROUPS + 1), PLAIN, reason));
        assertEquals("the pattern is too large", reason[0]);
    }

    private static RegexProgram compiled(String source) {
        String[] reason = {""};
        RegexProgram program = RegexEngine.tryCompile(source, PLAIN, reason);
        assertNotNull(program, reason[0]);
        return program;
    }

    private static String nested(int levels, String inner) {
        return "(?:".repeat(levels) + inner + ")*".repeat(levels);
    }

    private static Object field(Object target, String name) throws ReflectiveOperationException {
        var field = target.getClass().getDeclaredField(name);
        field.setAccessible(true);
        return field.get(target);
    }
}
