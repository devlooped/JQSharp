//! The abstract syntax tree: plain data, no behaviour, no back-pointers. Every node and pattern
//! carries the position of the token that introduces it (its keyword, operator, bracket or name).
//! Names stay as written (without '$', '.' or '@'; a qualified name keeps its "::"): the resolver
//! binds them. Forms the spec defines by rewriting (§3.3) are already rewritten by the parser (see
//! the list at the top of parser.rs), so the node set below is all an interpreter sees.
//!
//! Rust: one `Node` struct (position and kind) with an enum for what the reference declares as
//! one record per node. A node drops its children without recursion (`Drop for Node`): a long
//! pipe is a chain as deep as it is long, which the compiler's own drop would walk recursively.

use crate::values::JqValue;

/// A 1-based source position; columns count code points.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Position {
    pub line: u32,
    pub column: u32,
}

impl Position {
    pub fn new(line: u32, column: u32) -> Position {
        Position { line, column }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BinaryOp {
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

#[derive(Debug)]
pub struct Node {
    pub position: Position,
    pub kind: NodeKind,
}

#[derive(Debug)]
pub enum NodeKind {
    /// `.`
    Identity,
    /// A constant: a number, a string without interpolation, `null`, `true`, `false`.
    Literal(JqValue),
    /// A string with interpolations: `texts[0] parts[0] texts[1] … parts[n-1] texts[n]`, so
    /// `texts.len() == parts.len() + 1`. Each part's outputs are converted with `format` (None:
    /// `tostring`); the texts are taken as they are.
    Template { format: Option<String>, texts: Vec<String>, parts: Vec<Node> },
    /// `@name` alone: the format applied to the input.
    Format(String),
    /// `target[key]`; also `.name` and `."s"`. `key` runs on the input of `target`.
    Index { target: Box<Node>, key: Box<Node> },
    /// `target[from:to]`; a missing bound is null.
    Slice { target: Box<Node>, from: Option<Box<Node>>, to: Option<Box<Node>> },
    /// `target[]`
    Iterate { target: Box<Node> },
    /// `$name` or `$alias::name`.
    Variable(String),
    /// `name` or `name(a; b)`: a function, closure parameter or builtin of arity `arguments.len()`.
    Call { name: String, arguments: Vec<Node> },
    /// `left | right`
    Pipe { left: Box<Node>, right: Box<Node> },
    /// `left, right`
    Comma { left: Box<Node>, right: Box<Node> },
    /// An arithmetic operator or a comparison: one output per pair of outputs of the operands.
    Binary { op: BinaryOp, left: Box<Node>, right: Box<Node> },
    /// `left and right`
    And { left: Box<Node>, right: Box<Node> },
    /// `left or right`
    Or { left: Box<Node>, right: Box<Node> },
    /// `left // right`
    Alternative { left: Box<Node>, right: Box<Node> },
    /// Unary `-operand`.
    Negate { operand: Box<Node> },
    /// `[body]`; `[]` has no body.
    ArrayExpr { body: Option<Box<Node>> },
    /// `{…}`
    ObjectExpr { entries: Vec<ObjectEntry> },
    /// `if condition then then else otherwise end`; `elif` nests, a missing else is `Identity`.
    If { condition: Box<Node>, then: Box<Node>, otherwise: Box<Node> },
    /// `try body catch handler`; also `body?`. No handler discards the error.
    Try { body: Box<Node>, handler: Option<Box<Node>> },
    /// `reduce source as patterns (init; update)`
    Reduce { source: Box<Node>, patterns: Vec<Pattern>, init: Box<Node>, update: Box<Node> },
    /// `foreach source as patterns (init; update; extract)`; a missing extract is `Identity`.
    Foreach { source: Box<Node>, patterns: Vec<Pattern>, init: Box<Node>, update: Box<Node>, extract: Box<Node> },
    /// `source as patterns | body`. Several patterns are the `?//` alternatives.
    Bind { source: Box<Node>, patterns: Vec<Pattern>, body: Box<Node> },
    /// `def …; rest`: the definition is visible in its own body and in `rest`.
    FuncDef { definition: Box<Definition>, rest: Box<Node> },
    /// `label $name | body`
    Label { name: String, body: Box<Node> },
    /// `break $name`
    Break { name: String },
}

/// One entry of an object construction. `key` is the key expression (a literal for `name:` and
/// `"s":`, a template for interpolated keys, a variable for `$x:`, any expression for `(e):`).
/// No `value` is the shorthand `{"s"}`, `{name}`, `{@f "s"}`: the value is the input indexed by
/// each key, evaluated once per key. (`{$x}` is rewritten to the key `"x"` and the value `$x`.)
#[derive(Debug)]
pub struct ObjectEntry {
    pub key: Node,
    pub value: Option<Node>,
    pub position: Position,
}

/// `def name(params): body;`. Every parameter is a filter parameter; a `$name` parameter
/// (`is_variable`) also binds `$name` to each output of its own argument, first parameter
/// outermost (§3.3). The resolver does that binding by position, since a name may repeat: in
/// `def f($a; a)` the filter `a` is the second parameter and `$a` the first.
#[derive(Debug)]
pub struct Definition {
    pub name: String,
    pub params: Vec<Param>,
    pub body: Node,
    pub position: Position,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Param {
    pub name: String,
    pub is_variable: bool,
    pub position: Position,
}

#[derive(Debug)]
pub struct Pattern {
    pub position: Position,
    pub kind: PatternKind,
}

#[derive(Debug)]
pub enum PatternKind {
    /// `$name`
    Variable(String),
    /// `[P0, P1, …]`: element i is matched against `.[i]`.
    Array(Vec<Pattern>),
    /// `{…}`
    Object(Vec<PatternEntry>),
}

/// One entry of an object pattern: for each output of `key`, the value `.[key]` is bound to
/// `variable` (when present) and matched against `value` (when present). `$name` is the key
/// `"name"` with the variable `name`; `$name: P` adds the pattern; `name: P`, `"s": P` and
/// `(e): P` have no variable.
#[derive(Debug)]
pub struct PatternEntry {
    pub key: Node,
    pub variable: Option<String>,
    pub value: Option<Pattern>,
    pub position: Position,
}

/// `import "path" as alias metadata;`, `import "path" as $alias …;` (data) or `include "path" …;`
/// (no alias).
#[derive(Clone, Debug)]
pub struct Import {
    pub path: String,
    pub alias: Option<String>,
    pub is_data: bool,
    pub metadata: Option<JqValue>,
    pub position: Position,
}

/// A parsed program or module file. `definitions` are the top-level definitions in order (what a
/// module exports); `body` is the main expression, `Identity` for a program of definitions only,
/// and `None` for a module file.
#[derive(Debug)]
pub struct Program {
    pub metadata: Option<JqValue>,
    pub imports: Vec<Import>,
    pub definitions: Vec<Definition>,
    pub body: Option<Node>,
}

impl Node {
    pub fn new(kind: NodeKind, position: Position) -> Node {
        Node { position, kind }
    }

    // What stays in a node whose content was taken away.
    fn hollow() -> Node {
        Node { position: Position::default(), kind: NodeKind::Identity }
    }

    // Calls `each` with every child node, the keys of the patterns included.
    fn each_child(&mut self, each: &mut dyn FnMut(&mut Node)) {
        match &mut self.kind {
            NodeKind::Identity | NodeKind::Literal(_) | NodeKind::Format(_) | NodeKind::Variable(_) | NodeKind::Break { .. } => {}
            NodeKind::Template { parts, .. } => parts.iter_mut().for_each(each),
            NodeKind::Index { target, key } => {
                each(target);
                each(key);
            }
            NodeKind::Slice { target, from, to } => {
                each(target);
                if let Some(from) = from {
                    each(from);
                }
                if let Some(to) = to {
                    each(to);
                }
            }
            NodeKind::Iterate { target } => each(target),
            NodeKind::Call { arguments, .. } => arguments.iter_mut().for_each(each),
            NodeKind::Pipe { left, right }
            | NodeKind::Comma { left, right }
            | NodeKind::Binary { left, right, .. }
            | NodeKind::And { left, right }
            | NodeKind::Or { left, right }
            | NodeKind::Alternative { left, right } => {
                each(left);
                each(right);
            }
            NodeKind::Negate { operand } => each(operand),
            NodeKind::ArrayExpr { body } => {
                if let Some(body) = body {
                    each(body);
                }
            }
            NodeKind::ObjectExpr { entries } => {
                for entry in entries {
                    each(&mut entry.key);
                    if let Some(value) = &mut entry.value {
                        each(value);
                    }
                }
            }
            NodeKind::If { condition, then, otherwise } => {
                each(condition);
                each(then);
                each(otherwise);
            }
            NodeKind::Try { body, handler } => {
                each(body);
                if let Some(handler) = handler {
                    each(handler);
                }
            }
            NodeKind::Reduce { source, patterns, init, update } => {
                each(source);
                each_key(patterns, each);
                each(init);
                each(update);
            }
            NodeKind::Foreach { source, patterns, init, update, extract } => {
                each(source);
                each_key(patterns, each);
                each(init);
                each(update);
                each(extract);
            }
            NodeKind::Bind { source, patterns, body } => {
                each(source);
                each_key(patterns, each);
                each(body);
            }
            NodeKind::FuncDef { definition, rest } => {
                each(&mut definition.body);
                each(rest);
            }
            NodeKind::Label { body, .. } => each(body),
        }
    }

    fn has_children(&mut self) -> bool {
        let mut any = false;
        self.each_child(&mut |_| any = true);
        any
    }
}

// The key expressions of patterns; patterns nest no deeper than the parser's limit.
fn each_key(patterns: &mut [Pattern], each: &mut dyn FnMut(&mut Node)) {
    for pattern in patterns {
        match &mut pattern.kind {
            PatternKind::Variable(_) => {}
            PatternKind::Array(elements) => each_key(elements, each),
            PatternKind::Object(entries) => {
                for entry in entries {
                    each(&mut entry.key);
                    if let Some(value) = &mut entry.value {
                        each_key(std::slice::from_mut(value), each);
                    }
                }
            }
        }
    }
}

impl Drop for Node {
    fn drop(&mut self) {
        // A node whose children have no children is dropped as it is.
        let mut deep = false;
        self.each_child(&mut |child| deep = deep || child.has_children());
        if !deep {
            return;
        }

        // The others give their children to a list, so that no drop recurses.
        let mut pending: Vec<Node> = Vec::new();
        self.each_child(&mut |child| pending.push(std::mem::replace(child, Node::hollow())));
        while let Some(mut node) = pending.pop() {
            node.each_child(&mut |child| {
                if child.has_children() {
                    pending.push(std::mem::replace(child, Node::hollow()));
                }
            });
        }
    }
}
