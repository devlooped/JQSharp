package com.devlooped.jqlite.core.runtime;

import static com.devlooped.jqlite.core.runtime.Evaluate.compile;
import static com.devlooped.jqlite.core.runtime.Evaluate.line;
import static com.devlooped.jqlite.core.runtime.Evaluate.run;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.devlooped.jqlite.binding.Bound.BoundProgram;
import com.devlooped.jqlite.runtime.Evaluation.EvaluationOptions;
import com.devlooped.jqlite.values.JqObject;
import com.devlooped.jqlite.values.JqValue;
import java.util.List;
import java.util.Map;
import java.util.stream.IntStream;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;
import org.junit.jupiter.params.provider.ValueSource;

/**
 * The interpreter (spec §6 to §8, §11, §12) beyond the corpus: signals through every construct,
 * laziness, path mode, the depth limit and the host stack, and the host interface. Each case is a
 * program, its input and its outputs as one line (see Evaluate.line).
 */
class InterpreterTests {
    private static EvaluationOptions depth(int maxDepth) {
        EvaluationOptions options = new EvaluationOptions();
        options.maxDepth = maxDepth;
        return options;
    }

    static String[][] signalsPassThroughEveryConstructCases() {
        return new String[][] {
            // try catches its body's errors, keeps the outputs before, and stops the body.
            {"[try (1, error(\"x\"), 3) catch .]", "[1,\"x\"]"},
            {"[.[] | try error catch .]", "error: \"Cannot iterate over null (null)\""},
            {"[.[]?]", "[]"},
            {"try error({}) catch .", "{}"},
            {"[(1, 2) | try error catch . + 10]", "[11,12]"},
            // Errors raised by the handler propagate.
            {"try (try error(\"a\") catch error(\"b\")) catch .", "\"b\""},
            // Errors of the consumer are not errors of the producer (§6.2), for try, ? and ?// alike.
            {"\"x\" | try ((try . catch \"too much\") | error) catch \"right\"", "\"right\""},
            {"[try ((try (1,2) catch \"inner\") | if . == 2 then error(\"consumer\") else . end) catch (\"outer: \" + .)]", "[1,\"outer: consumer\"]"},
            {"try ((1, 2)? | error(tostring)) catch .", "\"1\""},
            {"try (([[1]] | .[] as [$a] ?// $a | $a) | error(tostring)) catch .", "\"1\""},
            // ?// tries the next pattern when matching fails or the body raises an error; errors with the last propagate.
            {"[[[1],2][] as [$a] ?// $a | $a]", "[1,2]"},
            {"[[[1]][] as [$a] ?// $a | if ($a | type) == \"number\" then error(\"n\") else $a end]", "[[1]]"},
            {"try (1 as [$a] ?// {$a} | $a) catch .", "\"Cannot index number with string (\\\"a\\\")\""},
            // ?// in reduce and foreach: a retried alternative starts from the state before the source value (§7.8, §7.9).
            {"[reduce ([1],[5]) as [$a] ?// $a (10; if $a == 1 then error(\"e\") else [., $a] end)]", "[[[10,[1]],5]]"},
            {"[foreach ([1]) as [$a] ?// $a (10; . + 1; if $a == 1 then ., error(\"x\") else [., $a] end)]", "[11,[11,[1]]]"},
            {"try (reduce ([1],[2]) as [$a] ?// $a (0; if $a == 1 then error(\"e\") else . + $a end)) catch .", "\"number (0) and array ([1]) cannot be added\""},
            {"[reduce ({\"a\":1}, [2]) as {a: $a} ?// [$a] (0; . + $a)]", "[3]"},
            {"try (foreach ([1]) as [$a] ?// $a (0; .; $a) | error(tostring)) catch .", "\"1\""},
            {"[foreach ([1], [2]) as [$a] ?// $a (0; . + (if $a == 2 then error(\"two\") else $a end); .)]", "error: \"number (1) and array ([2]) cannot be added\""},
            // A computed pattern key indexes with the rules of §7.6, whatever its kind (§7.5).
            {"[5,6] | . as {(.[0] * 0 + 1): $v} | $v", "6"},
            {"{\"a\":1} | . as {(.a): $v} | $v", "error: \"Cannot index object with number (1)\""},
            {"null | . as {(0 + 1): $v, (\"b\"): $w} | [$v, $w]", "[null,null]"},
            // // propagates errors of its left side; its right side only when the left has no true output.
            {"try [(1, error(\"x\")) // 3] catch .", "\"x\""},
            {"[empty // 3, (false, null) // 4, (1, null, 2) // 5]", "[3,4,1,2]"},
            // break stops its own label instance only, lexically, and is not an error.
            {"[label $f | def g: break $f; (label $f | 1, g, 2), 3]", "[1]"},
            {"[label $a | label $b | 1, break $a, 2]", "[1]"},
            {"label $out | try (0, break $out, 1) catch 99", "0"},
            {"def f: label $x | (1, break $x); [f, f]", "[1,1]"},
            // reduce and foreach with no and several outputs of the update.
            {"reduce (1,2,3) as $x (0; empty)", "null"},
            {"[reduce empty as $x (1, 2; .)]", "[1,2]"},
            {"reduce (1,2) as $x (0; ., 10)", "10"},
            {"[foreach (1,2) as $x (0; (. + 1, . + 10); .)]", "[1,10,11,20]"},
            {"[foreach (1,2) as $x (0; empty; .)]", "[]"},
            {"[foreach (1,2) as $x (0, 1; . + $x)]", "[1,3,2,4]"},
            // halt and halt_error stop everything; try does not catch them.
            {"1, halt, 2", "1, halt 0"},
            {"try (\"bye\" | halt_error(5)) catch 0", "halt 5 \"bye\""},
            {"try halt_error(\"x\") catch .", "\"null (null) halt_error/1: number required\""},
        };
    }

