//! The bound program: what the interpreter evaluates. A tree of its own node types rather than
//! the AST plus a side table, because every port can build it bottom-up as plain immutable data:
//! no node is mutated after construction, no map is keyed by node identity, and nothing is cyclic.
//!
//! Names are gone. The environment at run time is a chain of frames, innermost first, and every
//! reference counts frames outward from where it is written:
//! - a frame is pushed by `as` and by the binder of `reduce`/`foreach` (one value slot per
//!   variable of all its patterns), by `label` (the label instance), and by a call of a function
//!   with parameters (one closure slot per parameter);
//! - a variable is (hops, slot), a closure parameter (hops, slot), a `break` hops;
//! - a function call names its definition by index in `BoundProgram::functions` (recursion is an
//!   index, not a cycle), and hops is the number of frames between the call and the definition:
//!   the callee runs in the caller's environment with hops frames removed, plus its parameters.
//!
//! Host variables are the one late-bound name (spec §7.5): each has an index in
//! `BoundProgram::host_variables`, so the host's values can be looked up once per evaluation.
//!
//! Every node also says whether it yields at most one output (`at_most_one`: the single
//! expressions of spec §7.7), computed from its children when it is built: the interpreter
//! evaluates such nodes directly to a value, without callbacks (`Interpreter::eval_one`). A call
//! is single when its function is; the resolver knows that of a function still being defined by
//! assuming it (`Resolver::call`).
//!
//! Rust: one `Node` struct (the flag and the kind) with an enum for what the reference declares
//! as one record per node; the constructors below compute the flag. A bound program is `Send` and
//! `Sync`: it holds values (`Arc`), text and numbers only. A node drops its children without
//! recursion, as the AST does.

use std::sync::Arc;

use crate::builtins::natives::Native;
use crate::syntax::ast::BinaryOp;
use crate::values::{JqString, JqValue};

use super::modules::ModuleTable;

/// A bound node; `at_most_one`: it yields zero or one output whatever its input and environment.
#[derive(Debug)]
pub struct Node {
    pub at_most_one: bool,
    pub kind: NodeKind,
}

#[derive(Debug)]
pub enum NodeKind {
    Identity,
    Literal(JqValue),
    /// An interpolated string; see the AST's `Template`.
    Template {
        format: Option<String>,
        texts: Vec<String>,
        parts: Vec<Node>,
    },
    Format(String),
    Index {
        target: Box<Node>,
        key: Box<Node>,
    },
    Slice {
        target: Box<Node>,
        from: Option<Box<Node>>,
        to: Option<Box<Node>>,
    },
    Iterate {
        target: Box<Node>,
    },
    /// A lexical variable: slot `slot` of the frame `hops` frames out.
    Variable {
        hops: usize,
        slot: usize,
    },
    /// A `$name` without lexical binding: entry `index` of `BoundProgram::host_variables`.
    HostVariable {
        name: String,
        index: usize,
    },
    /// `$ENV` where no binding shadows it.
    EnvVariable,
    /// A call of `BoundProgram::functions[function]`, defined `hops` frames out. `single`: the
    /// function is single and the argument of each of its `$` parameters is (spec §7.7).
    CallFunction {
        function: usize,
        hops: usize,
        arguments: Vec<Node>,
        single: bool,
    },
    /// A call of a closure parameter: slot `slot` of the call frame `hops` frames out.
    CallParameter {
        hops: usize,
        slot: usize,
    },
    /// A call of a native; the arguments are closures over the caller's environment.
    CallNative {
        native: &'static Native,
        arguments: Vec<Node>,
    },
    Pipe {
        left: Box<Node>,
        right: Box<Node>,
    },
    /// `a, b, c`: a left-nested chain of commas is one node, so that evaluating it does not
    /// recurse per term.
    Comma {
        items: Vec<Node>,
    },
    Binary {
        op: BinaryOp,
        left: Box<Node>,
        right: Box<Node>,
    },
    And {
        left: Box<Node>,
        right: Box<Node>,
    },
    Or {
        left: Box<Node>,
        right: Box<Node>,
    },
    Alternative {
        left: Box<Node>,
        right: Box<Node>,
    },
    Negate {
        operand: Box<Node>,
    },
    ArrayExpr {
        body: Option<Box<Node>>,
    },
    ObjectExpr {
        entries: Vec<ObjectEntry>,
    },
    If {
        condition: Box<Node>,
        then: Box<Node>,
        otherwise: Box<Node>,
    },
    Try {
        body: Box<Node>,
        handler: Option<Box<Node>>,
    },
    /// `update` runs with the binder's frame pushed; `init` without it.
    Reduce {
        source: Box<Node>,
        binder: Binder,
        init: Box<Node>,
        update: Box<Node>,
    },
    /// `update` and `extract` run with the binder's frame pushed.
    Foreach {
        source: Box<Node>,
        binder: Binder,
        init: Box<Node>,
        update: Box<Node>,
        extract: Box<Node>,
    },
    /// `source as patterns | body`: `body` runs with the binder's frame pushed.
    Bind {
        source: Box<Node>,
        binder: Binder,
        body: Box<Node>,
    },
    /// `body` runs with a frame holding a new label instance.
    Label {
        body: Box<Node>,
    },
    /// Breaks to the label instance of the frame `hops` frames out.
    Break {
        hops: usize,
    },
}

