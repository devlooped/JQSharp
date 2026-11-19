package com.devlooped.jqlite.binding;

import com.devlooped.jqlite.binding.Bound.Function;
import com.devlooped.jqlite.syntax.Parser;
import com.devlooped.jqlite.syntax.SyntaxError;
import java.io.IOException;
import java.io.InputStream;
import java.io.UncheckedIOException;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.List;

/**
 * {@code spec/prelude.jq}, parsed and bound once against the natives, then shared by every program
 * (immutable, so safe to use from any thread). A program's function table starts with
 * {@link #functions}, so prelude calls keep their indices in every program.
 *
 * <p>Host-specific: reading the prelude, a resource of the jar (Maven copies the shared
 * {@code spec/prelude.jq} next to this class), with {@link Class#getResourceAsStream}.
 */
public final class Prelude {
    /** The name of the resource that holds {@code spec/prelude.jq}, next to this class (see pom.xml). */
    public static final String RESOURCE_NAME = "prelude.jq";

    private static final Prelude INSTANCE = load(readSource(), true);

    private static final class WithoutOverridesHolder {
        static final Prelude VALUE = load(readSource(), false);
    }

    private final HashMap<String, Integer> visible = new HashMap<>();
    private final Function[] functions;
    private final List<Function> definitions;

    private Prelude(Function[] functions, int[] exports) {
        this.functions = functions;
        Function[] tops = new Function[exports.length];
        for (int i = 0; i < exports.length; i++) {
            tops[i] = functions[exports[i]];
            visible.put(key(tops[i].name(), tops[i].arity()), exports[i]);
        }
        this.definitions = List.of(tops);
    }

    /** The prelude, with the native overrides of its builtins (Native.overridesPrelude). */
    public static Prelude instance() {
        return INSTANCE;
    }

    /** The prelude exactly as written, every builtin bound to its definition: to check that the overrides change nothing. */
    public static Prelude withoutOverrides() {
        return WithoutOverridesHolder.VALUE;
    }

    /** Every function of the prelude, nested ones included, in the order of definition. Callers must not write the array. */
    public Function[] functions() {
        return functions;
    }

    /** The top-level definitions: the builtins the prelude defines. */
    public List<Function> definitions() {
        return definitions;
    }

    /** The index in {@link #functions} of the builtin {@code name}/{@code arity}, or -1. */
    public int find(String name, int arity) {
        Integer index = visible.get(key(name, arity));
        return index == null ? -1 : index;
    }

    private static String key(String name, int arity) {
        return name + "/" + arity;
    }

    /** Parses and binds a prelude source. The embedded prelude must bind: a failure is a defect of the build, not of a program. */
    public static Prelude load(String source, boolean nativeOverrides) {
        Parser.Parsed parsed = Parser.tryParseModule(source);
        SyntaxError error = parsed.error();
        Resolver.PreludeBinding bound = null;
        if (parsed.program() != null) {
            bound = Resolver.tryBindPrelude(parsed.program(), nativeOverrides);
            error = bound.error();
        }
        if (error != null) {
            throw new IllegalStateException("The prelude does not bind: " + error.message() + " at " + error.line() + ":" + error.column());
        }
        return new Prelude(bound.functions(), bound.exports());
    }

    /** The text of the embedded prelude. */
    public static String readSource() {
        try (InputStream stream = Prelude.class.getResourceAsStream(RESOURCE_NAME)) {
            if (stream == null) {
                throw new IllegalStateException("Resource " + RESOURCE_NAME + " not found");
            }
            return new String(stream.readAllBytes(), StandardCharsets.UTF_8);
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
    }
}
