package syntax

import "github.com/devlooped/jqlite/go/internal/values"

// The abstract syntax tree: plain data, no behaviour, no back-pointers. Every node and pattern
// carries the position of the token that introduces it (its keyword, operator, bracket or name).
// Names stay as written (without '$', '.' or '@'; a qualified name keeps its "::"): the resolver
// binds them. Forms the spec defines by rewriting (§3.3) are already rewritten by the parser (see
// the list at the top of parser.go), so the node set below is all a resolver sees. The parser
// fills the "rest of the pipe" of a node it has just built; after parsing, nothing changes a node.

// Position is a 1-based source position; columns count code points.
type Position struct {
	Line   int
	Column int
}

// NodeKind is the kind of an AST node.
type NodeKind int

// The kinds of AST nodes.
const (
	NodeIdentity NodeKind = iota
	NodeLiteral
	NodeTemplate
	NodeFormat
	NodeIndex
	NodeSlice
	NodeIterate
	NodeVariable
	NodeCall
	NodePipe
	NodeComma
	NodeBinary
	NodeAnd
	NodeOr
	NodeAlternative
	NodeNegate
	NodeArrayExpr
	NodeObjectExpr
	NodeIf
	NodeTry
	NodeReduce
	NodeForeach
	NodeBind
	NodeFuncDef
	NodeLabel
	NodeBreak
)

// Node is an AST node: one of the pointer types below.
type Node interface {
	Kind() NodeKind
	Pos() Position
}

// NodeBase is the kind and position every node has.
type NodeBase struct {
	kind     NodeKind
	Position Position
}

// Kind gives the kind of the node.
func (n *NodeBase) Kind() NodeKind { return n.kind }

// Pos gives the position of the node.
func (n *NodeBase) Pos() Position { return n.Position }

func base(kind NodeKind, at Position) NodeBase { return NodeBase{kind, at} }

// Identity is `.`.
type Identity struct{ NodeBase }

// Literal is a constant: a number, a string without interpolation, null, true, false.
type Literal struct {
	NodeBase
	Value values.Value
}

// Template is a string with interpolations: Texts[0] Parts[0] Texts[1] … Parts[n-1] Texts[n], so
// len(Texts) == len(Parts) + 1. Each part's outputs are converted with Format ("" and HasFormat
// false: tostring); the texts are taken as they are.
type Template struct {
	NodeBase
	Format    string
	HasFormat bool
	Texts     []string
	Parts     []Node
}

// Format is @name alone: the format applied to the input.
type Format struct {
	NodeBase
	Name string
}

// Index is Target[Key]; also .name and ."s". Key runs on the input of Target.
type Index struct {
	NodeBase
	Target Node
	Key    Node
}

// Slice is Target[From:To]; a missing bound is nil.
type Slice struct {
	NodeBase
	Target Node
	From   Node
	To     Node
}

// Iterate is Target[].
type Iterate struct {
	NodeBase
	Target Node
}

// Variable is $name or $alias::name.
type Variable struct {
	NodeBase
	Name string
}

// Call is name or name(a; b): a function, closure parameter or builtin of arity len(Arguments).
type Call struct {
	NodeBase
	Name      string
	Arguments []Node
}

// Pipe is Left | Right.
type Pipe struct {
	NodeBase
	Left  Node
	Right Node
}

// Comma is Left, Right.
type Comma struct {
	NodeBase
	Left  Node
	Right Node
}

// BinaryOp is an arithmetic operator or a comparison.
type BinaryOp int

// The binary operators.
const (
	OpAdd BinaryOp = iota
	OpSubtract
	OpMultiply
	OpDivide
	OpModulo
	OpEqual
	OpNotEqual
	OpLess
	OpLessEqual
	OpGreater
	OpGreaterEqual
)

var binaryOpNames = [...]string{"Add", "Subtract", "Multiply", "Divide", "Modulo", "Equal", "NotEqual", "Less", "LessEqual", "Greater", "GreaterEqual"}

func (op BinaryOp) String() string { return binaryOpNames[op] }

// Binary is an arithmetic operator or a comparison: one output per pair of outputs of the operands.
type Binary struct {
	NodeBase
	Op    BinaryOp
	Left  Node
	Right Node
}

// And is Left and Right.
type And struct {
	NodeBase
	Left  Node
	Right Node
}

// Or is Left or Right.
type Or struct {
	NodeBase
	Left  Node
	Right Node
}

// Alternative is Left // Right.
type Alternative struct {
	NodeBase
	Left  Node
	Right Node
}

// Negate is unary -Operand.
type Negate struct {
	NodeBase
	Operand Node
}

