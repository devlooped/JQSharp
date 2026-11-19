package builtins

import (
	"math"

	"github.com/devlooped/jqlite/go/internal/values"
)

type collectionNatives struct{}

// CollectionNatives are §9.4: keys, membership, sorting and its relatives.
var CollectionNatives collectionNatives

func (collectionNatives) Register() {
	add("keys", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) { return keys(input, true) })
	add("keys_unsorted", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) { return keys(input, false) })
	add("has", 1, has)
	add("sort", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) {
		if input.Kind() == values.KindArray {
			return ok(values.FromArray(values.ValueOps.Sort(input.AsArray())))
		}
		return fail(values.Errors.CannotBeSorted(input))
	})
	add("unique", 0, unique)
	add("_sort_by_impl", 1, sortBy)
	add("_group_by_impl", 1, groupBy)
	add("_min_by_impl", 1, func(_ *interpreter, input value, arguments []value) (value, value, bool) {
		return extreme(input, arguments[0], false)
	})
	add("_max_by_impl", 1, func(_ *interpreter, input value, arguments []value) (value, value, bool) {
		return extreme(input, arguments[0], true)
	})
	add("min", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) { return extreme(input, input, false) })
	add("max", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) { return extreme(input, input, true) })
	add("bsearch", 1, binarySearch)
}

func keys(input value, sorted bool) (value, value, bool) {
	if input.Kind() == values.KindObject {
		obj := input.AsObject()
		result := make([]value, obj.Count())
		var order []string
		if sorted {
			order = obj.SortedKeys()
		}
		for i := range result {
			if order == nil {
				result[i] = values.FromString(obj.KeyAt(i))
			} else {
				result[i] = values.FromString(order[i])
			}
		}
		return ok(values.FromArray(values.ArrayFromOwned(result)))
	}

	if input.Kind() != values.KindArray {
		return fail(values.Errors.HasNoKeys(input))
	}
	indices := make([]value, input.AsArray().Count())
	for i := range indices {
		indices[i] = values.FromNumber(float64(i))
	}
	return ok(values.FromArray(values.ArrayFromOwned(indices)))
}

func has(_ *interpreter, input value, arguments []value) (value, value, bool) {
	key := arguments[0]
	if input.Kind() == values.KindObject && key.Kind() == values.KindString {
		return ok(values.FromBool(input.AsObject().ContainsKey(key.AsString())))
	}
	if input.Kind() == values.KindArray && key.Kind() == values.KindNumber {
		index := math.Trunc(key.AsNumber())
		return ok(values.FromBool(index >= 0 && index < float64(input.AsArray().Count())))
	}
	if input.Kind() == values.KindNull {
		return ok(values.False)
	}
	return fail(values.Errors.CannotCheckHas(input, key))
}

func unique(_ *interpreter, input value, _ []value) (value, value, bool) {
	if input.Kind() != values.KindArray {
		return fail(values.Errors.CannotBeSorted(input))
	}
	sorted := values.ValueOps.Sort(input.AsArray())
	kept := make([]value, 0, sorted.Count())
	for i := 0; i < sorted.Count(); i++ {
		if i == 0 || values.ValueOps.Compare(sorted.At(i-1), sorted.At(i)) != 0 {
			kept = append(kept, sorted.At(i))
		}
	}
	return ok(values.FromArray(values.ArrayFromOwned(kept)))
}

// sortBy: _sort_by_impl(keys): the input stably sorted by the corresponding keys.
func sortBy(_ *interpreter, input value, arguments []value) (value, value, bool) {
	if !sameLengthArrays(input, arguments[0]) {
		return fail(values.Errors.CannotBeSortedBy(input, arguments[0]))
	}
	items := input.AsArray()
	order := values.ValueOps.SortOrder(arguments[0].AsArray())
	sorted := make([]value, len(order))
	for i, at := range order {
		sorted[i] = items.At(at)
	}
	return ok(values.FromArray(values.ArrayFromOwned(sorted)))
}

// groupBy: _group_by_impl(keys): sorted as _sort_by_impl, then consecutive elements with equal
// keys grouped.
func groupBy(_ *interpreter, input value, arguments []value) (value, value, bool) {
	if !sameLengthArrays(input, arguments[0]) {
		return fail(values.Errors.CannotBeSortedBy(input, arguments[0]))
	}
	items := input.AsArray()
	by := arguments[0].AsArray()
	order := values.ValueOps.SortOrder(by)
	var groups, group []value
	for i, at := range order {
		if i > 0 && values.ValueOps.Compare(by.At(order[i-1]), by.At(at)) != 0 {
			groups = append(groups, values.FromArray(values.ArrayFromOwned(group)))
			group = nil
		}
		group = append(group, items.At(at))
	}
	if len(group) > 0 {
		groups = append(groups, values.FromArray(values.ArrayFromOwned(group)))
	}
	return ok(values.FromArray(values.ArrayFromOwned(groups)))
}

// extreme is _min_by_impl, _max_by_impl, min, max: null for an empty input; else the first element
// with the smallest key, or the last with the largest.
func extreme(input, keys value, isMax bool) (value, value, bool) {
	if !bothArrays(input, keys) {
		return fail(values.Errors.CannotBeIterated(input, keys))
	}
	items := input.AsArray()
	by := keys.AsArray()
	if items.Count() != by.Count() {
		return fail(values.Errors.WrongLength(input, keys))
	}
	if items.Count() == 0 {
		return ok(values.Null)
	}
	best := 0
	for i := 1; i < items.Count(); i++ {
		c := values.ValueOps.Compare(by.At(i), by.At(best))
		if (isMax && c >= 0) || (!isMax && c < 0) {
			best = i
		}
	}
	return ok(items.At(best))
}

func binarySearch(_ *interpreter, input value, arguments []value) (value, value, bool) {
	if input.Kind() != values.KindArray {
		return fail(values.Errors.CannotBeSearched(input))
	}
	items := input.AsArray()
	low, high := 0, items.Count()-1
	for low <= high {
		middle := (low + high) / 2
		c := values.ValueOps.Compare(items.At(middle), arguments[0])
		if c == 0 {
			return ok(values.FromNumber(float64(middle)))
		}
		if c < 0 {
			low = middle + 1
		} else {
			high = middle - 1
		}
	}
	return ok(values.FromNumber(float64(-1 - low)))
}

func bothArrays(a, b value) bool { return a.Kind() == values.KindArray && b.Kind() == values.KindArray }

func sameLengthArrays(a, b value) bool {
	return bothArrays(a, b) && a.AsArray().Count() == b.AsArray().Count()
}
