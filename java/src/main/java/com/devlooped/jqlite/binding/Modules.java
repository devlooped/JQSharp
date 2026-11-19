package com.devlooped.jqlite.binding;

import com.devlooped.jqlite.syntax.Ast;
import com.devlooped.jqlite.syntax.Parser;
import com.devlooped.jqlite.syntax.SyntaxError;
import com.devlooped.jqlite.values.JqArray;
import com.devlooped.jqlite.values.JqObject;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;
import com.devlooped.jqlite.values.ValueText;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * The modules group of spec §9.11, which a port may leave out. Everything of it is here, in
 * Resolver.bindImports and Resolver.bindModule (a partial class in the reference), and in
 * builtins/ModuleNatives.java. Java has one public type per file, so the types of Modules.cs are
 * nested here.
 */
public final class Modules {
    private Modules() {
    }

    /** What an import asks for (§9.11): a code module or a data module. */
    public enum ModuleKind {
        CODE,
        DATA,
    }

    /** The outcome of a lookup of {@link IModuleSource}: a value (an identity or a text), or an error message. */
    public record Lookup(String value, String error) {
        public static Lookup found(String value) {
            return new Lookup(value, null);
        }

        public static Lookup failed(String error) {
            return new Lookup(null, error);
        }

        public boolean ok() {
            return value != null;
        }
    }

    /**
     * What the core needs from its host to load modules (§9.11, §12): the host decides where a module
     * comes from. Called while binding, and at run time by {@code modulemeta}, possibly from several
     * threads at once.
     */
    public interface IModuleSource {
        /**
         * Resolves the module {@code path} (as written in the directive) of the given kind, imported
         * with {@code metadata} (the directive's metadata object, or null), from the module
         * {@code from} (its identity, or null for the main program), to the module's identity: the
         * same identity means the same module. On failure, the error is a message naming the path.
         */
        Lookup tryResolve(String path, ModuleKind kind, JqValue metadata, String from);

        /** The source text of a module {@link #tryResolve} found: read once per identity and binding. */
        Lookup tryRead(String identity);
    }

    /**
     * The modules of a bound program, kept for {@code modulemeta}: the host's source and the
     * description of every module loaded while binding. Immutable after binding.
     */
    public static final class ModuleTable {
        private final IModuleSource source;
        private final Map<String, JqValue> descriptions;

        ModuleTable(IModuleSource source, Map<String, JqValue> descriptions) {
            this.source = source;
            this.descriptions = descriptions;
        }

        /**
         * {@code modulemeta} of a path resolved as from the main program: the loaded module's
         * description, or, for a module the program does not import, the description of its parse.
         * The result is the description, or an error whose value is the message string.
         */
        public Result tryDescribe(String path) {
            Lookup identity = source.tryResolve(path, ModuleKind.CODE, null, null);
            if (!identity.ok()) {
                return Result.err(JqValue.fromString(identity.error()));
            }
            JqValue description = descriptions.get(identity.value());
            if (description != null) {
                return description;
            }
            Lookup text = source.tryRead(identity.value());
            if (!text.ok()) {
                return Result.err(JqValue.fromString(text.error()));
            }
            Parser.Parsed parsed = Parser.tryParseModule(text.value());
            if (parsed.program() == null) {
                SyntaxError syntax = parsed.error();
                return Result.err(JqValue.fromString(BindingMessages.inModule(syntax.message(), identity.value(), syntax.line(), syntax.column())));
            }

            return describe(parsed.program());
        }

        /**
         * What {@code modulemeta} outputs for a module (§9.11): its metadata keys, then {@code deps}
         * (per directive: its metadata keys, then {@code as} unless it is an include,
         * {@code is_data}, {@code relpath}), then {@code defs}, each top-level {@code name/arity}
         * once, by first definition.
         */
        public static JqValue describe(Ast.Program module) {
            JqObject description = module.metadata() != null ? module.metadata().asObject() : JqObject.EMPTY;
            JqValue[] deps = new JqValue[module.imports().size()];
            for (int i = 0; i < deps.length; i++) {
                Ast.Import directive = module.imports().get(i);
                JqObject dep = directive.metadata() != null ? directive.metadata().asObject() : JqObject.EMPTY;
                if (directive.alias() != null) {
                    dep = dep.set("as", JqValue.fromString(directive.alias()));
                }
                deps[i] = dep
                    .set("is_data", JqValue.fromBool(directive.isData()))
                    .set("relpath", JqValue.fromString(directive.path()));
            }

            List<JqValue> defs = new ArrayList<>();
            HashSet<String> seen = new HashSet<>();
            for (Ast.Definition definition : module.definitions()) {
                String signature = definition.name() + "/" + definition.params().size();
                if (seen.add(signature)) {
                    defs.add(JqValue.fromString(signature));
                }
            }

            return description
                .set("deps", JqArray.fromOwned(deps))
                .set("defs", JqArray.fromOwned(defs.toArray(new JqValue[0])));
        }
    }