/// See the AST's `ObjectEntry`: no value is the input indexed by each output of the key.
#[derive(Debug)]
pub struct ObjectEntry {
    pub key: Node,
    pub value: Option<Node>,
}

/// The patterns of one binding (several are the `?//` alternatives) and the size of the frame
/// they fill: one slot per distinct variable name of all of them, null until a pattern binds it.
#[derive(Debug)]
pub struct Binder {
    pub patterns: Vec<Pattern>,
    pub slots: usize,
    /// Every key expression of the patterns yields at most one output, so a value binds at most
    /// once per pattern.
    pub keys_at_most_one: bool,
    /// The binder is `$x` alone: the value is the frame.
    pub is_variable: bool,
}

impl Binder {
    pub fn new(patterns: Vec<Pattern>, slots: usize) -> Binder {
        let keys_at_most_one = patterns.iter().all(keys_of);
        let is_variable = slots == 1 && matches!(patterns.as_slice(), [Pattern::Variable { slot: Some(0) }]);
        Binder { patterns, slots, keys_at_most_one, is_variable }
    }
}

fn keys_of(pattern: &Pattern) -> bool {
    match pattern {
        Pattern::Variable { .. } => true,
        Pattern::Array { elements } => elements.iter().all(keys_of),
        Pattern::Object { entries } => entries.iter().all(|entry| entry.key.at_most_one && entry.value.as_ref().is_none_or(keys_of)),
    }
}

#[derive(Debug)]
pub enum Pattern {
    /// Stores the value in `slot`; `None` discards it. Patterns are matched in order (array
    /// elements from the last, object entries from the first), so a slot written twice keeps the
    /// later value; the resolver gives no slot to the repetitions that must not win (spec §7.5).
    Variable {
        slot: Option<usize>,
    },
    Array {
        elements: Vec<Pattern>,
    },
    Object {
        entries: Vec<PatternEntry>,
    },
}

/// See the AST's `PatternEntry`; `slot` is `None` without a variable. The key runs in the scope
/// outside the binding: the pattern's own variables are not visible in it.
#[derive(Debug)]
pub struct PatternEntry {
    pub key: Node,
    pub slot: Option<usize>,
    pub value: Option<Pattern>,
}

/// A jqlite-defined function (user or prelude). Its body runs with its parameters' frame pushed
/// when `arity` > 0. `single`: the body yields at most one output for each binding of the `$`
/// parameters, whose slots are `value_parameters` (spec §7.7).
#[derive(Debug)]
pub struct Function {
    pub name: String,
    pub arity: usize,
    pub body: Node,
    pub single: bool,
    pub value_parameters: Vec<usize>,
}

impl Function {
    /// `name/arity`.
    pub fn signature(&self) -> String {
        format!("{}/{}", self.name, self.arity)
    }
}

