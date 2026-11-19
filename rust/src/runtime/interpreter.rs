//! The push-based interpreter of a bound program (spec §6 to §8). One instance is one evaluation:
//! it holds every piece of per-evaluation state (host variables, `$ENV`, the call depth, label
//! numbers, scratch stacks), while the bound program and the prelude are immutable and shared, so
//! one program is evaluated concurrently from many threads, each with its own interpreter.
//!
//! [`Interpreter::eval`] evaluates a node with an input and an environment and pushes each output,
//! with its path in path mode, to `emit`. It returns `Ok(())` when the node is done, or the
//! [`Signal`] that stops it: an error, a break, a halt, or a signal returned by the consumer, which
//! is passed up unchanged. Nothing panics. Path mode (§8.1) is a path that is not `Path::Off`: then
//! every output carries where it came from (see [`Path`]).
//!
//! [`Interpreter::eval_one`] is the same evaluation for nodes that yield at most one output (the
//! resolver marks them, `Node::at_most_one`): it returns the output instead of pushing it, so
//! arithmetic, indexing, object construction and the like run without callbacks. `eval` hands such
//! nodes to it. A call it evaluates (a single call, spec §7.7) has ended when its output is
//! returned, so it leaves the depth of §11 before that output is used; a call `eval` enters stays
//! in the depth while its outputs are pushed.
//!
//! The host stack: both are loops where a node's last step is another node (a pipe whose left side
//! yields at most one value, a branch of `if`, the body of a binding or of a function, a closure,
//! the last item of a comma), so tail positions and calls in them do not recurse; left-deep chains
//! of operators and indexes are walked with scratch stacks. What still recurses (nesting, non-tail
//! calls, generators feeding generators) checks the host stack where an evaluation starts and
//! where an output is pushed, and continues on a fresh stack when it runs short (`stack_guard`),
//! so the only recursion limit a program meets is MaxDepth (§11).
//!
//! Rust: the interpreter is shared (`&self`) by the callbacks of an evaluation, so its counters
//! are `Cell`s. An input is a `Cow`: owned where the evaluation is its last user, so that a value
//! owned once is changed in place (`reduce … (. + [$i])` is linear), and borrowed where the same
//! input goes to several operands, so that reading a field of it costs no reference count.

use std::borrow::Cow;
use std::cell::{Cell, OnceCell, RefCell};
use std::sync::Arc;

use crate::binding::bound::{Binder, BoundProgram, Node, NodeKind, ObjectEntry, Pattern, PatternEntry};
use crate::builtins::formats;
use crate::builtins::native_table::{self, ValueNative};
use crate::builtins::natives::Native;
use crate::syntax::ast::BinaryOp;
use crate::values::value_ops::{self, Fallible};
use crate::values::{JqArray, JqObject, JqString, JqValue, errors, value_text};

use super::env::{Env, EnvRef};
use super::evaluation::{Clock, EvaluationOptions, Outcome};
use super::path::Path;
use super::signal::{Flow, Signal};
use crate::stack_guard;

/// Receives one output of an evaluation; returns `Ok(())` to go on, or the signal that stops it.
pub type Emit<'e> = &'e mut dyn FnMut(JqValue, Path) -> Flow;

/// The input of an evaluation step: owned by it, or borrowed from a step that still uses it.
pub type Input<'i> = Cow<'i, JqValue>;

/// What `eval_one` returns: the output and its path, no output, or the signal that stopped it.
pub type One = Result<Option<(JqValue, Path)>, Signal>;

/// As [`One`], for the steps whose output has no path.
type OneValue = Result<Option<JqValue>, Signal>;

// No native has more than three arguments (fma/3).
const MAX_ARITY: usize = 3;

pub struct Interpreter<'p> {
    program: &'p BoundProgram,
    // The host variables, by the index of BoundProgram::host_variables; None when the host has none.
    host_values: Vec<Option<JqValue>>,
    read_environment: Option<&'p dyn Fn() -> JqValue>,
    environment: OnceCell<JqValue>,
    max_depth: usize,
    depth: Cell<usize>,
    labels: Cell<u64>,
    // The last serial number given to a signal (see Consumer).
    serials: Cell<u64>,
    // Scratch stacks of the chain walks of eval_one; each walk uses the part above its mark.
    spine_nodes: RefCell<Vec<&'p Node>>,
    spine_values: RefCell<Vec<JqValue>>,
    clock: Option<&'p dyn Clock>,
    debug: Option<&'p dyn Fn(&JqValue)>,
    stderr: Option<&'p dyn Fn(&JqValue)>,
}

/// Tells an operand's own signals from those of its consumer (§6.2). It stands between the
/// operand and the consumer and remembers the last signal the consumer returned; every construct
/// passes a signal up unchanged and stops at once, so when the operand returns that very signal,
/// it came from the consumer. A signal is recognized by its serial number, given here the first
/// time a consumer returns it.
struct Consumer {
    last: Cell<u64>,
}

impl Consumer {
    fn new() -> Consumer {
        Consumer { last: Cell::new(0) }
    }

    fn emit(&self, interpreter: &Interpreter<'_>, emit: Emit<'_>, value: JqValue, path: Path) -> Flow {
        let mut result = output(emit, value, path);
        self.last.set(match &mut result {
            Ok(()) => 0,
            Err(signal) => interpreter.mark(signal),
        });
        result
    }

    fn raised(&self, signal: &Signal) -> bool {
        self.last.get() != 0 && signal.serial() == self.last.get()
    }
}

// The state of a reduce or foreach, with its path.
struct State {
    current: RefCell<(JqValue, Path)>,
}

impl State {
    fn new(value: JqValue, at: Path) -> State {
        State { current: RefCell::new((value, at)) }
    }

    // The state, which leaves its place until it is set again: its user is then its only owner.
    fn take(&self) -> (JqValue, Path) {
        self.current.take()
    }

    fn set(&self, value: JqValue, at: Path) {
        *self.current.borrow_mut() = (value, at);
    }

    // What takes the state back to what it is now.
    fn saved(&self) -> (JqValue, Path) {
        self.current.borrow().clone()
    }
}

/// Pushes an output to a consumer. Every output goes through here, because consumers chain: the
/// outputs of a deep recursion flow back through one callback per level, which adds to the stack
/// the evaluation used on the way down, so the guard of `has_stack` is needed here as much as at
/// the start of an evaluation.
#[inline]
pub fn output(emit: Emit<'_>, value: JqValue, at: Path) -> Flow {
    if stack_guard::has_stack() { emit(value, at) } else { stack_guard::on_fresh_stack(|| emit(value, at)) }
}

/// An error signal.
pub fn fail(error: JqValue) -> Signal {
    Signal::error(error)
}