    @ParameterizedTest
    @MethodSource("signalsPassThroughEveryConstructCases")
    void signalsPassThroughEveryConstruct(String program, String expected) {
        assertEquals(expected, line(program));
    }

    static String[][] consumersThatStopStopTheirProducersCases() {
        return new String[][] {
            {"first(range(1000000000))", "0"},
            {"[limit(3; repeat(1))]", "[1,1,1]"},
            {"isempty(repeat(1))", "false"},
            {"[label $f | 1, break $f, error(\"unreached\")]", "[1]"},
            {"first(range(10; infinite))", "10"},
            {"[limit(1; 1, error(\"unreached\"))]", "[1]"},
            {"[.[] | select(. > 1)] | first", "2"},
            {"any(range(1000000000); . > 2)", "true"},
        };
    }

    @ParameterizedTest
    @MethodSource("consumersThatStopStopTheirProducersCases")
    void consumersThatStopStopTheirProducers(String program, String expected) {
        long started = System.nanoTime();
        assertEquals(expected, line(program, "[1,2,3]"));
        long elapsedMs = (System.nanoTime() - started) / 1_000_000;
        assertTrue(elapsedMs < 5000, program + " took " + elapsedMs + " ms");
    }

    static String[][] pathsArePartOfEvaluationCases() {
        return new String[][] {
            // §8.1, construct by construct.
            {"path(.)", "[]"},
            {"[path(.a.b, .c[1:2], .c[])]", "[[\"a\",\"b\"],[\"c\",{\"start\":1,\"end\":2}],[\"c\",0],[\"c\",1]]"},
            {"[path(.a[.k])]", "[[\"a\",\"x\"]]"},
            {"[path(.z?, (.c | try .[] catch .), (.a | .b))]", "[[\"z\"],[\"c\",0],[\"c\",1],[\"a\",\"b\"]]"},
            {"[path(if .k == \"x\" then .a else .c end)]", "[[\"a\"]]"},
            {"[path(.z // .a)]", "[[\"a\"]]"},
            {"[path(.k as $k | .a[$k])]", "[[\"a\",\"x\"]]"},
            {"[path(reduce (\"a\", \"b\") as $k (.; .[$k]))]", "[[\"a\",\"b\"]]"},
            {"[path(foreach (\"a\", \"b\") as $k (.; .[$k]))]", "[[\"a\"],[\"a\",\"b\"]]"},
            {"[path(label $x | .a, break $x)]", "[[\"a\"]]"},
            {"def f(p): p | .b; [path(f(.a))]", "[[\"a\",\"b\"]]"},
            {"[path(empty), path(getpath([\"a\", \"q\"]))]", "[[\"a\",\"q\"]]"},
            {"[path(first(.c[]), last(.c[]), limit(1; .c[]), skip(1; .c[]))]", "[[\"c\",0],[\"c\",1],[\"c\",0],[\"c\",1]]"},
            {"[path(.. | select(type == \"number\"))]", "[[\"a\",\"b\"],[\"c\",0],[\"c\",1]]"},
            {"[path(.c | recurse(if . == null then empty else .[0]? end))]", "[[\"c\"],[\"c\",0]]"},
            // The three path errors.
            {"try path(1) catch .", "\"Invalid path expression with result 1\""},
            {"try path($v | .a) catch .", "\"Invalid path expression near attempt to access element \\\"a\\\" of null\""},
            {"try path(.k | ascii_upcase | .[]) catch .", "\"Invalid path expression near attempt to iterate through \\\"X\\\"\""},
            {"try path(.k + \"y\") catch .", "\"Invalid path expression with result \\\"xy\\\"\""},
        };
    }

