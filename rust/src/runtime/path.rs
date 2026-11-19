//! Paths of path mode (§8.1).

use std::rc::Rc;
use std::sync::Arc;

use crate::values::{JqArray, JqString, JqValue};

/// Where a value came from, in path mode (§8.1): the keys from the root input, as an immutable
/// list linked from the last key, so appending is O(1) and shares the prefix.
///
/// Evaluation takes and emits a `Path`: `Off` in value mode (no path is ever built), and in path
/// mode either a tracked path (`At`: the root and what is appended to it) or `Untracked`, for an
/// output that has no path (a literal, a variable, an arithmetic result). The reference spells the
/// three states null, a tracked `Path` and `Path.Untracked`.
#[derive(Clone, Debug, Default)]
pub enum Path {
    /// Value mode.
    #[default]
    Off,
    /// An output in path mode that has no path.
    Untracked,
    /// A tracked path; `None` is the empty path: the root input in path mode.
    At(Option<Rc<Step>>),
}

/// One key of a path, linked to the path before it.
#[derive(Debug)]
pub struct Step {
    parent: Option<Rc<Step>>,
    key: JqValue,
    length: usize,
}

// A path is as long as a value is deep: its steps are released by a loop, not by recursion.
impl Drop for Step {
    fn drop(&mut self) {
        let mut next = self.parent.take();
        while let Some(step) = next {
            next = match Rc::try_unwrap(step) {
                Ok(mut step) => step.parent.take(),
                Err(_) => None,
            };
        }
    }
}

impl Path {
    /// The empty path: the root input in path mode.
    pub fn root() -> Path {
        Path::At(None)
    }

    /// Whether evaluation is in value mode.
    pub fn is_off(&self) -> bool {
        matches!(self, Path::Off)
    }

    pub fn is_tracked(&self) -> bool {
        matches!(self, Path::At(_))
    }

    pub fn is_untracked(&self) -> bool {
        matches!(self, Path::Untracked)
    }

    /// The path of an output without one: `Off` in value mode, else `Untracked`.
    pub fn of(mode: &Path) -> Path {
        if mode.is_off() { Path::Off } else { Path::Untracked }
    }

    /// This path followed by `next` when it is tracked; unchanged otherwise.
    pub fn append(&self, next: JqValue) -> Path {
        match self {
            Path::At(steps) => {
                let length = steps.as_ref().map_or(0, |step| step.length) + 1;
                Path::At(Some(Rc::new(Step { parent: steps.clone(), key: next, length })))
            }
            other => other.clone(),
        }
    }

    /// This path followed by an array index, when it is tracked.
    pub fn append_index(&self, index: usize) -> Path {
        if self.is_tracked() { self.append(JqValue::Number(index as f64)) } else { self.clone() }
    }

    /// This path followed by an object key, when it is tracked.
    pub fn append_key(&self, key: &JqString) -> Path {
        if self.is_tracked() { self.append(JqValue::String(Arc::clone(key))) } else { self.clone() }
    }

    /// This path followed by the elements of `keys`, when it is tracked.
    pub fn append_all(&self, keys: &JqArray) -> Path {
        let mut path = self.clone();
        if path.is_tracked() {
            for key in keys {
                path = path.append(key.clone());
            }
        }
        path
    }

    /// The keys as a jqlite array, first key first; an untracked path has none.
    pub fn to_value(&self) -> JqValue {
        let mut keys = Vec::new();
        self.keys_into(&mut keys);
        JqValue::Array(JqArray::from_owned(keys))
    }

    /// The keys, first key first, in `keys` (emptied first): a list the caller uses again from
    /// one path to the next.
    pub fn keys_into(&self, keys: &mut Vec<JqValue>) {
        keys.clear();
        if let Path::At(steps) = self {
            let mut step = steps.as_ref();
            if let Some(last) = step {
                keys.reserve_exact(last.length);
            }
            while let Some(current) = step {
                keys.push(current.key.clone());
                step = current.parent.as_ref();
            }
            keys.reverse();
        }
    }
}
