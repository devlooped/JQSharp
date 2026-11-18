using System.Collections.Immutable;
using Devlooped.Core.Builtins;
using Devlooped.Core.Values;

namespace Devlooped.Core.Binding;

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

abstract record Node(NodeKind Kind);

sealed record Identity() : Node(NodeKind.Identity);

sealed record Literal(JqValue Value) : Node(NodeKind.Literal);

/// <summary>An interpolated string; see <see cref="Syntax.Template"/>.</summary>
sealed record Template(string? Format, ImmutableArray<string> Texts, ImmutableArray<Node> Parts) : Node(NodeKind.Template);

sealed record Format(string Name) : Node(NodeKind.Format);

sealed record Index(Node Target, Node Key) : Node(NodeKind.Index);

sealed record Slice(Node Target, Node? From, Node? To) : Node(NodeKind.Slice);

sealed record Iterate(Node Target) : Node(NodeKind.Iterate);

/// <summary>A lexical variable: slot <see cref="Slot"/> of the frame <see cref="Hops"/> frames out.</summary>
sealed record Variable(int Hops, int Slot) : Node(NodeKind.Variable);

/// <summary>A <c>$name</c> without lexical binding: entry <see cref="Index"/> of <see cref="BoundProgram.HostVariables"/>.</summary>
sealed record HostVariable(string Name, int Index) : Node(NodeKind.HostVariable);

/// <summary><c>$ENV</c> where no binding shadows it.</summary>
sealed record EnvVariable() : Node(NodeKind.EnvVariable);

/// <summary>A call of <see cref="BoundProgram.Functions"/>[<see cref="Function"/>], defined <see cref="Hops"/> frames out.</summary>
sealed record CallFunction(int Function, int Hops, ImmutableArray<Node> Arguments) : Node(NodeKind.CallFunction);

/// <summary>A call of a closure parameter: slot <see cref="Slot"/> of the call frame <see cref="Hops"/> frames out.</summary>
sealed record CallParameter(int Hops, int Slot) : Node(NodeKind.CallParameter);

/// <summary>A call of a native; the arguments are closures over the caller's environment.</summary>
sealed record CallNative(Native Native, ImmutableArray<Node> Arguments) : Node(NodeKind.CallNative);

sealed record Pipe(Node Left, Node Right) : Node(NodeKind.Pipe);

sealed record Comma(Node Left, Node Right) : Node(NodeKind.Comma);

sealed record Binary(Syntax.BinaryOp Op, Node Left, Node Right) : Node(NodeKind.Binary);

sealed record And(Node Left, Node Right) : Node(NodeKind.And);

sealed record Or(Node Left, Node Right) : Node(NodeKind.Or);

sealed record Alternative(Node Left, Node Right) : Node(NodeKind.Alternative);

sealed record Negate(Node Operand) : Node(NodeKind.Negate);

sealed record ArrayExpr(Node? Body) : Node(NodeKind.ArrayExpr);

sealed record ObjectExpr(ImmutableArray<ObjectEntry> Entries) : Node(NodeKind.ObjectExpr);

/// <summary>See <see cref="Syntax.ObjectEntry"/>: a null value is the input indexed by each output of the key.</summary>
sealed record ObjectEntry(Node Key, Node? Value);

sealed record If(Node Condition, Node Then, Node Else) : Node(NodeKind.If);

sealed record Try(Node Body, Node? Handler) : Node(NodeKind.Try);

/// <summary><see cref="Update"/> runs with the binder's frame pushed; <see cref="Init"/> without it.</summary>
sealed record Reduce(Node Source, Binder Binder, Node Init, Node Update) : Node(NodeKind.Reduce);

/// <summary><see cref="Update"/> and <see cref="Extract"/> run with the binder's frame pushed.</summary>
sealed record Foreach(Node Source, Binder Binder, Node Init, Node Update, Node Extract) : Node(NodeKind.Foreach);

/// <summary><c>Source as Patterns | Body</c>: <see cref="Body"/> runs with the binder's frame pushed.</summary>
sealed record Bind(Node Source, Binder Binder, Node Body) : Node(NodeKind.Bind);

/// <summary><see cref="Body"/> runs with a frame holding a new label instance.</summary>
sealed record Label(Node Body) : Node(NodeKind.Label);

/// <summary>Breaks to the label instance of the frame <see cref="Hops"/> frames out.</summary>
sealed record Break(int Hops) : Node(NodeKind.Break);

/// <summary>
/// The patterns of one binding (several are the <c>?//</c> alternatives) and the size of the frame
/// they fill: one slot per distinct variable name of all of them, null until a pattern binds it.
/// </summary>
sealed record Binder(ImmutableArray<Pattern> Patterns, int Slots);

enum PatternKind
{
    Variable,
    Array,
    Object,
}

abstract record Pattern(PatternKind Kind);

/// <summary>
/// Stores the value in <see cref="Slot"/>; -1 discards it. Within one pattern, the first occurrence of
/// a name in destructuring order (array elements from the last, object entries from the first) owns
/// the slot and later ones discard their value, as jq 1.8.2 (<c>[1,2] | . as [$a, $a] | $a</c> is 2).
/// </summary>
sealed record VariablePattern(int Slot) : Pattern(PatternKind.Variable);

sealed record ArrayPattern(ImmutableArray<Pattern> Elements) : Pattern(PatternKind.Array);

sealed record ObjectPattern(ImmutableArray<PatternEntry> Entries) : Pattern(PatternKind.Object);

/// <summary>
/// See <see cref="Syntax.PatternEntry"/>; <see cref="Slot"/> is -1 without a variable. The key runs in the
/// scope outside the binding: the pattern's own variables are not visible in it.
/// </summary>
sealed record PatternEntry(Node Key, int Slot, Pattern? Value);

/// <summary>A jqlite-defined function (user or prelude). Its body runs with its parameters' frame pushed when <see cref="Arity"/> &gt; 0.</summary>
sealed record Function(string Name, int Arity, Node Body)
{
    public override string ToString() => $"{Name}/{Arity}";
}

/// <summary>
/// A bound program: immutable and shareable between threads. <see cref="Functions"/> starts with the
/// prelude's functions (shared by every program), followed by the program's own.
/// </summary>
sealed record BoundProgram(ImmutableArray<Function> Functions, ImmutableArray<string> HostVariables, Node Body);