    @ParameterizedTest
    @MethodSource("pathsArePartOfEvaluationCases")
    void pathsArePartOfEvaluation(String program, String expected) {
        EvaluationOptions options = new EvaluationOptions();
        options.variables = Map.of("v", JqValue.NULL);
        assertEquals(expected, line(program, "{\"a\":{\"b\":0},\"k\":\"x\",\"c\":[1,2]}", options));
    }

    static String[][] assignmentWorksThroughThePreludeCases() {
        return new String[][] {
            {".a = 1", "{\"a\":1,\"b\":[1,2]}"},
            {".b[] |= . * 10", "{\"a\":0,\"b\":[10,20]}"},
            {".b[1] += 5", "{\"a\":0,\"b\":[1,7]}"},
            {"[.a += (1, 2)] | map(.a)", "[1,2]"},
            {".c //= \"d\"", "{\"a\":0,\"b\":[1,2],\"c\":\"d\"}"},
            {"(.a, .z) = 9", "{\"a\":9,\"b\":[1,2],\"z\":9}"},
            {".b |= map(select(. > 1))", "{\"a\":0,\"b\":[2]}"},
            {"(.b[] | select(. == 1)) |= empty", "{\"a\":0,\"b\":[2]}"},
            {"del(.a, .b[0])", "{\"b\":[2]}"},
            {"to_entries", "[{\"key\":\"a\",\"value\":0},{\"key\":\"b\",\"value\":[1,2]}]"},
            {"with_entries(.value |= tostring)", "{\"a\":\"0\",\"b\":\"[1,2]\"}"},
            {"[paths]", "[[\"a\"],[\"b\"],[\"b\",0],[\"b\",1]]"},
            {"def _modify(p; f): \"mine\"; .a |= 1", "\"mine\""},
        };
    }

    @ParameterizedTest
    @MethodSource("assignmentWorksThroughThePreludeCases")
    void assignmentWorksThroughThePrelude(String program, String expected) {
        assertEquals(expected, line(program, "{\"a\":0,\"b\":[1,2]}"));
    }

    static String[][] hostVariablesAndENVCases() {
        return new String[][] {
            {"$x", "1"},
            {"[$x, $y]", "[1,\"two\"]"},
            {"$x as $x | $x + 1", "2"},
            {"$missing", "error: \"$missing is not defined\""},
            {"try $missing catch .", "\"$missing is not defined\""},
            {"$ENV.HOME, env.HOME", "\"/home\", \"/home\""},
            {"1 as $ENV | $ENV", "1"},
        };
    }

    @ParameterizedTest
    @MethodSource("hostVariablesAndENVCases")
    void hostVariablesAndENV(String program, String expected) {
        EvaluationOptions options = new EvaluationOptions();
        options.variables = Map.of("x", JqValue.fromNumber(1), "y", JqValue.fromString("two"));
        options.environment = () -> JqObject.EMPTY.set("HOME", JqValue.fromString("/home"));
        assertEquals(expected, line(program, "null", options));
    }

    static String[][] loopNativesDoNotConsumeTheDepthCases() {
        return new String[][] {
            // Natives marked *loop* do not consume the call depth.
            {"[limit(5000; repeat(1))] | length", "5000"},
            {"[range(100000)] | length", "100000"},
            {"last(range(100000))", "99999"},
            {"0 | until(. >= 100000; . + 1)", "100000"},
            {"[0 | while(. < 100000; . + 1)] | length", "100000"},
            {"[skip(99998; range(100000))]", "[99998,99999]"},
        };
    }

    @ParameterizedTest
    @MethodSource("loopNativesDoNotConsumeTheDepthCases")
    void loopNativesDoNotConsumeTheDepth(String program, String expected) {
        EvaluationOptions options = new EvaluationOptions();
        options.maxDepth = 10;
        assertEquals(expected, Evaluate.onSmallStack(program, "null", options));
    }

