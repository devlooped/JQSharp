package com.devlooped.jqlite;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

import com.devlooped.jqlite.values.JqValue;
import java.util.List;
import java.util.function.Supplier;
import java.util.stream.Collectors;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;
import org.junit.jupiter.params.provider.ValueSource;

/**
 * Runaway recursion through the public API (spec §11): the error is {@code Maximum call depth
 * exceeded} and the process survives, on a normal thread and on a small one.
 */
class JqDepthTests {
    // N | f makes N + 1 calls of f.
    private static final String TAIL = "def f: if . == 0 then 0 else (. - 1 | f) end; f";
    private static final String NON_TAIL = "def f: if . == 0 then 0 else (. - 1 | f) + 1 end; f";

    static Object[][] boundaries() {
        // program, MaxDepth (0: the default, 1000), the deepest N that fits, its output
        return new Object[][] {
            {TAIL, 0, 999, "0"},
            {NON_TAIL, 0, 999, "999"},
            {TAIL, 3000, 2999, "0"},
            {NON_TAIL, 3000, 2999, "2999"},
        };
    }

    private static JqExpression parse(String program, int maxDepth) {
        return maxDepth > 0 ? Jq.parse(program, JqOptions.defaults().withMaxDepth(maxDepth)) : Jq.parse(program);
    }

    static <T> T onSmallStack(Supplier<T> run) {
        Object[] result = new Object[1];
        Throwable[] failure = new Throwable[1];
        Thread thread = new Thread(null, () -> {
            try {
                result[0] = run.get();
            } catch (Throwable e) {
                failure[0] = e;
            }
        }, "small", 256 * 1024);
        thread.start();
        try {
            thread.join();
        } catch (InterruptedException e) {
            throw new IllegalStateException(e);
        }
        if (failure[0] != null) {
            throw new AssertionError(failure[0]);
        }
        @SuppressWarnings("unchecked")
        T value = (T) result[0];
        return value;
    }

    @ParameterizedTest
    @ValueSource(strings = {TAIL, NON_TAIL})
    void runawayRecursionThrowsAndTheProcessSurvives(String program) {
        JqExpression expression = Jq.parse(program);
        JqException error = assertThrows(JqException.class, () -> expression.evaluate(JqValue.fromNumber(100000)));
        assertEquals("Maximum call depth exceeded", error.getMessage());
        assertEquals("\"Maximum call depth exceeded\"", JqJson.toJson(error.value()));
    }

    @ParameterizedTest
    @ValueSource(strings = {TAIL, NON_TAIL})
    void runawayRecursionOnASmallStackThrows(String program) {
        JqExpression expression = Jq.parse(program);
        JqException error = onSmallStack(() -> assertThrows(JqException.class, () -> expression.evaluate(JqValue.fromNumber(100000))));
        assertEquals("Maximum call depth exceeded", error.getMessage());
    }

    @Test
    void runawayRecursionIsCaughtByTheProgram() {
        List<JqValue> results = Jq.parse("def f: if . == 0 then 0 else (. - 1 | f) + 1 end; try f catch .").evaluate(JqValue.fromNumber(100000));
        assertEquals(List.of("\"Maximum call depth exceeded\""), results.stream().map(JqJson::toJson).toList());
    }

    @ParameterizedTest
    @MethodSource("boundaries")
    void theLastCallBelowMaxDepthSucceedsAndTheNextFails(String program, int maxDepth, int deepest, String output) {
        JqExpression expression = parse(program, maxDepth);
        for (boolean small : new boolean[] {false, true}) {
            Supplier<String> run = () -> expression.evaluate(JqValue.fromNumber(deepest)).stream().map(JqJson::toJson).collect(Collectors.joining(","));
            assertEquals(output, small ? onSmallStack(run) : run.get());
            Supplier<JqException> fail = () -> assertThrows(JqException.class, () -> expression.evaluate(JqValue.fromNumber(deepest + 1)));
            JqException error = small ? onSmallStack(fail) : fail.get();
            assertEquals("Maximum call depth exceeded", error.getMessage());
        }
    }
}
