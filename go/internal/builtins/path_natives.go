package builtins

import (
	"math"

	"github.com/devlooped/jqlite/go/internal/runtime"
	"github.com/devlooped/jqlite/go/internal/values"
)

type pathNatives struct{}

// PathNatives are §9.5 and §8: path, getpath, setpath, delpaths.
var PathNatives pathNatives

// maxIndex is setpath's index limit (§11).
const maxIndex = 536870911

func (pathNatives) Register() {
	addC("path", 1, pathOf)
	addC("getpath", 1, getPath)
	add("setpath", 2, func(_ *interpreter, input value, arguments []value) (value, value, bool) {
		return PathNatives.TrySetPath(input, arguments[0], arguments[1])
	})
	add("delpaths", 1, func(_ *interpreter, input value, arguments []value) (value, value, bool) {
		return PathNatives.TryDeletePaths(input, arguments[0])
	})
}

// pathOf: path(f): f in path mode from the empty path; each output must have a path.
func pathOf(in *interpreter, e *env, arguments []node, input value, p *path, out emit) *signal {
	at := runtime.PathOf(p)
	return in.Eval(arguments[0], e, input, runtime.RootPath, func(v value, valueAt *path) *signal {
		if valueAt != nil && valueAt.IsTracked() {
			return runtime.Output(out, valueAt.ToValue(), at)
		}
		return runtime.Fail(values.Errors.InvalidPathResult(v))
	})
}

// getPath: getpath(p): p in value mode; the output's path is the input's followed by p, even when absent.
func getPath(in *interpreter, e *env, arguments []node, input value, p *path, out emit) *signal {
	return in.ForEach(arguments[0], e, input, nil, func(keys value, _ *path) *signal {
		if keys.Kind() != values.KindArray {
			return runtime.Fail(values.Errors.PathNotArray())
		}
		steps := keys.AsArray()
		v := input
		for i := 0; i < steps.Count(); i++ {
			next, err, success := values.ValueOps.TryIndex(v, steps.At(i))
			if !success {
				return runtime.ErrorSignal(err)
			}
			v = next
		}
		at := p
		if p != nil && p.IsTracked() {
			at = p.AppendAll(steps)
		}
		return runtime.Output(out, v, at)
	})
}

// TrySetPath is setpath(p; v) (§8.2): each container on the way is indexed first (so its errors
// come first), then the containers are rebuilt from the innermost out, each copied one level.
func (pathNatives) TrySetPath(root, keys, v value) (value, value, bool) {
	if keys.Kind() != values.KindArray {
		return fail(values.Errors.PathNotArray())
	}
	steps := keys.AsArray()
	containers := make([]value, steps.Count())
	current := root
	for i := range containers {
		containers[i] = current
		next, err, success := values.ValueOps.TryIndex(current, steps.At(i))
		if !success {
			return fail(err)
		}
		current = next
	}

	for i := len(containers) - 1; i >= 0; i-- {
		next, err, success := trySet(containers[i], steps.At(i), v)
		if !success {
			return fail(err)
		}
		v = next
	}
	return ok(v)
}

// trySet is one level of setpath: the table of §8.2.
func trySet(container, key, v value) (value, value, bool) {
	kind := container.Kind()
	switch {
	case key.Kind() == values.KindString && (kind == values.KindObject || kind == values.KindNull):
		obj := values.EmptyObject
		if kind != values.KindNull {
			obj = container.AsObject()
		}
		return ok(values.FromObject(obj.Set(key.AsString(), v)))
	case key.Kind() == values.KindNumber && (kind == values.KindArray || kind == values.KindNull):
		array := values.EmptyArray
		if kind != values.KindNull {
			array = container.AsArray()
		}
		number := key.AsNumber()
		if math.IsNaN(number) {
			return fail(values.Errors.NaNIndex())
		}
		index := math.Trunc(number)
		if index < 0 {
			index += float64(array.Count())
		}
		if index < 0 {
			return fail(values.Errors.NegativeIndex())
		}
		if index > maxIndex {
			return fail(values.Errors.IndexTooLarge())
		}
		return ok(values.FromArray(array.SetItem(int(index), v)))
	case key.Kind() == values.KindObject && (kind == values.KindArray || kind == values.KindNull):
		array := values.EmptyArray
		if kind != values.KindNull {
			array = container.AsArray()
		}
		return trySetSlice(array, key.AsObject(), v)
	case key.Kind() == values.KindObject && kind == values.KindString:
		return fail(values.Errors.CannotUpdateStringSlices())
	default:
		return fail(values.Errors.CannotUpdateField(key, container))
	}
}

// trySetSlice: .[s:e] = v on an array: v (an array) replaces the elements the bounds select.
func trySetSlice(array *values.Array, slice *values.Object, v value) (value, value, bool) {
	start, hasStart := slice.TryGetValue("start")
	end, hasEnd := slice.TryGetValue("end")
	if !hasStart || !hasEnd {
		return fail(values.Errors.SliceIndicesNotIntegers())
	}
	first, last, err, success := values.ValueOps.TrySliceRange(array.Count(), start, end)
	if !success {
		return fail(err)
	}
	if v.Kind() != values.KindArray {
		return fail(values.Errors.SliceNeedsArray())
	}
	replaced := array.Slice(0, first).Concat(v.AsArray()).Concat(array.Slice(last, array.Count()))
	return ok(values.FromArray(replaced))
}