    static String[][] outputsComeInTheOrderOfTheSpecCases() {
        return new String[][] {
            {"[1, 2] | [.[] as $x | $x * 10]", "[10,20]"},
            {"[(1,2) + (10,20)]", "[11,12,21,22]"},
            {"[{a: (1,2), b: (3,4)}]", "[{\"a\":1,\"b\":3},{\"a\":1,\"b\":4},{\"a\":2,\"b\":3},{\"a\":2,\"b\":4}]"},
            {"[\"\\(1,2)-\\(3,4)\"]", "[\"1-3\",\"2-3\",\"1-4\",\"2-4\"]"},
            {"[range(0,1; 3,4)]", "[0,1,2,0,1,2,3,1,2,1,2,3]"},
            {"[.[(0,1)]]", "[1,2]"},
            {"[range(10)] | [.[(1,2):(3,4)] | length]", "[2,3,1,2]"},
            {"[(true, false) and (true, false)]", "[true,false,false]"},
            {"[(true, false) or (true, false)]", "[true,true,false]"},
            {"[-(1, 2)]", "[-1,-2]"},
            {"[{(\"a\", \"b\"): 1}]", "[{\"a\":1},{\"b\":1}]"},
            {"{\"x1\":\"a\",\"x2\":\"b\"} | [{\"x\\(1,2)\"}]", "[{\"x1\":\"a\"},{\"x2\":\"b\"}]"},
            {"[. as [$a, $b] | {$a, $b}]", "[{\"a\":1,\"b\":2}]"},
            {". as {(\"a\", \"b\"): $v} ?// $v | $v", "[1,2]"},
            {"{\"a\":1,\"b\":2} | [. as {(\"a\", \"b\"): $v} | $v]", "[1,2]"},
        };
    }

    @ParameterizedTest
    @MethodSource("outputsComeInTheOrderOfTheSpecCases")
    void outputsComeInTheOrderOfTheSpec(String program, String expected) {
        assertEquals(expected, line(program, "[1,2]"));
    }

    @Test
    void theHostStopsTheStream() {
        assertEquals(List.of("0", "1", "2"), run(compile("range(1000000000)"), JqValue.NULL, null, 3));
    }

    @Test
    void oneProgramRunsConcurrentlyWithDifferentVariables() {
        BoundProgram program = compile("def f: if . == 0 then $n else (. - 1 | f) end; [range(200) | {a: f}] | map(.a) | add");
        String[] results = new String[64];
        IntStream.range(0, results.length).parallel().forEach(i -> {
            EvaluationOptions options = new EvaluationOptions();
            options.variables = Map.of("n", JqValue.fromNumber(i));
            results[i] = String.join(", ", run(program, JqValue.fromNumber(50), options));
        });

        for (int i = 0; i < results.length; i++) {
            assertEquals(Integer.toString(200 * i), results[i]);
        }
    }

    @ParameterizedTest
    @ValueSource(ints = {1000, 50})
    void theCallDepthIsLimited(int maxDepth) {
        EvaluationOptions options = depth(maxDepth);
        String count = "def f: if . == 0 then 0 else (. - 1 | f) end; ";
        // n | f makes n + 1 calls.
        assertEquals("0", Evaluate.onSmallStack(count + (maxDepth - 1) + " | f", "null", options));
        assertEquals("error: \"Maximum call depth exceeded\"", Evaluate.onSmallStack(count + maxDepth + " | f", "null", options));
        assertEquals("\"Maximum call depth exceeded\"", Evaluate.onSmallStack("try (" + count + "100000 | f) catch .", "null", options));
    }

    @Test
    void aSingleCallHasEndedWhenItsOutputIsUsed() {
        // 20 | fib makes 21,891 calls, 20 deep.
        String fib = "def fib: if . < 2 then . else (. - 1 | fib) + (. - 2 | fib) end; 20 | fib";
        assertEquals("6765", line(fib, "null", depth(20)));
        assertEquals("error: \"Maximum call depth exceeded\"", line(fib, "null", depth(19)));
        assertEquals("6765", line("def fib($n): if $n < 2 then $n else fib($n - 1) + fib($n - 2) end; fib(20)", "null", depth(20)));

        // A call that is not single stays active while its outputs are consumed: the two calls nest.
        String many = "def g: if . < 2 then ., empty else (. - 1 | g) + (. - 2 | g) end; ";
        assertEquals("55", line(many + "10 | g"));
        assertEquals("error: \"Maximum call depth exceeded\"", line(many + "15 | g"));
    }

    @Test
    void tailRecursionReachesTheLimitWithoutTheHostStack() {
        assertEquals("error: \"Maximum call depth exceeded\"", Evaluate.onSmallStack("def f: if . == 0 then 0 else (. - 1 | f) end; 100000 | f", "null", null));
    }
}
