//! Binds a parsed program (spec §7): walks the AST once and resolves every variable, closure
//! parameter, function call and label to its definition, producing the `BoundProgram` described
//! at the top of bound.rs. Unknown names are binding errors with a position; nothing panics.
//!
//! The scope is a stack of entries searched from the top, so the innermost definition wins. A
//! call resolves to the innermost user definition or closure parameter of that name and arity,
//! then to the prelude, then to the natives (§7.7). Entries record the frame depth at which they
//! live, so the distance to a definition is the current depth minus the entry's.
//!
//! The walk does not recurse: the AST is up to 1,000 levels deep through nesting and 10,000 or
//! more along sequences (the parser folds `|`, `,`, operators, suffixes, `as`, `def` and `label`
//! chains iteratively), and between them an operand chain of each precedence level can add a
//! level, which no host stack budget covers in every language. So the walk is a loop over a stack
//! of tasks: `visit` resolves what it can at once and pushes the node's children, the scope
//! changes between them and the final `build`; bound children wait on a stack of results. Only
//! patterns are walked recursively, one small frame per level of pattern nesting, which the
//! parser limits.

use std::collections::HashSet;
use std::sync::Arc;

use crate::builtins::natives::{self, Native};
use crate::syntax::ast as s;
use crate::syntax::ast::Position;
use crate::syntax::parser::{self, Parser};
use crate::syntax::syntax_error::SyntaxError;
use crate::values::{JqObject, JqValue};

use super::bound::{Binder, BoundProgram, Function, Node, ObjectEntry, Pattern, PatternEntry, all_single};
use super::modules::{ModuleLoader, ModuleSource};
use super::prelude::Prelude;

#[derive(Clone, Copy, PartialEq, Eq)]
pub(super) enum EntryKind {
    Variable,
    Parameter,
    Label,
    Function,
    Constant,
}

// Variable, Parameter, Label: depth is the frame holding it, slot its slot there.
// Constant: a data module's value (modules.rs), constants[slot].
// Function: depth is the number of frames where it is defined, slot its index in functions,
// top_level whether it is a top-level definition of the program or module.
pub(super) struct Entry {
    pub(super) kind: EntryKind,
    pub(super) name: String,
    pub(super) arity: usize,
    pub(super) depth: usize,
    pub(super) slot: usize,
    pub(super) top_level: bool,
}

