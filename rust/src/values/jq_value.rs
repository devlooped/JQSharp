//! The value: kind + payload (spec §4.1).

use std::cell::Cell;
use std::fmt;
use std::sync::Arc;

use super::jq_array::JqArray;
use super::jq_object::JqObject;
use super::value_text;

/// The kinds of jqlite values, declared in their sort order.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum JqKind {
    Null,
    False,
    True,
    Number,
    String,
    Array,
    Object,
}

/// The text of a string value or of an object key: shared, and changed in place by its only
/// owner (`Arc::make_mut`), so that `. + "x"` on an accumulator does not copy it.
pub type JqString = Arc<String>;

/// An immutable jqlite value: null, false, true, a number (IEEE-754 double, NaN and ±Infinity
/// included), a string, an array or an object.
///
/// Rust: the natural enum, 16 bytes. Containers and strings are behind `Arc`: literal values
/// live in the bound program, which many threads evaluate at once. A value owned once is changed
/// in place by the operations that take it by value (`JqArray::append`, `JqObject::set`, …);
/// a shared one is copied first, so no value a program can see ever changes.
#[derive(Clone, Default)]
pub enum JqValue {
    #[default]
    Null,
    False,
    True,
    Number(f64),
    String(JqString),
    Array(JqArray),
    Object(JqObject),
}

impl JqValue {
    pub fn from_bool(value: bool) -> JqValue {
        if value { JqValue::True } else { JqValue::False }
    }

    pub fn from_number(value: f64) -> JqValue {
        JqValue::Number(value)
    }

    /// Creates a string value. A Rust string is well-formed, so there is no lone surrogate to
    /// replace here (spec §4.1): text is sanitized where it is decoded.
    pub fn from_string(value: String) -> JqValue {
        JqValue::String(Arc::new(value))
    }

    /// Creates a string value with a copy of `value`.
    pub fn from_text(value: &str) -> JqValue {
        JqValue::String(Arc::new(value.to_owned()))
    }

    pub fn from_array(value: JqArray) -> JqValue {
        JqValue::Array(value)
    }

    pub fn from_object(value: JqObject) -> JqValue {
        JqValue::Object(value)
    }

    pub fn kind(&self) -> JqKind {
        match self {
            JqValue::Null => JqKind::Null,
            JqValue::False => JqKind::False,
            JqValue::True => JqKind::True,
            JqValue::Number(_) => JqKind::Number,
            JqValue::String(_) => JqKind::String,
            JqValue::Array(_) => JqKind::Array,
            JqValue::Object(_) => JqKind::Object,
        }
    }

    /// False for null and false, true for every other value.
    pub fn is_truthy(&self) -> bool {
        !matches!(self, JqValue::Null | JqValue::False)
    }

    /// The name jq's `type` gives the kind: false and true are both "boolean".
    pub fn type_name(&self) -> &'static str {
        match self {
            JqValue::Null => "null",
            JqValue::False | JqValue::True => "boolean",
            JqValue::Number(_) => "number",
            JqValue::String(_) => "string",
            JqValue::Array(_) => "array",
            JqValue::Object(_) => "object",
        }
    }

    // The as_* accessors panic only on a programming error (asking a value for the wrong kind),
    // never as part of jqlite semantics: callers check the kind first.
    pub fn as_number(&self) -> f64 {
        match self {
            JqValue::Number(number) => *number,
            _ => self.wrong_kind(JqKind::Number),
        }
    }

    pub fn as_string(&self) -> &str {
        match self {
            JqValue::String(text) => text,
            _ => self.wrong_kind(JqKind::String),
        }
    }

    pub fn as_array(&self) -> &JqArray {
        match self {
            JqValue::Array(array) => array,
            _ => self.wrong_kind(JqKind::Array),
        }
    }

    pub fn as_object(&self) -> &JqObject {
        match self {
            JqValue::Object(object) => object,
            _ => self.wrong_kind(JqKind::Object),
        }
    }

    pub(crate) fn is_container(&self) -> bool {
        matches!(self, JqValue::Array(_) | JqValue::Object(_))
    }

    fn wrong_kind(&self, expected: JqKind) -> ! {
        panic!("A {:?} value is not a {:?}.", self.kind(), expected)
    }
}

/// The canonical JSON text of the value, for diagnostics and tests.
impl fmt::Display for JqValue {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&value_text::to_json(self))
    }
}

impl fmt::Debug for JqValue {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&value_text::to_json(self))
    }
}

// Dropping a container drops its elements, which the compiler does by recursion: a value nested
// 100,000 deep would exhaust the host stack (§11 allows no such failure). So a container that
// holds containers drops them itself: by recursion for the first levels (`DropLevel`), which is
// the fast path, and with an explicit list of pending values below that (`drop_pending`).
thread_local! {
    static DROP_DEPTH: Cell<u32> = const { Cell::new(0) };
}

const MAX_RECURSIVE_DROP: u32 = 64;

/// One level of recursive dropping, while it lives.
pub(crate) struct DropLevel;

impl DropLevel {
    /// Enters a level; `None` when the recursion is deep enough: use `drop_pending` then.
    pub(crate) fn enter() -> Option<DropLevel> {
        let depth = DROP_DEPTH.get();
        if depth >= MAX_RECURSIVE_DROP {
            return None;
        }
        DROP_DEPTH.set(depth + 1);
        Some(DropLevel)
    }
}

impl Drop for DropLevel {
    fn drop(&mut self) {
        DROP_DEPTH.set(DROP_DEPTH.get() - 1);
    }
}

/// Drops containers without recursion: each gives its own containers to the list.
pub(crate) fn drop_pending(mut pending: Vec<JqValue>) {
    while let Some(value) = pending.pop() {
        match value {
            JqValue::Array(array) => array.release(&mut pending),
            JqValue::Object(object) => object.release(&mut pending),
            _ => {}
        }
    }
}
