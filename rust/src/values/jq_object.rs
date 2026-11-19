//! Immutable insertion-ordered maps that are changed in place by their only owner (porting
//! guide §2.9).

use std::borrow::Borrow;
use std::collections::HashMap;
use std::hash::{Hash, Hasher};
use std::sync::{Arc, OnceLock};

use super::jq_value::{DropLevel, JqString, JqValue, drop_pending};

/// An immutable map from string keys to values that keeps the insertion order of its keys.
///
/// Rust: the entries are a `Vec` behind an `Arc`, as for [`super::JqArray`]: `set`, `remove` and
/// `merge` take the object by value and change it in place when it is the only owner of its
/// entries, so `reduce ... (.; . + {($k): $v})` and `setpath` on new keys do not copy the object.
/// Up to [`LINEAR_LIMIT`] entries, lookup scans the keys; above, it uses an index (key to
/// position) built on first use and kept up to date by the owner's changes. The limit is higher
/// than the reference's 8: an index is two allocations, and the records of most documents have
/// between 8 and 16 keys.
#[derive(Clone, Default)]
pub struct JqObject {
    data: Arc<Data>,
}

pub(crate) const LINEAR_LIMIT: usize = 16;

#[derive(Default)]
struct Data {
    entries: Vec<(JqString, JqValue)>,
    // Key to position, for objects above LINEAR_LIMIT; built on first lookup. Boxed: most objects
    // never need one, and the box keeps them small.
    #[allow(clippy::box_collection)]
    index: OnceLock<Box<HashMap<Key, u32>>>,
    // The positions in code point order of the keys, computed on first use.
    sorted: OnceLock<Box<[u32]>>,
}

// A copy starts without the caches: they are rebuilt when needed.
impl Clone for Data {
    fn clone(&self) -> Data {
        Data { entries: self.entries.clone(), index: OnceLock::new(), sorted: OnceLock::new() }
    }
}

impl Drop for Data {
    fn drop(&mut self) {
        if !self.entries.iter().any(|(_, value)| value.is_container()) {
            return;
        }
        match DropLevel::enter() {
            Some(_level) => self.entries.clear(),
            None => drop_pending(self.entries.drain(..).map(|(_, value)| value).filter(JqValue::is_container).collect()),
        }
    }
}

// A key of the index, hashed and compared as its text, so that a lookup needs no allocation.
#[derive(Clone)]
struct Key(JqString);

impl Borrow<str> for Key {
    fn borrow(&self) -> &str {
        self.0.as_str()
    }
}

impl Hash for Key {
    fn hash<H: Hasher>(&self, state: &mut H) {
        self.0.as_str().hash(state);
    }
}

impl PartialEq for Key {
    fn eq(&self, other: &Key) -> bool {
        self.0.as_str() == other.0.as_str()
    }
}

impl Eq for Key {}

impl JqObject {
    /// The empty object.
    pub fn empty() -> JqObject {
        JqObject::default()
    }

    /// An empty object with room for `capacity` entries.
    pub fn with_capacity(capacity: usize) -> JqObject {
        JqObject::from_owned(Vec::with_capacity(capacity))
    }

    /// Creates an object over entries the caller gives up; keys must be distinct.
    pub fn from_owned(entries: Vec<(JqString, JqValue)>) -> JqObject {
        JqObject { data: Arc::new(Data { entries, index: OnceLock::new(), sorted: OnceLock::new() }) }
    }

    /// Creates an object from entries in the order they were written, where a key may come
    /// again: its last value wins, at the position of the first (a JSON text, §5.3).
    pub fn from_entries(mut entries: Vec<(JqString, JqValue)>) -> JqObject {
        if entries.len() > LINEAR_LIMIT {
            let mut object = JqObject::with_capacity(entries.len());
            for (key, value) in entries {
                object.insert(key, value);
            }
            return object;
        }

        // Few entries: each key is compared with the ones kept before it.
        let mut count = 0;
        for i in 0..entries.len() {
            let first = entries[..count].iter().position(|(name, _)| Arc::ptr_eq(name, &entries[i].0) || name.as_str() == entries[i].0.as_str());
            match first {
                Some(first) => entries[first].1 = std::mem::take(&mut entries[i].1),
                None => {
                    entries.swap(count, i);
                    count += 1;
                }
            }
        }
        entries.truncate(count);
        JqObject::from_owned(entries)
    }

    pub fn count(&self) -> usize {
        self.data.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.data.entries.is_empty()
    }

    /// The key at `index` in insertion order.
    pub fn key_at(&self, index: usize) -> &JqString {
        &self.data.entries[index].0
    }

    /// The value at `index` in insertion order.
    pub fn value_at(&self, index: usize) -> &JqValue {
        &self.data.entries[index].1
    }