enum Task<'a> {
    // Resolve the node, or schedule its children and its build.
    Visit(&'a s::Node),
    // Pop the bound children of the node and push the bound node; the count is the number of
    // items of a comma.
    Build(&'a s::Node, usize),
    // Pop the arguments of a call and push the call of functions[function], defined hops frames out.
    BuildCall { arguments: usize, function: usize, hops: usize },
    // Pop the arguments of a call and push the call of the native.
    BuildNative { arguments: usize, native: &'static Native },
    // Pop the bound pattern keys of the binding node and push its binder.
    MakeBinder(&'a s::Node),
    // Push the frame of the binder on top of binders.
    EnterBinder,
    // Define the definition at this point of the scope.
    Define { definition: &'a s::Definition, top_level: bool },
    // Pop the body of the definition, which is functions[index].
    EndFunction { definition: &'a s::Definition, index: usize },
    // Restore the scope to that many entries and the depth.
    Leave { scope: usize, depth: usize },
}

// A function whose body is being bound. single: whether its calls of itself in that body are
// bound as single (spec §7.7): assumed at first, and checked when the body is bound, unless the
// answer is known already. self_calls counts the calls bound as single on that assumption.
struct Defining {
    index: usize,
    value_parameters: Vec<usize>,
    single: bool,
    known: bool,
    self_calls: usize,
}

/// What the main program and its modules share (modules.rs): one function table, one list of
/// host variables, one list of constants.
#[derive(Default)]
pub(super) struct Shared {
    pub(super) functions: Vec<Option<Arc<Function>>>,
    pub(super) host_variables: Vec<String>,
    pub(super) constants: Vec<JqValue>,
}

pub struct Resolver<'a> {
    pub(super) prelude: Option<&'static Prelude>,
    // Whether builtins with a native override bind to it (Native::overrides_prelude).
    pub(super) native_overrides: bool,
    pub(super) scope: Vec<Entry>,
    pub(super) shared: Shared,
    pub(super) exports: Vec<usize>,
    tasks: Vec<Task<'a>>,
    results: Vec<Node>,
    // The binders made and not yet built into their node, with the names of their slots.
    binders: Vec<(Binder, Vec<String>)>,
    // The functions being defined, innermost last.
    defining: Vec<Defining>,
    // While the body of a function is bound a second time (end_function): whether each function
    // nested in it is single, as the first time found, by index from replay_start.
    replay: Option<Vec<bool>>,
    replay_start: usize,
    depth: usize,
    pub(super) error: Option<SyntaxError>,
    // The modules of this binding (shared by the main program and every module), and the identity
    // of the module this resolver binds (None for the main program and the prelude).
    pub(super) loader: Option<ModuleLoader>,
    pub(super) module: Option<String>,
}

impl<'a> Resolver<'a> {
    pub(super) fn new(prelude: Option<&'static Prelude>, native_overrides: bool, loader: Option<ModuleLoader>, shared: Option<Shared>) -> Resolver<'a> {
        let shared = shared.unwrap_or_else(|| {
            let mut shared = Shared::default();
            if let Some(prelude) = prelude {
                shared.functions.extend(prelude.functions.iter().map(|function| Some(function.clone())));
            }
            shared
        });
        Resolver {
            prelude,
            native_overrides,
            scope: Vec::new(),
            shared,
            exports: Vec::new(),
            tasks: Vec::new(),
            results: Vec::new(),
            binders: Vec::new(),
            defining: Vec::new(),
            replay: None,
            replay_start: 0,
            depth: 0,
            error: None,
            loader,
            module: None,
        }
    }

    /// Binds a program against the prelude and the natives, loading its modules from `modules`
    /// (§9.11). Without `native_overrides` every prelude builtin binds to its definition, even
    /// where a native overrides it.
    pub fn try_bind(program: &'a s::Program, native_overrides: bool, modules: Option<Arc<dyn ModuleSource>>) -> Result<BoundProgram, SyntaxError> {
        let prelude = if native_overrides { Prelude::instance() } else { Prelude::without_overrides() };
        let mut resolver = Resolver::new(Some(prelude), native_overrides, Some(ModuleLoader::new(modules)), None);
        match resolver.bind_program(program) {
            Some(body) => Ok(BoundProgram {
                functions: resolver.functions(),
                host_variables: std::mem::take(&mut resolver.shared.host_variables),
                body,
                modules: resolver.loader.as_ref().and_then(ModuleLoader::table),
            }),
            None => Err(resolver.take_error()),
        }
    }

    /// Parses and binds a program.
    pub fn try_compile(source: &str, native_overrides: bool, modules: Option<Arc<dyn ModuleSource>>) -> Result<BoundProgram, SyntaxError> {
        let program = Parser::try_parse(source)?;
        Resolver::try_bind(&program, native_overrides, modules)
    }

    /// Binds the prelude module against the natives only: all its functions, and its top-level
    /// definitions in order.
    #[allow(clippy::type_complexity)]
    pub(super) fn try_bind_prelude(module: &'a s::Program, native_overrides: bool) -> Result<(Vec<Arc<Function>>, Vec<Arc<Function>>), SyntaxError> {
        let mut resolver = Resolver::new(None, native_overrides, None, None);
        if resolver.bind_program(module).is_none() {
            return Err(resolver.take_error());
        }

        let functions = resolver.functions();
        let definitions = resolver.exports.iter().map(|index| functions[*index].clone()).collect();
        Ok((functions, definitions))
    }

    pub(super) fn take_error(&mut self) -> SyntaxError {
        self.error.take().unwrap_or_else(|| SyntaxError::new("Binding error", 1, 1))
    }

    // The functions bound, which leave the resolver: its work is done.
    fn functions(&mut self) -> Vec<Arc<Function>> {
        let bound = std::mem::take(&mut self.shared.functions);
        let mut functions = Vec::with_capacity(bound.len());
        functions.extend(bound.into_iter().flatten());
        functions
    }

    // The body of a program (Identity for a module, whose result is its definitions), or None.
    pub(super) fn bind_program(&mut self, program: &'a s::Program) -> Option<Node> {
        if !self.bind_imports(&program.imports) {
            return None;
        }

        // Pushed last first: the definitions in order, then the body.
        match &program.body {
            None => self.results.push(Node::identity()),
            Some(body) => self.tasks.push(Task::Visit(body)),
        }
        for definition in program.definitions.iter().rev() {
            self.tasks.push(Task::Define { definition, top_level: true });
        }

        while self.error.is_none() {
            let Some(task) = self.tasks.pop() else {
                break;
            };
            self.run(task);
        }

        if self.error.is_some() {
            return None;
        }
        self.results.pop()
    }

    fn run(&mut self, task: Task<'a>) {
        match task {
            Task::Visit(node) => self.visit(node),
            Task::Build(node, count) => {
                let built = self.build(node, count);
                self.results.push(built);
            }
            Task::BuildCall { arguments, function, hops } => {
                let arguments = self.pop_all(arguments);
                let call = self.call(function, hops, arguments);
                self.results.push(call);
            }
            Task::BuildNative { arguments, native } => {
                let arguments = self.pop_all(arguments);
                self.results.push(Node::call_native(native, arguments));
            }
            Task::MakeBinder(node) => self.make_binder(node),
            Task::EnterBinder => self.enter_binder(),
            Task::Define { definition, top_level } => self.define(definition, top_level),
            Task::EndFunction { definition, index } => self.end_function(definition, index),
            Task::Leave { scope, depth } => {
                self.scope.truncate(scope);
                self.depth = depth;
            }
        }
    }

    // Tasks run last pushed first, so each case pushes its steps in reverse order.
    fn visit(&mut self, node: &'a s::Node) {
        match &node.kind {
            s::NodeKind::Identity => self.results.push(Node::identity()),
            s::NodeKind::Literal(value) => self.results.push(Node::literal(value.clone())),
            s::NodeKind::Format(name) => self.results.push(Node::format(name.clone())),
            s::NodeKind::Variable(name) => {
                if let Some(bound) = self.bind_variable(name, node.position) {
                    self.results.push(bound);
                }
            }
            s::NodeKind::Break { name } => {
                if let Some(bound) = self.bind_break(name, node.position) {
                    self.results.push(bound);
                }
            }
            s::NodeKind::Call { name, arguments } => self.visit_call(name, arguments, node.position),
            s::NodeKind::Template { parts, .. } => {
                self.tasks.push(Task::Build(node, 0));
                self.push_all(parts);
            }
            s::NodeKind::Pipe { left, right }
            | s::NodeKind::Binary { left, right, .. }
            | s::NodeKind::And { left, right }
            | s::NodeKind::Or { left, right }
            | s::NodeKind::Alternative { left, right } => {
                self.tasks.push(Task::Build(node, 0));
                self.tasks.push(Task::Visit(right));
                self.tasks.push(Task::Visit(left));
            }
            s::NodeKind::Comma { .. } => self.visit_comma(node),
            s::NodeKind::Negate { operand } => {
                self.tasks.push(Task::Build(node, 0));
                self.tasks.push(Task::Visit(operand));
            }
            s::NodeKind::Index { target, key } => {
                self.tasks.push(Task::Build(node, 0));
                self.tasks.push(Task::Visit(key));
                self.tasks.push(Task::Visit(target));
            }
            s::NodeKind::Slice { target, from, to } => {
                self.tasks.push(Task::Build(node, 0));
                self.push(to.as_deref());
                self.push(from.as_deref());
                self.tasks.push(Task::Visit(target));
            }
            s::NodeKind::Iterate { target } => {
                self.tasks.push(Task::Build(node, 0));
                self.tasks.push(Task::Visit(target));
            }
            s::NodeKind::Try { body, handler } => {
                self.tasks.push(Task::Build(node, 0));
                self.push(handler.as_deref());
                self.tasks.push(Task::Visit(body));
            }
            s::NodeKind::If { condition, then, otherwise } => {
                self.tasks.push(Task::Build(node, 0));
                self.tasks.push(Task::Visit(otherwise));
                self.tasks.push(Task::Visit(then));
                self.tasks.push(Task::Visit(condition));
            }
            s::NodeKind::ArrayExpr { body } => {
                self.tasks.push(Task::Build(node, 0));
                self.push(body.as_deref());
            }
            s::NodeKind::ObjectExpr { entries } => {
                self.tasks.push(Task::Build(node, 0));
                for entry in entries.iter().rev() {
                    self.push(entry.value.as_ref());
                    self.tasks.push(Task::Visit(&entry.key));
                }
            }
            s::NodeKind::Bind { source, patterns, body } => {
                // The source and the pattern keys in the current scope, the body with the variables' frame.
                self.leave();
                self.tasks.push(Task::Build(node, 0));
                self.tasks.push(Task::Visit(body));
                self.tasks.push(Task::EnterBinder);
                self.push_binder(node, patterns);
                self.tasks.push(Task::Visit(source));
            }
            s::NodeKind::Reduce { source, patterns, init, update } => {
                self.leave();
                self.tasks.push(Task::Build(node, 0));
                self.tasks.push(Task::Visit(update));
                self.tasks.push(Task::EnterBinder);
                self.tasks.push(Task::Visit(init));
                self.push_binder(node, patterns);
                self.tasks.push(Task::Visit(source));
            }
            s::NodeKind::Foreach { source, patterns, init, update, extract } => {
                self.leave();
                self.tasks.push(Task::Build(node, 0));
                self.tasks.push(Task::Visit(extract));
                self.tasks.push(Task::Visit(update));
                self.tasks.push(Task::EnterBinder);
                self.tasks.push(Task::Visit(init));
                self.push_binder(node, patterns);
                self.tasks.push(Task::Visit(source));
            }
            s::NodeKind::Label { name, body } => {
                self.leave();
                self.tasks.push(Task::Build(node, 0));
                self.tasks.push(Task::Visit(body));
                self.depth += 1;
                self.scope.push(Entry { kind: EntryKind::Label, name: name.clone(), arity: 0, depth: self.depth, slot: 0, top_level: false });
            }
            s::NodeKind::FuncDef { definition, rest } => {
                // The definition is in functions; the node's result is the rest's.
                self.leave();
                self.tasks.push(Task::Visit(rest));
                self.tasks.push(Task::Define { definition, top_level: false });
            }
        }
    }

    // A left-nested chain of commas becomes one node with the items in order.
    fn visit_comma(&mut self, node: &'a s::Node) {
        let mut count = 1;
        self.tasks.push(Task::Build(node, 0));
        let build = self.tasks.len() - 1;
        // The right operands from the outermost in are pushed first, so that they run last.
        let mut current = node;
        while let s::NodeKind::Comma { left, right } = &current.kind {
            self.tasks.push(Task::Visit(right));
            count += 1;
            current = left;
        }

        self.tasks.push(Task::Visit(current));
        self.tasks[build] = Task::Build(node, count);
    }

    // Pops the bound children of node, last first, and builds its bound node; count is the number
    // of items of a comma.
    fn build(&mut self, node: &'a s::Node, count: usize) -> Node {
        match &node.kind {
            s::NodeKind::Template { format, texts, parts } => {
                let parts = self.pop_all(parts.len());
                Node::template(format.clone(), texts.clone(), parts)
            }
            s::NodeKind::Pipe { .. } => {
                let right = self.pop();
                Node::pipe(self.pop(), right)
            }
            s::NodeKind::Comma { .. } => Node::comma(self.pop_all(count)),
            s::NodeKind::Binary { op, .. } => {
                let right = self.pop();
                Node::binary(*op, self.pop(), right)
            }
            s::NodeKind::And { .. } => {
                let right = self.pop();
                Node::and(self.pop(), right)
            }
            s::NodeKind::Or { .. } => {
                let right = self.pop();
                Node::or(self.pop(), right)
            }
            s::NodeKind::Alternative { .. } => {
                let right = self.pop();
                Node::alternative(self.pop(), right)
            }
            s::NodeKind::Negate { .. } => Node::negate(self.pop()),
            s::NodeKind::Index { .. } => {
                let key = self.pop();
                Node::index(self.pop(), key)
            }
            s::NodeKind::Slice { from, to, .. } => {
                let to = to.as_ref().map(|_| self.pop());
                let from = from.as_ref().map(|_| self.pop());
                Node::slice(self.pop(), from, to)
            }
            s::NodeKind::Iterate { .. } => Node::iterate(self.pop()),
            s::NodeKind::Try { handler, .. } => {
                let handler = handler.as_ref().map(|_| self.pop());
                Node::try_catch(self.pop(), handler)
            }
            s::NodeKind::If { .. } => {
                let otherwise = self.pop();
                let then = self.pop();
                Node::if_then_else(self.pop(), then, otherwise)
            }
            s::NodeKind::ArrayExpr { body } => Node::array_expr(body.as_ref().map(|_| self.pop())),
            s::NodeKind::ObjectExpr { entries } => {
                let mut bound = Vec::with_capacity(entries.len());
                for entry in entries.iter().rev() {
                    let value = entry.value.as_ref().map(|_| self.pop());
                    bound.push(ObjectEntry { key: self.pop(), value });
                }
                bound.reverse();
                Node::object_expr(bound)
            }
            s::NodeKind::Bind { .. } => {
                let body = self.pop();
                let source = self.pop();
                Node::bind(source, self.pop_binder(), body)
            }
            s::NodeKind::Reduce { .. } => {
                let update = self.pop();
                let init = self.pop();
                let source = self.pop();
                Node::reduce(source, self.pop_binder(), init, update)
            }
            s::NodeKind::Foreach { .. } => {
                let extract = self.pop();
                let update = self.pop();
                let init = self.pop();
                let source = self.pop();
                Node::foreach(source, self.pop_binder(), init, update, extract)
            }
            _ => Node::label(self.pop()),
        }
    }

    // Defines a function at the current point of the scope, visible in its own body and after it.
    fn define(&mut self, definition: &'a s::Definition, top_level: bool) {
        let index = self.shared.functions.len();
        self.shared.functions.push(None);
        if top_level {
            self.exports.push(index);
        }
        self.scope.push(Entry {
            kind: EntryKind::Function,
            name: definition.name.clone(),
            arity: definition.params.len(),
            depth: self.depth,
            slot: index,
            top_level,
        });

        // Single until its body says otherwise, or what the first binding of this body found.
        let replayed = self.replay.as_ref().and_then(|replay| replay.get(index.wrapping_sub(self.replay_start)).copied());
        self.defining.push(Defining {
            index,
            value_parameters: value_parameters(definition),
            single: replayed.unwrap_or(true),
            known: replayed.is_some(),
            self_calls: 0,
        });

        self.leave();
        self.tasks.push(Task::EndFunction { definition, index });
        self.tasks.push(Task::Visit(&definition.body));
        if definition.params.is_empty() {
            return;
        }
        self.depth += 1;
        for (slot, parameter) in definition.params.iter().enumerate() {
            self.scope.push(Entry { kind: EntryKind::Parameter, name: parameter.name.clone(), arity: 0, depth: self.depth, slot, top_level: false });
        }
        // Each $name parameter then has a frame of its own, first parameter outermost (bind_parameters).
        for parameter in &definition.params {
            if parameter.is_variable {
                self.depth += 1;
                self.scope.push(Entry { kind: EntryKind::Variable, name: parameter.name.clone(), arity: 0, depth: self.depth, slot: 0, top_level: false });
            }
        }
    }

    // The body of functions[index] is bound: the function is single when its body is (spec §7.7).
    fn end_function(&mut self, definition: &'a s::Definition, index: usize) {
        let body = self.pop();
        let Some(own) = self.defining.last_mut() else {
            return;
        };
        if own.single && !body.at_most_one && !own.known {
            // It was assumed single and is not. The calls of itself bound as single are wrong, and so
            // is every node built on them: bind the body again, this time knowing. The functions
            // nested in it do not depend on the assumption (their calls of this one are never
            // single, call), so they keep what this binding found and are not bound a third time.
            own.single = false;
            own.known = true;
            if own.self_calls > 0 {
                self.replay_start = index + 1;
                self.replay =
                    Some(self.shared.functions[self.replay_start..].iter().map(|function| function.as_ref().is_some_and(|function| function.single)).collect());
                self.shared.functions.truncate(self.replay_start);
                self.tasks.push(Task::EndFunction { definition, index });
                self.tasks.push(Task::Visit(&definition.body));
                return;
            }
        }

        let Some(own) = self.defining.pop() else {
            return;
        };
        if self.replay.is_some() && self.replay_start == index + 1 {
            self.replay = None;
        }
        let single = body.at_most_one;
        self.shared.functions[index] = Some(Arc::new(Function {
            name: definition.name.clone(),
            arity: definition.params.len(),
            body: bind_parameters(definition, body),
            single,
            value_parameters: own.value_parameters,
        }));
    }

    // A call of functions[function]: single when the function is and the argument of each of its $
    // parameters is. A function still being defined is single in its own body as long as its
    // Defining says so; seen from a function nested in it, it is not.
    fn call(&mut self, function: usize, hops: usize, arguments: Vec<Node>) -> Node {
        let mut single = false;
        if let Some(defined) = &self.shared.functions[function] {
            single = defined.single && all_single(&defined.value_parameters, &arguments);
        } else if let Some(own) = self.defining.last_mut() {
            if own.single && own.index == function && all_single(&own.value_parameters, &arguments) {
                single = true;
                if !own.known {
                    own.self_calls += 1;
                }
            }
        }

        Node::call_function(function, hops, arguments, single)
    }

    fn bind_variable(&mut self, name: &str, position: Position) -> Option<Node> {
        // The parser lets nothing bind $__loc__ (§7.5).
        if name == parser::LOCATION_VARIABLE {
            return Some(Node::literal(self.location(position)));
        }

        for entry in self.scope.iter().rev() {
            if entry.kind == EntryKind::Variable && entry.name == name {
                return Some(Node::variable(self.depth - entry.depth, entry.slot));
            }
            if entry.kind == EntryKind::Constant && entry.name == name {
                return Some(Node::literal(self.shared.constants[entry.slot].clone()));
            }
        }

        if name == "ENV" {
            return Some(Node::env_variable());
        }
        // A qualified name can only come from a data import (bind_imports); the prelude has no host variables.
        if name.contains("::") || self.prelude.is_none() {
            return self.fail(binding_messages::variable_not_defined(name), position);
        }

        let index = match self.shared.host_variables.iter().position(|known| known == name) {
            Some(index) => index,
            None => {
                self.shared.host_variables.push(name.to_owned());
                self.shared.host_variables.len() - 1
            }
        };

        Some(Node::host_variable(name.to_owned(), index))
    }

    // $__loc__: {"file": the module's identity or "<top-level>", "line": the line of its '$'}.
    fn location(&self, position: Position) -> JqValue {
        let file = self.module.as_deref().unwrap_or("<top-level>");
        JqValue::Object(JqObject::from_owned(vec![
            (Arc::new("file".to_owned()), JqValue::from_text(file)),
            (Arc::new("line".to_owned()), JqValue::Number(position.line as f64)),
        ]))
    }

    fn visit_call(&mut self, name: &str, arguments: &'a [s::Node], position: Position) {
        let arity = arguments.len();
        for i in (0..self.scope.len()).rev() {
            let entry = &self.scope[i];
            if entry.name != name || entry.arity != arity {
                continue;
            }
            if entry.kind == EntryKind::Parameter {
                self.results.push(Node::call_parameter(self.depth - entry.depth, entry.slot));
                return;
            }

            if entry.kind == EntryKind::Function {
                // Inside the prelude, its own builtins with a native override bind to the native too.
                let native = if self.prelude.is_none() && entry.top_level { self.override_of(name, arity) } else { None };
                let task = match native {
                    Some(native) => Task::BuildNative { arguments: arity, native },
                    None => Task::BuildCall { arguments: arity, function: entry.slot, hops: self.depth - entry.depth },
                };
                self.tasks.push(task);
                self.push_all(arguments);
                return;
            }
        }

        // The prelude is defined in the outermost, empty environment: every frame of the caller is left.
        if let Some(function) = self.prelude.and_then(|prelude| prelude.find(name, arity)) {
            let task = match self.override_of(name, arity) {
                Some(native) => Task::BuildNative { arguments: arity, native },
                None => Task::BuildCall { arguments: arity, function, hops: self.depth },
            };
            self.tasks.push(task);
        } else if let Some(native) = natives::find(name, arity) {
            self.tasks.push(Task::BuildNative { arguments: arity, native });
        } else {
            self.fail::<()>(binding_messages::not_defined(name, arity), position);
        }
        self.push_all(arguments);
    }

    // The native override of a prelude builtin, when overrides are on.
    fn override_of(&self, name: &str, arity: usize) -> Option<&'static Native> {
        if self.native_overrides { natives::override_of(name, arity) } else { None }
    }

    fn bind_break(&mut self, name: &str, position: Position) -> Option<Node> {
        for entry in self.scope.iter().rev() {
            if entry.kind == EntryKind::Label && entry.name == name {
                return Some(Node::break_to(self.depth - entry.depth));
            }
        }

        self.fail(binding_messages::label_not_defined(name), position)
    }

    // Schedules the binder of node: its pattern keys, in destructuring order, then make_binder.
    fn push_binder(&mut self, node: &'a s::Node, patterns: &'a [s::Pattern]) {
        self.tasks.push(Task::MakeBinder(node));
        let mut keys = Vec::new();
        for pattern in patterns {
            collect_keys(pattern, &mut keys);
        }
        for key in keys.into_iter().rev() {
            self.tasks.push(Task::Visit(key));
        }
    }

    // Builds the ?// alternatives of a binding from its bound keys (the last results, in the order
    // of collect_keys). Every distinct variable name of all the alternatives gets a slot. A name
    // repeated in one pattern (§7.5): without ?// its first occurrence in matching order gives the
    // value, so later ones discard theirs; with ?// every occurrence stores and the last one stays.
    fn make_binder(&mut self, node: &'a s::Node) {
        let patterns: &[s::Pattern] = match &node.kind {
            s::NodeKind::Bind { patterns, .. } | s::NodeKind::Reduce { patterns, .. } | s::NodeKind::Foreach { patterns, .. } => patterns,
            _ => &[],
        };

        let mut count = 0;
        for pattern in patterns {
            let mut keys = Vec::new();
            collect_keys(pattern, &mut keys);
            count += keys.len();
        }
        let mut keys = self.results.split_off(self.results.len() - count).into_iter();
        let mut names = Vec::new();
        let mut seen = if patterns.len() == 1 { Some(HashSet::new()) } else { None };
        let bound = patterns.iter().map(|pattern| bind_pattern(pattern, &mut names, &mut seen, &mut keys)).collect();
        let slots = names.len();
        self.binders.push((Binder::new(bound, slots), names));
    }

    // Pushes the frame of the last binder made: one variable per name, in slot order.
    fn enter_binder(&mut self) {
        self.depth += 1;
        let Some((_, names)) = self.binders.last() else {
            return;
        };
        for (slot, name) in names.iter().enumerate() {
            self.scope.push(Entry { kind: EntryKind::Variable, name: name.clone(), arity: 0, depth: self.depth, slot, top_level: false });
        }
    }

    fn pop_binder(&mut self) -> Binder {
        self.binders.pop().map(|(binder, _)| binder).unwrap_or_else(|| Binder::new(Vec::new(), 0))
    }

    // Schedules the restoring of the scope as it is now, after the tasks pushed later.
    fn leave(&mut self) {
        self.tasks.push(Task::Leave { scope: self.scope.len(), depth: self.depth });
    }

    // Schedules the visit of a node that may be missing.
    fn push(&mut self, node: Option<&'a s::Node>) {
        if let Some(node) = node {
            self.tasks.push(Task::Visit(node));
        }
    }

    fn push_all(&mut self, nodes: &'a [s::Node]) {
        for node in nodes.iter().rev() {
            self.tasks.push(Task::Visit(node));
        }
    }

    fn pop(&mut self) -> Node {
        self.results.pop().unwrap_or_else(Node::identity)
    }

    fn pop_all(&mut self, count: usize) -> Vec<Node> {
        self.results.split_off(self.results.len() - count)
    }

    pub(super) fn fail<T>(&mut self, message: String, position: Position) -> Option<T> {
        if self.error.is_none() {
            self.error = Some(SyntaxError { message, line: position.line, column: position.column, module: self.module.clone() });
        }
        None
    }
}

// The slots of the $ parameters.
fn value_parameters(definition: &s::Definition) -> Vec<usize> {
    definition.params.iter().enumerate().filter(|(_, parameter)| parameter.is_variable).map(|(slot, _)| slot).collect()
}

// def f($a; $b): B is def f(a; b): a as $a | b as $b | B, except that each binding names its
// parameter by position: the k-th $ parameter's frame is k frames inside the parameters' frame.
fn bind_parameters(definition: &s::Definition, mut body: Node) -> Node {
    let mut variables = definition.params.iter().filter(|parameter| parameter.is_variable).count();
    for (slot, parameter) in definition.params.iter().enumerate().rev() {
        if parameter.is_variable {
            variables -= 1;
            let binder = Binder::new(vec![Pattern::Variable { slot: Some(0) }], 1);
            body = Node::bind(Node::call_parameter(variables, slot), binder, body);
        }
    }

    body
}

fn collect_keys<'a>(pattern: &'a s::Pattern, keys: &mut Vec<&'a s::Node>) {
    match &pattern.kind {
        s::PatternKind::Variable(_) => {}
        s::PatternKind::Array(elements) => {
            for element in elements.iter().rev() {
                collect_keys(element, keys);
            }
        }
        s::PatternKind::Object(entries) => {
            for entry in entries {
                keys.push(&entry.key);
                if let Some(value) = &entry.value {
                    collect_keys(value, keys);
                }
            }
        }
    }
}

// Visits elements and entries in the order of collect_keys, which is matching order (spec §7.5).
fn bind_pattern(pattern: &s::Pattern, names: &mut Vec<String>, seen: &mut Option<HashSet<String>>, keys: &mut std::vec::IntoIter<Node>) -> Pattern {
    match &pattern.kind {
        s::PatternKind::Variable(name) => Pattern::Variable { slot: slot(name, names, seen) },
        s::PatternKind::Array(elements) => {
            let mut bound: Vec<Pattern> = elements.iter().rev().map(|element| bind_pattern(element, names, seen, keys)).collect();
            bound.reverse();
            Pattern::Array { elements: bound }
        }
        s::PatternKind::Object(entries) => {
            let mut bound = Vec::with_capacity(entries.len());
            for entry in entries {
                let key = keys.next().unwrap_or_else(Node::identity);
                let variable = entry.variable.as_ref().and_then(|name| slot(name, names, seen));
                let value = entry.value.as_ref().map(|value| bind_pattern(value, names, seen, keys));
                bound.push(PatternEntry { key, slot: variable, value });
            }
            Pattern::Object { entries: bound }
        }
    }
}

// The slot of a variable in names, or None when seen (first occurrence wins) already has the name.
fn slot(name: &str, names: &mut Vec<String>, seen: &mut Option<HashSet<String>>) -> Option<usize> {
    if let Some(seen) = seen {
        if !seen.insert(name.to_owned()) {
            return None;
        }
    }
    match names.iter().position(|known| known == name) {
        Some(slot) => Some(slot),
        None => {
            names.push(name.to_owned());
            Some(names.len() - 1)
        }
    }
}

/// The messages of the binding layer; the first three are fixed by spec §10.2.
pub mod binding_messages {
    pub fn not_defined(name: &str, arity: usize) -> String {
        format!("{name}/{arity} is not defined")
    }

    pub fn label_not_defined(name: &str) -> String {
        format!("$*label-{name} is not defined")
    }

    pub fn variable_not_defined(name: &str) -> String {
        format!("${name} is not defined")
    }

    // The modules group (modules.rs); the texts are jq's.
    pub fn module_not_found(path: &str) -> String {
        format!("module not found: {path}")
    }

    pub fn circular_import(identity: &str) -> String {
        format!("circular import of {identity}")
    }

    pub fn invalid_data(identity: &str, reason: &str) -> String {
        format!("error loading data file {identity}: {reason}")
    }

    /// An error of a module, with where it is: `… at <identity>, line L, column C`.
    pub fn in_module(message: &str, identity: &str, line: u32, column: u32) -> String {
        format!("{message} at {identity}, line {line}, column {column}")
    }
}