impl<'p> Interpreter<'p> {
    fn new(program: &'p BoundProgram, options: &EvaluationOptions<'p>) -> Interpreter<'p> {
        let host_values = program.host_variables.iter().map(|name| options.variables.and_then(|variables| variables.get(name)).cloned()).collect();
        Interpreter {
            program,
            host_values,
            read_environment: options.environment,
            environment: OnceCell::new(),
            max_depth: options.max_depth,
            depth: Cell::new(0),
            labels: Cell::new(0),
            serials: Cell::new(0),
            spine_nodes: RefCell::new(Vec::new()),
            spine_values: RefCell::new(Vec::new()),
            clock: options.clock,
            debug: options.debug,
            stderr: options.stderr,
        }
    }

    /// The program being evaluated.
    pub fn program(&self) -> &'p BoundProgram {
        self.program
    }

    /// The host's clock, for the date builtins.
    pub fn clock(&self) -> Option<&'p dyn Clock> {
        self.clock
    }

    /// The host's handler of `debug`.
    pub fn debug(&self) -> Option<&'p dyn Fn(&JqValue)> {
        self.debug
    }

    /// The host's handler of `stderr`.
    pub fn stderr(&self) -> Option<&'p dyn Fn(&JqValue)> {
        self.stderr
    }

    /// Evaluates `program` on `input`, giving each output to `output`, which returns false to stop
    /// the stream.
    pub fn run(program: &'p BoundProgram, input: JqValue, options: &EvaluationOptions<'p>, output: &mut dyn FnMut(JqValue) -> bool) -> Outcome {
        let interpreter = Interpreter::new(program, options);
        let result =
            interpreter.eval(&program.body, &None, Cow::Owned(input), Path::Off, &mut |value, _| if output(value) { Ok(()) } else { Err(Signal::stop()) });
        match result {
            Ok(()) => Outcome::Completed,
            Err(signal) if signal.is_error() => Outcome::Error(signal.into_value()),
            Err(signal) if signal.kind() == super::signal::SignalKind::Halt => {
                let code = signal.code();
                Outcome::Halt { value: if signal.has_value() { Some(signal.into_value()) } else { None }, code }
            }
            // A break always meets its label: the resolver binds every break to an enclosing one.
            Err(_) => Outcome::Stopped,
        }
    }

    /// The next label instance, unique within this evaluation.
    pub fn new_label(&self) -> u64 {
        self.labels.set(self.labels.get() + 1);
        self.labels.get()
    }

    // The serial number of a signal a consumer returned: given now if it has none.
    fn mark(&self, signal: &mut Signal) -> u64 {
        if signal.serial() == 0 {
            self.serials.set(self.serials.get() + 1);
            signal.set_serial(self.serials.get());
        }
        signal.serial()
    }

    /// Evaluates `node` and pushes each output to `emit`.
    pub fn eval(&self, node: &'p Node, env: &EnvRef<'p>, input: Input<'_>, path: Path, emit: Emit<'_>) -> Flow {
        if !stack_guard::has_stack() {
            return stack_guard::on_fresh_stack(|| self.eval(node, env, input, path, emit));
        }

        // Function calls entered by this frame (tail calls): they stay active, counting in the depth
        // of §11, while their outputs are consumed, that is until this frame returns.
        let mut calls = 0;
        let mut node = node;
        let mut env = Cow::Borrowed(env);
        let mut input = input;
        let mut path = path;
        let result = 'steps: loop {
            if node.at_most_one {
                break match self.eval_one(node, &env, input, path) {
                    Ok(Some((value, at))) => output(emit, value, at),
                    Ok(None) => Ok(()),
                    Err(signal) => Err(signal),
                };
            }

            match &node.kind {
                NodeKind::Comma { items } => {
                    // The items but the last, which is evaluated in tail position.
                    let Some((last, rest)) = items.split_last() else {
                        break Ok(());
                    };
                    for item in rest {
                        if let Err(signal) = self.eval(item, &env, Cow::Borrowed(&input), path.clone(), emit) {
                            break 'steps Err(signal);
                        }
                    }
                    node = last;
                }
                NodeKind::Pipe { left, right } if left.at_most_one => match self.eval_one(left, &env, input, path) {
                    Ok(Some((value, at))) => {
                        input = Cow::Owned(value);
                        path = at;
                        node = right;
                    }
                    Ok(None) => break Ok(()),
                    Err(signal) => break Err(signal),
                },
                NodeKind::If { condition, then, otherwise } if condition.at_most_one => match self.decide(condition, then, otherwise, &env, &input) {
                    Ok(Some(branch)) => node = branch,
                    Ok(None) => break Ok(()),
                    Err(signal) => break Err(signal),
                },
                NodeKind::Alternative { left, right } => match self.eval_alternative(left, &env, &input, &path, emit) {
                    Ok(false) => node = right,
                    Ok(true) => break Ok(()),
                    Err(signal) => break Err(signal),
                },
                NodeKind::Bind { source, binder, body } if is_simple(source, binder) => match self.bind_one(source, binder, &env, &input) {
                    Ok(Some(frame)) => {
                        env = Cow::Owned(frame);
                        node = body;
                    }
                    Ok(None) => break Ok(()),
                    Err(signal) => break Err(signal),
                },
                NodeKind::CallFunction { function, hops, arguments, .. } => {
                    calls += 1;
                    self.depth.set(self.depth.get() + 1);
                    if self.depth.get() > self.max_depth {
                        break Err(fail(errors::depth_exceeded()));
                    }

                    env = Cow::Owned(call_env(*hops, arguments, &env));
                    node = &self.program.functions[*function].body;
                }
                NodeKind::CallParameter { hops, slot } => {
                    let closure = Env::closure(&env, *hops, *slot);
                    let (argument, closed) = (closure.node, closure.env.clone());
                    env = Cow::Owned(closed);
                    node = argument;
                }
                _ => break self.eval_many(node, &env, input, path, emit),
            }
        };

        self.depth.set(self.depth.get() - calls);
        result
    }

    // if c then a else b end with a condition that yields at most one value: the branch to take;
    // None when the condition has no output.
    fn decide(&self, condition: &'p Node, then: &'p Node, otherwise: &'p Node, env: &EnvRef<'p>, input: &JqValue) -> Result<Option<&'p Node>, Signal> {
        Ok(self.eval_one(condition, env, Cow::Borrowed(input), Path::Off)?.map(|(condition, _)| if condition.is_truthy() { then } else { otherwise }))
    }

    // Binds the output of a simple binding's source: the environment with the new frame; None when
    // nothing binds.
    fn bind_one(&self, source: &'p Node, binder: &'p Binder, env: &EnvRef<'p>, input: &JqValue) -> Result<Option<EnvRef<'p>>, Signal> {
        let Some((value, _)) = self.eval_one(source, env, Cow::Borrowed(input), Path::Off)? else {
            return Ok(None);
        };
        if binder.is_variable {
            return Ok(Some(Env::value(env, value)));
        }

        let mut slots = vec![JqValue::Null; binder.slots];
        if !self.match_one(&binder.patterns[0], &value, &mut slots, env)? {
            return Ok(None);
        }
        Ok(Some(Env::values(env, slots)))
    }

    // The constructs of eval that are not in tail position.
    fn eval_many(&self, node: &'p Node, env: &EnvRef<'p>, input: Input<'_>, path: Path, emit: Emit<'_>) -> Flow {
        match &node.kind {
            NodeKind::Pipe { left, right } => self.eval(left, env, input, path, &mut |value, at| self.eval(right, env, Cow::Owned(value), at, emit)),
            NodeKind::If { condition, then, otherwise } => self.eval(condition, env, Cow::Borrowed(&input), Path::Off, &mut |condition, _| {
                self.eval(if condition.is_truthy() { then } else { otherwise }, env, Cow::Borrowed(&input), path.clone(), emit)
            }),
            NodeKind::Bind { source, binder, body } => self.eval_bind(source, binder, body, env, &input, &path, emit),
            NodeKind::Iterate { target } => self.eval_iterate(target, env, input, path, emit),
            NodeKind::Index { target, key } => self.eval_index(target, key, env, &input, &path, emit),
            NodeKind::Slice { target, from, to } => self.eval_slice(target, from.as_deref(), to.as_deref(), env, &input, &path, emit),
            NodeKind::Binary { op, left, right } => self.eval_binary(*op, left, right, env, input, &path, emit),
            NodeKind::And { left, right } => self.eval_logic(true, left, right, env, &input, &path, emit),
            NodeKind::Or { left, right } => self.eval_logic(false, left, right, env, &input, &path, emit),
            NodeKind::Negate { operand } => {
                let at = Path::of(&path);
                self.eval(operand, env, input, Path::Off, &mut |value, _| output(emit, value_ops::try_negate(value)?, at.clone()))
            }
            NodeKind::ObjectExpr { entries } => self.eval_object(entries, 0, JqObject::with_capacity(entries.len()), env, &input, &Path::of(&path), emit),
            NodeKind::Template { format, texts, parts } => {
                let mut pieces = vec![JqString::default(); parts.len()];
                self.eval_template(format.as_deref(), texts, parts, parts.len(), &mut pieces, env, &input, &Path::of(&path), emit)
            }
            NodeKind::Try { body, handler } => self.eval_try(body, handler.as_deref(), env, input, path, emit),
            NodeKind::Reduce { source, binder, init, update } => self.eval_reduce(source, binder, init, update, env, &input, &path, emit),
            NodeKind::Foreach { source, binder, init, update, extract } => self.eval_foreach(source, binder, init, update, extract, env, &input, &path, emit),
            NodeKind::Label { body } => {
                let label = self.new_label();
                match self.eval(body, &Env::label(env, label), input, path, emit) {
                    Err(signal) if signal.is_break_to(label) => Ok(()),
                    other => other,
                }
            }
            NodeKind::CallNative { native, arguments } => self.eval_native(native, arguments, env, input, path, emit),
            // The other nodes yield at most one output or are taken by eval itself.
            _ => match self.eval_one(node, env, input, path)? {
                Some((value, at)) => output(emit, value, at),
                None => Ok(()),
            },
        }
    }

    /// Evaluates a node that yields at most one output (`Node::at_most_one`): the output and its
    /// path, or `None`. Any signal returned is the node's own: there is no consumer.
    pub fn eval_one(&self, node: &'p Node, env: &EnvRef<'p>, input: Input<'_>, path: Path) -> One {
        if !stack_guard::has_stack() {
            return stack_guard::on_fresh_stack(|| self.eval_one(node, env, input, path));
        }

        let mut node = node;
        let mut env = Cow::Borrowed(env);
        let mut input = input;
        let mut path = path;
        loop {
            match &node.kind {
                NodeKind::Identity => return Ok(Some((input.into_owned(), path))),
                NodeKind::Literal(value) => return Ok(Some((value.clone(), Path::of(&path)))),
                NodeKind::Variable { hops, slot } => return Ok(Some((Env::variable(&env, *hops, *slot).clone(), Path::of(&path)))),
                NodeKind::Pipe { left, right } => {
                    let Some((value, at)) = self.eval_one(left, &env, input, path)? else {
                        return Ok(None);
                    };
                    input = Cow::Owned(value);
                    path = at;
                    node = right;
                }
                NodeKind::If { condition, then, otherwise } => {
                    let Some(branch) = self.decide(condition, then, otherwise, &env, &input)? else {
                        return Ok(None);
                    };
                    node = branch;
                }
                NodeKind::Alternative { left, right } => {
                    if let Some((value, at)) = self.eval_one(left, &env, Cow::Borrowed(&input), path.clone())? {
                        if value.is_truthy() {
                            return Ok(Some((value, at)));
                        }
                    }
                    node = right;
                }
                NodeKind::Try { body, handler } => match self.eval_one(body, &env, input, path.clone()) {
                    Err(signal) if signal.is_error() => {
                        let Some(handler) = handler else {
                            return Ok(None);
                        };
                        node = handler;
                        input = Cow::Owned(signal.into_value());
                        path = Path::of(&path);
                    }
                    other => return other,
                },
                NodeKind::Bind { source, binder, body } if binder.patterns.len() == 1 => {
                    let Some(frame) = self.bind_one(source, binder, &env, &input)? else {
                        return Ok(None);
                    };
                    env = Cow::Owned(frame);
                    node = body;
                }
                NodeKind::CallFunction { function, hops, arguments, .. } => return self.eval_call_one(*function, *hops, arguments, &env, input, path),
                NodeKind::CallParameter { hops, slot } => {
                    // The argument of a $ parameter of a single call (Resolver::call): it is single too.
                    let closure = Env::closure(&env, *hops, *slot);
                    let (argument, closed) = (closure.node, closure.env.clone());
                    env = Cow::Owned(closed);
                    node = argument;
                }
                _ => return self.eval_other(node, &env, input, path),
            }
        }
    }

    // A single call (spec §7.7): it has ended when its output is here, so, unlike a call eval
    // enters, it does not count in the depth of §11 while that output is consumed.
    fn eval_call_one(&self, function: usize, hops: usize, arguments: &'p [Node], env: &EnvRef<'p>, input: Input<'_>, path: Path) -> One {
        if self.depth.get() >= self.max_depth {
            return Err(fail(errors::depth_exceeded()));
        }

        self.depth.set(self.depth.get() + 1);
        let result = self.eval_one(&self.program.functions[function].body, &call_env(hops, arguments, env), input, path);
        self.depth.set(self.depth.get() - 1);
        result
    }

    // The nodes of eval_one that are not in tail position.
    fn eval_other(&self, node: &'p Node, env: &EnvRef<'p>, input: Input<'_>, path: Path) -> One {
        let at = Path::of(&path);
        let value = match &node.kind {
            NodeKind::Index { .. } | NodeKind::Slice { .. } => return self.eval_postfix_one(node, env, input, path),
            NodeKind::Binary { .. } => self.eval_binary_one(node, env, input)?,
            NodeKind::And { .. } | NodeKind::Or { .. } => self.eval_logic_one(node, env, input)?,
            NodeKind::Negate { .. } => self.eval_negate_one(node, env, input)?,
            NodeKind::ArrayExpr { body } => self.eval_array_one(body.as_deref(), env, input)?,
            NodeKind::ObjectExpr { entries } => self.eval_object_one(entries, env, &input)?,
            NodeKind::Template { format, texts, parts } => self.eval_template_one(format.as_deref(), texts, parts, env, &input)?,
            NodeKind::CallNative { native, arguments } => {
                if native.takes_closures {
                    return self.eval_closure_native_one(native, arguments, env, input, path);
                }
                self.eval_native_one(native, arguments, env, input)?
            }
            NodeKind::Label { body } => {
                let label = self.new_label();
                return match self.eval_one(body, &Env::label(env, label), input, path) {
                    Err(signal) if signal.is_break_to(label) => Ok(None),
                    other => other,
                };
            }
            NodeKind::Bind { source, binder, body } => return self.eval_bind_one(source, binder, body, env, &input, path),
            NodeKind::Reduce { source, binder, init, update } => {
                // Reduce with one initial value: through the general evaluation, keeping its output.
                let mut kept = None;
                self.eval_reduce(source, binder, init, update, env, &input, &path, &mut |value, at| {
                    kept = Some((value, at));
                    Ok(())
                })?;
                return Ok(kept);
            }
            // Variables, formats and break: no children.
            NodeKind::HostVariable { name, index } => match &self.host_values[*index] {
                Some(value) => Some(value.clone()),
                None => return Err(fail(errors::variable_not_defined(name))),
            },
            NodeKind::EnvVariable => {
                Some(self.environment.get_or_init(|| self.read_environment.map_or_else(|| JqValue::Object(JqObject::empty()), |read| read())).clone())
            }
            NodeKind::Format(name) => Some(formats::try_apply(name, &input)?),
            NodeKind::Break { hops } => return Err(Signal::break_to(Env::label_at(env, *hops))),
            _ => {
                // A node that is not marked: through the general evaluation, keeping its last output.
                let mut kept = None;
                self.eval(node, env, input, path, &mut |value, at| {
                    kept = Some((value, at));
                    Ok(())
                })?;
                return Ok(kept);
            }
        };

        Ok(value.map(|value| (value, at)))
    }

    // A native that takes closures and gives at most one output (Native::single_when): its output is kept.
    fn eval_closure_native_one(&self, native: &'static Native, arguments: &'p [Node], env: &EnvRef<'p>, input: Input<'_>, path: Path) -> One {
        let mut kept = None;
        native_table::closure(native)(self, env, arguments, input, path, &mut |value, at| {
            kept = Some((value, at));
            Ok(())
        })?;
        Ok(kept)
    }

    /// Evaluates `node` and calls `each` with each output, without a callback for a node that
    /// yields at most one.
    pub fn for_each(&self, node: &'p Node, env: &EnvRef<'p>, input: Input<'_>, path: Path, each: Emit<'_>) -> Flow {
        if !node.at_most_one {
            return self.eval(node, env, input, path, each);
        }
        match self.eval_one(node, env, input, path)? {
            Some((value, at)) => each(value, at),
            None => Ok(()),
        }
    }

    // a // b: the true outputs of a; whether there was one (then b is not evaluated).
    fn eval_alternative(&self, left: &'p Node, env: &EnvRef<'p>, input: &JqValue, path: &Path, emit: Emit<'_>) -> Result<bool, Signal> {
        let mut any = false;
        self.eval(left, env, Cow::Borrowed(input), path.clone(), &mut |value, at| {
            if !value.is_truthy() {
                return Ok(());
            }
            any = true;
            output(emit, value, at)
        })?;
        Ok(any)
    }

    // T[]: in path mode each element's path is the target's plus its index or key.
    fn eval_iterate(&self, target: &'p Node, env: &EnvRef<'p>, input: Input<'_>, path: Path, emit: Emit<'_>) -> Flow {
        self.for_each(target, env, input, path, &mut |target, at| {
            if at.is_untracked() {
                return Err(fail(errors::invalid_path_iterate(&target)));
            }
            match target {
                JqValue::Array(array) => {
                    if array.is_unique() {
                        // The last owner gives its elements away.
                        for (i, item) in array.into_vec().into_iter().enumerate() {
                            output(emit, item, at.append_index(i))?;
                        }
                    } else {
                        for (i, item) in array.iter().enumerate() {
                            output(emit, item.clone(), at.append_index(i))?;
                        }
                    }
                    Ok(())
                }
                JqValue::Object(object) => {
                    for (key, value) in object.entries() {
                        output(emit, value.clone(), at.append_key(key))?;
                    }
                    Ok(())
                }
                other => Err(fail(errors::cannot_iterate(&other))),
            }
        })
    }

    // T[e]: for each key (on the input of T, in value mode), for each target (§6.3).
    fn eval_index(&self, target: &'p Node, key: &'p Node, env: &EnvRef<'p>, input: &JqValue, path: &Path, emit: Emit<'_>) -> Flow {
        self.for_each(key, env, Cow::Borrowed(input), Path::Off, &mut |key, _| {
            self.for_each(target, env, Cow::Borrowed(input), path.clone(), &mut |target, at| {
                let (result, next) = index_step(Cow::Owned(target), at, &key)?;
                output(emit, result, next)
            })
        })
    }

    // T[from:to]: from outermost, then to, then T (§6.3).
    #[allow(clippy::too_many_arguments)]
    fn eval_slice(
        &self,
        target: &'p Node,
        from: Option<&'p Node>,
        to: Option<&'p Node>,
        env: &EnvRef<'p>,
        input: &JqValue,
        path: &Path,
        emit: Emit<'_>,
    ) -> Flow {
        self.bound(from, env, input, &mut |from, _| {
            self.bound(to, env, input, &mut |to, _| {
                self.for_each(target, env, Cow::Borrowed(input), path.clone(), &mut |target, at| {
                    let (result, next) = slice_step(target, at, &from, &to)?;
                    output(emit, result, next)
                })
            })
        })
    }

    // A slice bound: null when absent.
    fn bound(&self, bound: Option<&'p Node>, env: &EnvRef<'p>, input: &JqValue, each: Emit<'_>) -> Flow {
        match bound {
            None => each(JqValue::Null, Path::Off),
            Some(bound) => self.for_each(bound, env, Cow::Borrowed(input), Path::Off, each),
        }
    }

    // a op b: for each right output, for each left output (§6.3); operands in value mode. A right
    // operand that yields at most one value is evaluated first, so the common case has one callback.
    #[allow(clippy::too_many_arguments)]
    fn eval_binary(&self, op: BinaryOp, left: &'p Node, right: &'p Node, env: &EnvRef<'p>, input: Input<'_>, path: &Path, emit: Emit<'_>) -> Flow {
        let at = Path::of(path);
        if !right.at_most_one {
            return self
                .eval(right, env, Cow::Borrowed(&input), Path::Off, &mut |right, _| self.eval_left(op, left, right, env, Cow::Borrowed(&input), &at, emit));
        }
        match self.eval_one(right, env, Cow::Borrowed(&input), Path::Off)? {
            Some((right, _)) => self.eval_left(op, left, right, env, input, &at, emit),
            None => Ok(()),
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn eval_left(&self, op: BinaryOp, left: &'p Node, right: JqValue, env: &EnvRef<'p>, input: Input<'_>, at: &Path, emit: Emit<'_>) -> Flow {
        self.eval(left, env, input, Path::Off, &mut |left, _| output(emit, try_apply(op, left, Cow::Borrowed(&right))?, at.clone()))
    }

    // a and b, a or b (§7.3): for each left output, the right side only when the left does not decide.
    #[allow(clippy::too_many_arguments)]
    fn eval_logic(&self, is_and: bool, left: &'p Node, right: &'p Node, env: &EnvRef<'p>, input: &JqValue, path: &Path, emit: Emit<'_>) -> Flow {
        let at = Path::of(path);
        self.for_each(left, env, Cow::Borrowed(input), Path::Off, &mut |first, _| {
            if first.is_truthy() != is_and {
                return output(emit, JqValue::from_bool(!is_and), at.clone());
            }
            self.for_each(right, env, Cow::Borrowed(input), Path::Off, &mut |second, _| output(emit, JqValue::from_bool(second.is_truthy()), at.clone()))
        })
    }

    // {k1: v1, …} from entry i on, with the entries before it in obj: keys and values in value
    // mode, k1 outermost (§6.3). Entries that yield at most one output are taken in a loop.
    #[allow(clippy::too_many_arguments)]
    fn eval_object(&self, entries: &'p [ObjectEntry], i: usize, obj: JqObject, env: &EnvRef<'p>, input: &JqValue, at: &Path, emit: Emit<'_>) -> Flow {
        let mut obj = obj;
        let mut i = i;
        while i < entries.len() {
            let entry = &entries[i];
            if !entry.key.at_most_one || entry.value.as_ref().is_some_and(|value| !value.at_most_one) {
                break;
            }
            let Some((key, value)) = self.eval_entry_one(entry, env, input)? else {
                return Ok(());
            };
            obj.insert(key, value);
            i += 1;
        }

        if i == entries.len() {
            return output(emit, JqValue::Object(obj), at.clone());
        }
        let next = &entries[i];
        self.for_each(&next.key, env, Cow::Borrowed(input), Path::Off, &mut |key, _| {
            let name = match key {
                JqValue::String(name) => name,
                other => return Err(fail(errors::cannot_use_as_key(&other))),
            };
            match &next.value {
                None => {
                    let indexed = index_field(input, &name)?;
                    self.eval_object(entries, i + 1, obj.clone().set(name, indexed), env, input, at, emit)
                }
                Some(value) => self.for_each(value, env, Cow::Borrowed(input), Path::Off, &mut |value, _| {
                    self.eval_object(entries, i + 1, obj.clone().set(name.clone(), value), env, input, at, emit)
                }),
            }
        })
    }

    // One entry whose key and value yield at most one output.
    fn eval_entry_one(&self, entry: &'p ObjectEntry, env: &EnvRef<'p>, input: &JqValue) -> Result<Option<(JqString, JqValue)>, Signal> {
        let key = match entry.key.literal_string() {
            Some(name) => Arc::clone(name),
            None => match self.eval_one(&entry.key, env, Cow::Borrowed(input), Path::Off)? {
                Some((JqValue::String(name), _)) => name,
                Some((other, _)) => return Err(fail(errors::cannot_use_as_key(&other))),
                None => return Ok(None),
            },
        };
        let value = match &entry.value {
            None => index_field(input, &key)?,
            Some(value) => match self.eval_one(value, env, Cow::Borrowed(input), Path::Off)? {
                Some((value, _)) => value,
                None => return Ok(None),
            },
        };
        Ok(Some((key, value)))
    }

    // "a\(x)b\(y)": the parts from part i - 1 down to the first, the last part outermost (§6.3).
    #[allow(clippy::too_many_arguments)]
    fn eval_template(
        &self,
        format: Option<&'p str>,
        texts: &'p [String],
        parts: &'p [Node],
        i: usize,
        pieces: &mut Vec<JqString>,
        env: &EnvRef<'p>,
        input: &JqValue,
        at: &Path,
        emit: Emit<'_>,
    ) -> Flow {
        if i == 0 {
            return output(emit, JqValue::from_string(join(texts, pieces)), at.clone());
        }
        self.for_each(&parts[i - 1], env, Cow::Borrowed(input), Path::Off, &mut |part, _| {
            pieces[i - 1] = convert(format, part)?;
            self.eval_template(format, texts, parts, i - 1, pieces, env, input, at, emit)
        })
    }

    // try B catch H (§7.13): only B's own errors are caught, never one its consumer returned (§6.2).
    fn eval_try(&self, body: &'p Node, handler: Option<&'p Node>, env: &EnvRef<'p>, input: Input<'_>, path: Path, emit: Emit<'_>) -> Flow {
        // `.[]?` on a value with nothing to iterate (every leaf that `..` and `paths` visit): its
        // error would be written and dropped unseen.
        if handler.is_none() && !input.is_container() {
            if let NodeKind::Iterate { target } = &body.kind {
                if matches!(target.kind, NodeKind::Identity) {
                    return Ok(());
                }
            }
        }

        let consumer = Consumer::new();
        let result = self.eval(body, env, input, path.clone(), &mut |value, at| consumer.emit(self, emit, value, at));
        match result {
            Err(signal) if signal.is_error() && !consumer.raised(&signal) => match handler {
                None => Ok(()),
                Some(handler) => self.eval(handler, env, Cow::Owned(signal.into_value()), Path::of(&path), emit),
            },
            other => other,
        }
    }

    // E as P1 ?// P2 | B, in general: for each output of E (value mode), each binding, B in path mode.
    #[allow(clippy::too_many_arguments)]
    fn eval_bind(&self, source: &'p Node, binder: &'p Binder, body: &'p Node, env: &EnvRef<'p>, input: &JqValue, path: &Path, emit: Emit<'_>) -> Flow {
        let consumer = if binder.patterns.len() > 1 { Some(Consumer::new()) } else { None };
        let mut frame = None;
        self.for_each(source, env, Cow::Borrowed(input), Path::Off, &mut |source, _| {
            self.bind_each(
                binder,
                source,
                env,
                &mut frame,
                &mut |frame| match &consumer {
                    Some(consumer) => self.eval(body, frame, Cow::Borrowed(input), path.clone(), &mut |value, at| consumer.emit(self, emit, value, at)),
                    None => self.eval(body, frame, Cow::Borrowed(input), path.clone(), emit),
                },
                consumer.as_ref(),
                None,
            )
        })
    }

    // E as P1 ?// P2 | B where E, the keys and B yield at most one output.
    fn eval_bind_one(&self, source: &'p Node, binder: &'p Binder, body: &'p Node, env: &EnvRef<'p>, input: &JqValue, path: Path) -> One {
        let Some((source, _)) = self.eval_one(source, env, Cow::Borrowed(input), Path::Off)? else {
            return Ok(None);
        };
        if binder.is_variable {
            return self.eval_one(body, &Env::value(env, source), Cow::Borrowed(input), path);
        }
        let last = binder.patterns.len() - 1;
        for (i, pattern) in binder.patterns.iter().enumerate() {
            let mut slots = vec![JqValue::Null; binder.slots];
            let result = match self.match_one(pattern, &source, &mut slots, env) {
                Ok(true) => self.eval_one(body, &Env::values(env, slots), Cow::Borrowed(input), path.clone()),
                Ok(false) => Ok(None),
                Err(signal) => Err(signal),
            };
            match result {
                Err(signal) if signal.is_error() && i != last => {}
                other => return other,
            }
        }

        Ok(None)
    }

    /// Binds `value` by the binder's patterns and runs `body` in the environment with the new
    /// frame, once per binding (several when a key has several outputs). With `?//` alternatives
    /// (§7.5), when matching fails or the body raises its own error (not one of `consumer`), the
    /// next pattern is tried with the same value, all variables null again, after restoring
    /// `state` (the state of reduce and foreach) to what it was before the first; errors with the
    /// last pattern propagate. `frame` is the caller's, the same for every value it binds: the
    /// frame of `$x` alone is used again from one value to the next (`Env::rebind`).
    #[allow(clippy::too_many_arguments)]
    fn bind_each(
        &self,
        binder: &'p Binder,
        value: JqValue,
        env: &EnvRef<'p>,
        frame: &mut EnvRef<'p>,
        body: &mut dyn FnMut(&EnvRef<'p>) -> Flow,
        consumer: Option<&Consumer>,
        state: Option<&State>,
    ) -> Flow {
        if binder.is_variable {
            Env::rebind(frame, env, value);
            return body(frame);
        }

        let value = &value;
        let patterns = &binder.patterns;
        let last = patterns.len() - 1;
        let restore = if last > 0 { state.map(State::saved) } else { None };
        let mut i = 0;
        loop {
            if i > 0 {
                if let (Some(state), Some((value, at))) = (state, &restore) {
                    state.set(value.clone(), at.clone());
                }
            }

            let result = if binder.keys_at_most_one {
                let mut slots = vec![JqValue::Null; binder.slots];
                match self.match_one(&patterns[i], value, &mut slots, env) {
                    Ok(true) => body(&Env::values(env, slots)),
                    Ok(false) => Ok(()),
                    Err(signal) => Err(signal),
                }
            } else {
                let slots = RefCell::new(vec![JqValue::Null; binder.slots]);
                self.match_any(&patterns[i], value, &slots, env, &mut || {
                    let frame = Env::values(env, slots.borrow().clone());
                    body(&frame)
                })
            };

            match result {
                Err(signal) if signal.is_error() && i != last && !consumer.is_some_and(|consumer| consumer.raised(&signal)) => i += 1,
                other => return other,
            }
        }
    }

    // Destructures value by a pattern whose keys yield at most one output; false when a key yields
    // none. Array elements from the last, object entries from the first (§7.5); the keys are
    // evaluated in the binding's outer environment, on the value being matched.
    fn match_one(&self, pattern: &'p Pattern, value: &JqValue, slots: &mut [JqValue], env: &EnvRef<'p>) -> Result<bool, Signal> {
        match pattern {
            Pattern::Variable { slot } => {
                if let Some(slot) = slot {
                    slots[*slot] = value.clone();
                }
                Ok(true)
            }
            Pattern::Array { elements } => {
                for (i, element) in elements.iter().enumerate().rev() {
                    let item = value_ops::try_index(value, &JqValue::Number(i as f64))?;
                    if !self.match_one(element, &item, slots, env)? {
                        return Ok(false);
                    }
                }
                Ok(true)
            }
            Pattern::Object { entries } => {
                for entry in entries {
                    let member = match entry.key.literal_string() {
                        Some(name) => index_field(value, name)?,
                        None => {
                            let Some((key, _)) = self.eval_one(&entry.key, env, Cow::Borrowed(value), Path::Off)? else {
                                return Ok(false);
                            };
                            // v[k] with the indexing rules of §7.6, whatever the kind of the key
                            // (§7.5; only literal keys are checked, statically).
                            value_ops::try_index(value, &key)?
                        }
                    };
                    match &entry.value {
                        None => {
                            if let Some(slot) = entry.slot {
                                slots[slot] = member;
                            }
                        }
                        Some(pattern) => {
                            if let Some(slot) = entry.slot {
                                slots[slot] = member.clone();
                            }
                            if !self.match_one(pattern, &member, slots, env)? {
                                return Ok(false);
                            }
                        }
                    }
                }
                Ok(true)
            }
        }
    }

    // Destructures value when a key may yield several outputs: next runs once per complete binding.
    fn match_any(&self, pattern: &'p Pattern, value: &JqValue, slots: &RefCell<Vec<JqValue>>, env: &EnvRef<'p>, next: &mut dyn FnMut() -> Flow) -> Flow {
        match pattern {
            Pattern::Variable { slot } => {
                if let Some(slot) = slot {
                    slots.borrow_mut()[*slot] = value.clone();
                }
                next()
            }
            Pattern::Array { elements } => self.match_elements(elements, elements.len(), value, slots, env, next),
            Pattern::Object { entries } => self.match_entries(entries, 0, value, slots, env, next),
        }
    }

    // The elements before position i, from the last to the first.
    fn match_elements(
        &self,
        elements: &'p [Pattern],
        i: usize,
        value: &JqValue,
        slots: &RefCell<Vec<JqValue>>,
        env: &EnvRef<'p>,
        next: &mut dyn FnMut() -> Flow,
    ) -> Flow {
        if i == 0 {
            return next();
        }
        let item = value_ops::try_index(value, &JqValue::Number((i - 1) as f64))?;
        self.match_any(&elements[i - 1], &item, slots, env, &mut || self.match_elements(elements, i - 1, value, slots, env, next))
    }

    fn match_entries(
        &self,
        entries: &'p [PatternEntry],
        i: usize,
        value: &JqValue,
        slots: &RefCell<Vec<JqValue>>,
        env: &EnvRef<'p>,
        next: &mut dyn FnMut() -> Flow,
    ) -> Flow {
        if i == entries.len() {
            return next();
        }
        let entry = &entries[i];
        self.for_each(&entry.key, env, Cow::Borrowed(value), Path::Off, &mut |key, _| {
            let member = value_ops::try_index(value, &key)?;
            if let Some(slot) = entry.slot {
                slots.borrow_mut()[slot] = member.clone();
            }
            match &entry.value {
                None => self.match_entries(entries, i + 1, value, slots, env, next),
                Some(pattern) => self.match_any(pattern, &member, slots, env, &mut || self.match_entries(entries, i + 1, value, slots, env, next)),
            }
        })
    }

    // reduce SRC as P (INIT; UPDATE) (§7.8): one reduction per initial value; the state is the last
    // output of UPDATE, or null. In path mode the state carries its path. With ?// alternatives, a
    // retried alternative starts from the state before the source value.
    #[allow(clippy::too_many_arguments)]
    fn eval_reduce(
        &self,
        source: &'p Node,
        binder: &'p Binder,
        init: &'p Node,
        update: &'p Node,
        env: &EnvRef<'p>,
        input: &JqValue,
        path: &Path,
        emit: Emit<'_>,
    ) -> Flow {
        let mut frame = None;
        self.for_each(init, env, Cow::Borrowed(input), path.clone(), &mut |initial, initial_at| {
            let state = State::new(initial, initial_at);
            self.eval(source, env, Cow::Borrowed(input), Path::Off, &mut |source, _| {
                self.bind_each(
                    binder,
                    source,
                    env,
                    &mut frame,
                    &mut |frame| {
                        let (value, at) = state.take();
                        let next = if update.at_most_one {
                            self.eval_one(update, frame, Cow::Owned(value), at)?
                        } else {
                            let mut last = None;
                            self.eval(update, frame, Cow::Owned(value), at, &mut |value, at| {
                                last = Some((value, at));
                                Ok(())
                            })?;
                            last
                        };
                        match next {
                            Some((value, at)) => state.set(value, at),
                            None => state.set(JqValue::Null, Path::of(path)),
                        }
                        Ok(())
                    },
                    None,
                    Some(&state),
                )
            })?;
            let (value, at) = state.take();
            output(emit, value, at)
        })
    }

    // foreach SRC as P (INIT; UPDATE; EXTRACT) (§7.9).
    #[allow(clippy::too_many_arguments)]
    fn eval_foreach(
        &self,
        source: &'p Node,
        binder: &'p Binder,
        init: &'p Node,
        update: &'p Node,
        extract: &'p Node,
        env: &EnvRef<'p>,
        input: &JqValue,
        path: &Path,
        emit: Emit<'_>,
    ) -> Flow {
        let consumer = if binder.patterns.len() > 1 { Some(Consumer::new()) } else { None };
        let mut frame = None;
        self.for_each(init, env, Cow::Borrowed(input), path.clone(), &mut |initial, initial_at| {
            let state = State::new(initial, initial_at);
            self.eval(source, env, Cow::Borrowed(input), Path::Off, &mut |source, _| {
                self.bind_each(
                    binder,
                    source,
                    env,
                    &mut frame,
                    &mut |frame| {
                        let mut updated = false;
                        let (value, at) = state.take();
                        self.eval(update, frame, Cow::Owned(value), at, &mut |next, next_at| {
                            updated = true;
                            // The state is set once EXTRACT has run on it, so that EXTRACT does not share it.
                            let result = match &consumer {
                                Some(consumer) => {
                                    self.eval(extract, frame, Cow::Borrowed(&next), next_at.clone(), &mut |value, at| consumer.emit(self, emit, value, at))
                                }
                                None => self.eval(extract, frame, Cow::Borrowed(&next), next_at.clone(), emit),
                            };
                            state.set(next, next_at);
                            result
                        })?;
                        if !updated {
                            state.set(JqValue::Null, Path::of(path));
                        }
                        Ok(())
                    },
                    consumer.as_ref(),
                    Some(&state),
                )
            })
        })
    }

    // A native: a closure native decides everything; a value native is called once per combination
    // of its arguments' outputs, the last argument outermost (§6.3, §9.1).
    fn eval_native(&self, native: &'static Native, arguments: &'p [Node], env: &EnvRef<'p>, input: Input<'_>, path: Path, emit: Emit<'_>) -> Flow {
        if native.takes_closures {
            return native_table::closure(native)(self, env, arguments, input, path, emit);
        }

        let mut values: [JqValue; MAX_ARITY] = Default::default();
        self.combine(native_table::value(native), arguments, arguments.len(), &mut values[..native.arity], env, &input, &Path::of(&path), emit)
    }

    // The arguments before position i, from the last to the first.
    #[allow(clippy::too_many_arguments)]
    fn combine(
        &self,
        function: ValueNative,
        nodes: &'p [Node],
        i: usize,
        arguments: &mut [JqValue],
        env: &EnvRef<'p>,
        input: &JqValue,
        at: &Path,
        emit: Emit<'_>,
    ) -> Flow {
        if i == 0 {
            // The native may take its arguments: each combination gets its own.
            let mut own: [JqValue; MAX_ARITY] = Default::default();
            own[..arguments.len()].clone_from_slice(arguments);
            return output(emit, function(self, Cow::Borrowed(input), &mut own[..arguments.len()])?, at.clone());
        }
        self.for_each(&nodes[i - 1], env, Cow::Borrowed(input), Path::Off, &mut |argument, _| {
            arguments[i - 1] = argument;
            self.combine(function, nodes, i - 1, arguments, env, input, at, emit)
        })
    }

    // A value native whose arguments yield at most one output each.
    fn eval_native_one(&self, native: &'static Native, nodes: &'p [Node], env: &EnvRef<'p>, input: Input<'_>) -> OneValue {
        let function = native_table::value(native);
        if nodes.is_empty() {
            return Ok(Some(function(self, input, &mut [])?));
        }

        let mut arguments: [JqValue; MAX_ARITY] = Default::default();
        for (i, node) in nodes.iter().enumerate().rev() {
            let Some((argument, _)) = self.eval_one(node, env, Cow::Borrowed(&input), Path::Off)? else {
                return Ok(None);
            };
            arguments[i] = argument;
        }

        Ok(Some(function(self, input, &mut arguments[..nodes.len()])?))
    }

    // A chain of indexes and slices, T[k1][k2]…: the keys from the outermost in, each on the input
    // (§6.3: the key before its target), then the innermost target, then each index outward.
    fn eval_postfix_one(&self, node: &'p Node, env: &EnvRef<'p>, input: Input<'_>, path: Path) -> One {
        let nodes = self.spine_nodes.borrow().len();
        let keys = self.spine_values.borrow().len();
        let result = self.walk_postfix(node, nodes, env, input, path);
        self.spine_nodes.borrow_mut().truncate(nodes);
        self.spine_values.borrow_mut().truncate(keys);
        result
    }

    fn walk_postfix(&self, node: &'p Node, mark: usize, env: &EnvRef<'p>, input: Input<'_>, path: Path) -> One {
        let mut node = node;
        loop {
            match &node.kind {
                NodeKind::Index { target, key } => {
                    self.spine_nodes.borrow_mut().push(node);
                    // A literal key is used where it is.
                    if literal(key).is_none() {
                        let Some((key, _)) = self.eval_one(key, env, Cow::Borrowed(&input), Path::Off)? else {
                            return Ok(None);
                        };
                        self.spine_values.borrow_mut().push(key);
                    }
                    node = target;
                }
                NodeKind::Slice { target, from, to } => {
                    self.spine_nodes.borrow_mut().push(node);
                    let Some(from) = self.eval_bound(from.as_deref(), env, &input)? else {
                        return Ok(None);
                    };
                    let Some(to) = self.eval_bound(to.as_deref(), env, &input)? else {
                        return Ok(None);
                    };
                    let mut values = self.spine_values.borrow_mut();
                    values.push(from);
                    values.push(to);
                    node = target;
                }
                _ => break,
            }
        }

        // The innermost target: the input itself is indexed where it is, without becoming a value of its own.
        let (mut value, mut at) = match &node.kind {
            NodeKind::Identity => (input, path),
            _ => match self.eval_one(node, env, input, path)? {
                Some((value, at)) => (Cow::Owned(value), at),
                None => return Ok(None),
            },
        };

        // In value mode, the literal keys that find what the value holds are followed where it
        // is: only the last of them becomes a value of its own (a count is an atomic operation).
        if at.is_off() {
            let mut current: &JqValue = &value;
            let mut followed = false;
            loop {
                let step = {
                    let nodes = self.spine_nodes.borrow();
                    if nodes.len() == mark {
                        break;
                    }
                    nodes[nodes.len() - 1]
                };
                let NodeKind::Index { key, .. } = &step.kind else {
                    break;
                };
                let Some(next) = literal(key).and_then(|key| value_ops::index_ref(current, key)) else {
                    break;
                };
                current = next;
                followed = true;
                self.spine_nodes.borrow_mut().pop();
            }
            if followed {
                let found = current.clone();
                value = Cow::Owned(found);
            }
        }

        loop {
            let step = {
                let mut nodes = self.spine_nodes.borrow_mut();
                if nodes.len() == mark {
                    break;
                }
                nodes.pop()
            };
            let Some(step) = step else {
                break;
            };
            let (result, next) = match &step.kind {
                NodeKind::Index { key, .. } => match literal(key) {
                    Some(key) => index_step(value, at, key)?,
                    None => {
                        let key = self.spine_values.borrow_mut().pop().unwrap_or_default();
                        index_step(value, at, &key)?
                    }
                },
                _ => {
                    let (to, from) = {
                        let mut values = self.spine_values.borrow_mut();
                        (values.pop().unwrap_or_default(), values.pop().unwrap_or_default())
                    };
                    slice_step(value.into_owned(), at, &from, &to)?
                }
            };
            value = Cow::Owned(result);
            at = next;
        }

        Ok(Some((value.into_owned(), at)))
    }

    // A bound of a slice: null when absent.
    fn eval_bound(&self, bound: Option<&'p Node>, env: &EnvRef<'p>, input: &JqValue) -> OneValue {
        match bound {
            None => Ok(Some(JqValue::Null)),
            Some(bound) => Ok(self.eval_one(bound, env, Cow::Borrowed(input), Path::Off)?.map(|(value, _)| value)),
        }
    }

    // A left-deep chain of operators, ((a op1 b) op2 c)…: the right operands from the outermost in
    // (§6.3: the right operand first), then the innermost left one, then each operation outward.
    fn eval_binary_one(&self, node: &'p Node, env: &EnvRef<'p>, input: Input<'_>) -> OneValue {
        let NodeKind::Binary { op, left, right } = &node.kind else {
            return Ok(None);
        };
        if !matches!(left.kind, NodeKind::Binary { .. }) {
            // One operator: no chain to walk. A literal right operand is used where it is.
            let right = match literal(right) {
                Some(right) => Cow::Borrowed(right),
                None => match self.eval_one(right, env, Cow::Borrowed(&input), Path::Off)? {
                    Some((right, _)) => Cow::Owned(right),
                    None => return Ok(None),
                },
            };
            let Some((left, _)) = self.eval_one(left, env, input, Path::Off)? else {
                return Ok(None);
            };
            return Ok(Some(try_apply(*op, left, right)?));
        }

        let nodes = self.spine_nodes.borrow().len();
        let operands = self.spine_values.borrow().len();
        let result = self.walk_binary(node, nodes, env, input);
        self.spine_nodes.borrow_mut().truncate(nodes);
        self.spine_values.borrow_mut().truncate(operands);
        result
    }

    fn walk_binary(&self, node: &'p Node, mark: usize, env: &EnvRef<'p>, input: Input<'_>) -> OneValue {
        let mut left = node;
        while let NodeKind::Binary { left: inner, right, .. } = &left.kind {
            let Some((right, _)) = self.eval_one(right, env, Cow::Borrowed(&input), Path::Off)? else {
                return Ok(None);
            };
            self.spine_nodes.borrow_mut().push(left);
            self.spine_values.borrow_mut().push(right);
            left = inner;
        }

        let Some((mut value, _)) = self.eval_one(left, env, input, Path::Off)? else {
            return Ok(None);
        };
        loop {
            let step = {
                let mut nodes = self.spine_nodes.borrow_mut();
                if nodes.len() == mark {
                    break;
                }
                nodes.pop()
            };
            let Some(Node { kind: NodeKind::Binary { op, .. }, .. }) = step else {
                break;
            };
            let right = self.spine_values.borrow_mut().pop().unwrap_or_default();
            value = try_apply(*op, value, Cow::Owned(right))?;
        }

        Ok(Some(value))
    }

    // A left-deep chain of and/or: the innermost left operand first, then each operator outward,
    // evaluating its right operand only when the left value does not decide.
    fn eval_logic_one(&self, node: &'p Node, env: &EnvRef<'p>, input: Input<'_>) -> OneValue {
        let mark = self.spine_nodes.borrow().len();
        let mut node = node;
        while let NodeKind::And { left, .. } | NodeKind::Or { left, .. } = &node.kind {
            self.spine_nodes.borrow_mut().push(node);
            node = left;
        }

        let result = self.walk_logic(node, mark, env, &input);
        self.spine_nodes.borrow_mut().truncate(mark);
        result
    }

    fn walk_logic(&self, innermost: &'p Node, mark: usize, env: &EnvRef<'p>, input: &JqValue) -> OneValue {
        let Some((mut value, _)) = self.eval_one(innermost, env, Cow::Borrowed(input), Path::Off)? else {
            return Ok(None);
        };
        loop {
            let step = {
                let mut nodes = self.spine_nodes.borrow_mut();
                if nodes.len() == mark {
                    break;
                }
                nodes.pop()
            };
            let (is_and, right) = match step.map(|step| &step.kind) {
                Some(NodeKind::And { right, .. }) => (true, right),
                Some(NodeKind::Or { right, .. }) => (false, right),
                _ => break,
            };
            if value.is_truthy() == is_and {
                let Some((second, _)) = self.eval_one(right, env, Cow::Borrowed(input), Path::Off)? else {
                    return Ok(None);
                };
                value = second;
            }

            value = JqValue::from_bool(value.is_truthy());
        }

        Ok(Some(value))
    }

    // -…-x: the operand, then each minus.
    fn eval_negate_one(&self, node: &'p Node, env: &EnvRef<'p>, input: Input<'_>) -> OneValue {
        let mut count = 0;
        let mut operand = node;
        while let NodeKind::Negate { operand: inner } = &operand.kind {
            count += 1;
            operand = inner;
        }
        let Some((mut value, _)) = self.eval_one(operand, env, input, Path::Off)? else {
            return Ok(None);
        };
        for _ in 0..count {
            value = value_ops::try_negate(value)?;
        }

        Ok(Some(value))
    }

    fn eval_array_one(&self, body: Option<&'p Node>, env: &EnvRef<'p>, input: Input<'_>) -> OneValue {
        let Some(body) = body else {
            return Ok(Some(JqValue::Array(JqArray::empty())));
        };
        if body.at_most_one {
            return Ok(Some(JqValue::Array(match self.eval_one(body, env, input, Path::Off)? {
                Some((item, _)) => JqArray::from_owned(vec![item]),
                None => JqArray::empty(),
            })));
        }

        // [f] for an f with several outputs.
        let mut items = Vec::new();
        self.eval(body, env, input, Path::Off, &mut |item, _| {
            items.push(item);
            Ok(())
        })?;
        Ok(Some(JqValue::Array(JqArray::from_owned(items))))
    }

    fn eval_object_one(&self, entries: &'p [ObjectEntry], env: &EnvRef<'p>, input: &JqValue) -> OneValue {
        let mut obj = JqObject::with_capacity(entries.len());
        for entry in entries {
            let Some((key, value)) = self.eval_entry_one(entry, env, input)? else {
                return Ok(None);
            };
            obj.insert(key, value);
        }

        Ok(Some(JqValue::Object(obj)))
    }

    fn eval_template_one(&self, format: Option<&'p str>, texts: &'p [String], parts: &'p [Node], env: &EnvRef<'p>, input: &JqValue) -> OneValue {
        let mut pieces = vec![JqString::default(); parts.len()];
        for (i, part) in parts.iter().enumerate().rev() {
            let Some((part, _)) = self.eval_one(part, env, Cow::Borrowed(input), Path::Off)? else {
                return Ok(None);
            };
            pieces[i] = convert(format, part)?;
        }

        Ok(Some(JqValue::from_string(join(texts, &pieces))))
    }
}

// A binding eval takes in tail position: one pattern, a source and keys that yield at most one output.
fn is_simple(source: &Node, binder: &Binder) -> bool {
    source.at_most_one && binder.keys_at_most_one && binder.patterns.len() == 1
}

// The value of a literal node.
fn literal(node: &Node) -> Option<&JqValue> {
    match &node.kind {
        NodeKind::Literal(value) => Some(value),
        _ => None,
    }
}

// The environment of a call: the caller's with the frames between the call and the definition
// removed, plus the closures of the arguments when there are any.
fn call_env<'p>(hops: usize, arguments: &'p [Node], env: &EnvRef<'p>) -> EnvRef<'p> {
    let defined = Env::up(env, hops);
    if arguments.is_empty() {
        return defined.clone();
    }
    Env::closures(defined, arguments, env)
}

// .[name] on the input of an object construction or of an object pattern (§7.6).
fn index_field(target: &JqValue, name: &JqString) -> Fallible {
    match target {
        JqValue::Object(object) => Ok(object.get(name).cloned().unwrap_or(JqValue::Null)),
        JqValue::Null => Ok(JqValue::Null),
        _ => Err(errors::cannot_index(target, &JqValue::String(Arc::clone(name)))),
    }
}

// One index of a target with its path: in path mode the target must be tracked.
fn index_step(target: Input<'_>, at: Path, key: &JqValue) -> Result<(JqValue, Path), Signal> {
    if at.is_untracked() {
        return Err(fail(errors::invalid_path_access(key, &target)));
    }

    let result = value_ops::try_index(&target, key)?;
    let next = if at.is_tracked() { at.append(key.clone()) } else { at };
    Ok((result, next))
}

// One slice of a target with its path; in a path the slice is the key {"start": from, "end": to} (§8.1).
fn slice_step(target: JqValue, at: Path, from: &JqValue, to: &JqValue) -> Result<(JqValue, Path), Signal> {
    let mut next = at;
    if !next.is_off() {
        let key = value_ops::slice_key(from.clone(), to.clone());
        if next.is_untracked() {
            return Err(fail(errors::invalid_path_access(&key, &target)));
        }
        next = next.append(key);
    }

    Ok((value_ops::try_slice(target, from, to)?, next))
}

/// The arithmetic operators and comparisons of §4.
pub fn try_apply(op: BinaryOp, left: JqValue, right: Input<'_>) -> Fallible {
    match op {
        BinaryOp::Add => value_ops::try_add(left, right.into_owned()),
        BinaryOp::Subtract => value_ops::try_subtract(left, right.into_owned()),
        BinaryOp::Multiply => value_ops::try_multiply(left, right.into_owned()),
        BinaryOp::Divide => value_ops::try_divide(left, right.into_owned()),
        BinaryOp::Modulo => value_ops::try_modulo(left, right.into_owned()),
        BinaryOp::Equal => Ok(JqValue::from_bool(value_ops::are_equal(&left, &right))),
        BinaryOp::NotEqual => Ok(JqValue::from_bool(!value_ops::are_equal(&left, &right))),
        BinaryOp::Less => Ok(JqValue::from_bool(value_ops::compare(&left, &right).is_lt())),
        BinaryOp::LessEqual => Ok(JqValue::from_bool(value_ops::compare(&left, &right).is_le())),
        BinaryOp::Greater => Ok(JqValue::from_bool(value_ops::compare(&left, &right).is_gt())),
        BinaryOp::GreaterEqual => Ok(JqValue::from_bool(value_ops::compare(&left, &right).is_ge())),
    }
}

// An interpolated value as text: tostring, or the template's format.
fn convert(format: Option<&str>, part: JqValue) -> Result<JqString, JqValue> {
    match format {
        None => Ok(match part {
            JqValue::String(text) => text,
            other => Arc::new(value_text::to_json(&other)),
        }),
        Some(format) => match formats::try_apply(format, &part)? {
            JqValue::String(text) => Ok(text),
            other => Ok(Arc::new(value_text::to_json(&other))),
        },
    }
}

fn join(texts: &[String], pieces: &[JqString]) -> String {
    let mut builder = String::with_capacity(texts.iter().map(String::len).sum::<usize>() + pieces.iter().map(|piece| piece.len()).sum::<usize>());
    builder.push_str(&texts[0]);
    for (i, piece) in pieces.iter().enumerate() {
        builder.push_str(piece);
        builder.push_str(&texts[i + 1]);
    }
    builder
}
