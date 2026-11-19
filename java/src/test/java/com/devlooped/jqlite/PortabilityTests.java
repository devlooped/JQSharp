package com.devlooped.jqlite;

import static org.junit.jupiter.api.Assertions.assertTrue;

import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.regex.Pattern;
import java.util.stream.Stream;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

/**
 * The portability rules of AGENTS.md as tests on the Java core (the packages values, syntax,
 * binding, runtime and builtins): a host feature in its code fails here, naming the file and the
 * line. Comments and string literals are not code and are not matched. Where a rule has exceptions,
 * they are listed by file with the reason: a new use anywhere else must be justified by adding it here.
 */
class PortabilityTests {
    private static final Path CORE = JqTestParser.specTests().getParent().getParent().resolve("java/src/main/java/com/devlooped/jqlite");
    private static final List<String> LAYERS = List.of("values", "syntax", "binding", "runtime", "builtins");

    private record Rule(String name, String pattern, Map<String, String> allowed) {
    }

    // What every core file may import from the JDK: plain collections and functional interfaces.
    private static final Pattern COMMON_IMPORTS = Pattern.compile(
        "java\\.util\\.(ArrayDeque|ArrayList|Arrays|Collections|HashMap|HashSet|LinkedHashMap|List|Map)|java\\.util\\.function\\.\\w+");

    // The other JDK imports, by file, with the reason.
    private static final Map<String, Map<String, String>> FILE_IMPORTS = Map.of(
        "values/JqArray.java", Map.of("java\\.util\\.concurrent\\.atomic\\.AtomicInteger", "the claim of a buffer's next slots (compare-and-swap)"),
        "values/JqObject.java", Map.of("java\\.util\\.concurrent\\.atomic\\.AtomicInteger", "the claim of a buffer's next slot (compare-and-swap)"),
        "builtins/RegexEngine.java", Map.of("java\\.util\\.concurrent\\.ConcurrentHashMap", "the cache of compiled patterns, shared by threads"),
        "runtime/StackGuard.java", Map.of("java\\.util\\.concurrent\\.[\\w.]+", "the pool of large-stack threads"),
        "binding/Prelude.java", Map.of("java\\.io\\.\\w+|java\\.nio\\.charset\\.StandardCharsets", "reads the embedded prelude"));

