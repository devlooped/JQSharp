package com.devlooped.jqlite.core.runtime;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.devlooped.jqlite.binding.Bound.BoundProgram;
import com.devlooped.jqlite.binding.Resolver;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

/**
 * The native {@code _modify} and {@code _assign} against their prelude definitions (§8.3): each
 * program is compiled twice, with the native overrides and without them, and both must give the
 * same outputs, errors included.
 */
class AssignTests {
    static String[][] programs() {
        return new String[][] {
            /// <summary>
            /// The native <c>_modify</c> and <c>_assign</c> against their prelude definitions (§8.3): each
            /// program is compiled twice, with the native overrides and without them, and both must give the
            /// same outputs, errors included.
            /// </summary>
            // parent then child, child then parent, the same path twice
            {"(.a, .a.b) |= {b: 1}", "{\"a\":{\"b\":0}}"},
            {"(.a.b, .a) |= {x: .}", "{\"a\":{\"b\":0}}"},
            {"(.a, .a) |= . + 1", "{\"a\":1}"},
            {"(.a[0], .a, .a[0]) |= [.]", "{\"a\":[1,2]}"},
            // every path of a value, parents first
            {".. |= (if type == \"number\" then . + 1 else . end)", "{\"a\":[1,{\"b\":2}],\"c\":3}"},
            {"[..] as $all | .. |= length", "[[1,[2]],{\"x\":3}]"},
            // updates that return the input itself, or structures holding it twice
            {".a |= ., .b |= [., .]", "{\"a\":[1],\"b\":{\"c\":2}}"},
            {"(.a, .a[0]) |= [., .]", "{\"a\":[1]}"},
            {". as $in | .a |= $in", "{\"a\":1}"},
            // empty updates (deletions at the end) mixed with updates
            {"(.[] | select(. >= 2)) |= empty", "[1,5,3,0,7]"},
            {".[] |= (if . % 2 == 0 then empty else . * 10 end)", "[1,2,3,4,5]"},
            {"(.a, .b, .c) |= (if . == null then empty else . + 1 end)", "{\"a\":1,\"c\":3}"},
            {"(.a.x, .a) |= empty", "{\"a\":{\"x\":1}}"},
            {"to_entries |= map(select(.key != \"b\")) | from_entries", "{\"a\":1,\"b\":2}"},
            // errors midway, and their order
            {"(.a, .b, .c) |= (if . == 2 then error(\"two\") else . end)", "{\"a\":1,\"b\":2,\"c\":3}"},
            {"(.a, .b.x) |= 1", "{\"a\":0,\"b\":5}"},
            {"(.a, 1) |= 1", "{\"a\":0}"},
            {".[0] |= 1", "{}"},
            {"(.a, .[0]) |= 1", "null"},
            {"(.a | .b) |= 1", "{\"a\":[1]}"},
            // _assign: several right-hand outputs, none, and paths of the right side's input
            {"(.a, .b) = (1, 2)", "{}"},
            {".a = empty", "{}"},
            {"empty = 1", "{\"a\":0}"},
            {".a = .b", "{\"a\":0,\"b\":1}"},
            {"(.a, .a.b) = 1", "{}"},
            {".a[1:3] = [\"x\"]", "{\"a\":[0,1,2,3]}"},
            // slices, paths through null, negative indices, arithmetic updates
            {".[1:3] |= map(. * 10)", "[0,1,2,3,4]"},
            {"(.[1:3], .[0]) |= (if type == \"array\" then [] else -1 end)", "[0,1,2,3,4]"},
            {".[2:4][0] |= 9", "[0,1,2,3,4]"},
            {".[1:2] |= \"x\"", "\"abc\""},
            {".a.b[2].c |= 1", "null"},
            {".[-1] |= . + 1", "[1,2,3]"},
            {".[-5] |= 1", "[1,2,3]"},
            {".[nan] |= 1", "[1]"},
            {".[3] |= 1", "[1]"},
            {".[536870912] |= 1", "[]"},
            {".[] += 1, .a -= 1, .b *= 2, .c /= 2, .d %= 2, .e //= 7", "{\"a\":1,\"b\":2,\"c\":4,\"d\":5,\"e\":null}"},
            {"map_values(. + 1)", "{\"a\":1,\"b\":2}"},
            {"walk(if type == \"number\" then . + 1 else . end)", "[1,[2,{\"a\":3}]]"},
            {"INDEX(.id)", "[{\"id\":1},{\"id\":2}]"},
            {"sub(\"b\"; \"x\")", "\"abc\""},
            {"path(.a |= 1)", "{\"a\":0}"},
            {"path(empty = 1)", "{\"a\":0}"},
            {"[path(.a = 1)]", "{\"a\":0}"},
            {".a |= (1, 2)", "{\"a\":0}"},
            {"(.a, .b) |= (.x |= 1)", "{\"a\":{},\"b\":null}"},
        };
    }

    @ParameterizedTest
    @MethodSource("programs")
    void nativeAndPreludeAgree(String program, String input) {
        String nativeResult = line(compile(program, true), input);
        String prelude = line(compile(program, false), input);
        assertEquals(prelude, nativeResult);
    }

    // The reference counts the bytes allocated; Java has no portable counter, so this checks the
    // time: the prelude definition copies the array once per element (4000 × 4000 values).
    @Test
    void nativeUpdatesAreLinear() {
        BoundProgram program = compile("[range(40000)] | .[] |= . + 1 | length", true);
        long started = System.nanoTime();
        assertEquals("40000", line(program, "null"));
        long elapsedMs = (System.nanoTime() - started) / 1_000_000;
        assertTrue(elapsedMs < 3000, elapsedMs + " ms");
    }

    @Test
    void pathsOfTenThousandKeys() {
        // A value nested 10,000 arrays deep and the path of its innermost 0, run on a 1 MB stack.
        String deep = "reduce range(10000) as $i (0; [.]) | [range(10000) | 0] as $p | ";
        assertEquals("1", Evaluate.onSmallStack(deep + "getpath($p) |= . + 1 | getpath($p)", "null", null));
        assertEquals("2", Evaluate.onSmallStack(deep + "setpath($p; 2) | getpath($p)", "null", null));
        assertEquals("[]", Evaluate.onSmallStack(deep + "delpaths([$p]) | getpath($p[1:])", "null", null));
        assertEquals("[]", Evaluate.onSmallStack(deep + "del(getpath($p)) | getpath($p[1:])", "null", null));
        assertEquals("3", Evaluate.onSmallStack(deep + "getpath($p) = 3 | getpath($p)", "null", null));
    }

    private static BoundProgram compile(String program, boolean nativeOverrides) {
        Resolver.Compiled compiled = Resolver.tryCompile(program, nativeOverrides, null);
        assertNotNull(compiled.program(), String.valueOf(compiled.error()));
        return compiled.program();
    }

    private static String line(BoundProgram program, String input) {
        return String.join(", ", Evaluate.run(program, Evaluate.json(input)));
    }
}
