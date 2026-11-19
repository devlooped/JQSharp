// Package binding resolves the names of a parsed program (spec §7) into the bound program the
// interpreter evaluates, loads modules (§9.11) and holds the prelude and the native catalogue.
package binding

import (
	"github.com/devlooped/jqlite/go/internal/syntax"
	"github.com/devlooped/jqlite/go/internal/values"
)

// The bound program: what the interpreter evaluates. A tree of its own node types rather than the
// AST plus a side table: no node is mutated after construction, no map is keyed by node identity,
// and nothing is cyclic.
//
// Names are gone. The environment at run time is a chain of frames, innermost first, and every
// reference counts frames outward from where it is written:
//   - a frame is pushed by `as` and by the binder of `reduce`/`foreach` (one value slot per
//     variable of all its patterns), by `label` (the label instance), and by a call of a function
//     with parameters (one closure slot per parameter);
//   - a variable is (Hops, Slot), a closure parameter (Hops, Slot), a `break` Hops;
//   - a function call names its definition by index in BoundProgram.Functions (recursion is an
//     index, not a cycle), and Hops is the number of frames between the call and the definition:
//     the callee runs in the caller's environment with Hops frames removed, plus its parameters.
//
// Host variables are the one late-bound name (spec §7.5): each has an index in
// BoundProgram.HostVariables, so the host's values can be looked up once per evaluation.
//
// Every node also says whether it yields at most one output (AtMostOne: the single expressions of
// spec §7.7), computed from its children when it is built: the interpreter evaluates such nodes
// directly to a value, without callbacks (Interpreter.EvalOne). A call is single when its function
// is; the resolver knows that of a function still being defined by assuming it (Resolver.call).

// NodeKind is the kind of a bound node.
type NodeKind int

// The kinds of bound nodes.
const (
	NodeIdentity NodeKind = iota
	NodeLiteral
	NodeTemplate
	NodeFormat
	NodeIndex
	NodeSlice
	NodeIterate
	NodeVariable
	NodeHostVariable
	NodeEnvVariable
	NodeCallFunction
	NodeCallParameter
	NodeCallNative
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
	NodeLabel
	NodeBreak
)

// Node is a bound node; AtMostOne: it yields zero or one output whatever its input and environment.
type Node interface {
	Kind() NodeKind
	AtMostOne() bool
}

// NodeBase is the kind and the AtMostOne flag of every bound node.
type NodeBase struct {
	kind      NodeKind
	atMostOne bool
}

// Kind gives the kind of the node.
func (n *NodeBase) Kind() NodeKind { return n.kind }

// AtMostOne tells whether the node yields at most one output.
func (n *NodeBase) AtMostOne() bool { return n.atMostOne }

func all(nodes []Node) bool {
	for _, node := range nodes {
		if !node.AtMostOne() {
			return false
		}
	}
	return true
}

func one(node Node) bool { return node == nil || node.AtMostOne() }

// AllSingle tells whether the arguments in slots are all single: what makes a call single (spec §7.7).
func AllSingle(slots []int, arguments []Node) bool {
	for _, slot := range slots {
		if !arguments[slot].AtMostOne() {
			return false
		}
	}
	return true
}

// Identity is `.`.
type Identity struct{ NodeBase }

// NewIdentity builds `.`.
func NewIdentity() *Identity { return &Identity{NodeBase{NodeIdentity, true}} }

// Literal is a constant.
type Literal struct {
	NodeBase
	Value values.Value
}

// NewLiteral builds a constant.
func NewLiteral(value values.Value) *Literal { return &Literal{NodeBase{NodeLiteral, true}, value} }

// Template is an interpolated string; see syntax.Template.
type Template struct {
	NodeBase
	Format    string
	HasFormat bool
	Texts     []string
	Parts     []Node
}

// NewTemplate builds an interpolated string.
func NewTemplate(format string, hasFormat bool, texts []string, parts []Node) *Template {
	return &Template{NodeBase{NodeTemplate, all(parts)}, format, hasFormat, texts, parts}
}

// Format is @name alone.
type Format struct {
	NodeBase
	Name string
}

