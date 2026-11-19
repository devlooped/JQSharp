using System.Collections.Immutable;
using JQLite.Core.Builtins;
using JQLite.Core.Values;

namespace JQLite.Core.Binding;

// The bound program: what the interpreter evaluates. A tree of its own node types rather than
// the AST plus a side table, because every port can build it bottom-up as plain immutable data:
// no node is mutated after construction, no map is keyed by node identity, and nothing is cyclic.
//
// Names are gone. The environment at run time is a chain of frames, innermost first, and every
// reference counts frames outward from where it is written:
// - a frame is pushed by `as` and by the binder of `reduce`/`foreach` (one value slot per
//   variable of all its patterns), by `label` (the label instance), and by a call of a function
//   with parameters (one closure slot per parameter);
// - a variable is (Hops, Slot), a closure parameter (Hops, Slot), a `break` Hops;
// - a function call names its definition by index in BoundProgram.Functions (recursion is an
//   index, not a cycle), and Hops is the number of frames between the call and the definition:
//   the callee runs in the caller's environment with Hops frames removed, plus its parameters.
// Host variables are the one late-bound name (spec §7.5): each has an index in
// BoundProgram.HostVariables, so the host's values can be looked up once per evaluation.
//
// Every node also says whether it yields at most one output (AtMostOne: the single expressions of
// spec §7.7), computed from its children when it is built: the interpreter evaluates such nodes
// directly to a value, without callbacks (Interpreter.EvalOne). A call is single when its function
// is; the resolver knows that of a function still being defined by assuming it (Resolver.Call).

enum NodeKind
{
    Identity,
    Literal,
    Template,
    Format,
    Index,
    Slice,
    Iterate,
    Variable,
    HostVariable,
    EnvVariable,
    CallFunction,
    CallParameter,
    CallNative,
    Pipe,
    Comma,
    Binary,
    And,
    Or,
    Alternative,
    Negate,
    ArrayExpr,
    ObjectExpr,
    If,
    Try,
    Reduce,
    Foreach,
    Bind,
    Label,
    Break,
}

/// <summary>A bound node; <see cref="AtMostOne"/>: it yields zero or one output whatever its input and environment.</summary>
abstract record Node(NodeKind Kind, bool AtMostOne)
{
    protected static bool All(ImmutableArray<Node> nodes)
    {
        foreach (var node in nodes)
        {
            if (!node.AtMostOne)
                return false;
        }

        return true;
    }

    protected static bool One(Node? node) => node is null || node.AtMostOne;

    /// <summary>Whether the arguments in <paramref name="slots"/> are all single: what makes a call single (spec §7.7).</summary>
    public static bool All(ImmutableArray<int> slots, ImmutableArray<Node> arguments)
    {
        foreach (var slot in slots)
        {
            if (!arguments[slot].AtMostOne)
                return false;
        }

        return true;
    }
}

sealed record Identity() : Node(NodeKind.Identity, true);

sealed record Literal(JqValue Value) : Node(NodeKind.Literal, true);

/// <summary>An interpolated string; see <see cref="Syntax.Template"/>.</summary>
sealed record Template(string? Format, ImmutableArray<string> Texts, ImmutableArray<Node> Parts) : Node(NodeKind.Template, All(Parts));

sealed record Format(string Name) : Node(NodeKind.Format, true);

sealed record Index(Node Target, Node Key) : Node(NodeKind.Index, Target.AtMostOne && Key.AtMostOne);

sealed record Slice(Node Target, Node? From, Node? To) : Node(NodeKind.Slice, Target.AtMostOne && One(From) && One(To));

sealed record Iterate(Node Target) : Node(NodeKind.Iterate, false);

/// <summary>A lexical variable: slot <see cref="Slot"/> of the frame <see cref="Hops"/> frames out.</summary>
sealed record Variable(int Hops, int Slot) : Node(NodeKind.Variable, true);

/// <summary>A <c>$name</c> without lexical binding: entry <see cref="Index"/> of <see cref="BoundProgram.HostVariables"/>.</summary>
sealed record HostVariable(string Name, int Index) : Node(NodeKind.HostVariable, true);

/// <summary><c>$ENV</c> where no binding shadows it.</summary>
sealed record EnvVariable() : Node(NodeKind.EnvVariable, true);

/// <summary>
/// A call of <see cref="BoundProgram.Functions"/>[<see cref="Function"/>], defined <see cref="Hops"/> frames out.
/// <see cref="Single"/>: the function is single and the argument of each of its <c>$</c> parameters is (spec §7.7).
/// </summary>
sealed record CallFunction(int Function, int Hops, ImmutableArray<Node> Arguments, bool Single) : Node(NodeKind.CallFunction, Single);

/// <summary>A call of a closure parameter: slot <see cref="Slot"/> of the call frame <see cref="Hops"/> frames out.</summary>
sealed record CallParameter(int Hops, int Slot) : Node(NodeKind.CallParameter, false);

/// <summary>A call of a native; the arguments are closures over the caller's environment.</summary>
sealed record CallNative(Native Native, ImmutableArray<Node> Arguments) : Node(NodeKind.CallNative, Native.SingleWhen is { } slots && All(slots, Arguments));

sealed record Pipe(Node Left, Node Right) : Node(NodeKind.Pipe, Left.AtMostOne && Right.AtMostOne);

/// <summary><c>a, b, c</c>: a left-nested chain of commas is one node, so that evaluating it does not recurse per term.</summary>
sealed record Comma(ImmutableArray<Node> Items) : Node(NodeKind.Comma, false);

sealed record Binary(Syntax.BinaryOp Op, Node Left, Node Right) : Node(NodeKind.Binary, Left.AtMostOne && Right.AtMostOne);

