package com.devlooped.jqlite.core.binding;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.devlooped.jqlite.JqTestParser;
import com.devlooped.jqlite.binding.Bound;
import com.devlooped.jqlite.binding.Prelude;
import com.devlooped.jqlite.builtins.Native;
import com.devlooped.jqlite.builtins.Natives;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.TreeSet;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.stream.Collectors;
import org.junit.jupiter.api.Test;

/**
 * The native catalogue and the embedded prelude against spec §9: the prelude binds completely, and
 * together they are exactly the builtins the spec names, natives where the spec says N and prelude
 * definitions where it says P. The lists are read from the spec, so spec and code cannot drift.
 */
class BuiltinCatalogueTests {
    private static final Path SPEC = JqTestParser.specTests().getParent();

    @Test
    void theEmbeddedPreludeIsSpecPreludeJq() throws IOException {
        assertEquals(Files.readString(SPEC.resolve("prelude.jq"), StandardCharsets.UTF_8), Prelude.readSource());
    }

    @Test
    void thePreludeBindsAgainstTheNatives() {
        Prelude prelude = Prelude.instance();
        assertEquals(102, prelude.definitions().size());
        // Nested definitions (recurse's r, walk's w) are functions of the prelude too.
        assertTrue(prelude.functions().length > prelude.definitions().size());
        assertEquals(prelude.definitions().size(), prelude.definitions().stream().map(BuiltinCatalogueTests::signature).distinct().count());
    }

    @Test
    void aPreludeCannotUseHostVariables() {
        IllegalStateException error = assertThrows(IllegalStateException.class, () -> Prelude.load("def f: $x;", true));
        assertTrue(error.getMessage().contains("$x is not defined at 1:8"), error.getMessage());
    }

    @Test
    void theCatalogueHasUniqueSignaturesAndIndices() {
        assertEquals(Natives.ALL.size(), Natives.ALL.stream().map(BuiltinCatalogueTests::signature).distinct().count());
        for (int i = 0; i < Natives.ALL.size(); i++) {
            assertEquals(i, Natives.ALL.get(i).index());
            assertSame(Natives.ALL.get(i), Natives.find(Natives.ALL.get(i).name(), Natives.ALL.get(i).arity()));
        }

        assertNull(Natives.find("map", 1));
    }

    @Test
    void nativesAndPreludeAreTheBuiltinsOfTheSpec() throws IOException {
        SpecLists spec = specBuiltins();
        Set<String> natives = Natives.ALL.stream().filter(n -> !n.overridesPrelude()).map(BuiltinCatalogueTests::signature).collect(Collectors.toSet());
        Set<String> prelude = Prelude.instance().definitions().stream().map(BuiltinCatalogueTests::signature).collect(Collectors.toSet());
        // An override implements a prelude builtin natively: it stays a prelude builtin of §9.
        assertTrue(prelude.containsAll(Natives.ALL.stream().filter(Native::overridesPrelude).map(BuiltinCatalogueTests::signature).toList()));

        Set<String> both = new HashSet<>(natives);
        both.retainAll(prelude);
        assertTrue(both.isEmpty(), both.toString());
        Set<String> union = new TreeSet<>(natives);
        union.addAll(prelude);
        assertEquals(new TreeSet<>(spec.all), union);
        assertTrue(spec.natives.containsAll(natives));
        assertTrue(spec.prelude.containsAll(prelude));
        assertEquals(new TreeSet<>(spec.modules), Natives.ALL.stream().filter(n -> n.group() == Native.NativeGroup.MODULES)
            .map(BuiltinCatalogueTests::signature).collect(Collectors.toCollection(TreeSet::new)));
    }

    @Test
    void internalBuiltinsStartWithAnUnderscore() {
        assertEquals(List.of("_group_by_impl/1", "_max_by_impl/1", "_min_by_impl/1", "_sort_by_impl/1", "_strindices/1"),
            Natives.ALL.stream().filter(n -> n.isInternal() && !n.overridesPrelude()).map(BuiltinCatalogueTests::signature).sorted().toList());
        assertEquals(List.of("_assign/2", "_flatten/1", "_modify/2"),
            Prelude.instance().definitions().stream().filter(f -> f.name().startsWith("_")).map(BuiltinCatalogueTests::signature).sorted().toList());
    }

    private static String signature(Native builtin) {
        return builtin.name() + "/" + builtin.arity();
    }