// ArrayExpr is [Body]; [] has no body (nil).
type ArrayExpr struct {
	NodeBase
	Body Node
}

// ObjectExpr is {…}.
type ObjectExpr struct {
	NodeBase
	Entries []*ObjectEntry
}

// ObjectEntry is one entry of an object construction. Key is the key expression (a literal for
// name: and "s":, a template for interpolated keys, a variable for $x:, any expression for (e):). A
// nil Value is the shorthand {"s"}, {name}, {@f "s"}: the value is the input indexed by each key,
// evaluated once per key. ({$x} is rewritten to the key "x" and the value $x.)
type ObjectEntry struct {
	Key      Node
	Value    Node
	Position Position
}

// If is if Condition then Then else Else end; elif nests, a missing else is Identity.
type If struct {
	NodeBase
	Condition Node
	Then      Node
	Else      Node
}

// Try is try Body catch Handler; also Body?. A nil handler discards the error.
type Try struct {
	NodeBase
	Body    Node
	Handler Node
}

// Reduce is reduce Source as Patterns (Init; Update).
type Reduce struct {
	NodeBase
	Source   Node
	Patterns []Pattern
	Init     Node
	Update   Node
}

// Foreach is foreach Source as Patterns (Init; Update; Extract); a missing extract is Identity.
type Foreach struct {
	NodeBase
	Source   Node
	Patterns []Pattern
	Init     Node
	Update   Node
	Extract  Node
}

// Bind is Source as Patterns | Body. Several patterns are the ?// alternatives.
type Bind struct {
	NodeBase
	Source   Node
	Patterns []Pattern
	Body     Node
}

// FuncDef is def …; Rest: the definition is visible in its own body and in Rest.
type FuncDef struct {
	NodeBase
	Definition *Definition
	Rest       Node
}

// Label is label $Name | Body.
type Label struct {
	NodeBase
	Name string
	Body Node
}

// Break is break $Name.
type Break struct {
	NodeBase
	Name string
}

// Definition is def Name(Params): Body;. Every parameter is a filter parameter; a $name parameter
// (Param.IsVariable) also binds $name to each output of its own argument, first parameter
// outermost (§3.3). The resolver does that binding by position, since a name may repeat: in
// def f($a; a) the filter a is the second parameter and $a the first.
type Definition struct {
	Name     string
	Params   []*Param
	Body     Node
	Position Position
}

// Param is a parameter of a definition.
type Param struct {
	Name       string
	IsVariable bool
	Position   Position
}

// PatternKind is the kind of a destructuring pattern.
type PatternKind int

// The kinds of patterns.
const (
	PatternVariable PatternKind = iota
	PatternArray
	PatternObject
)

// Pattern is a destructuring pattern: *VariablePattern, *ArrayPattern or *ObjectPattern.
type Pattern interface {
	PatternKind() PatternKind
	Pos() Position
}

// VariablePattern is $name.
type VariablePattern struct {
	Name     string
	Position Position
}

// ArrayPattern is [P0, P1, …]: element i is matched against .[i].
type ArrayPattern struct {
	Elements []Pattern
	Position Position
}

// ObjectPattern is {…}.
type ObjectPattern struct {
	Entries  []*PatternEntry
	Position Position
}

func (*VariablePattern) PatternKind() PatternKind { return PatternVariable }
func (*ArrayPattern) PatternKind() PatternKind    { return PatternArray }
func (*ObjectPattern) PatternKind() PatternKind   { return PatternObject }
func (p *VariablePattern) Pos() Position          { return p.Position }
func (p *ArrayPattern) Pos() Position             { return p.Position }
func (p *ObjectPattern) Pos() Position            { return p.Position }

// PatternEntry is one entry of an object pattern: for each output of Key, the value .[key] is
// bound to Variable (when HasVariable) and matched against Value (when not nil). $name is the key
// "name" with the variable name; $name: P adds the pattern; name: P, "s": P and (e): P have no
// variable.
type PatternEntry struct {
	Key         Node
	Variable    string
	HasVariable bool
	Value       Pattern
	Position    Position
}

// Import is import "Path" as Alias Metadata;, import "Path" as $Alias …; (data) or include
// "Path" …; (no alias: HasAlias false).
type Import struct {
	Path     string
	Alias    string
	HasAlias bool
	IsData   bool
	Metadata *values.Value
	Position Position
}

// Program is a parsed program or module file. Definitions are the top-level definitions in
// order (what a module exports); Body is the main expression, Identity for a program of
// definitions only, and nil for a module file.
type Program struct {
	Metadata    *values.Value
	Imports     []*Import
	Definitions []*Definition
	Body        Node
}
