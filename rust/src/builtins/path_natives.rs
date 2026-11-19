//! §9.5 and §8: `path`, `getpath`, `setpath`, `delpaths`.

use std::borrow::Cow;
use std::cmp::Ordering;

use crate::binding::bound::Node;
use crate::runtime::env::EnvRef;
use crate::runtime::interpreter::{Emit, Input, Interpreter, fail, output};
use crate::runtime::path::Path;
use crate::runtime::signal::Flow;
use crate::values::value_ops::Fallible;
use crate::values::{JqArray, JqObject, JqValue, errors, value_ops};

use super::native_table::Registry;

// setpath's index limit (§11).
const MAX_INDEX: f64 = 536870911.0;

pub fn register(table: &mut Registry) {
    table.closure("path", 1, path_of);
    table.closure("getpath", 1, get_path);
    table.value("setpath", 2, |_, input, arguments| {
        let value = std::mem::take(&mut arguments[1]);
        try_set_path(input.into_owned(), &arguments[0], value)
    });
    table.value("delpaths", 1, |_, input, arguments| try_delete_paths(input.into_owned(), &arguments[0]));
}

// path(f): f in path mode from the empty path; each output must have a path.
fn path_of<'p>(interpreter: &Interpreter<'p>, env: &EnvRef<'p>, arguments: &'p [Node], input: Input<'_>, path: Path, emit: Emit<'_>) -> Flow {
    let at = Path::of(&path);
    interpreter.eval(&arguments[0], env, input, Path::root(), &mut |value, value_at| {
        if value_at.is_tracked() { output(emit, value_at.to_value(), at.clone()) } else { Err(fail(errors::invalid_path_result(&value))) }
    })
}

// getpath(p): p in value mode; the output's path is the input's followed by p, even when absent.
fn get_path<'p>(interpreter: &Interpreter<'p>, env: &EnvRef<'p>, arguments: &'p [Node], input: Input<'_>, path: Path, emit: Emit<'_>) -> Flow {
    interpreter.for_each(&arguments[0], env, Cow::Borrowed(&input), Path::Off, &mut |keys, _| {
        let JqValue::Array(steps) = &keys else {
            return Err(fail(errors::path_not_array()));
        };
        output(emit, try_get_path(&input, steps.as_slice())?, path.append_all(steps))
    })
}

/// `getpath(keys)` (§8.2): each key indexes the value before it.
pub fn try_get_path(root: &JqValue, keys: &[JqValue]) -> Fallible {
    let mut value = Cow::Borrowed(root);
    for key in keys {
        value = Cow::Owned(value_ops::try_index(&value, key)?);
    }
    Ok(value.into_owned())
}

/// setpath(p; v) (§8.2): each container on the way is indexed first (so its errors come first),
/// then the containers are set from the innermost out.
///
/// Rust: the errors are found first (`try_prepare_set`); then, on a path of fields and indexes,
/// the value is set where it is (`apply_set`). A path with a slice is rebuilt level by level, as
/// the reference does.
pub fn try_set_path(root: JqValue, keys: &JqValue, value: JqValue) -> Fallible {
    let JqValue::Array(path) = keys else {
        return Err(errors::path_not_array());
    };
    try_set_keys(root, path.as_slice(), value)
}

/// `setpath` with the keys of the path.
pub fn try_set_keys(root: JqValue, path: &[JqValue], value: JqValue) -> Fallible {
    match try_prepare_set(&root, path)? {
        Prepared::InPlace => Ok(apply_set(root, path, value)),
        Prepared::Rebuild(containers) => {
            drop(root);
            let mut value = value;
            for (container, key) in containers.into_iter().zip(path).rev() {
                value = try_set(container, key, value)?;
            }
            Ok(value)
        }
    }
}

/// What `try_prepare_set` found: how a path is written.
pub enum Prepared {
    /// A path of fields and indexes that `setpath` accepts: `apply_set` writes it and cannot fail.
    InPlace,
    /// A path with a slice: the containers on the way, to rebuild from the innermost out.
    Rebuild(Vec<JqValue>),
}

/// The first half of `setpath(path; v)` on `root`: the errors it raises whatever `v` is, in the
/// reference's order (every index, then, for a path without a slice, every set from the innermost
/// out).
pub fn try_prepare_set(root: &JqValue, path: &[JqValue]) -> Result<Prepared, JqValue> {
    let mut current = Cow::Borrowed(root);
    if path.iter().any(|key| matches!(key, JqValue::Object(_))) {
        let mut containers = Vec::with_capacity(path.len());
        for key in path {
            let child = value_ops::try_index(&current, key)?;
            containers.push(std::mem::replace(&mut current, Cow::Owned(child)).into_owned());
        }
        return Ok(Prepared::Rebuild(containers));
    }

    // The sets are checked on the way in, with no list of the containers: the error of the
    // innermost set that fails is the one setpath raises, once every index has passed.
    let mut refused = None;
    for key in path {
        let child = value_ops::try_index(&current, key)?;
        if let Err(error) = check_set(&current, key) {
            refused = Some(error);
        }
        current = Cow::Owned(child);
    }
    match refused {
        Some(error) => Err(error),
        None => Ok(Prepared::InPlace),
    }
}

