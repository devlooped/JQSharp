//! `_modify/2` and `_assign/2` (§8.3) implemented natively, overriding their prelude definitions
//! (`Native::overrides_prelude`): same results, errors, order of evaluation and path behavior, but
//! linear in the number of paths, where the prelude's one `setpath` per path copies the container
//! each time. The value being updated is a [`WorkingTree`].

use std::borrow::Cow;

use crate::binding::bound::Node;
use crate::runtime::env::EnvRef;
use crate::runtime::interpreter::{Emit, Input, Interpreter, fail, output};
use crate::runtime::path::Path;
use crate::runtime::signal::{Flow, Signal};
use crate::values::value_ops::Fallible;
use crate::values::{JqArray, JqValue, errors};

use super::native_table::Registry;
use super::path_natives::{self, Prepared};

pub fn register(table: &mut Registry) {
    table.closure("_modify", 2, modify);
    table.closure("_assign", 2, assign);
}

// The first output of update on the value, if it has one.
fn first_output<'p>(interpreter: &Interpreter<'p>, env: &EnvRef<'p>, update: &'p Node, current: JqValue) -> Result<Option<JqValue>, Signal> {
    if update.at_most_one {
        return Ok(interpreter.eval_one(update, env, Cow::Owned(current), Path::Off)?.map(|(value, _)| value));
    }

    let first = interpreter.new_label();
    let mut next = None;
    let result = interpreter.eval(update, env, Cow::Owned(current), Path::Off, &mut |value, _| {
        next = Some(value);
        Err(Signal::break_to(first))
    });
    match result {
        Err(signal) if !signal.is_break_to(first) => Err(signal),
        _ => Ok(next),
    }
}

// def _modify(paths; update): for each path of paths (on the input, lazily), the first output of
// update applied to the current value there replaces it; no output: the path is deleted at the
// end. update runs in value mode; the result has no path.
fn modify<'p>(interpreter: &Interpreter<'p>, env: &EnvRef<'p>, arguments: &'p [Node], input: Input<'_>, path: Path, emit: Emit<'_>) -> Flow {
    let (paths, update) = (&arguments[0], &arguments[1]);
    if paths.at_most_one {
        // One path at most: it is known before the tree changes, so the input itself is the tree.
        let Some((value, at)) = interpreter.eval_one(paths, env, Cow::Borrowed(&input), Path::root())? else {
            return output(emit, input.into_owned(), Path::of(&path));
        };
        if !at.is_tracked() {
            return Err(fail(errors::invalid_path_result(&value)));
        }
        drop(value);
        let mut keys = Vec::new();
        at.keys_into(&mut keys);
        drop(at);
        let mut tree = input.into_owned();
        let current = match take_path(&mut tree, &keys) {
            Some(current) => current,
            None => path_natives::try_get_path(&tree, &keys)?,
        };
        return match first_output(interpreter, env, update, current)? {
            Some(next) => output(emit, path_natives::try_set_keys(tree, &keys, next)?, Path::of(&path)),
            None => {
                let deletions = JqValue::Array(JqArray::from_owned(vec![JqValue::Array(JqArray::from_owned(keys))]));
                output(emit, path_natives::try_delete_paths(tree, &deletions)?, Path::of(&path))
            }
        };
    }

    let mut tree = WorkingTree::new(JqValue::clone(&input));
    let mut deletions = Vec::new();
    // The keys of the path at hand: one list for all the paths.
    let mut keys = Vec::new();
    interpreter.eval(paths, env, Cow::Borrowed(&input), Path::root(), &mut |value, at| {
        if !at.is_tracked() {
            return Err(fail(errors::invalid_path_result(&value)));
        }
        at.keys_into(&mut keys);
        let current = tree.try_get(&keys)?;
        match first_output(interpreter, env, update, current)? {
            Some(next) => tree.try_set(&keys, next)?,
            None => deletions.push(JqValue::Array(JqArray::of(&keys))),
        }
        Ok(())
    })?;
    // The paths are all known: the input is not needed any more, and the tree may be its only owner.
    drop(input);
    let paths = JqValue::Array(JqArray::from_owned(deletions));
    output(emit, path_natives::try_delete_paths(tree.freeze(), &paths)?, Path::of(&path))
}

// The value at a path of fields and indexes that all exist, which leaves the tree (null takes its
// place) so that its update is its only owner; None when the path is not such a path.
fn take_path(tree: &mut JqValue, keys: &[JqValue]) -> Option<JqValue> {
    // Checked first, so that nothing of a shared tree is copied for a path that is not there.
    let mut value = &*tree;
    for key in keys {
        value = match (value, key) {
            (JqValue::Object(object), JqValue::String(name)) => object.get(name)?,
            (JqValue::Array(array), JqValue::Number(number)) => {
                if number.is_nan() || *number < 0.0 || number.trunc() >= array.count() as f64 {
                    return None;
                }
                array.get(number.trunc() as usize)
            }
            _ => return None,
        };
    }

    let mut slot = tree;
    for key in keys {
        slot = match (slot, key) {
            (JqValue::Object(object), JqValue::String(name)) => object.slot(name),
            (JqValue::Array(array), JqValue::Number(number)) => &mut array.make_mut()[number.trunc() as usize],
            _ => return None,
        };
    }
    Some(std::mem::take(slot))
}