sealed record And(Node Left, Node Right) : Node(NodeKind.And, Left.AtMostOne && Right.AtMostOne);

sealed record Or(Node Left, Node Right) : Node(NodeKind.Or, Left.AtMostOne && Right.AtMostOne);

sealed record Alternative(Node Left, Node Right) : Node(NodeKind.Alternative, Left.AtMostOne && Right.AtMostOne);

sealed record Negate(Node Operand) : Node(NodeKind.Negate, Operand.AtMostOne);

sealed record ArrayExpr(Node? Body) : Node(NodeKind.ArrayExpr, true);

sealed record ObjectExpr(ImmutableArray<ObjectEntry> Entries) : Node(NodeKind.ObjectExpr, AllEntries(Entries))
{
    static bool AllEntries(ImmutableArray<ObjectEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (!entry.Key.AtMostOne || !One(entry.Value))
                return false;
        }

        return true;
    }
}

/// <summary>See <see cref="Syntax.ObjectEntry"/>: a null value is the input indexed by each output of the key.</summary>
sealed record ObjectEntry(Node Key, Node? Value);

sealed record If(Node Condition, Node Then, Node Else) : Node(NodeKind.If, Condition.AtMostOne && Then.AtMostOne && Else.AtMostOne);

sealed record Try(Node Body, Node? Handler) : Node(NodeKind.Try, Body.AtMostOne && One(Handler));

/// <summary><see cref="Update"/> runs with the binder's frame pushed; <see cref="Init"/> without it.</summary>
sealed record Reduce(Node Source, Binder Binder, Node Init, Node Update) : Node(NodeKind.Reduce, Init.AtMostOne);

/// <summary><see cref="Update"/> and <see cref="Extract"/> run with the binder's frame pushed.</summary>
sealed record Foreach(Node Source, Binder Binder, Node Init, Node Update, Node Extract) : Node(NodeKind.Foreach, false);

/// <summary><c>Source as Patterns | Body</c>: <see cref="Body"/> runs with the binder's frame pushed.</summary>
sealed record Bind(Node Source, Binder Binder, Node Body) : Node(NodeKind.Bind, Source.AtMostOne && Binder.KeysAtMostOne && Body.AtMostOne);

/// <summary><see cref="Body"/> runs with a frame holding a new label instance.</summary>
sealed record Label(Node Body) : Node(NodeKind.Label, Body.AtMostOne);

/// <summary>Breaks to the label instance of the frame <see cref="Hops"/> frames out.</summary>
sealed record Break(int Hops) : Node(NodeKind.Break, true);

/// <summary>
/// The patterns of one binding (several are the <c>?//</c> alternatives) and the size of the frame
/// they fill: one slot per distinct variable name of all of them, null until a pattern binds it.
/// </summary>
sealed record Binder(ImmutableArray<Pattern> Patterns, int Slots)
{
    /// <summary>Every key expression of the patterns yields at most one output, so a value binds at most once per pattern.</summary>
    public bool KeysAtMostOne { get; } = AllKeys(Patterns);

    static bool AllKeys(ImmutableArray<Pattern> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (!KeysOf(pattern))
                return false;
        }

        return true;
    }

    static bool KeysOf(Pattern pattern)
    {
        switch (pattern)
        {
            case ArrayPattern array:
                return AllKeys(array.Elements);
            case ObjectPattern obj:
                foreach (var entry in obj.Entries)
                {
                    if (!entry.Key.AtMostOne || (entry.Value is not null && !KeysOf(entry.Value)))
                        return false;
                }

                return true;
            default:
                return true;
        }
    }
}

enum PatternKind
{
    Variable,
    Array,
    Object,
}

abstract record Pattern(PatternKind Kind);

/// <summary>
/// Stores the value in <see cref="Slot"/>; -1 discards it. Patterns are matched in order (array
/// elements from the last, object entries from the first), so a slot written twice keeps the later
/// value; the resolver gives -1 to the repetitions that must not win (spec §7.5).
/// </summary>
sealed record VariablePattern(int Slot) : Pattern(PatternKind.Variable);

sealed record ArrayPattern(ImmutableArray<Pattern> Elements) : Pattern(PatternKind.Array);

sealed record ObjectPattern(ImmutableArray<PatternEntry> Entries) : Pattern(PatternKind.Object);

/// <summary>
/// See <see cref="Syntax.PatternEntry"/>; <see cref="Slot"/> is -1 without a variable. The key runs in the
/// scope outside the binding: the pattern's own variables are not visible in it.
/// </summary>
sealed record PatternEntry(Node Key, int Slot, Pattern? Value);

/// <summary>
/// A jqlite-defined function (user or prelude). Its body runs with its parameters' frame pushed when <see cref="Arity"/> &gt; 0.
/// <see cref="Single"/>: the body yields at most one output for each binding of the <c>$</c> parameters,
/// whose slots are <see cref="ValueParameters"/> (spec §7.7).
/// </summary>
sealed record Function(string Name, int Arity, Node Body, bool Single, ImmutableArray<int> ValueParameters)
{
    public override string ToString() => $"{Name}/{Arity}";
}

/// <summary>
/// A bound program: immutable and shareable between threads. <see cref="Functions"/> starts with the
/// prelude's functions (shared by every program), followed by the program's own.
/// </summary>
sealed record BoundProgram(ImmutableArray<Function> Functions, ImmutableArray<string> HostVariables, Node Body)
{
    /// <summary>The modules loaded, for <c>modulemeta</c>; null when the host gave no module source (§9.11).</summary>
    public ModuleTable? Modules { get; init; }
}