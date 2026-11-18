using System.Collections.Immutable;
using Devlooped.Core.Syntax;
using Devlooped.Core.Values;
using S = Devlooped.Core.Syntax;

namespace Devlooped.Core.Binding;

// The modules group of spec §9.11, which a port may leave out. Everything of it is here and in
// Builtins/ModuleNatives.cs, behind Resolver.BindImports. A port without the group rejects
// `import`, `include` and `module` in its parser, so a program never has imports: it drops this
// file, the call of BindImports and the Modules property of BoundProgram, and does not list
// modulemeta/0.

/// <summary>What an import asks for (§9.11): a code module or a data module.</summary>
enum ModuleKind
{
    Code,
    Data,
}

/// <summary>
/// What the core needs from its host to load modules (§9.11, §12): the host decides where a module
/// comes from. Called while binding, and at run time by <c>modulemeta</c>, possibly from several
/// threads at once.
/// </summary>
interface IModuleSource
{
    /// <summary>
    /// Resolves the module <paramref name="path"/> (as written in the directive) of the given kind,
    /// imported with <paramref name="metadata"/> (the directive's metadata object, or null), from the
    /// module <paramref name="from"/> (its identity, or null for the main program), to the module's
    /// identity: the same identity means the same module. On failure, <paramref name="error"/> is a
    /// message naming the path.
    /// </summary>
    bool TryResolve(string path, ModuleKind kind, JqValue? metadata, string? from, out string identity, out string error);

    /// <summary>The source text of a module <see cref="TryResolve"/> found: read once per identity and binding.</summary>
    bool TryRead(string identity, out string text, out string error);
}

/// <summary>
/// The modules of a bound program, kept for <c>modulemeta</c>: the host's source and the description
/// of every module loaded while binding. Immutable after binding.
/// </summary>
sealed class ModuleTable(IModuleSource source, IReadOnlyDictionary<string, JqValue> descriptions)
{
    /// <summary>
    /// <c>modulemeta</c> of a path resolved as from the main program: the loaded module's description,
    /// or, for a module the program does not import, the description of its parse.
    /// </summary>
    public bool TryDescribe(string path, out JqValue description, out string error)
    {
        description = default;
        if (!source.TryResolve(path, ModuleKind.Code, null, null, out var identity, out error))
            return false;
        if (descriptions.TryGetValue(identity, out description))
            return true;
        if (!source.TryRead(identity, out var text, out error))
            return false;
        if (!Parser.TryParseModule(text, out var module, out var syntax))
        {
            error = BindingMessages.InModule(syntax.Message, identity, syntax.Line, syntax.Column);
            return false;
        }

        description = Describe(module);
        return true;
    }

    /// <summary>
    /// What <c>modulemeta</c> outputs for a module (§9.11): its metadata keys, then <c>deps</c> (per
    /// directive: its metadata keys, then <c>as</c> unless it is an include, <c>is_data</c>,
    /// <c>relpath</c>), then <c>defs</c>, each top-level <c>name/arity</c> once, by first definition.
    /// </summary>
    public static JqValue Describe(S.Program module)
    {
        var description = module.Metadata is { } metadata ? metadata.AsObject() : JqObject.Empty;
        var deps = new JqValue[module.Imports.Length];
        for (var i = 0; i < deps.Length; i++)
        {
            var import = module.Imports[i];
            var dep = import.Metadata is { } meta ? meta.AsObject() : JqObject.Empty;
            if (import.Alias is not null)
                dep = dep.Set("as", JqValue.FromString(import.Alias));
            deps[i] = JqValue.FromObject(dep
                .Set("is_data", JqValue.FromBool(import.IsData))
                .Set("relpath", JqValue.FromString(import.Path)));
        }

        var defs = new List<JqValue>();
        var seen = new HashSet<string>();
        foreach (var definition in module.Definitions)
        {
            var signature = $"{definition.Name}/{definition.Params.Length}";
            if (seen.Add(signature))
                defs.Add(JqValue.FromString(signature));
        }

        return JqValue.FromObject(description
            .Set("deps", JqValue.FromArray(JqArray.FromOwned(deps)))
            .Set("defs", JqValue.FromArray(JqArray.FromOwned([.. defs]))));
    }
}

/// <summary>
/// Loads the modules of one binding: resolves and parses each identity once, depth first from the
/// main program's directives, with an explicit stack (an import chain of any length is no
/// recursion), detecting cycles. Code modules are then bound one after the other, each after the
/// modules it imports, each in a scope of its own (Resolver). Loaded modules live as long as the
/// binding: a host that wants to share parses between programs caches in its source.
/// </summary>
sealed class ModuleLoader(IModuleSource? source)
{
    /// <summary>A loaded module.</summary>
    internal sealed class Module(string identity, ModuleKind kind)
    {
        public string Identity { get; } = identity;

        public ModuleKind Kind { get; } = kind;

        /// <summary>A code module's parse.</summary>
        public S.Program? Program { get; init; }

        /// <summary>A data module's values, as one array.</summary>
        public JqValue Data { get; init; }

        /// <summary>The identities the module's directives resolved to, in order.</summary>
        public List<string> Imports { get; } = [];

        /// <summary>A code module's exports once bound: each top-level name/arity with its function, the last definition winning.</summary>
        public Dictionary<string, (string Name, int Arity, int Function)> Exports { get; } = [];

        // On the loading stack: importing it again is a cycle.
        public bool Loading;
    }

    readonly Dictionary<string, Module> modules = [];

    public IModuleSource? Source => source;

    public Module this[string identity] => modules[identity];

