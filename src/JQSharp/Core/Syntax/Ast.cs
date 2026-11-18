using System.Collections.Immutable;
using Devlooped.Core.Values;

namespace Devlooped.Core.Syntax;

// The abstract syntax tree: immutable plain data, no behaviour, no back-pointers. Every node and
// pattern carries the position of the token that introduces it (its keyword, operator, bracket or
// name). Names stay as written (without '$', '.' or '@'; a qualified name keeps its "::"): the
// resolver binds them. Forms the spec defines by rewriting (§3.3) are already rewritten by the
// parser (see the list at the top of Parser.cs), so the node set below is all an interpreter sees.

/// <summary>A 1-based source position; columns count code points.</summary>
readonly record struct Position(int Line, int Column);

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
    Call,
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
    FuncDef,
    Label,
    Break,
}

abstract record Node(NodeKind Kind, Position Position);

/// <summary><c>.</c></summary>
sealed record Identity(Position Position) : Node(NodeKind.Identity, Position);

/// <summary>A constant: a number, a string without interpolation, <c>null</c>, <c>true</c>, <c>false</c>.</summary>
sealed record Literal(JqValue Value, Position Position) : Node(NodeKind.Literal, Position);

/// <summary>
/// A string with interpolations: <c>Texts[0] Parts[0] Texts[1] … Parts[n-1] Texts[n]</c>, so
/// <c>Texts.Length == Parts.Length + 1</c>. Each part's outputs are converted with
/// <see cref="Format"/> (null: <c>tostring</c>); the texts are taken as they are.
/// </summary>
sealed record Template(string? Format, ImmutableArray<string> Texts, ImmutableArray<Node> Parts, Position Position) : Node(NodeKind.Template, Position);

/// <summary><c>@name</c> alone: the format applied to the input.</summary>
sealed record Format(string Name, Position Position) : Node(NodeKind.Format, Position);

/// <summary><c>Target[Key]</c>; also <c>.name</c> and <c>."s"</c>. <see cref="Key"/> runs on the input of <see cref="Target"/>.</summary>
sealed record Index(Node Target, Node Key, Position Position) : Node(NodeKind.Index, Position);

/// <summary><c>Target[From:To]</c>; a missing bound is null.</summary>
sealed record Slice(Node Target, Node? From, Node? To, Position Position) : Node(NodeKind.Slice, Position);

/// <summary><c>Target[]</c></summary>
sealed record Iterate(Node Target, Position Position) : Node(NodeKind.Iterate, Position);

/// <summary><c>$name</c> or <c>$alias::name</c>.</summary>
sealed record Variable(string Name, Position Position) : Node(NodeKind.Variable, Position);

/// <summary><c>name</c> or <c>name(a; b)</c>: a function, closure parameter or builtin of arity <c>Arguments.Length</c>.</summary>
sealed record Call(string Name, ImmutableArray<Node> Arguments, Position Position) : Node(NodeKind.Call, Position);

/// <summary><c>Left | Right</c></summary>
sealed record Pipe(Node Left, Node Right, Position Position) : Node(NodeKind.Pipe, Position);

/// <summary><c>Left, Right</c></summary>
sealed record Comma(Node Left, Node Right, Position Position) : Node(NodeKind.Comma, Position);

enum BinaryOp
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Equal,
    NotEqual,
    Less,
    LessEqual,
    Greater,
    GreaterEqual,
}

/// <summary>An arithmetic operator or a comparison: one output per pair of outputs of the operands.</summary>
sealed record Binary(BinaryOp Op, Node Left, Node Right, Position Position) : Node(NodeKind.Binary, Position);

/// <summary><c>Left and Right</c></summary>
sealed record And(Node Left, Node Right, Position Position) : Node(NodeKind.And, Position);

/// <summary><c>Left or Right</c></summary>
sealed record Or(Node Left, Node Right, Position Position) : Node(NodeKind.Or, Position);

/// <summary><c>Left // Right</c></summary>
sealed record Alternative(Node Left, Node Right, Position Position) : Node(NodeKind.Alternative, Position);

/// <summary>Unary <c>-Operand</c>.</summary>
sealed record Negate(Node Operand, Position Position) : Node(NodeKind.Negate, Position);

/// <summary><c>[Body]</c>; <c>[]</c> has no body.</summary>
sealed record ArrayExpr(Node? Body, Position Position) : Node(NodeKind.ArrayExpr, Position);

/// <summary><c>{…}</c></summary>
sealed record ObjectExpr(ImmutableArray<ObjectEntry> Entries, Position Position) : Node(NodeKind.ObjectExpr, Position);