// def _assign(paths; $value): for each output of value (on the input), every path of paths set to
// it. With no path the result is the input itself, with its path (the reduce's initial state).
fn assign<'p>(interpreter: &Interpreter<'p>, env: &EnvRef<'p>, arguments: &'p [Node], input: Input<'_>, path: Path, emit: Emit<'_>) -> Flow {
    let (paths, source) = (&arguments[0], &arguments[1]);
    if paths.at_most_one && source.at_most_one {
        // One value and one path at most: the input itself is the tree.
        let Some((value, _)) = interpreter.eval_one(source, env, Cow::Borrowed(&input), Path::Off)? else {
            return Ok(());
        };
        let Some((target, at)) = interpreter.eval_one(paths, env, Cow::Borrowed(&input), Path::root())? else {
            return output(emit, input.into_owned(), path);
        };
        if !at.is_tracked() {
            return Err(fail(errors::invalid_path_result(&target)));
        }
        drop(target);
        let mut keys = Vec::new();
        at.keys_into(&mut keys);
        drop(at);
        return output(emit, path_natives::try_set_keys(input.into_owned(), &keys, value)?, Path::of(&path));
    }

    let mut keys = Vec::new();
    interpreter.for_each(source, env, Cow::Borrowed(&input), Path::Off, &mut |value, _| {
        let mut tree = WorkingTree::new(JqValue::clone(&input));
        let mut any = false;
        interpreter.eval(paths, env, Cow::Borrowed(&input), Path::root(), &mut |target, at| {
            if !at.is_tracked() {
                return Err(fail(errors::invalid_path_result(&target)));
            }
            any = true;
            at.keys_into(&mut keys);
            tree.try_set(&keys, value.clone())?;
            Ok(())
        })?;
        if any { output(emit, tree.freeze(), Path::of(&path)) } else { output(emit, JqValue::clone(&input), path.clone()) }
    })
}

/// A value under update, owned by one call of `_modify` or `_assign`.
///
/// Rust: the tree is the value itself, changed in place by `setpath` (path_natives.rs,
/// `make_mut`): a container is copied at most once, when something else still shares it. The
/// reference needs drafts (builders along the paths being written) for the same effect, because
/// its values cannot be changed in place; here ownership does it, so there is nothing to freeze
/// but one thing. The paths are generated lazily from the call's input, which therefore shares
/// its containers with the tree until the last path is out. So the last write waits: its errors
/// are raised at once (`try_prepare_set`), and the write itself, which cannot fail any more, is
/// applied when the next read or write needs the tree, or by [`WorkingTree::freeze`] once the
/// input is released. An update with one computed path (`.[$k] |= f` in a `reduce`, as `INDEX`
/// does) then changes a value owned once and copies nothing.
struct WorkingTree {
    root: JqValue,
    // A write that try_prepare_set accepted and that is not applied yet: its value, and the keys
    // of its path (a list used again from one write to the next).
    pending: Option<JqValue>,
    pending_keys: Vec<JqValue>,
}

impl WorkingTree {
    fn new(root: JqValue) -> WorkingTree {
        WorkingTree { root, pending: None, pending_keys: Vec::new() }
    }

    // Applies the write that waits.
    fn flush(&mut self) {
        if let Some(value) = self.pending.take() {
            self.root = path_natives::apply_set(std::mem::take(&mut self.root), &self.pending_keys, value);
        }
    }

    /// The value of the whole tree.
    fn freeze(mut self) -> JqValue {
        self.flush();
        self.root
    }

    /// `getpath(keys)` on the current value.
    fn try_get(&mut self, keys: &[JqValue]) -> Fallible {
        self.flush();
        path_natives::try_get_path(&self.root, keys)
    }

    /// `setpath(keys; value)` on the current value.
    fn try_set(&mut self, keys: &[JqValue], value: JqValue) -> Result<(), JqValue> {
        self.flush();
        match path_natives::try_prepare_set(&self.root, keys)? {
            Prepared::InPlace => {
                self.pending_keys.clear();
                self.pending_keys.extend_from_slice(keys);
                self.pending = Some(value);
            }
            // A path with a slice is rebuilt at once: its last errors depend on the value.
            Prepared::Rebuild(_) => self.root = path_natives::try_set_keys(std::mem::take(&mut self.root), keys, value)?,
        }
        Ok(())
    }
}