    private static String signature(Bound.Function function) {
        return function.name() + "/" + function.arity();
    }

    private record SpecLists(Set<String> all, Set<String> natives, Set<String> prelude, Set<String> modules) {
    }

    private static final Pattern SIGNATURES = Pattern.compile("`([A-Za-z_][A-Za-z0-9_]*/[0-9]+)`");
    // The whole paragraph (group 1) and its list of one-input names (group 2).
    private static final Pattern LIBRARY_LIST = Pattern.compile("^\\*\\*Library functions\\*\\*(.*?`([a-z0-9 ]+)` \\(one input\\).*)$", Pattern.MULTILINE);
    private static final Pattern FIRST_CELLS = Pattern.compile("^\\| ([^|\\n]*) \\|", Pattern.MULTILINE);
    // One or more signatures at the start of a line, separated by ", ".
    private static final Pattern HEADINGS = Pattern.compile("^`[A-Za-z_][A-Za-z0-9_]*/[0-9]+`(, `[A-Za-z_][A-Za-z0-9_]*/[0-9]+`)*", Pattern.MULTILINE);
    // The first sentence of §9.11, which names the members of the optional group.
    private static final Pattern MODULE_GROUP = Pattern.compile("A port MAY leave out the whole group:[^.]*\\.");
    private static final Pattern FORMULAS = Pattern.compile("^\\s*([a-z][a-z0-9]*)\\(x\\):", Pattern.MULTILINE);
    private static final Pattern KIND_ROWS = Pattern.compile("^\\| ([^|]*) \\| ([NP]) \\|", Pattern.MULTILINE);

    // Section 9 of spec/jqlite.md, read only where it declares builtins (see the reference's test).
    private static SpecLists specBuiltins() throws IOException {
        String text = Files.readString(SPEC.resolve("jqlite.md"), StandardCharsets.UTF_8).replace("\r\n", "\n");
        String section = section(text, "## 9. ", "## 10. ");
        String math = section(section, "### 9.8 ", "### 9.9 ");
        Set<String> mathDeclared = declarations(math);
        Matcher library = LIBRARY_LIST.matcher(math);
        assertTrue(library.find(), "The library functions of §9.8 were not found");
        for (String name : library.group(2).split(" ")) {
            mathDeclared.add(name + "/0");
        }
        Matcher formula = FORMULAS.matcher(math);
        while (formula.find()) {
            mathDeclared.add(formula.group(1) + "/0");
        }

        Matcher group = MODULE_GROUP.matcher(section(section, "### 9.11 ", "### 9.12 "));
        assertTrue(group.find(), "The group sentence of §9.11 was not found");
        Set<String> modules = signatures(group.group());

        Set<String> all = declarations(section);
        all.addAll(mathDeclared);
        all.addAll(modules);

        Set<String> natives = new HashSet<>(modules);
        Set<String> prelude = new HashSet<>();
        Matcher row = KIND_ROWS.matcher(section);
        while (row.find()) {
            (row.group(2).equals("N") ? natives : prelude).addAll(signatures(row.group(1)));
        }

        for (String signature : mathDeclared) {
            (signature.equals("abs/0") ? prelude : natives).add(signature);
        }
        return new SpecLists(all, natives, prelude, modules);
    }

    private static Set<String> signatures(String text) {
        Set<String> found = new HashSet<>();
        Matcher matcher = SIGNATURES.matcher(text);
        while (matcher.find()) {
            found.add(matcher.group(1));
        }
        return found;
    }

    // The signatures in the first cells of table rows, in the library paragraph, and at the start of lines.
    private static Set<String> declarations(String text) {
        Set<String> declared = new HashSet<>();
        Matcher cell = FIRST_CELLS.matcher(text);
        while (cell.find()) {
            declared.addAll(signatures(cell.group(1)));
        }
        Matcher library = LIBRARY_LIST.matcher(text);
        while (library.find()) {
            declared.addAll(signatures(library.group(1)));
        }
        Matcher heading = HEADINGS.matcher(text);
        while (heading.find()) {
            declared.addAll(signatures(heading.group()));
        }
        return declared;
    }

    private static String section(String text, String start, String end) {
        int from = text.indexOf("\n" + start);
        int to = text.indexOf("\n" + end, from + 1);
        assertTrue(from >= 0 && to > from, "Section '" + start + "' not found in the spec");
        return text.substring(from, to);
    }
}
