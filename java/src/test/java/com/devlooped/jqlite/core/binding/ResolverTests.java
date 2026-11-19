package com.devlooped.jqlite.core.binding;

import static com.devlooped.jqlite.core.binding.BoundPrinter.bind;
import static com.devlooped.jqlite.core.binding.BoundPrinter.error;
import static com.devlooped.jqlite.core.binding.BoundPrinter.print;
import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertSame;

import com.devlooped.jqlite.binding.Bound;
import com.devlooped.jqlite.binding.Prelude;
import com.devlooped.jqlite.binding.Resolver;
import java.util.ArrayList;
import java.util.List;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

/** Lexical binding (spec §7.5, §7.7, §7.15) stated on the printed bound program, and the binding errors of §10.2. */
class ResolverTests {
    static String[][] variablesBindLexicallyCases() {
        return new String[][] {
            // Variables: the innermost binding, counted in frames outward.
            {"1 as $x | $x", "(as 1 $0 $0.0)"},
            {"1 as $x | 2 as $x | $x", "(as 1 $0 (as 2 $0 $0.0))"},
            {"1 as $x | 2 as $y | $x, $y", "(as 1 $0 (as 2 $0 (, $1.0 $0.0)))"},
            {"1 as $x | (2 as $x | $x), $x", "(as 1 $0 (, (as 2 $0 $0.0) $0.0))"},
            // A binding is not visible in its own source.
            {"1 as $x | ($x + 1) as $x | $x", "(as 1 $0 (as (+ $0.0 1) $0 $0.0))"},
            // A function body sees the variables of its definition, not of its caller.
            {"1 as $x | def f: $x; 2 as $x | f", "(def f/0#0 $0.0) (as 1 $0 (as 2 $0 f/0#0^1))"},
            {"def f: 1; 2 as $x | f", "(def f/0#0 1) (as 2 $0 f/0#0^1)"},
        };
    }

    @ParameterizedTest
    @MethodSource("variablesBindLexicallyCases")
    void variablesBindLexically(String program, String expected) {
        assertEquals(expected, bind(program));
    }

    static String[][] functionsBindToTheInnermostVisibleDefinitionCases() {
        return new String[][] {
            // Recursion: a definition is visible in its own body.
            {"def f: f; f", "(def f/0#0 f/0#0^0) f/0#0^0"},
            // Later definitions shadow earlier ones; an earlier body keeps what it saw.
            {"def f: 1; def g: f; def f: 2; g", "(def f/0#0 1) (def g/0#1 f/0#0^0) (def f/0#2 2) g/0#1^0"},
            // Name and arity together identify a function.
            {"def f: 1; def f(a): 2; f, f(3)", "(def f/0#0 1) (def f/1#1 2) (, f/0#0^0 (f/1#1^0 3))"},
            // Local definitions are visible in their scope only.
            {"def f: 1; [f, (def f: 2; f), f]", "(def f/0#0 1) (def f/0#1 2) (array (, f/0#0^0 f/0#1^0 f/0#0^0))"},
            // A call of an enclosing function from a nested one, across the parameters' frame.
            {"def f(g): def h: f(g); h; f(.)", "(def f/1#0 h/0#1^0) (def h/0#1 (f/1#0^1 param^0.0)) (f/1#0^0 .)"},
        };
    }

    @ParameterizedTest
    @MethodSource("functionsBindToTheInnermostVisibleDefinitionCases")
    void functionsBindToTheInnermostVisibleDefinition(String program, String expected) {
        assertEquals(expected, bind(program));
    }

