package values

// ArrayBuilder is a private, mutable array under construction, the array counterpart of
// ObjectBuilder. ToArray hands its storage to the new Array without copying; the builder must not
// be used afterwards.
type ArrayBuilder struct {
	items []Value
	count int
	built bool
}

// NewArrayBuilder starts from the elements of source (one level copied).
func NewArrayBuilder(source *Array) *ArrayBuilder {
	items := make([]Value, max(4, source.Count()))
	source.CopyTo(items, 0)
	return &ArrayBuilder{items: items, count: source.Count()}
}

// Count gives the number of elements.
func (b *ArrayBuilder) Count() int { return b.count }

// At gives the element at index, 0 <= index < Count.
func (b *ArrayBuilder) At(index int) Value {
	if uint(index) >= uint(b.count) {
		panic("index out of range")
	}
	return b.items[index]
}

// Set sets the element at index (>= 0); an index past the end pads with nulls, as jq does.
func (b *ArrayBuilder) Set(index int, value Value) {
	if b.built {
		panic("The builder was already turned into an array.")
	}
	if index >= len(b.items) {
		items := make([]Value, max(index+1, len(b.items)*2))
		copy(items, b.items)
		b.items = items
	}
	b.items[index] = value
	if index >= b.count {
		b.count = index + 1
	}
}

// ToArray hands the storage to a new Array.
func (b *ArrayBuilder) ToArray() *Array {
	if b.built {
		panic("The builder was already turned into an array.")
	}
	b.built = true
	return arrayFromOwnedCount(b.items, b.count)
}