// NewFormat builds @name.
func NewFormat(name string) *Format { return &Format{NodeBase{NodeFormat, true}, name} }

// Index is Target[Key].
type Index struct {
	NodeBase
	Target Node
	Key    Node
}

// NewIndex builds Target[Key].
func NewIndex(target, key Node) *Index {
	return &Index{NodeBase{NodeIndex, target.AtMostOne() && key.AtMostOne()}, target, key}
}

// Slice is Target[From:To]; a missing bound is nil.
type Slice struct {
	NodeBase
	Target Node
	From   Node
	To     Node
}

// NewSlice builds Target[From:To].
func NewSlice(target, from, to Node) *Slice {
	return &Slice{NodeBase{NodeSlice, target.AtMostOne() && one(from) && one(to)}, target, from, to}
}

// Iterate is Target[].
type Iterate struct {
	NodeBase
	Target Node
}

// NewIterate builds Target[].
func NewIterate(target Node) *Iterate { return &Iterate{NodeBase{NodeIterate, false}, target} }

// Variable is a lexical variable: slot Slot of the frame Hops frames out.
type Variable struct {
	NodeBase
	Hops int
	Slot int
}

// NewVariable builds a lexical variable.
func NewVariable(hops, slot int) *Variable {
	return &Variable{NodeBase{NodeVariable, true}, hops, slot}
}

// HostVariable is a $name without lexical binding: entry Index of BoundProgram.HostVariables.
type HostVariable struct {
	NodeBase
	Name  string
	Index int
}

// NewHostVariable builds a host variable.
func NewHostVariable(name string, index int) *HostVariable {
	return &HostVariable{NodeBase{NodeHostVariable, true}, name, index}
}

// EnvVariable is $ENV where no binding shadows it.
type EnvVariable struct{ NodeBase }

// NewEnvVariable builds $ENV.
func NewEnvVariable() *EnvVariable { return &EnvVariable{NodeBase{NodeEnvVariable, true}} }

// CallFunction is a call of BoundProgram.Functions[Function], defined Hops frames out. Single:
// the function is single and the argument of each of its $ parameters is (spec §7.7).
type CallFunction struct {
	NodeBase
	Function  int
	Hops      int
	Arguments []Node
	Single    bool
}

// NewCallFunction builds a call of a jqlite function.
func NewCallFunction(function, hops int, arguments []Node, single bool) *CallFunction {
	return &CallFunction{NodeBase{NodeCallFunction, single}, function, hops, arguments, single}
}

// CallParameter is a call of a closure parameter: slot Slot of the call frame Hops frames out.
type CallParameter struct {
	NodeBase
	Hops int
	Slot int
}

// NewCallParameter builds a call of a closure parameter.
func NewCallParameter(hops, slot int) *CallParameter {
	return &CallParameter{NodeBase{NodeCallParameter, false}, hops, slot}
}

// CallNative is a call of a native; the arguments are closures over the caller's environment.
type CallNative struct {
	NodeBase
	Native    *Native
	Arguments []Node
}

// NewCallNative builds a call of a native.
func NewCallNative(native *Native, arguments []Node) *CallNative {
	single := native.SingleWhen != nil && AllSingle(native.SingleWhen, arguments)
	return &CallNative{NodeBase{NodeCallNative, single}, native, arguments}
}

// Pipe is Left | Right.
type Pipe struct {
	NodeBase
	Left  Node
	Right Node
}

// NewPipe builds Left | Right.
func NewPipe(left, right Node) *Pipe {
	return &Pipe{NodeBase{NodePipe, left.AtMostOne() && right.AtMostOne()}, left, right}
}

// Comma is a, b, c: a left-nested chain of commas is one node, so that evaluating it does not
// recurse per term.
type Comma struct {
	NodeBase
	Items []Node
}

// NewComma builds a chain of commas.
func NewComma(items []Node) *Comma { return &Comma{NodeBase{NodeComma, false}, items} }

// Binary is an arithmetic operator or a comparison.
type Binary struct {
	NodeBase
	Op    syntax.BinaryOp
	Left  Node
	Right Node
}

