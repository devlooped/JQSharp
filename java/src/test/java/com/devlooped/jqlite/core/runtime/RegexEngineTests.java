package com.devlooped.jqlite.core.runtime;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

/**
 * The regex engine on long inputs (spec §11: no operation fails by exhausting the host stack). A
 * backtracking engine recurses once per repetition of a group; the port's own engine is linear and
 * recurses nowhere, so these run on a 256 KB thread, with inputs of up to 2,000,000 characters.
 */
class RegexEngineTests {
    static String[][] programs() {
        return new String[][] {
            {"\"ab\" * 20000 | test(\"^(a|b)*$\")", "true"},
            {"\"ab\" * 200000 | test(\"^(a|b)*$\")", "true"},
            {"\"ab\" * 200000 | [match(\"(a|b)+\")] | length", "1"},
            {"\"ab\" * 1000000 | [match(\"(a|b)+\")] | length", "1"},
            {"\"ab\" * 200000 | match(\"(a|b)+\") | [.offset, .length, .captures[0].string]", "[0,400000,\"b\"]"},
            {"\"ab\" * 200000 + \"c\" | test(\"^(a|b)*$\")", "false"},
            {"\"ab\" * 100000 | [match(\"(?<x>a)(b)\"; \"g\")] | length", "100000"},
            {"\"ab\" * 100000 | sub(\"(a|b)+?\"; \"x\")", "\"x" + "b" + "ab".repeat(99999) + "\""},
            // gsub is quadratic in the number of matches in the prelude (the reference too): a shorter input.
            {"\"ab\" * 5000 | gsub(\"a\"; \"\") | length", "5000"},
            {"\"x\" * 20000 | test(\"(x?){100}y\")", "false"},
        };
    }

    @ParameterizedTest
    @MethodSource("programs")
    void longInputsMatchWithoutTheHostStack(String program, String expected) {
        long started = System.nanoTime();
        assertEquals(expected, Evaluate.onStack(256 * 1024, program, "null", null));
        long elapsedMs = (System.nanoTime() - started) / 1_000_000;
        assertTrue(elapsedMs < 20_000, program + " took " + elapsedMs + " ms");
    }
}
