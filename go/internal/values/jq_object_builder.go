package values

// ObjectBuilder is a private, mutable object under construction (object literals, *, the JSON
// reader, merges that replace keys). ToObject hands its storage to the new Object without copying;
// the builder must not be used afterwards.
type ObjectBuilder struct {
	keys   []string
	values []Value
	count  int
	index  map[string]int
	built  bool
}

// NewObjectBuilder starts an empty object with room for capacity keys.
func NewObjectBuilder(capacity int) *ObjectBuilder {
	n := max(capacity, 1)
	return &ObjectBuilder{keys: make([]string, n), values: make([]Value, n)}
}

// NewObjectBuilderFrom starts from the entries of source, with room for extra more.
func NewObjectBuilderFrom(source *Object, extra int) *ObjectBuilder {
	b := NewObjectBuilder(source.Count() + extra)
	copy(b.keys, source.buffer.keys[:source.count])
	copy(b.values, source.buffer.values[:source.count])
	b.count = source.Count()
	if b.count > linearLimit {
		b.buildIndex()
	}
	return b
}

// Count gives the number of keys.
func (b *ObjectBuilder) Count() int { return b.count }

// TryGetValue gives the value of key, if any.
func (b *ObjectBuilder) TryGetValue(key string) (Value, bool) {
	i := b.indexOf(key)
	if i < 0 {
		return Null, false
	}
	return b.values[i], true
}

// Set sets key: an existing key keeps its position, a new key goes last.
func (b *ObjectBuilder) Set(key string, value Value) {
	b.ensureNotBuilt()
	if i := b.indexOf(key); i >= 0 {
		b.values[i] = value
		return
	}

	if b.count == len(b.keys) {
		keys := make([]string, b.count*2)
		values := make([]Value, b.count*2)
		copy(keys, b.keys)
		copy(values, b.values)
		b.keys, b.values = keys, values
	}

	b.keys[b.count] = key
	b.values[b.count] = value
	b.count++
	if b.index != nil {
		b.index[key] = b.count - 1
	} else if b.count > linearLimit {
		b.buildIndex()
	}
}

// Remove removes key; false when it is absent.
func (b *ObjectBuilder) Remove(key string) bool {
	b.ensureNotBuilt()
	i := b.indexOf(key)
	if i < 0 {
		return false
	}

	copy(b.keys[i:], b.keys[i+1:b.count])
	copy(b.values[i:], b.values[i+1:b.count])
	b.count--
	b.keys[b.count] = ""
	b.values[b.count] = Null
	b.index = nil
	if b.count > linearLimit {
		b.buildIndex()
	}
	return true
}

// ToObject hands the storage to a new Object.
func (b *ObjectBuilder) ToObject() *Object {
	b.ensureNotBuilt()
	b.built = true
	return objectFromOwned(b.keys, b.values, b.count, b.index)
}

func (b *ObjectBuilder) indexOf(key string) int {
	if b.index != nil {
		if i, ok := b.index[key]; ok {
			return i
		}
		return -1
	}
	for i := 0; i < b.count; i++ {
		if b.keys[i] == key {
			return i
		}
	}
	return -1
}

func (b *ObjectBuilder) buildIndex() {
	b.index = make(map[string]int, b.count*2)
	for i := 0; i < b.count; i++ {
		b.index[b.keys[i]] = i
	}
}

func (b *ObjectBuilder) ensureNotBuilt() {
	if b.built {
		panic("The builder was already turned into an object.")
	}
}