    static String[][] functionsAndCallsAreSingleByTheRulesCases() {
        return new String[][] {
            // A function is single when its body is, its own calls taken as single (spec §7.7).
            {"def fib: if . < 2 then . else (. - 1 | fib) + (. - 2 | fib) end; fib", "fib/0+ body+"},
            {"def r: ., (.[]? | r); r", "r/0- body-"},
            // A call is single when the function is and the argument of each $ parameter is.
            {"def f($a): $a + 1; f(1)", "f/1+ body+"},
            {"def f($a): $a + 1; f(1, 2)", "f/1+ body-"},
            {"def f($a; b; $c): [b] | $a + $c; f(1; 2, 3; 4)", "f/3+ body+"},
            {"def f($a; b; $c): [b] | $a + $c; f(1; 2; 3, 4)", "f/3+ body-"},
            {"def f($n): if $n < 2 then $n else f($n - 1) + f($n - 2) end; f(9)", "f/1+ body+"},
            // A call of a closure parameter is never single; a function is, when none of them comes out.
            {"def f(g): g + 1; f(1)", "f/1- body-"},
            {"def f(g): [g]; f(1, 2)", "f/1+ body+"},
            // Seen from a nested definition, the function being defined is not single.
            {"def f: def g: f; g; f", "f/0- g/0- body-"},
            // A function found not single is bound again; the functions nested in it keep their answer and their index.
            {"def f: def g: if . < 3 then . + 1 | g else . end; g, (. + 1 | f); f", "f/0- g/0+ body-"},
            {"def f: def g: def h: ., (. + 1 | h); [h], (. + 1 | g); def k: 1 + k; [g] | k, f; [f]", "f/0- g/0- h/0- k/0+ body+"},
            // Natives: with value arguments when these are single; with closures by the list of §7.7.
            {"def f: if . then empty else last(.[]) end; f", "f/0+ body+"},
            {"def f: getpath([\"a\"]) | until(. > 3; . + 1) | length; f", "f/0+ body+"},
            {"def f: getpath([\"a\"], [\"b\"]); f", "f/0- body-"},
            {"def f: limit(1; .); f", "f/0- body-"},
            // Through the prelude: error/1, any, all and isempty are single; select is not.
            {"def f: if any(.[]; . > 1) then error(\"x\") elif isempty(.[]) then 0 else map(f) | add end; f", "f/0+ body+"},
            {"def f: select(. > 1); f", "f/0- body-"},
            // An update is single like its prelude definition, with or without the native that overrides it.
            {"def f: .a |= . + 1 | .b = 2; f", "f/0+ body+"},
            {"def f: .a = (1, 2); f", "f/0- body-"},
        };
    }

    @ParameterizedTest
    @MethodSource("functionsAndCallsAreSingleByTheRulesCases")
    void functionsAndCallsAreSingleByTheRules(String program, String expected) {
        for (boolean nativeOverrides : new boolean[] {true, false}) {
            Resolver.Compiled compiled = Resolver.tryCompile(program, nativeOverrides, null);
            assertNotNull(compiled.program(), String.valueOf(compiled.error()));
            Bound.Function[] functions = compiled.program().functions();
            List<String> own = new ArrayList<>();
            for (int i = Prelude.instance().functions().length; i < functions.length; i++) {
                own.add(functions[i] + (functions[i].single() ? "+" : "-"));
            }
            own.add("body" + (compiled.program().body().atMostOne ? "+" : "-"));
            assertEquals(expected, String.join(" ", own));
        }
    }

    static String[][] parametersAreClosuresCases() {
        return new String[][] {
            // A parameter is a closure: a call of slot s of the call frame.
            {"def f(g): g; f(.)", "(def f/1#0 param^0.0) (f/1#0^0 .)"},
            {"def f(a; b): b, a; f(1; 2)", "(def f/2#0 (, param^0.1 param^0.0)) (f/2#0^0 1 2)"},
            // A repeated parameter name: the last one wins (jq 1.8.2: 2).
            {"def f(a; a): a; f(1; 2)", "(def f/2#0 param^0.1) (f/2#0^0 1 2)"},
            // Seen from a nested function, the parameter is one frame further when the nested one has parameters.
            {"def f(g): def h(x): g, x; h(1); f(2)", "(def f/1#0 (h/1#1^0 1)) (def h/1#1 (, param^1.0 param^0.0)) (f/1#0^0 2)"},
            // A parameter shadows a function of its name and arity 0 only (jq 1.8.2: 7).
            {"def x: 7; def f(x): x; f(1)", "(def x/0#0 7) (def f/1#1 param^0.0) (f/1#1^0 1)"},
            {"def x(a): 7; def f(x): x(0); f(1)", "(def x/1#0 7) (def f/1#1 (x/1#0^1 0)) (f/1#1^0 1)"},
            // A definition inside the body shadows the parameter.
            {"def f(g): def g: 1; g; f(2)", "(def f/1#0 g/0#1^0) (def g/0#1 1) (f/1#0^0 2)"},
            // def f($a) is def f(a): a as $a | …: both the filter and the variable are visible.
            {"def f($a): $a, a; f(1)", "(def f/1#0 (as param^0.0 $0 (, $0.0 param^1.0))) (f/1#0^0 1)"},
            {"def f($a; $b): $a, $b; f(1; 2)", "(def f/2#0 (as param^0.0 $0 (as param^1.1 $0 (, $1.0 $0.0)))) (f/2#0^0 1 2)"},
            // The binding of $a names its parameter by position; the filter a is the last parameter of that name (spec §7.7).
            {"def f(a; $a): [a, $a]; f(1; 2)", "(def f/2#0 (as param^0.1 $0 (array (, param^1.1 $0.0)))) (f/2#0^0 1 2)"},
            {"def f($a; a): [a, $a]; f(1; 2)", "(def f/2#0 (as param^0.0 $0 (array (, param^1.1 $0.0)))) (f/2#0^0 1 2)"},
            {"def f(x): def x: 5; x; f(1)", "(def f/1#0 x/0#1^0) (def x/0#1 5) (f/1#0^0 1)"},
            // Qualified names are ordinary names when defined (jq 1.8.2).
            {"def f(a::b): a::b; f(3)", "(def f/1#0 param^0.0) (f/1#0^0 3)"},
            {"def f::g: 1; f::g", "(def f::g/0#0 1) f::g/0#0^0"},
        };
    }

