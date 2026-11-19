package com.devlooped.jqlite.core.runtime;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.devlooped.jqlite.binding.Bound.BoundProgram;
import com.devlooped.jqlite.runtime.Evaluation.OutcomeKind;
import com.devlooped.jqlite.runtime.Interpreter;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.ValueText;
import java.util.ArrayList;
import com.devlooped.jqlite.runtime.Evaluation.EvaluationOptions;
import java.util.List;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;
import org.junit.jupiter.params.provider.ValueSource;

/**
 * MaxDepth is the only recursion limit a program meets (spec §11): on a small host stack, deep
 * evaluations continue on a fresh stack (StackGuard). Each case runs on a thread with a 1 MB or a
 * 256 KB stack.
 */
class StackGuardTests {
    private static EvaluationOptions depth(int maxDepth) {
        EvaluationOptions options = new EvaluationOptions();
        options.maxDepth = maxDepth;
        return options;
    }

    static String[][] nestings() {
        // opening, closing, and the output for 1000 levels around "1"
        return new String[][] {
            {"[", "]", "[".repeat(1000) + "1" + "]".repeat(1000)},
            {"{a: ", "}", "{\"a\":".repeat(1000) + "1" + "}".repeat(1000)},
            {".[", "]", "error: \"Cannot index null with null (null)\""},
            {"\"\\(", ")\"", "\"1\""},
            {"reduce . as $x (", ";.)", "1"},
            {"[1 + ", "]", "error"},
            {"1 + (", ")", "1001"},
            {"[label $x | ", "]", "[".repeat(1000) + "1" + "]".repeat(1000)},
            {"if . then 1 else ", " end", "1"},
            {"try ", "", "1"},
        };
    }

    @ParameterizedTest
    @MethodSource("nestings")
    void nestingToTheParserLimitWorks(String open, String close, String expected) {
        for (int stack : new int[] {1024 * 1024, 256 * 1024}) {
            String result = Evaluate.onStack(stack, open.repeat(1000) + "1" + close.repeat(1000), "null", null);
            if (expected.equals("error")) {
                assertTrue(result.startsWith("error: \"number (1) and array ([2]) cannot be added"), result);
            } else {
                assertEquals(expected, result);
            }
        }
    }

    static Object[][] recursions() {
        // program with N, stack
        return new Object[][] {
            {"def f: if . == 0 then 0 else (. - 1 | f) + 1 end; N | f", 1024 * 1024},
            {"def f: if . == 0 then 0 else (. - 1 | f) + 1 end; N | f", 256 * 1024},
            {"def f: if . == 0 then 0 else [. - 1 | f] | length end; N | f", 256 * 1024},
            {"def f: if . == 0 then 0 else ((. - 1, . - 1) | f) end; label $out | (N | f) | ., break $out", 1024 * 1024},
            {"def f: if . == 0 then 0 else ((. - 1, . - 1) | f) end; label $out | (N | f) | ., break $out", 256 * 1024},
            {"def f(g): if . == 0 then g else (. - 1 | f(g)) + 0 end; N | f(0)", 256 * 1024},
            // Single calls nest through evalOne (spec §7.7, §11).
            {"def f: if . == 0 then 0 else 1 + (. - 1 | f) end; N | f", 256 * 1024},
        };
    }

    @ParameterizedTest
    @MethodSource("recursions")
    void recursionReachesExactlyMaxDepth(String program, int stack) {
        for (int maxDepth : new int[] {1000, 5000}) {
            // N | f makes N + 1 calls.
            String deepest = Evaluate.onStack(stack, program.replace("N", Integer.toString(maxDepth - 1)), "null", depth(maxDepth));
            assertFalse(deepest.contains("error"), deepest);
            assertEquals("error: \"Maximum call depth exceeded\"", Evaluate.onStack(stack, program.replace("N", Integer.toString(maxDepth)), "null", depth(maxDepth)));
        }
    }

    @Test
    void hopsNest() {
        assertEquals("19999", Evaluate.onStack(256 * 1024, "def f: if . == 0 then 0 else (. - 1 | f) + 1 end; 19999 | f", "null", depth(20000)));
    }

    @ParameterizedTest
    @ValueSource(ints = {1024 * 1024, 256 * 1024})
    void aStopOfTheHostComesBackAcrossHops(int stack) throws InterruptedException {
        BoundProgram program = Evaluate.compile("def f: if . == 0 then (1, 2, 3) else (. - 1 | f) + 0 end; 900 | f");
        List<List<String>> outputs = new java.util.ArrayList<>();
        Thread thread = new Thread(null, () -> outputs.add(Evaluate.run(program, Evaluate.json("null"), null, 1)), "stop", stack);
        thread.start();
        thread.join();
        assertEquals(List.of(List.of("1")), outputs);
    }

    // An evaluation that the host starts from an output callback of another, on the same thread,
    // goes on from the guarded steps the outer one uses (the budget is the thread's).
    @ParameterizedTest
    @ValueSource(ints = {1024 * 1024, 256 * 1024})
    void evaluationsNestedInCallbacksShareTheThreadsBudget(int stack) throws InterruptedException {
        // The output of f passes through one callback per level, so the host's handler runs deep.
        BoundProgram program = Evaluate.compile("def f: if . == 0 then 0 else (. - 1 | f) | (., empty) end; f");
        List<String> outputs = new ArrayList<>();
        Throwable[] failure = new Throwable[1];
        Thread thread = new Thread(null, () -> {
            try {
                nest(program, new int[] {40, 40, 999}, 0, outputs);
            } catch (Throwable e) {
                failure[0] = e;
            }
        }, "nested", stack);
        thread.start();
        thread.join();
        assertEquals(null, failure[0]);
        assertEquals(List.of("0 at 2", "0 at 1", "0 at 0"), outputs);
    }

    // Evaluates N | f with N = depths[level], and from its output callback the next level.
    private static void nest(BoundProgram program, int[] depths, int level, List<String> outputs) {
        var outcome = Interpreter.run(program, JqValue.fromNumber(depths[level]), depth(1000), value -> {
            if (level + 1 < depths.length) {
                nest(program, depths, level + 1, outputs);
            }
            outputs.add(ValueText.toJson(value) + " at " + level);
            return true;
        });
        assertEquals(OutcomeKind.COMPLETED, outcome.kind());
    }

    @ParameterizedTest
    @ValueSource(ints = {1024 * 1024, 256 * 1024})
    void anErrorComesBackAcrossHops(int stack) {
        assertEquals("\"deep\"", Evaluate.onStack(stack, "try (def f: if . == 0 then error(\"deep\") else (. - 1 | f) + 1 end; 900 | f) catch .", "null", null));
    }
}