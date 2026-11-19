package values

import "sync/atomic"

// Array is an immutable sequence of values.
//
// Storage is a shared buffer plus a window (start, count) on it, as a Go slice. A slot of a
// buffer is written once, before any array that covers it exists, and never again, so every Array
// is immutable however many arrays share its buffer.
//
// Appending (Append, Concat) to the array whose window ends where the buffer's used part ends
// (its latest version) claims the next free slots with one compare-and-swap on used and writes
// there: amortized O(1), so reduce range(n) as $i ([]; . + [$i]) is O(n). Go's own append is not
// used: two versions appending from the same base would overwrite each other's slot. When the
// slots are already taken (an older version, or another goroutine won the claim) or the buffer is
// full, the window is copied to a new buffer with twice the room. Replacing one element copies one
// level. A slice shares the buffer and keeps all of it alive.
type Array struct {
	buffer *arrayBuffer
	start  int
	count  int
}

type arrayBuffer struct {
	items []Value
	// Slots [0, used) are claimed. Only the goroutine that claimed a slot writes it, before it
	// publishes the array that covers it.
	used atomic.Int64
}

func newArrayBuffer(items []Value, used int) *arrayBuffer {
	b := &arrayBuffer{items: items}
	b.used.Store(int64(used))
	return b
}

// EmptyArray is the empty array.
var EmptyArray = &Array{buffer: newArrayBuffer(nil, 0)}

// ArrayOf creates an array with a copy of items.
func ArrayOf(items ...Value) *Array {
	if len(items) == 0 {
		return EmptyArray
	}
	c := make([]Value, len(items))
	copy(c, items)
	return ArrayFromOwned(c)
}

// ArrayFromOwned creates an array over items without copying: the caller gives up the slice and
// never writes it again.
func ArrayFromOwned(items []Value) *Array {
	if len(items) == 0 {
		return EmptyArray
	}
	return &Array{buffer: newArrayBuffer(items, len(items)), count: len(items)}
}

// arrayFromOwnedCount creates an array over the first count items without copying; the rest of
// the storage is room to append.
func arrayFromOwnedCount(items []Value, count int) *Array {
	if count == 0 {
		return EmptyArray
	}
	return &Array{buffer: newArrayBuffer(items, count), count: count}
}

// Count gives the number of elements.
func (a *Array) Count() int { return a.count }

// At gives the element at index, 0 <= index < Count.
func (a *Array) At(index int) Value {
	if uint(index) >= uint(a.count) {
		panic("index out of range")
	}
	return a.buffer.items[a.start+index]
}

// Append gives this array followed by value.
func (a *Array) Append(value Value) *Array {
	end := a.start + a.count
	if end < len(a.buffer.items) && a.buffer.used.CompareAndSwap(int64(end), int64(end+1)) {
		a.buffer.items[end] = value
		return &Array{buffer: a.buffer, start: a.start, count: a.count + 1}
	}

	items := make([]Value, grow(a.count+1))
	copy(items, a.buffer.items[a.start:end])
	items[a.count] = value
	return &Array{buffer: newArrayBuffer(items, a.count+1), count: a.count + 1}
}

// Concat gives this array followed by other (jq's + on arrays).
func (a *Array) Concat(other *Array) *Array {
	if other.count == 0 {
		return a
	}
	if a.count == 0 {
		return other
	}

	end := a.start + a.count
	total := a.count + other.count
	if end+other.count <= len(a.buffer.items) && a.buffer.used.CompareAndSwap(int64(end), int64(end+other.count)) {
		copy(a.buffer.items[end:], other.buffer.items[other.start:other.start+other.count])
		return &Array{buffer: a.buffer, start: a.start, count: total}
	}

	items := make([]Value, grow(total))
	copy(items, a.buffer.items[a.start:end])
	copy(items[a.count:], other.buffer.items[other.start:other.start+other.count])
	return &Array{buffer: newArrayBuffer(items, total), count: total}
}

// Slice gives the elements from (included) to to (excluded), sharing this array's buffer;
// 0 <= from <= to <= Count.
func (a *Array) Slice(from, to int) *Array {
	if uint(from) > uint(a.count) || to < from || to > a.count {
		panic("slice out of range")
	}
	if from == 0 && to == a.count {
		return a
	}
	if to == from {
		return EmptyArray
	}
	return &Array{buffer: a.buffer, start: a.start + from, count: to - from}
}

// SetItem gives a copy with value at index (index >= 0); an index past the end pads with nulls,
// as jq does.
func (a *Array) SetItem(index int, value Value) *Array {
	if index < 0 {
		panic("negative index")
	}
	if index == a.count {
		return a.Append(value)
	}

	items := make([]Value, max(a.count, index+1))
	a.CopyTo(items, 0)
	items[index] = value
	return ArrayFromOwned(items)
}

// CopyTo copies the elements into target from targetIndex.
func (a *Array) CopyTo(target []Value, targetIndex int) {
	copy(target[targetIndex:], a.buffer.items[a.start:a.start+a.count])
}

// ToArray gives a new slice with the elements.
func (a *Array) ToArray() []Value {
	items := make([]Value, a.count)
	a.CopyTo(items, 0)
	return items
}

func grow(needed int) int { return max(4, needed*2) }
