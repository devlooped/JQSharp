package builtins

import (
	"math"

	"github.com/devlooped/jqlite/go/internal/runtime"
	"github.com/devlooped/jqlite/go/internal/values"
)

type assignNatives struct{}

// AssignNatives are _modify/2 and _assign/2 (§8.3) implemented natively, overriding their prelude
// definitions (Native.OverridesPrelude): same results, errors, order of evaluation and path
// behavior, but linear in the number of paths, where the prelude's one setpath per path copies the
// container each time. The value being updated is a WorkingTree.
var AssignNatives assignNatives

func (assignNatives) Register() {
	addC("_modify", 2, modify)
	addC("_assign", 2, assign)
}

// modify: def _modify(paths; update): for each path of paths (on the input, lazily), the first
// output of update applied to the current value there replaces it; no output: the path is deleted
// at the end. update runs in value mode; the result has no path.
func modify(in *interpreter, e *env, arguments []node, input value, p *path, out emit) *signal {
	tree := NewWorkingTree(input)
	var deletions []value
	first := runtime.BreakSignal(in.NewLabel())
	s := in.Eval(arguments[0], e, input, runtime.RootPath, func(v value, at *path) *signal {
		if at == nil || !at.IsTracked() {
			return runtime.Fail(values.Errors.InvalidPathResult(v))
		}
		keys := at.ToValue()
		current, err, success := tree.TryGet(keys.AsArray())
		if !success {
			return runtime.ErrorSignal(err)
		}
		updated := false
		next := values.Null
		stop := in.Eval(arguments[1], e, current, nil, func(output value, _ *path) *signal {
			updated, next = true, output
			return first
		})
		if stop != nil && stop != first {
			return stop
		}
		if !updated {
			deletions = append(deletions, keys)
			return nil
		}
		if err, success := tree.TrySet(keys.AsArray(), next); !success {
			return runtime.ErrorSignal(err)
		}
		return nil
	})
	if s != nil {
		return s
	}
	paths := values.FromArray(values.ArrayFromOwned(deletions))
	result, failed, success := PathNatives.TryDeletePaths(tree.Freeze(), paths)
	if !success {
		return runtime.ErrorSignal(failed)
	}
	return runtime.Output(out, result, runtime.PathOf(p))
}

// assign: def _assign(paths; $value): for each output of value (on the input), every path of paths
// set to it. With no path the result is the input itself, with its path (the reduce's initial state).
func assign(in *interpreter, e *env, arguments []node, input value, p *path, out emit) *signal {
	return in.ForEach(arguments[1], e, input, nil, func(v value, _ *path) *signal {
		tree := NewWorkingTree(input)
		any := false
		s := in.Eval(arguments[0], e, input, runtime.RootPath, func(target value, at *path) *signal {
			if at == nil || !at.IsTracked() {
				return runtime.Fail(values.Errors.InvalidPathResult(target))
			}
			any = true
			if err, success := tree.TrySet(at.ToValue().AsArray(), v); !success {
				return runtime.ErrorSignal(err)
			}
			return nil
		})
		if s != nil {
			return s
		}
		if any {
			return runtime.Output(out, tree.Freeze(), runtime.PathOf(p))
		}
		return runtime.Output(out, input, p)
	})
}

// draft is a container copied from a value: its builder, the drafts below it by slot, and where it
// hangs (index in an array parent, key in an object parent).
type draft struct {
	array  *values.ArrayBuilder
	object *values.ObjectBuilder
	parent *draft
	index  int
	key    string
	hasKey bool
	items  map[int]*draft
	fields map[string]*draft
	// The order children were attached in, so that freezing is deterministic.
	children []*draft
}

func (d *draft) build() value {
	if d.array != nil {
		return values.FromArray(d.array.ToArray())
	}
	return values.FromObject(d.object.ToObject())
}

// slot is a slot of the tree: the root (parent nil), an element of an array draft or a field of an
// object draft.
type slot struct {
	parent *draft
	index  int
	key    string
	hasKey bool
}

// WorkingTree is a value under update, owned by one call of _modify or _assign. Each node is either
// an immutable value or a draft: a container the tree copied (one level) and then updates in place,
// so a container is copied at most once however many paths go through it.
//
// Nothing a program can see is ever changed. TrySet stores the new value as an immutable leaf and
// opens drafts only along its path; TryGet, which hands a value to the update closure, and Freeze
// turn the drafts below the path back into values, giving them their storage without copying, so a
// later write there copies that level again. Paths with keys the drafts do not handle (slices, keys
// that setpath rejects) fall back to getpath/setpath on the frozen value below that point, which
// keeps their results and errors, in their order, those of the prelude definition. No walk
// recurses: paths and drafts are followed with loops and an explicit stack.
type WorkingTree struct {
	root      value
	rootDraft *draft
}