// NewBinary builds a binary operator.
func NewBinary(op syntax.BinaryOp, left, right Node) *Binary {
	return &Binary{NodeBase{NodeBinary, left.AtMostOne() && right.AtMostOne()}, op, left, right}
}

// And is Left and Right.
type And struct {
	NodeBase
	Left  Node
	Right Node
}

// NewAnd builds Left and Right.
func NewAnd(left, right Node) *And {
	return &And{NodeBase{NodeAnd, left.AtMostOne() && right.AtMostOne()}, left, right}
}

// Or is Left or Right.
type Or struct {
	NodeBase
	Left  Node
	Right Node
}

// NewOr builds Left or Right.
func NewOr(left, right Node) *Or {
	return &Or{NodeBase{NodeOr, left.AtMostOne() && right.AtMostOne()}, left, right}
}

// Alternative is Left // Right.
type Alternative struct {
	NodeBase
	Left  Node
	Right Node
}

// NewAlternative builds Left // Right.
func NewAlternative(left, right Node) *Alternative {
	return &Alternative{NodeBase{NodeAlternative, left.AtMostOne() && right.AtMostOne()}, left, right}
}

// Negate is -Operand.
type Negate struct {
	NodeBase
	Operand Node
}

// NewNegate builds -Operand.
func NewNegate(operand Node) *Negate {
	return &Negate{NodeBase{NodeNegate, operand.AtMostOne()}, operand}
}

// ArrayExpr is [Body]; [] has a nil body.
type ArrayExpr struct {
	NodeBase
	Body Node
}

// NewArrayExpr builds [Body].
func NewArrayExpr(body Node) *ArrayExpr { return &ArrayExpr{NodeBase{NodeArrayExpr, true}, body} }

// ObjectExpr is {…}.
type ObjectExpr struct {
	NodeBase
	Entries []ObjectEntry
}

// ObjectEntry is an entry of an object construction; see syntax.ObjectEntry: a nil value is the
// input indexed by each output of the key.
type ObjectEntry struct {
	Key   Node
	Value Node
}

// NewObjectExpr builds {…}.
func NewObjectExpr(entries []ObjectEntry) *ObjectExpr {
	atMostOne := true
	for _, entry := range entries {
		if !entry.Key.AtMostOne() || !one(entry.Value) {
			atMostOne = false
			break
		}
	}
	return &ObjectExpr{NodeBase{NodeObjectExpr, atMostOne}, entries}
}

// If is if Condition then Then else Else end.
type If struct {
	NodeBase
	Condition Node
	Then      Node
	Else      Node
}

// NewIf builds a conditional.
func NewIf(condition, then, otherwise Node) *If {
	return &If{NodeBase{NodeIf, condition.AtMostOne() && then.AtMostOne() && otherwise.AtMostOne()}, condition, then, otherwise}
}

// Try is try Body catch Handler; a nil handler discards the error.
type Try struct {
	NodeBase
	Body    Node
	Handler Node
}

// NewTry builds try Body catch Handler.
func NewTry(body, handler Node) *Try {
	return &Try{NodeBase{NodeTry, body.AtMostOne() && one(handler)}, body, handler}
}

// Reduce is reduce: Update runs with the binder's frame pushed; Init without it.
type Reduce struct {
	NodeBase
	Source Node
	Binder *Binder
	Init   Node
	Update Node
}

// NewReduce builds a reduce.
func NewReduce(source Node, binder *Binder, init, update Node) *Reduce {
	return &Reduce{NodeBase{NodeReduce, init.AtMostOne()}, source, binder, init, update}
}

// Foreach is foreach: Update and Extract run with the binder's frame pushed.
type Foreach struct {
	NodeBase
	Source  Node
	Binder  *Binder
	Init    Node
	Update  Node
	Extract Node
}

// NewForeach builds a foreach.
func NewForeach(source Node, binder *Binder, init, update, extract Node) *Foreach {
	return &Foreach{NodeBase{NodeForeach, false}, source, binder, init, update, extract}
}

