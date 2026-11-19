package com.devlooped.jqlite;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.devlooped.jqlite.values.JqValue;
import java.io.IOException;
import java.io.Reader;
import java.io.StringReader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Map;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.stream.IntStream;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/**
 * The public resolvers ({@link JqFileResolver}, {@link JqResourceResolver}, a custom {@link JqResolver})
 * through {@link Jq#parse(String, JqOptions)}. What modules mean is the corpus's (spec/tests/modules.test).
 */
class JqModuleTests {
    @TempDir
    Path directory;

    private void write(String name, String content) throws IOException {
        Path path = directory.resolve(name);
        Files.createDirectories(path.getParent());
        Files.writeString(path, content, StandardCharsets.UTF_8);
    }

    private static List<String> run(String expression, JqResolver resolver, String input) {
        return Jq.parse(expression, JqOptions.defaults().withResolver(resolver)).evaluate(JqJson.parse(input)).stream().map(JqJson::toJson).toList();
    }

    @Test
    void fileResolverAddsTheExtensionOfTheKind() throws IOException {
        write("utils.jq", "def inc: . + 1;");
        write("config.json", "{\"key\":\"value\"} 2");
        assertEquals(List.of("6", "[{\"key\":\"value\"},2]"), run("include \"utils\"; import \"config\" as $c; inc, $c", new JqFileResolver(directory), "5"));
    }

    @Test
    void parseWithAResolverUsesItForModules() throws IOException {
        write("utils.jq", "def inc: . + 1;");
        assertEquals(6, Jq.parse("include \"utils\"; inc", new JqFileResolver(directory)).evaluate(JqValue.fromNumber(5)).get(0).asNumber());
    }

    @Test
    void fileResolverKeepsAnExplicitExtension() throws IOException {
        write("ext.jq", "def ext: . + 100;");
        assertEquals(List.of("105", "105"), run("include \"ext.jq\"; import \"ext.jq\" as e; ext, e::ext", new JqFileResolver(directory), "5"));
    }

    @Test
    void fileResolverResolvesFromTheImportingModuleDirectory() throws IOException {
        write("lib/outer.jq", "include \"inner\"; def outer: inner * 2;");
        write("lib/inner.jq", "def inner: . + 10;");
        assertEquals(List.of("30"), run("include \"lib/outer\"; outer", new JqFileResolver(directory), "5"));
    }

    @Test
    void aMissingModuleIsAParseError() {
        JqParseException error = assertThrows(JqParseException.class, () -> run("include \"nonexistent\"; .", new JqFileResolver(directory), "null"));
        assertEquals("module not found: nonexistent at line 1, column 1", error.getMessage());
        assertEquals(1, error.line());
        assertEquals(1, error.column());
    }

    @Test
    void withoutAResolverNoModuleExists() {
        assertThrows(JqParseException.class, () -> run("import \"something\" as m; .", null, "null"));
        assertThrows(JqParseException.class, () -> run("import \"data\" as $d; $d", null, "null"));
        assertEquals(List.of("\"module not found: x\""), run("try (\"x\" | modulemeta) catch .", null, "null"));
    }

    @Test
    void anErrorInAModuleNamesIt() throws IOException {
        write("bad.jq", "def ok: 1;\ndef broken: (1;");
        JqParseException error = assertThrows(JqParseException.class, () -> run("import \"bad\" as b; 1", new JqFileResolver(directory), "null"));
        assertEquals(2, error.line());
        assertEquals(15, error.column());
        assertTrue(error.getMessage().contains(directory.resolve("bad.jq").toAbsolutePath().normalize().toString()), error.getMessage());
    }

    @Test
    void dataModulesBindAnArrayOfTheirValues() throws IOException {
        write("config.json", "{\"key\":\"value\"}");
        assertEquals(List.of("[{\"key\":\"value\"}]", "\"value\""), run("import \"config\" as $cfg; $cfg::cfg, $cfg[0].key", new JqFileResolver(directory), "null"));
    }

    @Test
    void invalidDataIsAParseError() throws IOException {
        write("bad.json", "not valid json {{{");
        assertThrows(JqParseException.class, () -> run("import \"bad\" as $b; $b", new JqFileResolver(directory), "null"));
    }

    @Test
    void eachModuleIsReadOncePerParse() throws IOException {
        write("counted.jq", "def counted: . + 1;");
        CountingResolver resolver = new CountingResolver(new JqFileResolver(directory));
        assertEquals(List.of("6"), run("include \"counted\"; import \"counted\" as c1; import \"counted\" as c2; c1::counted", resolver, "5"));
        assertEquals(1, resolver.opens.get());
    }

    @Test
    void modulemetaResolvesThroughTheResolverAtRunTime() throws IOException {
        write("meta.jq", "module {\"version\": \"1.0\"}; def double: . * 2;");
        JqExpression expression = Jq.parse("modulemeta", JqOptions.defaults().withResolver(new JqFileResolver(directory)));
        String[] results = new String[16];
        IntStream.range(0, results.length).parallel().forEach(i -> results[i] = JqJson.toJson(expression.evaluate(JqValue.fromString("meta")).get(0)));
        for (String result : results) {
            assertEquals("{\"version\":\"1.0\",\"deps\":[],\"defs\":[\"double/0\"]}", result);
        }
    }

    @Test
    void resourceResolverFindsEmbeddedModules() {
        JqResourceResolver resolver = new JqResourceResolver(JqModuleTests.class.getClassLoader(), "com/devlooped/jqlite/modules");
        assertEquals(List.of("\"hello world!\""), run("include \"greet\"; greet", resolver, "\"world\""));
    }

    @Test
    void resourceResolverReportsMissingModules() {
        JqResourceResolver resolver = new JqResourceResolver(JqModuleTests.class.getClassLoader(), "com/devlooped/jqlite/modules");
        assertThrows(JqParseException.class, () -> run("include \"missing\"; .", resolver, "null"));
    }

    @Test
    void aCustomResolverNamesAndOpensModules() {
        MemoryResolver resolver = new MemoryResolver(Map.of(
            "mem:greet.jq", "include \"punctuation\"; def greet: \"hello \" + . + mark;",
            "mem:punctuation.jq", "def mark: \"!\";"));
        assertEquals(List.of("\"hello world!\""), run("include \"greet\"; greet", resolver, "\"world\""));
    }

    // Counts the modules opened.
    private static final class CountingResolver extends JqResolver {
        private final JqResolver inner;
        final AtomicInteger opens = new AtomicInteger();

        CountingResolver(JqResolver inner) {
            this.inner = inner;
        }

        @Override
        public String getCanonicalPath(String path, JqModuleKind kind, JqValue metadata, String fromPath) throws IOException {
            return inner.getCanonicalPath(path, kind, metadata, fromPath);
        }

        @Override
        public Reader open(String canonicalPath) throws IOException {
            opens.incrementAndGet();
            return inner.open(canonicalPath);
        }
    }

    // Modules in a map, by "mem:" and the path with its extension.
    private static final class MemoryResolver extends JqResolver {
        private final Map<String, String> modules;

        MemoryResolver(Map<String, String> modules) {
            this.modules = modules;
        }

        @Override
        public String getCanonicalPath(String path, JqModuleKind kind, JqValue metadata, String fromPath) {
            String name = "mem:" + path + (kind == JqModuleKind.CODE ? ".jq" : ".json");
            return modules.containsKey(name) ? name : null;
        }

        @Override
        public Reader open(String canonicalPath) {
            return new StringReader(modules.get(canonicalPath));
        }
    }
}