/// A bound program: immutable and shareable between threads. `functions` starts with the
/// prelude's functions (shared by every program), followed by the program's own.
#[derive(Debug)]
pub struct BoundProgram {
    pub functions: Vec<Arc<Function>>,
    pub host_variables: Vec<String>,
    pub body: Node,
    /// The modules loaded, for `modulemeta`; `None` when the host gave no module source (§9.11).
    pub modules: Option<ModuleTable>,
}

fn all(nodes: &[Node]) -> bool {
    nodes.iter().all(|node| node.at_most_one)
}

fn one(node: &Option<Box<Node>>) -> bool {
    node.as_ref().is_none_or(|node| node.at_most_one)
}

/// Whether the arguments in `slots` are all single: what makes a call single (spec §7.7).
pub fn all_single(slots: &[usize], arguments: &[Node]) -> bool {
    slots.iter().all(|slot| arguments[*slot].at_most_one)
}

impl Node {
    fn new(at_most_one: bool, kind: NodeKind) -> Node {
        Node { at_most_one, kind }
    }

    pub fn identity() -> Node {
        Node::new(true, NodeKind::Identity)
    }

    pub fn literal(value: JqValue) -> Node {
        Node::new(true, NodeKind::Literal(value))
    }

    pub fn template(format: Option<String>, texts: Vec<String>, parts: Vec<Node>) -> Node {
        Node::new(all(&parts), NodeKind::Template { format, texts, parts })
    }

    pub fn format(name: String) -> Node {
        Node::new(true, NodeKind::Format(name))
    }

    pub fn index(target: Node, key: Node) -> Node {
        Node::new(target.at_most_one && key.at_most_one, NodeKind::Index { target: Box::new(target), key: Box::new(key) })
    }

    pub fn slice(target: Node, from: Option<Node>, to: Option<Node>) -> Node {
        let (from, to) = (from.map(Box::new), to.map(Box::new));
        Node::new(target.at_most_one && one(&from) && one(&to), NodeKind::Slice { target: Box::new(target), from, to })
    }

    pub fn iterate(target: Node) -> Node {
        Node::new(false, NodeKind::Iterate { target: Box::new(target) })
    }

    pub fn variable(hops: usize, slot: usize) -> Node {
        Node::new(true, NodeKind::Variable { hops, slot })
    }

    pub fn host_variable(name: String, index: usize) -> Node {
        Node::new(true, NodeKind::HostVariable { name, index })
    }

    pub fn env_variable() -> Node {
        Node::new(true, NodeKind::EnvVariable)
    }

    pub fn call_function(function: usize, hops: usize, arguments: Vec<Node>, single: bool) -> Node {
        Node::new(single, NodeKind::CallFunction { function, hops, arguments, single })
    }

    pub fn call_parameter(hops: usize, slot: usize) -> Node {
        Node::new(false, NodeKind::CallParameter { hops, slot })
    }

    pub fn call_native(native: &'static Native, arguments: Vec<Node>) -> Node {
        let single = native.single_when.as_ref().is_some_and(|slots| all_single(slots, &arguments));
        Node::new(single, NodeKind::CallNative { native, arguments })
    }

    pub fn pipe(left: Node, right: Node) -> Node {
        Node::new(left.at_most_one && right.at_most_one, NodeKind::Pipe { left: Box::new(left), right: Box::new(right) })
    }

    pub fn comma(items: Vec<Node>) -> Node {
        Node::new(false, NodeKind::Comma { items })
    }

    pub fn binary(op: BinaryOp, left: Node, right: Node) -> Node {
        Node::new(left.at_most_one && right.at_most_one, NodeKind::Binary { op, left: Box::new(left), right: Box::new(right) })
    }

    pub fn and(left: Node, right: Node) -> Node {
        Node::new(left.at_most_one && right.at_most_one, NodeKind::And { left: Box::new(left), right: Box::new(right) })
    }

    pub fn or(left: Node, right: Node) -> Node {
        Node::new(left.at_most_one && right.at_most_one, NodeKind::Or { left: Box::new(left), right: Box::new(right) })
    }

