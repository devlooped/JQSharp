using System.Collections.Immutable;

namespace Devlooped.Core.Builtins;

/// <summary>The groups of spec §9: <see cref="Modules"/> is the optional group of §9.11.</summary>
enum NativeGroup
{
    Core,
    Modules,
}

/// <summary>
/// One native builtin of spec §9: identity, catalogue position, and how it takes its arguments. The
/// catalogue is plain data so that the resolver can bind calls to an entry before the entry is
/// implemented; the interpreter finds an implementation by <see cref="Index"/> in NativeTable
/// (until there is one, a call is the runtime error <c>name/arity is not implemented</c>).
/// </summary>
/// <remarks>
/// By default a native takes value arguments (§9.1): each argument is evaluated on the input and
/// the native gives one output, or an error, per combination. <see cref="TakesClosures"/> marks
/// the entries of §9 that say otherwise: they receive their arguments unevaluated and decide what
/// to evaluate, in which mode, and how many outputs to give.
/// </remarks>
sealed record Native(string Name, int Arity, NativeGroup Group, int Index, bool TakesClosures)
{
    /// <summary>Internal builtins (a name starting with <c>_</c>) are callable but not listed by <c>builtins</c> (§9.1).</summary>
    public bool IsInternal => Name.StartsWith('_');

    /// <summary>
    /// A native implementation of a builtin that §9 defines in the prelude, for speed (§9.1 allows
    /// it when results, errors and paths are identical): calls that would bind to the prelude
    /// definition bind to the native instead (Resolver). It is not one of the natives of §9.
    /// </summary>
    public bool OverridesPrelude { get; init; }

    public override string ToString() => $"{Name}/{Arity}";
}

/// <summary>
/// The native catalogue: every builtin that spec §9 marks N, in the order of the spec. With the
/// definitions of <c>spec/prelude.jq</c> it is the whole builtin set, and what <c>builtins/0</c> lists.
/// </summary>
static class Natives
{
    public static readonly ImmutableArray<Native> All = WithOverrides(["_modify/2", "_assign/2"], Build(
    [
        // §9.2 Core
        "empty/0", "error/0", "type/0", "length/0", "utf8bytelength/0", "builtins/0", "halt/0", "halt_error/1", "last/1",
        // §9.3 Types and selection
        "infinite/0", "nan/0", "isinfinite/0", "isnan/0", "isnormal/0",
        // §9.4 Objects, arrays and collections
        "keys/0", "keys_unsorted/0", "has/1", "contains/1", "range/2", "limit/2", "skip/2", "while/2", "until/2", "repeat/1",
        "sort/0", "_sort_by_impl/1", "_group_by_impl/1", "_min_by_impl/1", "_max_by_impl/1", "unique/0", "min/0", "max/0", "bsearch/1",
        // §9.5 Paths
        "path/1", "getpath/1", "setpath/2", "delpaths/1",
        // §9.6 Strings
        "tostring/0", "tojson/0", "fromjson/0", "tonumber/0", "explode/0", "implode/0", "ascii_downcase/0", "ascii_upcase/0",
        "startswith/1", "endswith/1", "split/1", "_strindices/1", "test/2", "match/2",
        // §9.7 Dates
        "now/0", "gmtime/0", "localtime/0", "mktime/0", "strftime/1", "strflocaltime/1", "strptime/1",
        // §9.8 Math: exactly specified, library functions, defined by formula
        "floor/0", "ceil/0", "trunc/0", "round/0", "nearbyint/0", "fabs/0", "sqrt/0", "fmod/2", "remainder/2", "fma/3",
        "fmin/2", "fmax/2", "ldexp/2", "scalbln/2", "logb/0", "frexp/0", "modf/0",
        "acos/0", "asin/0", "atan/0", "acosh/0", "asinh/0", "atanh/0", "cbrt/0", "cos/0", "cosh/0", "exp/0", "exp2/0",
        "expm1/0", "log/0", "log10/0", "log1p/0", "log2/0", "sin/0", "sinh/0", "tan/0", "tanh/0", "atan2/2", "pow/2", "hypot/2",
        "erf/0", "erfc/0", "tgamma/0", "lgamma/0", "j0/0", "j1/0",
    ],
    [
        // §9.11 Modules
        "modulemeta/0",
    ],
    [
        // §9.2, §9.4, §9.5, §9.6: no output, a signal, generators, path-capable, many outputs.
        "empty/0", "halt/0", "halt_error/1", "last/1", "range/2", "limit/2", "skip/2", "while/2", "until/2", "repeat/1",
        "path/1", "getpath/1", "match/2",
    ]));

    static readonly Dictionary<string, Native> byKey = Index(All);

    /// <summary>The native <paramref name="name"/>/<paramref name="arity"/>, or null.</summary>
    public static Native? Find(string name, int arity) => byKey.GetValueOrDefault(Key(name, arity));

    /// <summary>The native that overrides the prelude builtin <paramref name="name"/>/<paramref name="arity"/>, or null.</summary>
    public static Native? Override(string name, int arity) => Find(name, arity) is { OverridesPrelude: true } native ? native : null;

    static string Key(string name, int arity) => $"{name}/{arity}";

    // The overrides of prelude builtins, after the natives of §9; they take closures.
    static ImmutableArray<Native> WithOverrides(string[] overrides, ImmutableArray<Native> natives)
    {
        var all = natives.ToBuilder();
        foreach (var signature in overrides)
        {
            var slash = signature.IndexOf('/');
            all.Add(new Native(signature[..slash], int.Parse(signature[(slash + 1)..]), NativeGroup.Core, all.Count, TakesClosures: true) { OverridesPrelude = true });
        }

        return all.ToImmutable();
    }

    static ImmutableArray<Native> Build(string[] core, string[] modules, string[] closures)
    {
        var natives = ImmutableArray.CreateBuilder<Native>(core.Length + modules.Length);
        Add(natives, core, NativeGroup.Core, closures);
        Add(natives, modules, NativeGroup.Modules, closures);
        return natives.MoveToImmutable();
    }

    static void Add(ImmutableArray<Native>.Builder natives, string[] signatures, NativeGroup group, string[] closures)
    {
        foreach (var signature in signatures)
        {
            var slash = signature.IndexOf('/');
            var takesClosures = Array.IndexOf(closures, signature) >= 0;
            natives.Add(new Native(signature[..slash], int.Parse(signature[(slash + 1)..]), group, natives.Count, takesClosures));
        }
    }

    static Dictionary<string, Native> Index(ImmutableArray<Native> natives)
    {
        var index = new Dictionary<string, Native>(natives.Length);
        foreach (var native in natives)
            index.Add(Key(native.Name, native.Arity), native);
        return index;
    }
}