    /**
     * Loads the modules of one binding: resolves and parses each identity once, depth first from the
     * main program's directives, with an explicit stack (an import chain of any length is no
     * recursion), detecting cycles. Code modules are then bound one after the other, each after the
     * modules it imports, each in a scope of its own (Resolver). Loaded modules live as long as the
     * binding: a host that wants to share parses between programs caches in its source.
     */
    static final class ModuleLoader {
        /** A loaded module. */
        static final class Module {
            final String identity;
            final ModuleKind kind;

            /** A code module's parse. */
            Ast.Program program;

            /** A data module's values, as one array. */
            JqValue data;

            /** The identities the module's directives resolved to, in order. */
            final List<String> imports = new ArrayList<>();

            /** A code module's exports once bound: each top-level name/arity with its function, the last definition winning. */
            final LinkedHashMap<String, Export> exports = new LinkedHashMap<>();

            // On the loading stack: importing it again is a cycle.
            boolean loading;

            Module(String identity, ModuleKind kind) {
                this.identity = identity;
                this.kind = kind;
            }
        }

        record Export(String name, int arity, int function) {
        }

        /** What {@link #tryLoad} found: the identities of the directives, the code modules to bind in order, or the error. */
        record Loaded(List<String> identities, List<Module> order, SyntaxError error) {
        }

        private record Frame(Module module, List<Ast.Import> imports, List<String> identities) {
        }

        private final IModuleSource source;
        private final HashMap<String, Module> modules = new HashMap<>();

        ModuleLoader(IModuleSource source) {
            this.source = source;
        }

        IModuleSource source() {
            return source;
        }

        Module get(String identity) {
            return modules.get(identity);
        }

        /**
         * Loads what {@code imports} (the main program's directives) need: the identities they
         * resolved to, and the code modules to bind, each after those it imports.
         */
        Loaded tryLoad(List<Ast.Import> imports) {
            List<Module> order = new ArrayList<>();
            List<String> identities = new ArrayList<>();
            // One frame per module being loaded: its directives, the next one to resolve, where identities go.
            ArrayDeque<Frame> frames = new ArrayDeque<>();
            ArrayDeque<Integer> next = new ArrayDeque<>();
            frames.push(new Frame(null, imports, identities));
            next.push(0);
            while (!frames.isEmpty()) {
                Frame frame = frames.peek();
                int i = next.pop();
                if (i == frame.imports.size()) {
                    frames.pop();
                    if (frame.module != null) {
                        frame.module.loading = false;
                        order.add(frame.module);
                    }

                    continue;
                }

                next.push(i + 1);
                Ast.Import directive = frame.imports.get(i);
                ModuleKind kind = directive.isData() ? ModuleKind.DATA : ModuleKind.CODE;
                String from = frame.module == null ? null : frame.module.identity;
                Lookup resolved = tryResolve(directive, kind, from);
                if (!resolved.ok()) {
                    return failed(error(resolved.error(), directive.position(), from));
                }

                String identity = resolved.value();
                frame.identities.add(identity);
                Module loaded = modules.get(identity);
                if (loaded != null) {
                    if (loaded.loading) {
                        return failed(error(BindingMessages.circularImport(identity), directive.position(), from));
                    }

                    continue;
                }

                Lookup text = source.tryRead(identity);
                if (!text.ok()) {
                    return failed(error(text.error(), directive.position(), from));
                }

                if (kind == ModuleKind.DATA) {
                    Result values = ValueText.tryParseSequence(text.value());
                    if (values instanceof Result.Err invalid) {
                        return failed(error(BindingMessages.invalidData(identity, invalid.error().asString()), directive.position(), from));
                    }

                    Module data = new Module(identity, kind);
                    data.data = (JqValue) values;
                    modules.put(identity, data);
                    continue;
                }

                Parser.Parsed parsed = Parser.tryParseModule(text.value());
                if (parsed.program() == null) {
                    SyntaxError syntax = parsed.error();
                    return failed(new SyntaxError(syntax.message(), syntax.line(), syntax.column(), identity));
                }

                Module code = new Module(identity, kind);
                code.program = parsed.program();
                code.loading = true;
                modules.put(identity, code);
                frames.push(new Frame(code, parsed.program().imports(), code.imports));
                next.push(0);
            }

            return new Loaded(identities, order, null);
        }

        private static Loaded failed(SyntaxError error) {
            return new Loaded(null, null, error);
        }

        /** The descriptions of the code modules loaded, for {@code modulemeta}; null without a source. */
        ModuleTable table() {
            if (source == null) {
                return null;
            }
            HashMap<String, JqValue> descriptions = new HashMap<>();
            for (Module module : modules.values()) {
                if (module.program != null) {
                    descriptions.put(module.identity, ModuleTable.describe(module.program));
                }
            }

            return new ModuleTable(source, descriptions);
        }

        // Without a source no module exists.
        private Lookup tryResolve(Ast.Import directive, ModuleKind kind, String from) {
            if (source != null) {
                return source.tryResolve(directive.path(), kind, directive.metadata(), from);
            }
            return Lookup.failed(BindingMessages.moduleNotFound(directive.path()));
        }

        private static SyntaxError error(String message, Ast.Position position, String module) {
            return new SyntaxError(message, position.line(), position.column(), module);
        }
    }
}