    @ParameterizedTest
    @MethodSource("parametersAreClosuresCases")
    void parametersAreClosures(String program, String expected) {
        assertEquals(expected, bind(program));
    }

    static String[][] labelsBindLexicallyCases() {
        return new String[][] {
            // A break refers to the label visible where it is written, also inside a function: the spec's example.
            {"[label $f | def g: break $f; (label $f | 1, g, 2), 3]", "(def g/0#0 (break ^0)) (array (label (, (label (, 1 g/0#0^1 2)) 3)))"},
            {"label $a | label $b | break $a, break $b", "(label (label (, (break ^1) (break ^0))))"},
            {"label $a | 1 as $x | break $a", "(label (as 1 $0 (break ^1)))"},
            // Labels and variables are separate name spaces.
            {"label $x | 1 as $x | $x, break $x", "(label (as 1 $0 (, $0.0 (break ^1))))"},
            {"label $a::b | break $a::b", "(label (break ^0))"},
        };
    }

    @ParameterizedTest
    @MethodSource("labelsBindLexicallyCases")
    void labelsBindLexically(String program, String expected) {
        assertEquals(expected, bind(program));
    }

    static String[][] patternsBindEveryVariableCases() {
        return new String[][] {
            {". as [$a, $b] | $a, $b", "(as . [$1 $0] (, $0.1 $0.0))"},
            {". as {$a, b: [$c]} | $a, $c", "(as . {(\"a\" $0 _) (\"b\" _ [$1])} (, $0.0 $0.1))"},
            {". as {$a: [$b]} | $a, $b", "(as . {(\"a\" $0 [$1])} (, $0.0 $0.1))"},
            // Every variable of every alternative has a slot; a name shared by alternatives shares it.
            {". as [$a] ?// {a: $b} ?// $a | [$a, $b]", "(as . (?// [$0] {(\"a\" _ $1)} $0) (array (, $0.0 $0.1)))"},
            // A repeated name: the first occurrence in destructuring order owns the slot (array from the last element, object from the first entry).
            {". as [$a, $a] | $a", "(as . [$_ $0] $0.0)"},
            {". as {$a, b: $a} | $a", "(as . {(\"a\" $0 _) (\"b\" _ $_)} $0.0)"},
            {". as [$a, [$a]] | $a", "(as . [$_ [$0]] $0.0)"},
            // With ?// every occurrence stores and the last in matching order stays (spec §7.5).
            {". as {$a, b: $a} ?// $c | $a", "(as . (?// {(\"a\" $0 _) (\"b\" _ $0)} $1) $0.0)"},
            {". as {x: $c} ?// [$a, $a] | $a", "(as . (?// {(\"x\" _ $0)} [$1 $1]) $0.1)"},
            // Key expressions see the scope outside the binding, not the pattern's variables (jq 1.8.2).
            {"\"b\" as $x | . as {a: $x, ($x): $y} | $y", "(as \"b\" $0 (as . {(\"a\" _ $0) ($0.0 _ $1)} $0.1))"},
            {". as {a: $x, ($x): $y} | $y", "(as . {(\"a\" _ $0) ($$x _ $1)} $0.1)"},
            // reduce/foreach: the source and the initial value outside the binder's frame, update and extract inside.
            {"1 as $x | reduce . as $y ($x; $x, $y)", "(as 1 $0 (reduce . $0 $0.0 (, $1.0 $0.0)))"},
            {"foreach .[] as [$a] (0; $a; [$a, .])", "(foreach (iter .) [$0] 0 $0.0 (array (, $0.0 .)))"},
            {"reduce .[] as $x (.; .) | $x", "(| (reduce (iter .) $0 . .) $$x)"},
        };
    }