/// <summary>
/// One entry of an object construction. <see cref="Key"/> is the key expression (a literal for
/// <c>name:</c> and <c>"s":</c>, a template for interpolated keys, a variable for <c>$x:</c>, any
/// expression for <c>(e):</c>). A null <see cref="Value"/> is the shorthand <c>{"s"}</c>,
/// <c>{name}</c>, <c>{@f "s"}</c>: the value is the input indexed by each key, evaluated once per key.
/// (<c>{$x}</c> is rewritten to the key <c>"x"</c> and the value <c>$x</c>.)
/// </summary>
sealed record ObjectEntry(Node Key, Node? Value, Position Position);

/// <summary><c>if Condition then Then else Else end</c>; <c>elif</c> nests, a missing else is <see cref="Identity"/>.</summary>
sealed record If(Node Condition, Node Then, Node Else, Position Position) : Node(NodeKind.If, Position);

/// <summary><c>try Body catch Handler</c>; also <c>Body?</c>. A null handler discards the error.</summary>
sealed record Try(Node Body, Node? Handler, Position Position) : Node(NodeKind.Try, Position);

/// <summary><c>reduce Source as Patterns (Init; Update)</c></summary>
sealed record Reduce(Node Source, ImmutableArray<Pattern> Patterns, Node Init, Node Update, Position Position) : Node(NodeKind.Reduce, Position);

/// <summary><c>foreach Source as Patterns (Init; Update; Extract)</c>; a missing extract is <see cref="Identity"/>.</summary>
sealed record Foreach(Node Source, ImmutableArray<Pattern> Patterns, Node Init, Node Update, Node Extract, Position Position) : Node(NodeKind.Foreach, Position);

/// <summary><c>Source as Patterns | Body</c>. Several patterns are the <c>?//</c> alternatives.</summary>
sealed record Bind(Node Source, ImmutableArray<Pattern> Patterns, Node Body, Position Position) : Node(NodeKind.Bind, Position);

/// <summary><c>def …; Rest</c>: the definition is visible in its own body and in <see cref="Rest"/>.</summary>
sealed record FuncDef(Definition Definition, Node Rest, Position Position) : Node(NodeKind.FuncDef, Position);

/// <summary><c>label $Name | Body</c></summary>
sealed record Label(string Name, Node Body, Position Position) : Node(NodeKind.Label, Position);

/// <summary><c>break $Name</c></summary>
sealed record Break(string Name, Position Position) : Node(NodeKind.Break, Position);

/// <summary>
/// <c>def Name(Params): Body;</c>. Every parameter is a filter parameter when evaluated: for a
/// <c>$name</c> parameter (<see cref="Param.IsVariable"/>) the parser has already wrapped the body
/// in <c>name as $name | …</c> (§3.3), first parameter outermost.
/// </summary>
sealed record Definition(string Name, ImmutableArray<Param> Params, Node Body, Position Position);

sealed record Param(string Name, bool IsVariable, Position Position);

enum PatternKind
{
    Variable,
    Array,
    Object,
}

abstract record Pattern(PatternKind Kind, Position Position);

/// <summary><c>$name</c></summary>
sealed record VariablePattern(string Name, Position Position) : Pattern(PatternKind.Variable, Position);

/// <summary><c>[P0, P1, …]</c>: element i is matched against <c>.[i]</c>.</summary>
sealed record ArrayPattern(ImmutableArray<Pattern> Elements, Position Position) : Pattern(PatternKind.Array, Position);

/// <summary><c>{…}</c></summary>
sealed record ObjectPattern(ImmutableArray<PatternEntry> Entries, Position Position) : Pattern(PatternKind.Object, Position);

/// <summary>
/// One entry of an object pattern: for each output of <see cref="Key"/>, the value <c>.[key]</c> is
/// bound to <see cref="Variable"/> (when not null) and matched against <see cref="Value"/> (when
/// not null). <c>$name</c> is the key <c>"name"</c> with the variable <c>name</c>; <c>$name: P</c>
/// adds the pattern; <c>name: P</c>, <c>"s": P</c> and <c>(e): P</c> have no variable.
/// </summary>
sealed record PatternEntry(Node Key, string? Variable, Pattern? Value, Position Position);

/// <summary><c>import "Path" as Alias Metadata;</c>, <c>import "Path" as $Alias …;</c> (data) or <c>include "Path" …;</c> (no alias).</summary>
sealed record Import(string Path, string? Alias, bool IsData, JqValue? Metadata, Position Position);

/// <summary>
/// A parsed program or module file. <see cref="Definitions"/> are the top-level definitions in
/// order (what a module exports); <see cref="Body"/> is the main expression, <see cref="Identity"/>
/// for a program of definitions only, and null for a module file.
/// </summary>
sealed record Program(JqValue? Metadata, ImmutableArray<Import> Imports, ImmutableArray<Definition> Definitions, Node? Body);