// NewWorkingTree starts a tree from a value.
func NewWorkingTree(root value) *WorkingTree { return &WorkingTree{root: root} }

// Freeze gives the value of the whole tree; the tree holds no draft afterwards.
func (t *WorkingTree) Freeze() value { return t.freezeAt(slot{}) }

// TryGet is getpath(keys) on the current value. The value given is frozen in the tree, so the
// caller may keep it: the tree never changes it.
func (t *WorkingTree) TryGet(keys *values.Array) (value, value, bool) {
	at := slot{}
	for i := 0; i < keys.Count(); i++ {
		if d := t.draftAt(at); d != nil {
			if next, missing, read := tryRead(d, keys.At(i)); read {
				if !missing {
					at = next
					continue
				}
				// An array element out of range: null, which the rest of the path indexes.
				return tryIndexFrom(values.Null, keys, i+1)
			}
		}
		return tryIndexFrom(t.freezeAt(at), keys, i)
	}
	return t.freezeAt(at), values.Null, true
}

// TrySet is setpath(keys; v) on the current value.
func (t *WorkingTree) TrySet(keys *values.Array, v value) (value, bool) {
	at := slot{}
	for i := 0; i < keys.Count(); i++ {
		key := keys.At(i)
		d := t.draftAt(at)
		if d != nil {
			if next, written := tryWrite(d, key); written {
				at = next
				continue
			}
		}

		var current value
		if d == nil {
			current = t.valueAt(at)
		} else {
			current = t.freezeAt(at)
		}
		if d == nil && appends(current, key) {
			// A new key or the next element: the rest of the path is set on the null it holds,
			// then added to the value itself, in place when it is the latest version of its
			// storage (§2.9 of the porting guide), as . + {($k): $v} does.
			inner, err, success := PathNatives.TrySetPath(values.Null, values.FromArray(keys.Slice(i+1, keys.Count())), v)
			if !success {
				return err, false
			}
			if current.Kind() == values.KindObject {
				t.store(at, values.FromObject(current.AsObject().Set(key.AsString(), inner)))
			} else {
				t.store(at, values.FromArray(current.AsArray().Append(inner)))
			}
			return values.Null, true
		}

		if opened := t.tryOpen(current, key, at); opened != nil {
			if next, written := tryWrite(opened, key); written {
				at = next
				continue
			}
		}

		// A key the drafts do not handle: setpath with the rest of the path, on the value here.
		result, err, success := PathNatives.TrySetPath(current, values.FromArray(keys.Slice(i, keys.Count())), v)
		if !success {
			return err, false
		}
		t.store(at, result)
		return values.Null, true
	}

	t.store(at, v)
	return values.Null, true
}

// tryIndexFrom indexes keys[from..] from v, as getpath does.
func tryIndexFrom(v value, keys *values.Array, from int) (value, value, bool) {
	result := v
	for i := from; i < keys.Count(); i++ {
		next, err, success := values.ValueOps.TryIndex(result, keys.At(i))
		if !success {
			return values.Null, err, false
		}
		result = next
	}
	return result, values.Null, true
}

// tryRead gives the slot a read goes to next: a field (present or not) or an element in range; an
// element out of range is missing (the value there is null). False for keys a draft does not read
// itself.
func tryRead(d *draft, key value) (next slot, missing, read bool) {
	if d.object != nil {
		if key.Kind() != values.KindString {
			return slot{}, false, false
		}
		return slot{parent: d, key: key.AsString(), hasKey: true}, false, true
	}

	if key.Kind() != values.KindNumber {
		return slot{}, false, false
	}
	number := key.AsNumber()
	index := -1.0
	if !math.IsNaN(number) {
		index = math.Trunc(number)
	}
	if index < 0 {
		index += float64(d.array.Count())
	}
	missing = math.IsNaN(number) || index < 0 || index >= float64(d.array.Count())
	if missing {
		return slot{parent: d}, true, true
	}
	return slot{parent: d, index: int(index)}, false, true
}

// tryWrite gives the slot a write goes to next, when setpath accepts the key for this container
// without error.
func tryWrite(d *draft, key value) (slot, bool) {
	if d.object != nil {
		if key.Kind() != values.KindString {
			return slot{}, false
		}
		return slot{parent: d, key: key.AsString(), hasKey: true}, true
	}
	index, success := tryArrayIndex(key, d.array.Count())
	if !success {
		return slot{}, false
	}
	return slot{parent: d, index: index}, true
}