    @ParameterizedTest
    @MethodSource("patternsBindEveryVariableCases")
    void patternsBindEveryVariable(String program, String expected) {
        assertEquals(expected, bind(program));
    }

    static String[][] unboundVariablesAreHostVariablesCases() {
        return new String[][] {
            // An unbound simple $name is a host variable, numbered by first appearance.
            {"$foo, $bar, $foo", "(, $$foo $$bar $$foo)"},
            {"$__prog__", "$$__prog__"},
            {"$ENV", "$ENV"},
            {"1 as $ENV | $ENV", "(as 1 $0 $0.0)"},
            {"def env: $ENV; env", "(def env/0#0 $ENV) env/0#0^0"},
        };
    }

    @ParameterizedTest
    @MethodSource("unboundVariablesAreHostVariablesCases")
    void unboundVariablesAreHostVariables(String program, String expected) {
        assertEquals(expected, bind(program));
    }

    static String[][] theLocationIsALiteralCases() {
        return new String[][] {
            // $__loc__ is not a variable: the resolver writes its location object (§7.5).
            {"$__loc__", "{\"file\":\"<top-level>\",\"line\":1}"},
            {"1 as $x |\n\n$__loc__", "(as 1 $0 {\"file\":\"<top-level>\",\"line\":3})"},
        };
    }

    @ParameterizedTest
    @MethodSource("theLocationIsALiteralCases")
    void theLocationIsALiteral(String program, String expected) {
        assertEquals(expected, bind(program));
    }

    static String[][] callsResolveToUserThenPreludeThenNativeCases() {
        return new String[][] {
            // Builtins after user definitions: prelude, then natives (§7.7). A prelude function is defined
            // in the outermost environment, so the call leaves every frame of the caller.
            {"length", "native:length/0"},
            {"map(.)", "(prelude:map/1^0 .)"},
            {"1 as $x | 2 as $y | map($x)", "(as 1 $0 (as 2 $0 (prelude:map/1^2 $1.0)))"},
            {"def map(f): 1; map(.)", "(def map/1#0 1) (map/1#0^0 .)"},
            {"def length: 1; length", "(def length/0#0 1) length/0#0^0"},
            // The parser's own calls resolve the same way: .. is recurse/0, |= is _modify/2, op= binds $*update.
            {"..", "prelude:recurse/0^0"},
            {"def recurse: 1; ..", "(def recurse/0#0 1) recurse/0#0^0"},
            // _modify and _assign are prelude builtins with a native override (Native.OverridesPrelude).
            {".a |= 1", "(native:_modify/2 (index . \"a\") 1)"},
            {".a = 1", "(native:_assign/2 (index . \"a\") 1)"},
            {".a += 1", "(as 1 $0 (native:_modify/2 (index . \"a\") (+ . $0.0)))"},
            {"def _modify(a; b): 42; .a |= 1", "(def _modify/2#0 42) (_modify/2#0^0 (index . \"a\") 1)"},
            // A definition named like a literal does not change the literal (spec §2).
            {"def true: 1; true", "(def true/0#0 1) true"},
        };
    }

    @ParameterizedTest
    @MethodSource("callsResolveToUserThenPreludeThenNativeCases")
    void callsResolveToUserThenPreludeThenNative(String program, String expected) {
        assertEquals(expected, bind(program));
    }