/// The second half, for a path that `try_prepare_set` found `InPlace` on this `root`: the value
/// is set where it is, each container on the way changed in place when `root` is its only owner
/// (`make_mut`) and copied one level when it is shared.
pub fn apply_set(root: JqValue, path: &[JqValue], value: JqValue) -> JqValue {
    let mut root = root;
    let mut slot = &mut root;
    for key in path {
        slot = descend(slot, key);
    }
    *slot = value;
    root
}

// Whether try_set accepts the key for the container: its errors, for a key that is not a slice.
fn check_set(container: &JqValue, key: &JqValue) -> Result<(), JqValue> {
    match (key, container) {
        (JqValue::String(_), JqValue::Object(_) | JqValue::Null) => Ok(()),
        (JqValue::Number(number), JqValue::Array(_) | JqValue::Null) => {
            let length = match container {
                JqValue::Array(array) => array.count(),
                _ => 0,
            };
            array_index(*number, length).map(|_| ())
        }
        _ => Err(errors::cannot_update_field(key, container)),
    }
}

// setpath's index of an array of the given length: not NaN, negative counted from the end, within the limit.
fn array_index(number: f64, length: usize) -> Result<usize, JqValue> {
    if number.is_nan() {
        return Err(errors::nan_index());
    }
    let mut index = number.trunc();
    if index < 0.0 {
        index += length as f64;
    }
    if index < 0.0 {
        return Err(errors::negative_index());
    }
    if index > MAX_INDEX {
        return Err(errors::index_too_large());
    }
    Ok(index as usize)
}

// The place of a key in a container that check_set accepted: null becomes the container the key
// asks for, a missing field or element is added (an index past the end pads with nulls).
fn descend<'a>(container: &'a mut JqValue, key: &JqValue) -> &'a mut JqValue {
    match key {
        JqValue::String(name) => {
            if !matches!(container, JqValue::Object(_)) {
                *container = JqValue::Object(JqObject::empty());
            }
            match container {
                JqValue::Object(object) => object.slot(name),
                other => other,
            }
        }
        JqValue::Number(number) => {
            if !matches!(container, JqValue::Array(_)) {
                *container = JqValue::Array(JqArray::empty());
            }
            match container {
                JqValue::Array(array) => {
                    let index = array_index(*number, array.count()).unwrap_or(0);
                    let items = array.make_mut();
                    if index >= items.len() {
                        items.resize(index + 1, JqValue::Null);
                    }
                    &mut items[index]
                }
                other => other,
            }
        }
        _ => container,
    }
}

// One level of setpath: the table of §8.2.
fn try_set(container: JqValue, key: &JqValue, value: JqValue) -> Fallible {
    match (key, container) {
        (JqValue::String(name), JqValue::Object(object)) => Ok(JqValue::Object(object.set(name.clone(), value))),
        (JqValue::String(name), JqValue::Null) => Ok(JqValue::Object(JqObject::empty().set(name.clone(), value))),
        (JqValue::Number(number), container @ (JqValue::Array(_) | JqValue::Null)) => {
            let array = match container {
                JqValue::Array(array) => array,
                _ => JqArray::empty(),
            };
            let index = array_index(*number, array.count())?;
            Ok(JqValue::Array(array.set_item(index, value)))
        }
        (JqValue::Object(slice), JqValue::Array(array)) => try_set_slice(array, slice, value),
        (JqValue::Object(slice), JqValue::Null) => try_set_slice(JqArray::empty(), slice, value),
        (JqValue::Object(_), JqValue::String(_)) => Err(errors::cannot_update_string_slices()),
        (key, container) => Err(errors::cannot_update_field(key, &container)),
    }
}

// .[s:e] = v on an array: v (an array) replaces the elements the bounds select.
fn try_set_slice(mut array: JqArray, slice: &JqObject, value: JqValue) -> Fallible {
    let (Some(start), Some(end)) = (slice.get("start"), slice.get("end")) else {
        return Err(errors::slice_indices_not_integers());
    };
    let (first, last) = value_ops::try_slice_range(array.count(), start, end)?;
    let JqValue::Array(replacement) = value else {
        return Err(errors::slice_needs_array());
    };
    array.make_mut().splice(first..last, replacement.into_vec());
    Ok(JqValue::Array(array))
}