// appends tells whether setpath with this key adds a key the object lacks, or the element just
// past the end of the array.
func appends(v value, key value) bool {
	switch v.Kind() {
	case values.KindObject:
		return key.Kind() == values.KindString && !v.AsObject().ContainsKey(key.AsString())
	case values.KindArray:
		index, success := tryArrayIndex(key, v.AsArray().Count())
		return success && index == v.AsArray().Count()
	}
	return false
}

// tryArrayIndex gives setpath's index of an array of the given length: not NaN, negative counted
// from the end, within the limit.
func tryArrayIndex(key value, length int) (int, bool) {
	if key.Kind() != values.KindNumber || math.IsNaN(key.AsNumber()) {
		return 0, false
	}
	i := math.Trunc(key.AsNumber())
	if i < 0 {
		i += float64(length)
	}
	if i < 0 || i > maxIndex {
		return 0, false
	}
	return int(i), true
}

// tryOpen copies one level of the value at a slot into a draft there, when setpath would write
// into it with this key (null becomes the container the key asks for).
func (t *WorkingTree) tryOpen(v value, key value, at slot) *draft {
	kind := v.Kind()
	var d *draft
	switch {
	case key.Kind() == values.KindString && (kind == values.KindObject || kind == values.KindNull):
		source := values.EmptyObject
		if kind != values.KindNull {
			source = v.AsObject()
		}
		d = &draft{object: values.NewObjectBuilderFrom(source, 4)}
	case key.Kind() == values.KindNumber && (kind == values.KindArray || kind == values.KindNull):
		source := values.EmptyArray
		if kind != values.KindNull {
			source = v.AsArray()
		}
		if _, success := tryArrayIndex(key, source.Count()); !success {
			return nil
		}
		d = &draft{array: values.NewArrayBuilder(source)}
	default:
		return nil
	}
	d.parent, d.index, d.key, d.hasKey = at.parent, at.index, at.key, at.hasKey
	t.attach(at, d)
	return d
}

func (t *WorkingTree) draftAt(at slot) *draft {
	if at.parent == nil {
		return t.rootDraft
	}
	if at.hasKey {
		return at.parent.fields[at.key]
	}
	return at.parent.items[at.index]
}

// valueAt gives the value at a slot that holds no draft.
func (t *WorkingTree) valueAt(at slot) value {
	if at.parent == nil {
		return t.root
	}
	if at.hasKey {
		field, _ := at.parent.object.TryGetValue(at.key)
		return field
	}
	if at.index < at.parent.array.Count() {
		return at.parent.array.At(at.index)
	}
	return values.Null
}

// store puts a value at a slot, replacing whatever was there (a value or a draft).
func (t *WorkingTree) store(at slot, v value) {
	switch {
	case at.parent == nil:
		t.root, t.rootDraft = v, nil
	case at.hasKey:
		at.parent.object.Set(at.key, v)
		delete(at.parent.fields, at.key)
	default:
		at.parent.array.Set(at.index, v)
		delete(at.parent.items, at.index)
	}
}

// attach hangs a draft at a slot. Its place in the parent is taken now (a new key goes last now,
// missing elements become null now), as setpath would; the value is written when it freezes.
func (t *WorkingTree) attach(at slot, d *draft) {
	t.store(at, values.Null)
	switch {
	case at.parent == nil:
		t.rootDraft = d
	case at.hasKey:
		if at.parent.fields == nil {
			at.parent.fields = map[string]*draft{}
		}
		at.parent.fields[at.key] = d
		at.parent.children = append(at.parent.children, d)
	default:
		if at.parent.items == nil {
			at.parent.items = map[int]*draft{}
		}
		at.parent.items[at.index] = d
		at.parent.children = append(at.parent.children, d)
	}
}

// isAttached tells whether a child draft is still the one at its slot of its parent.
func isAttached(d *draft) bool {
	if d.hasKey {
		return d.parent.fields[d.key] == d
	}
	return d.parent.items[d.index] == d
}

// freezeAt gives the value at a slot, freezing the drafts there first: children before parents,
// each written into its parent's builder, the top one stored at the slot.
func (t *WorkingTree) freezeAt(at slot) value {
	top := t.draftAt(at)
	if top == nil {
		return t.valueAt(at)
	}

	var order []*draft
	pending := []*draft{top}
	for len(pending) > 0 {
		d := pending[len(pending)-1]
		pending = pending[:len(pending)-1]
		order = append(order, d)
		for _, child := range d.children {
			if isAttached(child) {
				pending = append(pending, child)
			}
		}
	}

	for i := len(order) - 1; i > 0; i-- {
		d := order[i]
		built := d.build()
		if d.hasKey {
			d.parent.object.Set(d.key, built)
		} else {
			d.parent.array.Set(d.index, built)
		}
	}

	frozen := top.build()
	t.store(at, frozen)
	return frozen
}