    private static final List<Rule> RULES = List.of(
        // A JDK name written in full in the code bypasses the imports.
        new Rule("qualified JDK names", "^(?!import )\\s*.*\\bjava\\.(?!util\\.(ArrayDeque|ArrayList|Arrays|Collections|HashMap|HashSet|LinkedHashMap|List|Map|function)\\b)\\w+", Map.of()),
        new Rule("JSON libraries", "\\b(javax\\.json|jakarta\\.json|com\\.fasterxml|org\\.json|com\\.google\\.gson)\\b", Map.of()),
        new Rule("streams and iterators", "\\.stream\\(|\\bArrays\\.stream\\b|\\b(Int|Long|Double)?Stream\\b|\\.chars\\(\\)|\\.codePoints\\(\\)|\\.lines\\(\\)|\\bIterator\\b|\\bIterable\\b|\\bSpliterator\\b", Map.of()),
        new Rule("reflection", "\\bgetResource(AsStream)?\\b|\\bClass\\.forName\\b|\\bgetDeclared\\w*\\b|\\.getClass\\(\\)|\\b\\w+\\.class\\b", Map.of(
            "binding/Prelude.java", "reads the embedded prelude: Prelude.class.getResourceAsStream")),
        new Rule("regex", "\\bPattern\\.compile\\b|\\bMatcher\\b|\\.(matches|replaceAll|replaceFirst)\\(", Map.of()),
        new Rule("dates and clocks", "\\b(Instant|LocalDate\\w*|ZonedDateTime|ZoneId|ZoneOffset|ZoneRules|Date|TimeZone|Clock)\\b|\\bSystem\\.(currentTimeMillis|nanoTime)\\b", Map.of()),
        new Rule("culture", "\\bLocale\\b|\\bString\\.format\\b|\\.formatted\\(|\\.to(Upper|Lower)Case\\(|\\bCharacter\\.(isDigit|isLetter\\w*|isWhitespace|isSpaceChar|isAlphabetic|getNumericValue|to(Upper|Lower)Case)\\b", Map.of()),
        new Rule("process", "\\bSystem\\.(getenv|getProperty|exit|out|err|in)\\b|\\bRuntime\\.getRuntime\\b", Map.of()),
        new Rule("threads", "\\bThread\\b|\\bThreadLocal\\b|\\bExecutor\\w*|\\bFuture\\b", Map.of(
            "runtime/StackGuard.java", "the host stack: a step continues on a thread with a stack of its own",
            "syntax/Parser.java", "the host stack: deep nesting continues on a thread with a stack of its own (frames of the JVM)")),
        new Rule("synchronization", "\\bsynchronized\\b|\\bvolatile\\b|\\bAtomic\\w+|\\bConcurrent\\w+", Map.of(
            "values/JqArray.java", "the claim of a buffer's next slots (compare-and-swap)",
            "values/JqObject.java", "the claim of a slot, the shared key index (a lock) and the sorted keys (a benign race)",
            "binding/Bound.java", "the flag of a program whose evaluations start on a fresh stack (a hint)",
            "builtins/RegexEngine.java", "the cache of compiled patterns, shared by threads",
            "runtime/StackGuard.java", "the numbering of the pool's threads")),
        new Rule("throw", "\\bthrow\\b", Map.of(
            "binding/Prelude.java", "the embedded prelude does not bind or is missing: a build defect",
            "builtins/NativeTable.java", "start-up checks of the registrations against the catalogue",
            "values/JqArray.java", "index guards of the host caller",
            "values/JqArrayBuilder.java", "index guard, and use after toArray(): programming errors",
            "values/JqObject.java", "index guards of the host caller",
            "values/JqObjectBuilder.java", "use after toObject(): a programming error",
            "values/JqValue.java", "the as* accessors on a value of another kind: a programming error",
            "runtime/StackGuard.java", "carries a defect of a step from the fresh stack back to the caller's thread",
            "syntax/Parser.java", "carries a defect of a step from the fresh stack back to the caller's thread")),
        new Rule("catch", "\\bcatch\\b", Map.of(
            "binding/Prelude.java", "reading the embedded resource",
            "runtime/StackGuard.java", "carries a defect of a step from the fresh stack back to the caller's thread",
            "syntax/Parser.java", "carries a defect of a step from the fresh stack back to the caller's thread")));

    @Test
    void coreImportsOnlyWhatItsFileIsAllowed() {
        Pattern imports = Pattern.compile("^import (static )?(java[\\w.]*|javax[\\w.]*|jdk[\\w.]*|sun[\\w.]*)\\s*;", Pattern.MULTILINE);
        List<String> violations = new ArrayList<>();
        for (String file : coreFiles()) {
            var matcher = imports.matcher(read(file));
            while (matcher.find()) {
                String name = matcher.group(2);
                boolean allowed = COMMON_IMPORTS.matcher(name).matches()
                    || FILE_IMPORTS.getOrDefault(file, Map.of()).keySet().stream().anyMatch(p -> name.matches(p));
                if (!allowed) {
                    violations.add(file + ": " + name);
                }
            }
        }
        assertTrue(violations.isEmpty(), "JDK imports outside the allow-list:\n" + String.join("\n", violations));
    }

    @Test
    void importAllowancesAreStillUsed() {
        for (Map.Entry<String, Map<String, String>> entry : FILE_IMPORTS.entrySet()) {
            for (String pattern : entry.getValue().keySet()) {
                assertTrue(!matches(entry.getKey(), "^import (static )?(" + pattern + ")\\s*;").isEmpty(),
                    entry.getKey() + " no longer imports " + pattern + ": remove it from the allowed imports.");
            }
        }
    }

    static Stream<String> ruleNames() {
        return RULES.stream().map(Rule::name);
    }

    @ParameterizedTest
    @MethodSource("ruleNames")
    void coreDoesNotUse(String name) {
        Rule rule = RULES.stream().filter(r -> r.name.equals(name)).findFirst().orElseThrow();
        List<String> violations = new ArrayList<>();
        for (String file : coreFiles()) {
            if (rule.allowed.containsKey(file)) {
                continue;
            }
            for (String line : matches(file, rule.pattern)) {
                violations.add(file + ":" + line);
            }
        }
        assertTrue(violations.isEmpty(), name + " is not allowed in the core outside " + rule.allowed.keySet() + ":\n" + String.join("\n", violations));
    }

