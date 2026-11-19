package com.devlooped.jqlite.core.binding;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.fail;

import com.devlooped.jqlite.binding.Bound;
import com.devlooped.jqlite.binding.Bound.BoundProgram;
import com.devlooped.jqlite.binding.Prelude;
import com.devlooped.jqlite.binding.Resolver;
import com.devlooped.jqlite.syntax.Parser;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

/**
 * Binding does not exhaust the host stack on what the parser accepts: nesting up to the limit of
 * spec §11, and sequences of any length, which the parser builds as deep spines. Each program is
 * parsed and bound on a thread with a 1 MB stack, as in ParserLimitTests.
 */
class ResolverLimitTests {
    private static final int LIMIT = Parser.MAX_NESTING;

    static BoundProgram binds(String source) {
        Resolver.Compiled[] result = new Resolver.Compiled[1];
        Throwable[] failure = new Throwable[1];
        Thread thread = new Thread(null, () -> {
            try {
                result[0] = Resolver.tryCompile(source);
            } catch (Throwable e) {
                failure[0] = e;
            }
        }, "bind", 1024 * 1024);
        thread.start();
        try {
            thread.join();
        } catch (InterruptedException e) {
            throw new IllegalStateException(e);
        }
        if (failure[0] != null) {
            fail("the binding failed on a 1 MB stack", failure[0]);
        }
        if (result[0].program() == null) {
            fail(result[0].error().message() + " at " + result[0].error().line() + ":" + result[0].error().column());
        }
        return result[0].program();
    }

    static String[][] nestings() {
        return new String[][] {
            {"(", ")"}, {"[", "]"}, {"{a: ", "}"}, {"{(\"a\" + ", "): 1}"}, {".[", "]"}, {"f(", ")"}, {"\"\\(", ")\""},
            {"if . then ", " end"}, {"try ", ""}, {"reduce . as $x (", ";.)"}, {"foreach . as $x (", ";.)"}, {"def f: ", "; f"},
            {"def f(g): ", "; f(.)"}, {"[1 + ", "]"}, {"[1, 2 as $x | ", "]"}, {"(1 | ", ")"}, {".[1, ", "]"}, {"{a: 1 | 1 + ", "}"},
            {"if 1, 1 as $x | ", " then . end"}, {"try -", ""}, {"f(label $x | ", ")"}, {". as [$x] ?// {(\"a\" + ", "): $y} | 1"},
        };
    }

    // Opening and closing repeated LIMIT times around "1"; each opening is one level of nesting
    // and costs the binder the most frames that level can (a construct, then a sequence inside it).
    @ParameterizedTest
    @MethodSource("nestings")
    void nestingUpToTheLimitBinds(String open, String close) {
        int levels = open.contains("{(") ? LIMIT / 2 : LIMIT;
        binds("def f: 1; def f(g): 1; " + open.repeat(levels) + "1" + close.repeat(levels));
    }

    static String[][] sequences() {
        return new String[][] {
            {"", "1 + ", "1"}, {"", "1 * ", "1"}, {"", "1 // ", "1"}, {"", "1 and ", "1"}, {"", "1 == 1 or ", "1"}, {"", "1 | ", "1"},
            {"", "1, ", "1"}, {"", "1 as $x | ", "$x"}, {"", "label $x | ", "break $x"}, {"", "def f: 1; ", "f"}, {"(", "def f: 1; ", "f)"},
            {"[1, ", "def f: 1; ", "f]"}, {"", "-", "1"}, {"", "1 + -", "1"}, {"", ".a", ""}, {"", ".[0]", ""}, {"", ".a?", ""},
            {". as $a", " ?// $a", " | 1"}, {"{a: ", "1 | ", "1}"}, {"{", "a: 1, ", "}"}, {"\"", "\\(1)", "\""}, {"[", "1, 1 as $x | ", "$x]"},
            {"", "1, def f: 1; ", "f"}, {"", "1, label $x | ", "1"}, {"", "1 // 1 | ", "1"}, {"", "1 as $x | label $y | def f: $x; ", "f"},
            {"", "1 | 1, 1 | ", "1"}, {"", "def f(g): g; ", "f(.)"}, {"", "reduce . as $x (.; .) | ", "1"},
        };
    }

    // The sequences of ParserLimitTests: prefix, 10,000 elements, suffix.
    @ParameterizedTest
    @MethodSource("sequences")
    void longSequencesDoNotRecurse(String prefix, String element, String suffix) {
        binds(prefix + element.repeat(10_000) + suffix);
    }

    @Test
    void longElifChainsDoNotRecurse() {
        binds("if . then 1 " + "elif . then 1 ".repeat(10_000) + "else 2 end");
    }

    @Test
    void aVariableTenThousandFramesOut() {
        BoundProgram program = binds("0 as $a | " + "1 as $x | ".repeat(10_000) + "$a");
        Bound.Node node = program.body();
        int depth = 0;
        while (node instanceof Bound.Bind bind) {
            node = bind.body;
            depth++;
        }

        assertEquals(10_001, depth);
        Bound.Variable variable = assertInstanceOf(Bound.Variable.class, node);
        assertEquals(10_000, variable.hops);
        assertEquals(0, variable.slot);
    }

    @Test
    void manyDefinitionsInARow() {
        BoundProgram program = binds("def f: 1; ".repeat(10_000) + "f");
        assertEquals(Prelude.instance().functions().length + 10_000, program.functions().length);
        Bound.CallFunction call = assertInstanceOf(Bound.CallFunction.class, program.body());
        assertEquals(program.functions().length - 1, call.function);
        assertEquals(0, call.hops);
    }

    @Test
    void manyParametersAndArguments() {
        BoundProgram program = binds("def f(" + "a; ".repeat(10_000) + "b): b; f(" + "1; ".repeat(10_000) + "1)");
        assertEquals(10_001, assertInstanceOf(Bound.CallFunction.class, program.body()).arguments.length);
        Bound.CallParameter parameter = assertInstanceOf(Bound.CallParameter.class, program.functions()[program.functions().length - 1].body());
        assertEquals(0, parameter.hops);
        assertEquals(10_000, parameter.slot);
    }
}