/// delpaths(ps) (§8.2): the paths sorted, then deleted level by level.
pub fn try_delete_paths(root: JqValue, paths: &JqValue) -> Fallible {
    let JqValue::Array(list) = paths else {
        return Err(errors::paths_not_array());
    };
    for path in list {
        match path {
            JqValue::Array(keys) if keys.is_empty() => return Ok(JqValue::Null),
            JqValue::Array(_) => {}
            other => return Err(errors::path_element_not_array(other)),
        }
    }
    if list.is_empty() {
        return Ok(root);
    }

    let sorted = value_ops::sort(list.clone());
    try_delete(root, &sorted)
}

// One container being rebuilt by try_delete: the paths [next, to) still to look at, at depth; the
// keys deleted at this level; and the key of the child being rebuilt below it, if any.
struct Level {
    value: JqValue,
    next: usize,
    to: usize,
    depth: usize,
    deleted: Vec<JqValue>,
    child_key: JqValue,
    child_end: usize,
}

// The key of a path at a depth (the paths are arrays: try_delete_paths checked).
fn key_at(paths: &JqArray, path: usize, depth: usize) -> &JqValue {
    paths.get(path).as_array().get(depth)
}

fn length_of(paths: &JqArray, path: usize) -> usize {
    paths.get(path).as_array().count()
}

// Deletes the paths (sorted, none empty) from value: groups by the key at each depth; a group
// holding the path that ends there deletes the key, the others go into the child first. All the
// keys of one level are removed at once. The levels being rebuilt are on an explicit stack, so a
// path of any length is no recursion.
fn try_delete(value: JqValue, paths: &JqArray) -> Fallible {
    let mut levels = vec![Level { value, next: 0, to: paths.count(), depth: 0, deleted: Vec::new(), child_key: JqValue::Null, child_end: 0 }];
    loop {
        let Some(level) = levels.last_mut() else {
            return Ok(JqValue::Null);
        };
        let mut descended = None;
        while !matches!(level.value, JqValue::Null) && level.next < level.to && descended.is_none() {
            let key = key_at(paths, level.next, level.depth);
            let mut end = level.next + 1;
            let mut ends = length_of(paths, level.next) == level.depth + 1;
            while end < level.to && value_ops::compare(key_at(paths, end, level.depth), key) == Ordering::Equal {
                ends |= length_of(paths, end) == level.depth + 1;
                end += 1;
            }

            if ends {
                level.deleted.push(key.clone());
                level.next = end;
                continue;
            }

            let child = value_ops::try_index(&level.value, key)?;
            if matches!(child, JqValue::Null) {
                level.next = end;
                continue;
            }

            level.child_key = key.clone();
            level.child_end = end;
            descended =
                Some(Level { value: child, next: level.next, to: end, depth: level.depth + 1, deleted: Vec::new(), child_key: JqValue::Null, child_end: 0 });
        }

        if let Some(child) = descended {
            levels.push(child);
            continue;
        }

        // This level is done: remove its keys, then put it back into its parent.
        let Some(level) = levels.pop() else {
            return Ok(JqValue::Null);
        };
        let done = try_remove(level.value, &level.deleted)?;
        let Some(parent) = levels.last_mut() else {
            return Ok(done);
        };
        parent.value = try_set(std::mem::take(&mut parent.value), &parent.child_key, done)?;
        parent.next = parent.child_end;
    }
}

// Removes keys from a container, all relative to the container as it is.
fn try_remove(container: JqValue, keys: &[JqValue]) -> Fallible {
    if keys.is_empty() {
        return Ok(container);
    }
    match container {
        JqValue::Null => Ok(JqValue::Null),
        JqValue::Object(mut object) => {
            for key in keys {
                let JqValue::String(name) = key else {
                    return Err(errors::cannot_delete_field(key));
                };
                object = object.remove(name);
            }

            Ok(JqValue::Object(object))
        }
        JqValue::Array(mut array) => {
            let mut removed = vec![false; array.count()];
            for key in keys {
                match key {
                    JqValue::Number(number) => {
                        if number.is_nan() {
                            continue;
                        }
                        let mut index = number.trunc();
                        if index < 0.0 {
                            index += array.count() as f64;
                        }
                        if index >= 0.0 && index < array.count() as f64 {
                            removed[index as usize] = true;
                        }
                    }
                    JqValue::Object(slice) => {
                        let (Some(start), Some(end)) = (slice.get("start"), slice.get("end")) else {
                            return Err(errors::slice_indices_not_integers());
                        };
                        let (first, last) = value_ops::try_slice_range(array.count(), start, end)?;
                        for flag in &mut removed[first..last] {
                            *flag = true;
                        }
                    }
                    other => return Err(errors::cannot_delete_element(other)),
                }
            }

            let mut index = 0;
            array.make_mut().retain(|_| {
                index += 1;
                !removed[index - 1]
            });
            Ok(JqValue::Array(array))
        }
        other => Err(errors::cannot_delete_from(&other)),
    }
}
