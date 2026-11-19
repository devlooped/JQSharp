package com.devlooped.jqlite.core.syntax;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.fail;

import com.devlooped.jqlite.syntax.Ast;
import com.devlooped.jqlite.syntax.Parser;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

/**
 * The nesting limit of spec §11, and programs that must not exhaust the host stack. Each parse runs
 * on a thread with a 1 MB stack (the default of a Java thread), so the result does not depend on the
 * stack size of the test runner's threads.
 */
class ParserLimitTests {
    private static final int LIMIT = Parser.MAX_NESTING;

    static Parser.Parsed parseOnSmallStack(String source) {
        Parser.Parsed[] result = new Parser.Parsed[1];
        Thread thread = new Thread(null, () -> result[0] = Parser.tryParse(source), "parse", 1024 * 1024);
        thread.start();
        try {
            thread.join();
        } catch (InterruptedException e) {
            throw new IllegalStateException(e);
        }
        if (result[0] == null) {
            fail("the parse failed on a 1 MB stack");
        }
        return result[0];
    }

    static Ast.Program parses(String source) {
        Parser.Parsed parsed = parseOnSmallStack(source);
        if (parsed.program() == null) {
            fail(parsed.error().message() + " at " + parsed.error().line() + ":" + parsed.error().column());
        }
        return parsed.program();
    }

    static String fails(String source) {
        Parser.Parsed parsed = parseOnSmallStack(source);
        assertNull(parsed.program());
        return parsed.error().message() + " @" + parsed.error().line() + ":" + parsed.error().column();
    }

    static Object[][] nestings() {
        // name, opening repeated, closing repeated (the innermost term is "1"), offset in the
        // opening of the token that opens the level
        return new Object[][] {
            {"parentheses", "(", ")", 0},
            {"arrays", "[", "]", 0},
            {"objects", "{a:", "}", 0},
            {"indexes", ".[", "]", 1},
            {"calls", "f(", ")", 1},
            {"interpolations", "\"\\(", ")\"", 0},
            {"if", "if . then ", " end", 0},
            {"try", "try ", "", 0},
            {"reduce", "reduce . as $x (", ";.)", 0},
            {"foreach", "foreach . as $x (", ";.)", 0},
            {"definitions", "def f: ", "; f", 0},
            {"object keys", "{(", "):1}", 0},
        };
    }

    // "{(" opens two levels (the object and the parentheses).
    private static int levels(String open) {
        return open.equals("{(") ? LIMIT / 2 : LIMIT;
    }

    // A parenthesized key must not be a number.
    private static String innermost(String open) {
        return open.equals("{(") ? "\"a\"" : "1";
    }

    @ParameterizedTest
    @MethodSource("nestings")
    void nestingUpToTheLimitParses(String name, String open, String close, int offset) {
        int count = levels(open);
        parses(open.repeat(count) + innermost(open) + close.repeat(count));
    }

    @ParameterizedTest
    @MethodSource("nestings")
    void nestingBeyondTheLimitFailsAtTheOpeningToken(String name, String open, String close, int offset) {
        int count = levels(open) + 1;
        String source = open.repeat(count) + innermost(open) + close.repeat(count);
        // The level beyond the limit is opened by the token at offset in the last opening.
        int column = (count - 1) * open.length() + offset + 1;
        assertEquals("Program too deeply nested @1:" + column, fails(source));
    }

    @Test
    void nestedPatternsCountToo() {
        parses(". as " + "[".repeat(LIMIT) + "$x" + "]".repeat(LIMIT) + " | $x");
        assertEquals("Program too deeply nested @1:" + (LIMIT + 6), fails(". as " + "[".repeat(LIMIT + 1) + "$x" + "]".repeat(LIMIT + 1) + " | $x"));
    }

    @Test
    void nestedMetadataCountsToo() {
        assertEquals("Program too deeply nested @1:" + (LIMIT + 11), fails("module {a: " + "[".repeat(LIMIT) + "]".repeat(LIMIT) + "}; 1"));
    }

    @Test
    void nestedMetadataWithinTheLimitParses() {
        parses("module {a: " + "[".repeat(LIMIT - 1) + "]".repeat(LIMIT - 1) + "}; 1");
    }

    @Test
    void mixedNestingCountsEveryKind() {
        assertEquals("Program too deeply nested @1:" + (LIMIT / 2 * 2 + 1), fails("([".repeat(LIMIT / 2 + 1) + "1" + "])".repeat(LIMIT / 2 + 1)));
    }

    static String[][] sequences() {
        return new String[][] {
            {"", "1 + ", "1"}, {"", "1 * ", "1"}, {"", "1 // ", "1"}, {"", "1 and ", "1"}, {"", "1 == 1 or ", "1"},
            {"", "1 | ", "1"}, {"", "1, ", "1"}, {"", "1 as $x | ", "$x"}, {"", "label $x | ", "1"}, {"", "def f: 1; ", "f"},
            {"(", "def f: 1; ", "f)"}, {"[1, ", "def f: 1; ", "f]"}, {"", "-", "1"}, {"", "1 + -", "1"}, {"", ".a", ""},
            {"", ".[0]", ""}, {"", ".a?", ""}, {". as $a", " ?// $a", " | 1"}, {"{a: ", "1 | ", "1}"}, {"{", "a: 1, ", "}"},
            {"\"", "\\(1)", "\""}, {"f(", "1; ", "1)"},
        };
    }

    // Sequences of any length parse without recursing per element: prefix, 10,000 elements, suffix.
    @ParameterizedTest
    @MethodSource("sequences")
    void longSequencesDoNotRecurse(String prefix, String element, String suffix) {
        parses(prefix + element.repeat(10_000) + suffix);
    }

    @Test
    void longElifChainsDoNotRecurse() {
        parses("if . then 1 " + "elif . then 1 ".repeat(10_000) + "else 2 end");
    }

    @Test
    void aLongChainKeepsItsAssociativity() {
        Ast.Program program = parses("1 - ".repeat(10_000) + "1");
        // Left-associative: the left spine is 10,000 subtractions deep.
        int depth = 0;
        Ast.Node node = program.body();
        while (node instanceof Ast.Binary binary) {
            assertInstanceOf(Ast.Literal.class, binary.right());
            node = binary.left();
            depth++;
        }

        assertEquals(10_000, depth);

        program = parses("1 // ".repeat(10_000) + "1");
        depth = 0;
        node = program.body();
        while (node instanceof Ast.Alternative alternative) {
            assertInstanceOf(Ast.Literal.class, alternative.left());
            node = alternative.right();
            depth++;
        }

        assertEquals(10_000, depth);
    }

    @Test
    void aLongPipeOfBindingsNestsToTheRight() {
        Ast.Program program = parses("1 as $x | ".repeat(10_000) + "$x");
        int depth = 0;
        Ast.Node node = program.body();
        while (node instanceof Ast.Bind bind) {
            node = bind.body();
            depth++;
        }

        assertEquals(10_000, depth);
        assertInstanceOf(Ast.Variable.class, node);
    }

    @Test
    void manyTopLevelDefinitionsAreAList() {
        Ast.Program program = parses("def f: 1; ".repeat(10_000) + "f");
        assertEquals(10_000, program.definitions().size());
        assertInstanceOf(Ast.Call.class, program.body());
    }
}