    /// <summary>
    /// Loads what <paramref name="imports"/> (the main program's directives) need. <paramref name="identities"/>
    /// are what they resolved to; <paramref name="order"/> the code modules to bind, each after those it imports.
    /// </summary>
    public bool TryLoad(ImmutableArray<S.Import> imports, out List<string> identities, out List<Module> order, out SyntaxError? error)
    {
        error = null;
        order = [];
        identities = [];
        // One frame per module being loaded: its directives, the next one to resolve, where identities go.
        var frames = new Stack<(Module? Module, ImmutableArray<S.Import> Imports, List<string> Identities)>();
        var next = new Stack<int>();
        frames.Push((null, imports, identities));
        next.Push(0);
        while (frames.Count > 0)
        {
            var (module, directives, resolved) = frames.Peek();
            var i = next.Pop();
            if (i == directives.Length)
            {
                frames.Pop();
                if (module is not null)
                {
                    module.Loading = false;
                    order.Add(module);
                }

                continue;
            }

            next.Push(i + 1);
            var import = directives[i];
            var kind = import.IsData ? ModuleKind.Data : ModuleKind.Code;
            var from = module?.Identity;
            if (!TryResolve(import, kind, from, out var identity, out var failure))
            {
                error = Error(failure, import.Position, from);
                return false;
            }

            resolved.Add(identity);
            if (modules.TryGetValue(identity, out var loaded))
            {
                if (loaded.Loading)
                {
                    error = Error(BindingMessages.CircularImport(identity), import.Position, from);
                    return false;
                }

                continue;
            }

            if (!source!.TryRead(identity, out var text, out failure))
            {
                error = Error(failure, import.Position, from);
                return false;
            }

            if (kind == ModuleKind.Data)
            {
                if (!ValueText.TryParseSequence(text, out var values, out var invalid))
                {
                    error = Error(BindingMessages.InvalidData(identity, invalid.AsString()), import.Position, from);
                    return false;
                }

                modules.Add(identity, new Module(identity, kind) { Data = values });
                continue;
            }

            if (!Parser.TryParseModule(text, out var program, out var syntax))
            {
                error = syntax with { Module = identity };
                return false;
            }

            var code = new Module(identity, kind) { Program = program, Loading = true };
            modules.Add(identity, code);
            frames.Push((code, program.Imports, code.Imports));
            next.Push(0);
        }

        return true;
    }

    /// <summary>The descriptions of the code modules loaded, for <c>modulemeta</c>; null without a source.</summary>
    public ModuleTable? Table()
    {
        if (source is null)
            return null;
        var descriptions = new Dictionary<string, JqValue>();
        foreach (var module in modules.Values)
        {
            if (module.Program is not null)
                descriptions.Add(module.Identity, ModuleTable.Describe(module.Program));
        }

        return new ModuleTable(source, descriptions);
    }

    // Without a source no module exists.
    bool TryResolve(S.Import import, ModuleKind kind, string? from, out string identity, out string error)
    {
        if (source is not null)
            return source.TryResolve(import.Path, kind, import.Metadata, from, out identity, out error);
        (identity, error) = ("", BindingMessages.ModuleNotFound(import.Path));
        return false;
    }

    static SyntaxError Error(string message, Position position, string? module) => new(message, position.Line, position.Column, module);
}

partial class Resolver
{
    // The modules of this binding (shared by the main program and every module), and the module
    // this resolver binds (null for the main program and the prelude).
    readonly ModuleLoader? loader;
    readonly ModuleLoader.Module? module;

    // A resolver for a module: a scope of its own, the functions, host variables and constants of
    // the whole program.
    Resolver(Resolver main, ModuleLoader.Module module)
        : this(main.prelude, main.nativeOverrides, main.loader, main.functions, main.hostVariables, main.constants)
        => this.module = module;

    /// <summary>
    /// Where modules enter a scope (§9.11), before the top-level definitions: the main program loads
    /// and binds every module it needs first. Each directive then adds its module's exports as
    /// definitions at that point: <c>alias::f</c> for <c>import</c>, <c>f</c> for <c>include</c>, so
    /// that later definitions and directives shadow them as usual; a data import adds the constants
    /// <c>$alias</c> and <c>$alias::alias</c>.
    /// </summary>
    bool BindImports(ImmutableArray<S.Import> imports)
    {
        if (imports.Length == 0)
            return true;

        List<string> identities;
        if (module is not null)
        {
            identities = module.Imports;
        }
        else
        {
            if (!loader!.TryLoad(imports, out identities, out var order, out var failure))
            {
                error ??= failure;
                return false;
            }

            foreach (var code in order)
            {
                if (!BindModule(code))
                    return false;
            }
        }

        for (var i = 0; i < imports.Length; i++)
        {
            var import = imports[i];
            var loaded = loader![identities[i]];
            if (import.IsData)
            {
                constants.Add(loaded.Data);
                scope.Add(new Entry(EntryKind.Constant, import.Alias!, 0, 0, constants.Count - 1));
                scope.Add(new Entry(EntryKind.Constant, $"{import.Alias}::{import.Alias}", 0, 0, constants.Count - 1));
                continue;
            }

            var prefix = import.Alias is null ? "" : import.Alias + "::";
            foreach (var (name, arity, function) in loaded.Exports.Values)
                scope.Add(new Entry(EntryKind.Function, prefix + name, arity, 0, function));
        }

        return true;
    }

    // Binds a code module in a scope of its own and records its exports.
    bool BindModule(ModuleLoader.Module code)
    {
        var resolver = new Resolver(this, code);
        if (resolver.BindProgram(code.Program!) is null)
        {
            error ??= resolver.error;
            return false;
        }

        foreach (var index in resolver.exports)
        {
            var function = functions[index]!;
            code.Exports[$"{function.Name}/{function.Arity}"] = (function.Name, function.Arity, index);
        }

        return true;
    }
}