//! Immutable arrays that are changed in place by their only owner (porting guide §2.9).

use std::sync::Arc;

use super::jq_value::{DropLevel, JqValue, drop_pending};

/// An immutable sequence of values.
///
/// Rust: the elements are a `Vec` behind an `Arc`. The operations that give a new array
/// (`append`, `concat`, `set_item`, `slice`) take the array by value: when it is the only owner
/// of its elements (`Arc::make_mut`), they change them in place, so
/// `reduce range(n) as $i ([]; . + [$i])` is O(n); a shared array is copied first, so every array
/// a program can see stays immutable. The reference gets the same effect with a shared buffer, a
/// window on it and a `Used` counter, which ownership makes unnecessary here; a slice of a shared
/// array copies its elements instead of sharing the buffer.
#[derive(Clone, Default)]
pub struct JqArray {
    items: Arc<Items>,
}

#[derive(Clone, Default)]
struct Items(Vec<JqValue>);

impl Drop for Items {
    fn drop(&mut self) {
        if !self.0.iter().any(JqValue::is_container) {
            return;
        }
        match DropLevel::enter() {
            Some(_level) => self.0.clear(),
            None => drop_pending(self.0.drain(..).filter(JqValue::is_container).collect()),
        }
    }
}

impl JqArray {
    /// The empty array.
    pub fn empty() -> JqArray {
        JqArray::default()
    }

    /// Creates an array with a copy of `items`.
    pub fn of(items: &[JqValue]) -> JqArray {
        JqArray::from_owned(items.to_vec())
    }

    /// Creates an array over `items` without copying.
    pub fn from_owned(items: Vec<JqValue>) -> JqArray {
        JqArray { items: Arc::new(Items(items)) }
    }

    pub fn count(&self) -> usize {
        self.items.0.len()
    }

    pub fn is_empty(&self) -> bool {
        self.items.0.is_empty()
    }

    /// The element at `index`, 0 <= index < count.
    pub fn get(&self, index: usize) -> &JqValue {
        &self.items.0[index]
    }

    pub fn as_slice(&self) -> &[JqValue] {
        &self.items.0
    }

    pub fn iter(&self) -> std::slice::Iter<'_, JqValue> {
        self.items.0.iter()
    }

    /// Whether this array is the only owner of its elements.
    pub fn is_unique(&self) -> bool {
        Arc::strong_count(&self.items) == 1
    }

    /// The elements, to change in place: copied first when the array is shared.
    pub fn make_mut(&mut self) -> &mut Vec<JqValue> {
        &mut Arc::make_mut(&mut self.items).0
    }

    /// The elements as a vector: the array's own when it is not shared, else a copy.
    pub fn into_vec(self) -> Vec<JqValue> {
        match Arc::try_unwrap(self.items) {
            Ok(mut items) => std::mem::take(&mut items.0),
            Err(shared) => shared.0.clone(),
        }
    }

    pub fn append(mut self, value: JqValue) -> JqArray {
        self.make_mut().push(value);
        self
    }

    /// This array followed by `other` (jq's `+` on arrays).
    pub fn concat(mut self, other: JqArray) -> JqArray {
        if other.is_empty() {
            return self;
        }
        if self.is_empty() {
            return other;
        }

        let items = self.make_mut();
        match Arc::try_unwrap(other.items) {
            Ok(mut owned) => items.append(&mut owned.0),
            Err(shared) => items.extend_from_slice(&shared.0),
        }
        self
    }

    /// The elements `from` (included) to `to` (excluded); 0 <= from <= to <= count.
    pub fn slice(mut self, from: usize, to: usize) -> JqArray {
        let count = self.count();
        assert!(from <= to && to <= count, "slice out of range");
        if from == 0 && to == count {
            return self;
        }
        if !self.is_unique() {
            return JqArray::from_owned(self.items.0[from..to].to_vec());
        }

        let items = self.make_mut();
        items.truncate(to);
        items.drain(..from);
        self
    }

    /// The array with `value` at `index`; an index past the end pads with nulls, as jq does.
    pub fn set_item(mut self, index: usize, value: JqValue) -> JqArray {
        let items = self.make_mut();
        if index >= items.len() {
            items.resize(index, JqValue::Null);
            items.push(value);
        } else {
            items[index] = value;
        }
        self
    }

    /// Gives the elements to `pending` when this is their last owner (see `drop_pending`).
    pub(crate) fn release(self, pending: &mut Vec<JqValue>) {
        if let Some(mut items) = Arc::into_inner(self.items) {
            pending.extend(items.0.drain(..).filter(JqValue::is_container));
        }
    }
}

impl<'a> IntoIterator for &'a JqArray {
    type Item = &'a JqValue;
    type IntoIter = std::slice::Iter<'a, JqValue>;

    fn into_iter(self) -> Self::IntoIter {
        self.iter()
    }
}