    /// The entries in insertion order.
    pub fn entries(&self) -> &[(JqString, JqValue)] {
        &self.data.entries
    }

    pub fn contains_key(&self, key: &str) -> bool {
        self.index_of(key).is_some()
    }

    /// The value of `key`, if present.
    pub fn get(&self, key: &str) -> Option<&JqValue> {
        self.index_of(key).map(|index| &self.data.entries[index].1)
    }

    /// The position of `key` in insertion order.
    pub fn index_of(&self, key: &str) -> Option<usize> {
        let entries = &self.data.entries;
        if entries.len() <= LINEAR_LIMIT {
            return entries.iter().position(|(name, _)| name.as_str() == key);
        }

        let index = self.data.index.get_or_init(|| {
            let mut index = HashMap::with_capacity(entries.len() * 2);
            for (slot, (name, _)) in entries.iter().enumerate() {
                index.insert(Key(name.clone()), slot as u32);
            }
            Box::new(index)
        });
        index.get(key).map(|slot| *slot as usize)
    }

    /// Whether this object is the only owner of its entries.
    pub fn is_unique(&self) -> bool {
        Arc::strong_count(&self.data) == 1
    }

    /// The object with `key` set: an existing key keeps its position, a new key goes last.
    pub fn set(mut self, key: JqString, value: JqValue) -> JqObject {
        self.insert(key, value);
        self
    }

    /// Sets `key` in place (the entries are copied first when the object is shared).
    pub fn insert(&mut self, key: JqString, value: JqValue) {
        match self.index_of(&key) {
            Some(index) => Arc::make_mut(&mut self.data).entries[index].1 = value,
            None => self.add(key, value),
        }
    }

    /// The value of `key`, to change in place; a missing key is added with null first.
    pub fn slot(&mut self, key: &JqString) -> &mut JqValue {
        let index = match self.index_of(key) {
            Some(index) => index,
            None => {
                self.add(key.clone(), JqValue::Null);
                self.count() - 1
            }
        };
        &mut Arc::make_mut(&mut self.data).entries[index].1
    }

    // Appends a key this object does not have.
    fn add(&mut self, key: JqString, value: JqValue) {
        let data = Arc::make_mut(&mut self.data);
        let slot = data.entries.len() as u32;
        if let Some(index) = data.index.get_mut() {
            index.insert(Key(key.clone()), slot);
        }
        data.sorted = OnceLock::new();
        data.entries.push((key, value));
    }

    /// The object without `key`; the other keys keep their order.
    pub fn remove(mut self, key: &str) -> JqObject {
        if let Some(index) = self.index_of(key) {
            let data = Arc::make_mut(&mut self.data);
            data.entries.remove(index);
            data.index = OnceLock::new();
            data.sorted = OnceLock::new();
        }
        self
    }

    /// jq's `+` on objects: the keys of `other` win; existing keys keep their position, new ones
    /// follow in `other`'s order.
    pub fn merge(mut self, other: JqObject) -> JqObject {
        if other.is_empty() {
            return self;
        }
        if self.is_empty() {
            return other;
        }

        match Arc::try_unwrap(other.data) {
            Ok(mut owned) => {
                for (key, value) in owned.entries.drain(..) {
                    self.insert(key, value);
                }
            }
            Err(shared) => {
                for (key, value) in &shared.entries {
                    self.insert(key.clone(), value.clone());
                }
            }
        }
        self
    }

    /// The positions of the entries in code point order of their keys (jq's `keys`); computed once
    /// per object.
    pub fn sorted_order(&self) -> &[u32] {
        self.data.sorted.get_or_init(|| {
            let entries = &self.data.entries;
            let mut order: Vec<u32> = (0..entries.len() as u32).collect();
            // UTF-8 byte order is code point order.
            order.sort_unstable_by(|a, b| entries[*a as usize].0.as_bytes().cmp(entries[*b as usize].0.as_bytes()));
            order.into_boxed_slice()
        })
    }

    /// The entries as a vector: the object's own when it is not shared, else a copy.
    pub fn into_entries(self) -> Vec<(JqString, JqValue)> {
        match Arc::try_unwrap(self.data) {
            Ok(mut data) => std::mem::take(&mut data.entries),
            Err(shared) => shared.entries.clone(),
        }
    }

    /// Gives the values to `pending` when this is their last owner (see `drop_pending`).
    pub(crate) fn release(self, pending: &mut Vec<JqValue>) {
        if let Some(mut data) = Arc::into_inner(self.data) {
            pending.extend(data.entries.drain(..).map(|(_, value)| value).filter(JqValue::is_container));
        }
    }
}