// TryDeletePaths is delpaths(ps) (§8.2): the paths sorted, then deleted level by level.
func (pathNatives) TryDeletePaths(root, paths value) (value, value, bool) {
	if paths.Kind() != values.KindArray {
		return fail(values.Errors.PathsNotArray())
	}
	list := paths.AsArray()
	for i := 0; i < list.Count(); i++ {
		if list.At(i).Kind() != values.KindArray {
			return fail(values.Errors.PathElementNotArray(list.At(i)))
		}
		if list.At(i).AsArray().Count() == 0 {
			return ok(values.Null)
		}
	}

	sorted := values.ValueOps.Sort(list)
	return tryDelete(root, sorted, 0, sorted.Count(), 0)
}

// level is one container being rebuilt by tryDelete: the paths [next, to) still to look at, at
// depth; the keys deleted at this level; and the key of the child being rebuilt below it, if any.
type level struct {
	value    value
	next     int
	to       int
	depth    int
	deleted  []value
	childKey value
	childEnd int
}

// tryDelete deletes paths[from, to) (sorted, all longer than depth) from v at the given depth:
// groups by the key at that depth; a group holding the path that ends there deletes the key, the
// others go into the child first. All the keys of one level are removed at once. The levels being
// rebuilt are on an explicit stack, so a path of any length is no recursion.
func tryDelete(v value, paths *values.Array, from, to, depth int) (value, value, bool) {
	keyAt := func(i, depth int) value { return paths.At(i).AsArray().At(depth) }
	levels := []*level{{value: v, next: from, to: to, depth: depth}}
	for {
		l := levels[len(levels)-1]
		descended := false
		for l.value.Kind() != values.KindNull && l.next < l.to && !descended {
			key := keyAt(l.next, l.depth)
			end := l.next + 1
			ends := paths.At(l.next).AsArray().Count() == l.depth+1
			for ; end < l.to && values.ValueOps.Compare(keyAt(end, l.depth), key) == 0; end++ {
				ends = ends || paths.At(end).AsArray().Count() == l.depth+1
			}

			if ends {
				l.deleted = append(l.deleted, key)
				l.next = end
				continue
			}

			child, err, success := values.ValueOps.TryIndex(l.value, key)
			if !success {
				return fail(err)
			}
			if child.Kind() == values.KindNull {
				l.next = end
				continue
			}

			l.childKey, l.childEnd = key, end
			levels = append(levels, &level{value: child, next: l.next, to: end, depth: l.depth + 1})
			descended = true
		}

		if descended {
			continue
		}

		// This level is done: remove its keys, then put it back into its parent.
		done, err, success := tryRemove(l.value, l.deleted)
		if !success {
			return fail(err)
		}
		levels = levels[:len(levels)-1]
		if len(levels) == 0 {
			return ok(done)
		}
		parent := levels[len(levels)-1]
		set, err, success := trySet(parent.value, parent.childKey, done)
		if !success {
			return fail(err)
		}
		parent.value = set
		parent.next = parent.childEnd
	}
}

// tryRemove removes keys from a container, all relative to the container as it is.
func tryRemove(container value, keys []value) (value, value, bool) {
	if len(keys) == 0 || container.Kind() == values.KindNull {
		return ok(container)
	}
	if container.Kind() == values.KindObject {
		obj := container.AsObject()
		for _, key := range keys {
			if key.Kind() != values.KindString {
				return fail(values.Errors.CannotDeleteField(key))
			}
			obj = obj.Remove(key.AsString())
		}
		return ok(values.FromObject(obj))
	}

	if container.Kind() != values.KindArray {
		return fail(values.Errors.CannotDeleteFrom(container))
	}

	array := container.AsArray()
	removed := make([]bool, array.Count())
	for _, key := range keys {
		switch key.Kind() {
		case values.KindNumber:
			number := key.AsNumber()
			if math.IsNaN(number) {
				continue
			}
			index := math.Trunc(number)
			if index < 0 {
				index += float64(array.Count())
			}
			if index >= 0 && index < float64(array.Count()) {
				removed[int(index)] = true
			}
		case values.KindObject:
			slice := key.AsObject()
			start, hasStart := slice.TryGetValue("start")
			end, hasEnd := slice.TryGetValue("end")
			if !hasStart || !hasEnd {
				return fail(values.Errors.SliceIndicesNotIntegers())
			}
			first, last, err, success := values.ValueOps.TrySliceRange(array.Count(), start, end)
			if !success {
				return fail(err)
			}
			for i := first; i < last; i++ {
				removed[i] = true
			}
		default:
			return fail(values.Errors.CannotDeleteElement(key))
		}
	}

	kept := make([]value, 0, array.Count())
	for i := 0; i < array.Count(); i++ {
		if !removed[i] {
			kept = append(kept, array.At(i))
		}
	}
	return ok(values.FromArray(values.ArrayFromOwned(kept)))
}