    @Test
    void coreAllowancesAreStillUsed() {
        // An allowance nothing uses any more is removed, so the lists stay the truth.
        for (Rule rule : RULES) {
            for (String file : rule.allowed.keySet()) {
                assertTrue(!matches(file, rule.pattern).isEmpty(), file + " no longer uses " + rule.name + ": remove it from the allowed files.");
            }
        }
    }

    @Test
    void layersUseOnlyTheLayersBeforeThem() {
        // values < syntax < binding < runtime < builtins; binding uses the native catalogue
        // (builtins/Native, builtins/Natives) and the interpreter uses NativeTable and Formats.
        Map<String, List<String>> allowed = Map.of(
            "values", List.of(),
            "syntax", List.of("values"),
            "binding", List.of("values", "syntax", "builtins.Native", "builtins.Natives"),
            "runtime", List.of("values", "syntax", "binding", "builtins.Native", "builtins.NativeTable", "builtins.Formats"),
            "builtins", List.of("values", "syntax", "binding", "runtime"));
        List<String> violations = new ArrayList<>();
        Pattern imports = Pattern.compile("^import (static )?com\\.devlooped\\.jqlite\\.(\\w+)(\\.\\w+)?", Pattern.MULTILINE);
        for (String file : coreFiles()) {
            String layer = file.substring(0, file.indexOf('/'));
            var matcher = imports.matcher(read(file));
            while (matcher.find()) {
                String target = matcher.group(2);
                String type = matcher.group(3) == null ? "" : target + matcher.group(3);
                if (!target.equals(layer) && !allowed.get(layer).contains(target) && !allowed.get(layer).contains(type)) {
                    violations.add(file + ": " + matcher.group());
                }
            }
        }
        assertTrue(violations.isEmpty(), String.join("\n", violations));
    }

    private static List<String> coreFiles() {
        List<String> files = new ArrayList<>();
        for (String layer : LAYERS) {
            try (Stream<Path> list = Files.list(CORE.resolve(layer))) {
                list.filter(f -> f.toString().endsWith(".java")).forEach(f -> files.add(layer + "/" + f.getFileName()));
            } catch (IOException e) {
                throw new UncheckedIOException(e);
            }
        }
        return files;
    }

    private static String read(String file) {
        try {
            return Files.readString(CORE.resolve(file), StandardCharsets.UTF_8);
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
    }

    // The lines of code (comments and literals blanked) that match, as "line: text".
    private static List<String> matches(String file, String pattern) {
        String[] lines = code(read(file)).split("\n", -1);
        Pattern regex = Pattern.compile(pattern);
        List<String> found = new ArrayList<>();
        for (int i = 0; i < lines.length; i++) {
            if (regex.matcher(lines[i]).find()) {
                found.add((i + 1) + ": " + lines[i].strip());
            }
        }
        return found;
    }

    // The source with comments, string literals (text blocks included) and char literals blanked,
    // keeping the line breaks.
    private static String code(String source) {
        StringBuilder out = new StringBuilder(source.length());
        int i = 0;
        while (i < source.length()) {
            char c = source.charAt(i);
            if (source.startsWith("//", i)) {
                while (i < source.length() && source.charAt(i) != '\n') {
                    i++;
                }
            } else if (source.startsWith("/*", i)) {
                int end = source.indexOf("*/", i + 2);
                end = end < 0 ? source.length() : end + 2;
                out.append(source.substring(i, end).replaceAll("[^\n]", " "));
                i = end;
            } else if (source.startsWith("\"\"\"", i)) {
                int end = source.indexOf("\"\"\"", i + 3);
                end = end < 0 ? source.length() : end + 3;
                out.append(source.substring(i, end).replaceAll("[^\n]", " "));
                i = end;
            } else if (c == '"' || c == '\'') {
                int end = i + 1;
                while (end < source.length() && source.charAt(end) != c) {
                    end += source.charAt(end) == '\\' ? 2 : 1;
                }
                out.append(c).append(c);
                i = end + 1;
            } else {
                out.append(c);
                i++;
            }
        }
        return out.toString();
    }
}