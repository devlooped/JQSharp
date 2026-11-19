package values

import (
	"slices"
	"sync"
	"sync/atomic"
)

// linearLimit is the number of entries up to which lookup scans the keys.
const linearLimit = 8

// Object is an immutable map from string keys to values that keeps the insertion order of its
// keys.
//
// Storage is a shared buffer of entries plus a count, as for Array. Adding a new key to the
// latest version of a buffer claims the next slot with one compare-and-swap: amortized O(1), so
// reduce ... (.; . + {($k): $v}) and setpath on new keys do not copy the object. The keys of a
// buffer are all distinct (a slot is only claimed for a key the version lacks), so one index (key
// to slot) serves every version: a version with count entries owns the key when its slot is below
// count.
//
// Replacing or removing a key copies one level. Up to linearLimit entries, lookup scans the keys;
// above, it uses the index, built lazily and extended under a lock because versions on other
// goroutines share it.
type Object struct {
	buffer *objectBuffer
	count  int
	// The keys in code point order, computed on first use; two goroutines may both compute it.
	sortedKeys atomic.Pointer[[]string]
}

type objectBuffer struct {
	keys   []string
	values []Value
	// Slots [0, used) are claimed; only their claimer writes them, before publishing.
	used atomic.Int64
	// Key to slot for slots [0, indexed); guarded by mu.
	mu      sync.Mutex
	index   map[string]int
	indexed int
}

func newObjectBuffer(keys []string, values []Value, used int, index map[string]int) *objectBuffer {
	b := &objectBuffer{keys: keys, values: values, index: index, indexed: len(index)}
	b.used.Store(int64(used))
	return b
}

// EmptyObject is the empty object.
var EmptyObject = &Object{buffer: newObjectBuffer(nil, nil, 0, nil)}

// objectFromOwned creates an object over slices the caller gives up; keys must be distinct;
// index, if any, maps exactly the first count keys.
func objectFromOwned(keys []string, values []Value, count int, index map[string]int) *Object {
	if count == 0 {
		return EmptyObject
	}
	return &Object{buffer: newObjectBuffer(keys, values, count, index), count: count}
}

// Count gives the number of keys.
func (o *Object) Count() int { return o.count }

// KeyAt gives the key at index in insertion order.
func (o *Object) KeyAt(index int) string {
	if uint(index) >= uint(o.count) {
		panic("index out of range")
	}
	return o.buffer.keys[index]
}

// ValueAt gives the value at index in insertion order.
func (o *Object) ValueAt(index int) Value {
	if uint(index) >= uint(o.count) {
		panic("index out of range")
	}
	return o.buffer.values[index]
}

// ContainsKey tells whether the object has key.
func (o *Object) ContainsKey(key string) bool { return o.IndexOf(key) >= 0 }

// TryGetValue gives the value of key, if any.
func (o *Object) TryGetValue(key string) (Value, bool) {
	index := o.IndexOf(key)
	if index < 0 {
		return Null, false
	}
	return o.buffer.values[index], true
}

// IndexOf gives the position of key in insertion order, or -1.
func (o *Object) IndexOf(key string) int {
	b := o.buffer
	if o.count <= linearLimit {
		for i := 0; i < o.count; i++ {
			if b.keys[i] == key {
				return i
			}
		}
		return -1
	}

	b.mu.Lock()
	defer b.mu.Unlock()
	if b.index == nil {
		b.index = make(map[string]int, o.count)
	}
	// Slots below this version's count are written (it was published after them).
	for ; b.indexed < o.count; b.indexed++ {
		b.index[b.keys[b.indexed]] = b.indexed
	}
	if slot, ok := b.index[key]; ok && slot < o.count {
		return slot
	}
	return -1
}

// Set gives a copy with key set: an existing key keeps its position, a new key goes last.
func (o *Object) Set(key string, value Value) *Object {
	index := o.IndexOf(key)
	if index < 0 {
		return o.add(key, value)
	}

	keys := make([]string, o.count)
	values := make([]Value, o.count)
	copy(keys, o.buffer.keys[:o.count])
	copy(values, o.buffer.values[:o.count])
	values[index] = value
	return objectFromOwned(keys, values, o.count, nil)
}

// add appends a key this version does not have.
func (o *Object) add(key string, value Value) *Object {
	b := o.buffer
	if o.count < len(b.keys) && b.used.CompareAndSwap(int64(o.count), int64(o.count+1)) {
		b.keys[o.count] = key
		b.values[o.count] = value
		return &Object{buffer: b, count: o.count + 1}
	}

	capacity := max(4, (o.count+1)*2)
	keys := make([]string, capacity)
	values := make([]Value, capacity)
	copy(keys, b.keys[:o.count])
	copy(values, b.values[:o.count])
	keys[o.count] = key
	values[o.count] = value
	return objectFromOwned(keys, values, o.count+1, nil)
}

// Remove gives a copy without key; the other keys keep their order.
func (o *Object) Remove(key string) *Object {
	index := o.IndexOf(key)
	if index < 0 {
		return o
	}

	keys := make([]string, 0, o.count-1)
	values := make([]Value, 0, o.count-1)
	keys = append(append(keys, o.buffer.keys[:index]...), o.buffer.keys[index+1:o.count]...)
	values = append(append(values, o.buffer.values[:index]...), o.buffer.values[index+1:o.count]...)
	return objectFromOwned(keys, values, o.count-1, nil)
}

// Merge is jq's + on objects: the keys of other win; existing keys keep their position, new
// ones follow in other's order.
func (o *Object) Merge(other *Object) *Object {
	if other.count == 0 {
		return o
	}
	if o.count == 0 {
		return other
	}

	overlaps := false
	for i := 0; i < other.count && !overlaps; i++ {
		overlaps = o.IndexOf(other.buffer.keys[i]) >= 0
	}

	if !overlaps {
		// Only new keys: append them, in place when this is the latest version.
		result := o
		for i := 0; i < other.count; i++ {
			result = result.add(other.buffer.keys[i], other.buffer.values[i])
		}
		return result
	}

	builder := NewObjectBuilderFrom(o, other.count)
	for i := 0; i < other.count; i++ {
		builder.Set(other.buffer.keys[i], other.buffer.values[i])
	}
	return builder.ToObject()
}

// SortedKeys gives the keys in code point order (jq's keys); computed once per object.
func (o *Object) SortedKeys() []string {
	if keys := o.sortedKeys.Load(); keys != nil {
		return *keys
	}
	keys := make([]string, o.count)
	copy(keys, o.buffer.keys[:o.count])
	slices.SortFunc(keys, CodePoints.Compare)
	o.sortedKeys.Store(&keys)
	return keys
}