    pub fn alternative(left: Node, right: Node) -> Node {
        Node::new(left.at_most_one && right.at_most_one, NodeKind::Alternative { left: Box::new(left), right: Box::new(right) })
    }

    pub fn negate(operand: Node) -> Node {
        Node::new(operand.at_most_one, NodeKind::Negate { operand: Box::new(operand) })
    }

    pub fn array_expr(body: Option<Node>) -> Node {
        Node::new(true, NodeKind::ArrayExpr { body: body.map(Box::new) })
    }

    pub fn object_expr(entries: Vec<ObjectEntry>) -> Node {
        let single = entries.iter().all(|entry| entry.key.at_most_one && entry.value.as_ref().is_none_or(|value| value.at_most_one));
        Node::new(single, NodeKind::ObjectExpr { entries })
    }

    pub fn if_then_else(condition: Node, then: Node, otherwise: Node) -> Node {
        Node::new(
            condition.at_most_one && then.at_most_one && otherwise.at_most_one,
            NodeKind::If { condition: Box::new(condition), then: Box::new(then), otherwise: Box::new(otherwise) },
        )
    }

    pub fn try_catch(body: Node, handler: Option<Node>) -> Node {
        let handler = handler.map(Box::new);
        Node::new(body.at_most_one && one(&handler), NodeKind::Try { body: Box::new(body), handler })
    }

    pub fn reduce(source: Node, binder: Binder, init: Node, update: Node) -> Node {
        Node::new(init.at_most_one, NodeKind::Reduce { source: Box::new(source), binder, init: Box::new(init), update: Box::new(update) })
    }

    pub fn foreach(source: Node, binder: Binder, init: Node, update: Node, extract: Node) -> Node {
        Node::new(false, NodeKind::Foreach { source: Box::new(source), binder, init: Box::new(init), update: Box::new(update), extract: Box::new(extract) })
    }

    pub fn bind(source: Node, binder: Binder, body: Node) -> Node {
        Node::new(source.at_most_one && binder.keys_at_most_one && body.at_most_one, NodeKind::Bind { source: Box::new(source), binder, body: Box::new(body) })
    }

    pub fn label(body: Node) -> Node {
        Node::new(body.at_most_one, NodeKind::Label { body: Box::new(body) })
    }

    pub fn break_to(hops: usize) -> Node {
        Node::new(true, NodeKind::Break { hops })
    }

    /// The text of a literal string node (an object key written as a name or a plain string).
    pub fn literal_string(&self) -> Option<&JqString> {
        match &self.kind {
            NodeKind::Literal(JqValue::String(text)) => Some(text),
            _ => None,
        }
    }

    // What stays in a node whose content was taken away.
    fn hollow() -> Node {
        Node::identity()
    }

    // Calls `each` with every child node, the keys of the patterns included.
    fn each_child(&mut self, each: &mut dyn FnMut(&mut Node)) {
        match &mut self.kind {
            NodeKind::Identity
            | NodeKind::Literal(_)
            | NodeKind::Format(_)
            | NodeKind::Variable { .. }
            | NodeKind::HostVariable { .. }
            | NodeKind::EnvVariable
            | NodeKind::CallParameter { .. }
            | NodeKind::Break { .. } => {}
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
            NodeKind::CallFunction { arguments, .. } | NodeKind::CallNative { arguments, .. } => arguments.iter_mut().for_each(each),
            NodeKind::Comma { items } => items.iter_mut().for_each(each),
            NodeKind::Pipe { left, right }
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
            NodeKind::Reduce { source, binder, init, update } => {
                each(source);
                each_key(&mut binder.patterns, each);
                each(init);
                each(update);
            }
            NodeKind::Foreach { source, binder, init, update, extract } => {
                each(source);
                each_key(&mut binder.patterns, each);
                each(init);
                each(update);
                each(extract);
            }
            NodeKind::Bind { source, binder, body } => {
                each(source);
                each_key(&mut binder.patterns, each);
                each(body);
            }
            NodeKind::Label { body } => each(body),
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
        match pattern {
            Pattern::Variable { .. } => {}
            Pattern::Array { elements } => each_key(elements, each),
            Pattern::Object { entries } => {
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
