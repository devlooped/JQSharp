using System.Collections.Immutable;
using Devlooped.Core.Syntax;

namespace Devlooped.Core.Binding;

/// <summary>
/// <c>spec/prelude.jq</c>, parsed and bound once against the natives, then shared by every program
/// (immutable, so safe to use from any thread). A program's function table starts with
/// <see cref="Functions"/>, so prelude calls keep their indices in every program.
/// </summary>
sealed class Prelude
{
    /// <summary>The name of the embedded resource that holds <c>spec/prelude.jq</c> (see JQSharp.csproj).</summary>
    public const string ResourceName = "jqlite.prelude.jq";

    /// <summary>The prelude, with the native overrides of its builtins (Native.OverridesPrelude).</summary>
    public static Prelude Instance { get; } = Load(ReadSource());

    static readonly Lazy<Prelude> withoutOverrides = new(() => Load(ReadSource(), nativeOverrides: false));

    /// <summary>The prelude exactly as written, every builtin bound to its definition: to check that the overrides change nothing.</summary>
    public static Prelude WithoutOverrides => withoutOverrides.Value;

    readonly Dictionary<string, int> visible = [];

    Prelude(ImmutableArray<Function> functions, ImmutableArray<Function> definitions)
    {
        Functions = functions;
        Definitions = definitions;
        foreach (var definition in definitions)
            visible[Key(definition.Name, definition.Arity)] = functions.IndexOf(definition);
    }

    /// <summary>Every function of the prelude, nested ones included, in the order of definition.</summary>
    public ImmutableArray<Function> Functions { get; }

    /// <summary>The top-level definitions: the builtins the prelude defines.</summary>
    public ImmutableArray<Function> Definitions { get; }

    /// <summary>The index in <see cref="Functions"/> of the builtin <paramref name="name"/>/<paramref name="arity"/>, or null.</summary>
    public int? Find(string name, int arity) => visible.TryGetValue(Key(name, arity), out var index) ? index : null;

    static string Key(string name, int arity) => $"{name}/{arity}";

    /// <summary>Parses and binds a prelude source. The embedded prelude must bind: a failure is a defect of the build, not of a program.</summary>
    internal static Prelude Load(string source, bool nativeOverrides = true)
    {
        if (!Parser.TryParseModule(source, out var module, out var error)
            || !Resolver.TryBindPrelude(module, nativeOverrides, out var functions, out var definitions, out error))
            throw new InvalidOperationException($"The prelude does not bind: {error.Message} at {error.Line}:{error.Column}");
        return new Prelude(functions, definitions);
    }

    // The one host-specific step of the core: reading a resource embedded in the assembly.
    static string ReadSource()
    {
        using var stream = typeof(Prelude).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Resource {ResourceName} not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}