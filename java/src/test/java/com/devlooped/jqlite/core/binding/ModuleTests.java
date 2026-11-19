package com.devlooped.jqlite.core.binding;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.devlooped.jqlite.DirectoryModules;
import com.devlooped.jqlite.JqTestParser;
import com.devlooped.jqlite.MemoryModules;
import com.devlooped.jqlite.binding.Modules.IModuleSource;
import com.devlooped.jqlite.binding.Resolver;
import com.devlooped.jqlite.core.runtime.Evaluate;
import com.devlooped.jqlite.runtime.Evaluation.EvaluationOptions;
import com.devlooped.jqlite.syntax.Parser;
import com.devlooped.jqlite.values.JqValue;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

/** The modules group (spec §9.11) beyond the corpus: errors and where they are, loading once, limits, modulemeta. */
class ModuleTests {
    private static final DirectoryModules CORPUS = new DirectoryModules(JqTestParser.specTests().resolve("modules"));

    private static String error(String program, IModuleSource modules) {
        Resolver.Compiled compiled = Resolver.tryCompile(program, true, modules);
        assertNull(compiled.program());
        return compiled.error().message() + " @" + (compiled.error().module() == null ? "main" : compiled.error().module()) + ":" + compiled.error().line() + ":" + compiled.error().column();
    }

    private static String run(String program, IModuleSource modules) {
        Resolver.Compiled compiled = Resolver.tryCompile(program, true, modules);
        assertNotNull(compiled.program(), String.valueOf(compiled.error()));
        return String.join(", ", Evaluate.run(compiled.program(), JqValue.NULL));
    }

    static String[][] errors() {
        return new String[][] {
            {"import \"double\" as d; import \"nope\" as n; 1", "module not found: nope @main:1:23"},
            {"import \"broken\" as b; 1", "Expected ')' but found ';' @broken.jq:1:15"},
            {"import \"usesmain\" as u; 1", "mainf/0 is not defined @usesmain.jq:1:15"},
            {"import \"cycle1\" as c; 1", "circular import of cycle1.jq @cycle2.jq:1:1"},
            {"import \"self\" as s; 1", "circular import of self.jq @self.jq:1:1"},
            {"include \"config\"; 1", "module not found: config @main:1:1"},
            {"import \"a\" as a; a::zz", "a::zz/0 is not defined @main:1:18"},
            {"import \"values\" as $d; $d::x", "$d::x is not defined @main:1:24"},
        };
    }

    @ParameterizedTest
    @MethodSource("errors")
    void bindingErrorsSayWhereTheyAre(String program, String expected) {
        assertEquals(expected, error(program, CORPUS));
    }

    @Test
    void badDataIsABindingError() {
        assertTrue(error("import \"bad\" as $b; $b", CORPUS).startsWith("error loading data file bad.json: Unfinished JSON term at EOF"));
    }

    @Test
    void withoutASourceNoModuleExists() {
        assertEquals("module not found: a @main:1:1", error("import \"a\" as a; 1", null));
        assertEquals("\"module not found: a\"", String.join(", ", Evaluate.run("try (\"a\" | modulemeta) catch .")));
    }

    @Test
    void eachModuleIsReadOnce() {
        DirectoryModules modules = new DirectoryModules(JqTestParser.specTests().resolve("modules"));
        assertEquals("2", run("import \"one\" as o; import \"one\" as p; include \"one\"; o::k + p::k", modules));
        assertEquals(1, modules.reads());
    }

    @Test
    void aLongImportChainIsNoRecursion() throws InterruptedException {
        // m0 imports m1, …, m999 imports nothing; bound on a 1 MB stack.
        Map<String, String> sources = new HashMap<>();
        for (int i = 0; i < 1000; i++) {
            sources.put("m" + i + ".jq", i == 999 ? "def f: 999;" : "import \"m" + (i + 1) + "\" as n; def f: n::f; def g: " + i + ";");
        }
        MemoryModules modules = new MemoryModules(sources);
        String[] result = new String[1];
        Thread thread = new Thread(null, () -> result[0] = run("import \"m0\" as m; m::f, m::g", modules), "chain", 1024 * 1024);
        thread.start();
        thread.join();
        assertEquals("999, 0", result[0]);
    }

    @Test
    void modulemetaUsesWhatTheProgramLoaded() {
        Map<String, String> sources = new HashMap<>();
        sources.put("x.jq", "def a: 1;");
        MemoryModules modules = new MemoryModules(sources);
        Resolver.Compiled compiled = Resolver.tryCompile("import \"x\" as x; [\"x\", \"y\"][] | modulemeta | .defs", true, modules);
        sources.put("x.jq", "def changed: 1;");
        sources.put("y.jq", "def b: 1; def c: 2;");
        // x as the program loaded it; y, which it does not import, from its source now.
        assertEquals(List.of("[\"a/0\"]", "[\"b/0\",\"c/0\"]"), Evaluate.run(compiled.program(), JqValue.NULL));
    }

    @Test
    void modulesShareTheHostVariables() {
        MemoryModules modules = new MemoryModules(Map.of("v.jq", "def v: $x;"));
        Resolver.Compiled compiled = Resolver.tryCompile("import \"v\" as v; v::v, $x", true, modules);
        EvaluationOptions options = new EvaluationOptions();
        options.variables = Map.of("x", JqValue.fromNumber(7));
        assertEquals(List.of("7", "7"), Evaluate.run(compiled.program(), JqValue.NULL, options));
    }

    @Test
    void qualifiedAliasesNest() {
        MemoryModules modules = new MemoryModules(Map.of("a.jq", "def f: 1;"));
        assertEquals("1", run("import \"a\" as x::y; x::y::f", modules));
    }

    @Test
    void aModuleObeysTheNestingLimitOfItsFile() {
        String deep = "[".repeat(Parser.MAX_NESTING + 1) + "]".repeat(Parser.MAX_NESTING + 1);
        MemoryModules modules = new MemoryModules(Map.of("deep.jq", "def f: " + deep + ";"));
        // def opens the first level, so the 1000th bracket is one too many.
        assertEquals("Program too deeply nested @deep.jq:1:1007", error("import \"deep\" as d; 1", modules));
    }
}