    static String[][] unknownNamesAreBindingErrorsCases() {
        return new String[][] {
            {"foo", "foo/0 is not defined @1:1"},
            {"1 | foo(1; 2)", "foo/2 is not defined @1:5"},
            {"map(1; 2)", "map/2 is not defined @1:1"},
            {"1 |\n  foo", "foo/0 is not defined @2:3"},
            {"foo::bar", "foo::bar/0 is not defined @1:1"},
            // The first unknown name in source order.
            {"1 + foo, bar", "foo/0 is not defined @1:5"},
            {"foo(bar)", "foo/1 is not defined @1:1"},
            {"map(bar)", "bar/0 is not defined @1:5"},
            // A definition is visible after its own def only, and local ones in their scope only (jq 1.8.2 positions).
            {"def f: g; def g: 1; f", "g/0 is not defined @1:8"},
            {"def f: def g: 3; g; g", "g/0 is not defined @1:21"},
            {"(def f: 1; f), f", "f/0 is not defined @1:16"},
            // A parameter is f/0 only.
            {"def f(g): g(1); f(1)", "g/1 is not defined @1:11"},
            {"def f($a): a(1); f(1)", "a/1 is not defined @1:12"},
            {". as $foo | break $foo", "$*label-foo is not defined @1:13"},
            {"label $f | def g: 1; break $g", "$*label-g is not defined @1:22"},
            // A label is not visible in a function defined outside it.
            {"def g: break $f; label $f | g", "$*label-f is not defined @1:8"},
            {"$foo::bar", "$foo::bar is not defined @1:1"},
            {"1 as $x | $x::y", "$x::y is not defined @1:11"},
            // Errors inside patterns, reduce and foreach.
            {". as {(foo): $x} | $x", "foo/0 is not defined @1:8"},
            {"reduce . as $x (0; foo)", "foo/0 is not defined @1:20"},
            {"foreach . as $x (0; .; foo)", "foo/0 is not defined @1:24"},
            // Without a module source no module exists.
            {"import \"a\" as a; 1", "module not found: a @1:1"},
            {"1 as $x | include \"a\"; 1", "Unexpected 'include' @1:11"},
        };
    }

    @ParameterizedTest
    @MethodSource("unknownNamesAreBindingErrorsCases")
    void unknownNamesAreBindingErrors(String program, String expected) {
        assertEquals(expected, error(program));
    }

    @Test
    void hostVariablesAreListedOnce() {
        Resolver.Compiled compiled = Resolver.tryCompile("$foo, $bar, ($foo as $bar | $bar), $baz");
        assertArrayEquals(new String[] {"foo", "bar", "baz"}, compiled.program().hostVariables());
    }

    @Test
    void withoutOverridesBuiltinsBindToThePrelude() {
        Resolver.Compiled compiled = Resolver.tryCompile(".a |= 1, (.b = 2), map_values(.)", false, null);
        assertEquals("(, (prelude:_modify/2^0 (index . \"a\") 1) (prelude:_assign/2^0 (index . \"b\") 2) (prelude:map_values/1^0 .))", print(compiled.program()));

        // Inside the prelude too: map_values calls _modify.
        Bound.Node mapValues = Prelude.instance().functions()[Prelude.instance().find("map_values", 1)].body();
        assertEquals(Bound.NodeKind.CALL_NATIVE, mapValues.kind);
        mapValues = Prelude.withoutOverrides().functions()[Prelude.withoutOverrides().find("map_values", 1)].body();
        assertEquals(Bound.NodeKind.CALL_FUNCTION, mapValues.kind);
    }

    @Test
    void everyConstructBindsItsChildren() {
        assertEquals(
            "(as . $0 (array (, (object (\"a\" $0.0) (\"b\" _) ($0.0 1)) (str @base64 \"x\" $0.0 \"\") (slice (index . $0.0) $0.0 _) (if $0.0 (try $0.0 $0.0) (// (or (and $0.0 $0.0) (neg $0.0)) @csv)) (iter $0.0) (try (index . \"a\")))))",
            bind(". as $x | [{a: $x, b, ($x): 1}, @base64 \"x\\($x)\", .[$x][$x:], if $x then try $x catch $x else $x and $x or -$x // @csv end, $x[], .a?]"));
    }

    @Test
    void preludeFunctionsComeFirstInEveryProgram() {
        Bound.BoundProgram first = Resolver.tryCompile("def f: 1; f").program();
        Bound.BoundProgram second = Resolver.tryCompile("map(.)").program();
        Bound.Function[] prelude = Prelude.instance().functions();
        assertEquals(prelude.length + 1, first.functions().length);
        assertEquals(prelude.length, second.functions().length);
        for (int i = 0; i < prelude.length; i++) {
            assertSame(prelude[i], first.functions()[i]);
            assertSame(prelude[i], second.functions()[i]);
        }
    }

    @Test
    void syntaxErrorsComeFromTheParser() {
        assertEquals("Unexpected end of program @1:4", error("1 +"));
    }

    @Test
    void moduleMetadataAloneIsAccepted() {
        assertEquals("1", bind("module {a: 1}; 1"));
    }
}