// Bind is Source as Patterns | Body: Body runs with the binder's frame pushed.
type Bind struct {
	NodeBase
	Source Node
	Binder *Binder
	Body   Node
}

// NewBind builds a binding.
func NewBind(source Node, binder *Binder, body Node) *Bind {
	return &Bind{NodeBase{NodeBind, source.AtMostOne() && binder.KeysAtMostOne && body.AtMostOne()}, source, binder, body}
}

// Label is label: Body runs with a frame holding a new label instance.
type Label struct {
	NodeBase
	Body Node
}

// NewLabel builds a label.
func NewLabel(body Node) *Label { return &Label{NodeBase{NodeLabel, body.AtMostOne()}, body} }

// Break breaks to the label instance of the frame Hops frames out.
type Break struct {
	NodeBase
	Hops int
}

// NewBreak builds a break.
func NewBreak(hops int) *Break { return &Break{NodeBase{NodeBreak, true}, hops} }

// Binder is the patterns of one binding (several are the ?// alternatives) and the size of the
// frame they fill: one slot per distinct variable name of all of them, null until a pattern binds
// it. KeysAtMostOne: every key expression of the patterns yields at most one output, so a value
// binds at most once per pattern.
type Binder struct {
	Patterns      []Pattern
	Slots         int
	KeysAtMostOne bool
}

// NewBinder builds a binder.
func NewBinder(patterns []Pattern, slots int) *Binder {
	return &Binder{patterns, slots, allKeys(patterns)}
}

func allKeys(patterns []Pattern) bool {
	for _, pattern := range patterns {
		if !keysOf(pattern) {
			return false
		}
	}
	return true
}

func keysOf(pattern Pattern) bool {
	switch p := pattern.(type) {
	case *ArrayPattern:
		return allKeys(p.Elements)
	case *ObjectPattern:
		for _, entry := range p.Entries {
			if !entry.Key.AtMostOne() || (entry.Value != nil && !keysOf(entry.Value)) {
				return false
			}
		}
	}
	return true
}

// PatternKind is the kind of a bound pattern.
type PatternKind int

// The kinds of bound patterns.
const (
	PatternVariable PatternKind = iota
	PatternArray
	PatternObject
)

// Pattern is a bound pattern: *VariablePattern, *ArrayPattern or *ObjectPattern.
type Pattern interface{ PatternKind() PatternKind }

// VariablePattern stores the value in Slot; -1 discards it. Patterns are matched in order (array
// elements from the last, object entries from the first), so a slot written twice keeps the later
// value; the resolver gives -1 to the repetitions that must not win (spec §7.5).
type VariablePattern struct{ Slot int }

// ArrayPattern matches element i against .[i].
type ArrayPattern struct{ Elements []Pattern }

// ObjectPattern matches its entries.
type ObjectPattern struct{ Entries []PatternEntry }

func (*VariablePattern) PatternKind() PatternKind { return PatternVariable }
func (*ArrayPattern) PatternKind() PatternKind    { return PatternArray }
func (*ObjectPattern) PatternKind() PatternKind   { return PatternObject }

// PatternEntry is an entry of an object pattern; see syntax.PatternEntry; Slot is -1 without a
// variable. The key runs in the scope outside the binding: the pattern's own variables are not
// visible in it.
type PatternEntry struct {
	Key   Node
	Slot  int
	Value Pattern
}

// Function is a jqlite-defined function (user or prelude). Its body runs with its parameters'
// frame pushed when Arity > 0. Single: the body yields at most one output for each binding of the
// $ parameters, whose slots are ValueParameters (spec §7.7).
type Function struct {
	Name            string
	Arity           int
	Body            Node
	Single          bool
	ValueParameters []int
}

func (f *Function) String() string { return f.Name + "/" + itoa(f.Arity) }

// BoundProgram is a bound program: immutable and shareable between goroutines. Functions starts
// with the prelude's functions (shared by every program), followed by the program's own. Modules
// are the modules loaded, for modulemeta; nil when the host gave no module source (§9.11).
type BoundProgram struct {
	Functions     []*Function
	HostVariables []string
	Body          Node
	Modules       *ModuleTable
}
