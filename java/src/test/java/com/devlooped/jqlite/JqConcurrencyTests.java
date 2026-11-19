package com.devlooped.jqlite;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.devlooped.jqlite.values.JqValue;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentLinkedQueue;
import java.util.concurrent.CyclicBarrier;
import java.util.stream.Collectors;
import java.util.stream.IntStream;
import org.junit.jupiter.api.Test;

/**
 * An expression, the prelude and the modules are immutable and shared; one evaluation owns all its
 * state. Many threads evaluating at once, started together, must each get what a single thread
 * gets. No timing is involved: a shared mutable cell shows up as a wrong result.
 */
class JqConcurrencyTests {
    private static final int THREADS = 32;
    private static final int ROUNDS = 500;

    // Variables ($n, $name), a recursive function, a closure parameter, reduce, label/break, limit,
    // a regex builtin, an update assignment, $ENV and a module import: each piece of shared state.
    private static final String PROGRAM = """
        import "funcs" as f;
        def fact: if . <= 1 then 1 else . * (. - 1 | fact) end;
        def twice(g): g | g;
        {
          fact: ($n | fact),
          twice: ($n | twice(. * 2)),
          sum: (reduce (.xs[] | . * $n) as $x (0; . + $x)),
          first_over: [label $out | .xs[] | if . > $n then ., break $out else empty end],
          limit: [limit(3; .xs[] | . + $n)],
          words: [.text | match("[a-z]+"; "g").string] | join($name),
          update: (.a |= . + $n | .a),
          env: $ENV.JQ_CONCURRENCY,
          module: ($n | f::addtwo)
        }
        """;

    private static final JqOptions OPTIONS = JqOptions.defaults()
        .withEnvironment(Map.of("JQ_CONCURRENCY", "set"))
        .withResolver(new JqFileResolver(JqTestParser.specTests().resolve("modules")));

    private static String evaluate(JqExpression expression, int n) {
        String xs = IntStream.range(n - 2, n + 5).mapToObj(Integer::toString).collect(Collectors.joining(","));
        JqValue input = JqJson.parse("{\"xs\":[" + xs + "],\"text\":\"ab" + n + "cd ef" + n + "gh\",\"a\":" + (n * 10) + "}");
        Map<String, JqValue> variables = Map.of("n", JqValue.fromNumber(n), "name", JqValue.fromString("<" + n + ">"));
        return expression.evaluate(input, variables).stream().map(JqJson::toJson).collect(Collectors.joining("\n"));
    }

    @FunctionalInterface
    private interface Work {
        void run(int thread, ConcurrentLinkedQueue<String> wrong) throws Exception;
    }

    // Runs work on THREADS threads that start together; returns what they report as wrong.
    private static List<String> onManyThreads(Work work) throws InterruptedException {
        ConcurrentLinkedQueue<String> wrong = new ConcurrentLinkedQueue<>();
        CyclicBarrier start = new CyclicBarrier(THREADS);
        List<Thread> threads = new ArrayList<>();
        for (int t = 0; t < THREADS; t++) {
            int index = t;
            threads.add(new Thread(() -> {
                try {
                    start.await();
                    work.run(index, wrong);
                } catch (Exception e) {
                    wrong.add("thread " + index + ": " + e);
                }
            }));
        }
        threads.forEach(Thread::start);
        for (Thread thread : threads) {
            thread.join();
        }
        return new ArrayList<>(wrong);
    }

    @Test
    void oneExpressionGivesEachThreadTheSingleThreadedResult() throws InterruptedException {
        JqExpression expression = Jq.parse(PROGRAM, OPTIONS);

        // Single-threaded, from a parse of its own: what each case must give.
        JqExpression reference = Jq.parse(PROGRAM, OPTIONS);
        Map<Integer, String> expected = new HashMap<>();
        for (int n = 3; n < 12; n++) {
            expected.put(n, evaluate(reference, n));
        }
        assertTrue(expected.get(3).contains("\"module\":"));
        assertTrue(expected.get(3).contains("\"env\":\"set\""));

        List<String> wrong = onManyThreads((t, bad) -> {
            for (int i = 0; i < ROUNDS; i++) {
                int n = 3 + (t * 7 + i) % 9;
                String actual = evaluate(expression, n);
                if (!actual.equals(expected.get(n))) {
                    bad.add("thread " + t + ", round " + i + ", n = " + n + ": " + actual);
                }
            }
        });

        assertEquals(List.of(), wrong.stream().limit(5).toList());
    }

    @Test
    void differentExpressionsParseAndEvaluateAtOnce() throws InterruptedException {
        List<String> wrong = onManyThreads((t, bad) -> {
            for (int i = 0; i < 20; i++) {
                int k = t * 100 + i;
                JqExpression expression = Jq.parse("def g: . + " + k + "; [limit(3; range(10) | g)] | map(select(. % 2 == 0)) | add // 0");
                String actual = expression.evaluate(JqValue.fromNumber(0)).stream().map(JqJson::toJson).collect(Collectors.joining(","));
                int even = IntStream.range(0, 3).map(x -> x + k).filter(x -> x % 2 == 0).sum();
                if (!actual.equals(Integer.toString(even))) {
                    bad.add("thread " + t + ", round " + i + ": " + actual + ", expected " + even);
                }
            }
        });

        assertEquals(List.of(), wrong.stream().limit(5).toList());
    }
}
