package values

import (
	"math"
	"strings"
)

type valueOps struct{}

// ValueOps is the one implementation of equality, ordering, arithmetic, slicing and sorting of
// values, with jq 1.8 semantics.
//
// An operation that can fail in jq gives ok false and the jqlite error value (the message string)
// in err; on success it gives ok true and result. Nothing here panics as part of jqlite semantics.
var ValueOps valueOps

// maxStringBytes is the longest string a repeat may build (§11: 2^31 - 1 UTF-8 bytes).
const maxStringBytes = math.MaxInt32

// AreEqual is jq's ==: same kind and same content. Numbers compare as doubles, so nan == nan is
// false and so is [nan] == [nan]; objects are equal whatever their key order.
func (valueOps) AreEqual(a, b Value) bool { return walk(a, b, true) == 0 }

// Compare is jq's total order; gives -1, 0 or 1. Kinds first: null < false < true < numbers <
// strings < arrays < objects. Strings by code point; arrays lexicographically; objects by their
// sorted key lists, then by their values in that key order. NaN is below every other number and
// equal to NaN, so the order is total (jq 1.8 has nan < nan; spec §13).
func (valueOps) Compare(a, b Value) int { return walk(a, b, false) }

// walkFrame is a pair of containers being walked: children 0 to count - 1 are compared in turn,
// then tail (the length comparison of two arrays) decides.
type walkFrame struct {
	a, b  Value
	count int
	index int
	tail  int
	keys  []string
}

// walk compares (equality false) or tests equality (equality true, any non-zero result means
// "different") without recursion: the containers being compared are on an explicit stack, so
// nesting depth is not limited by the host stack.
func walk(a, b Value, equality bool) int {
	c, frame := shallow(a, b, equality)
	if c != 0 || frame.count == 0 {
		if c != 0 {
			return c
		}
		return frame.tail
	}

	stack := []walkFrame{frame}
	var found bool
	if a, b, found = child(&frame, equality); !found {
		return 1
	}

	for {
		c, frame = shallow(a, b, equality)
		if c != 0 {
			return c
		}
		if frame.count > 0 {
			stack = append(stack, frame)
			if a, b, found = child(&frame, equality); !found {
				return 1
			}
			continue
		}

		if frame.tail != 0 {
			return frame.tail
		}

		// The pair is equal: go on with the next pair, leaving the containers that end.
		for {
			if len(stack) == 0 {
				return 0
			}

			top := &stack[len(stack)-1]
			top.index++
			if top.index < top.count {
				if a, b, found = child(top, equality); !found {
					return 1
				}
				break
			}

			tail := top.tail
			stack = stack[:len(stack)-1]
			if tail != 0 {
				return tail
			}
		}
	}
}

// shallow compares what can be compared without looking at children. A non-zero result decides.
// With zero, frame.count > 0 means the children must be compared, and frame.tail is the result
// once they are all equal.
func shallow(a, b Value, equality bool) (int, walkFrame) {
	kind := a.kind
	other := b.kind
	if kind != other {
		if kind < other {
			return -1, walkFrame{}
		}
		return 1, walkFrame{}
	}

	switch kind {
	case KindNumber:
		x, y := a.num, b.num
		if equality {
			if x == y {
				return 0, walkFrame{}
			}
			return 1, walkFrame{}
		}
		if math.IsNaN(x) {
			if math.IsNaN(y) {
				return 0, walkFrame{}
			}
			return -1, walkFrame{}
		}
		if math.IsNaN(y) {
			return 1, walkFrame{}
		}
		if x < y {
			return -1, walkFrame{}
		}
		if x == y {
			return 0, walkFrame{}
		}
		return 1, walkFrame{}
	case KindString:
		if equality {
			if a.ref.(string) == b.ref.(string) {
				return 0, walkFrame{}
			}
			return 1, walkFrame{}
		}
		return CodePoints.Compare(a.ref.(string), b.ref.(string)), walkFrame{}
	case KindArray:
		p := a.AsArray().Count()
		q := b.AsArray().Count()
		if equality && p != q {
			return 1, walkFrame{}
		}
		tail := 0
		if p < q {
			tail = -1
		} else if p > q {
			tail = 1
		}
		return 0, walkFrame{a: a, b: b, count: min(p, q), tail: tail}
	case KindObject:
		m := a.AsObject()
		n := b.AsObject()
		if equality {
			// Same size, then each key of a found in b with an equal value: no sorting.
			if m.Count() != n.Count() {
				return 1, walkFrame{}
			}
			return 0, walkFrame{a: a, b: b, count: m.Count()}
		}

		keys := m.SortedKeys()
		others := n.SortedKeys()
		length := min(len(keys), len(others))
		for i := 0; i < length; i++ {
			if c := CodePoints.Compare(keys[i], others[i]); c != 0 {
				return c, walkFrame{}
			}
		}

		if len(keys) != len(others) {
			if len(keys) < len(others) {
				return -1, walkFrame{}
			}
			return 1, walkFrame{}
		}
		return 0, walkFrame{a: a, b: b, count: len(keys), keys: keys}
	default:
		return 0, walkFrame{}
	}
}

// child gives the children at frame.index; false when b lacks a key of a (equality of objects only).
func child(frame *walkFrame, equality bool) (Value, Value, bool) {
	if frame.a.kind == KindArray {
		return frame.a.AsArray().At(frame.index), frame.b.AsArray().At(frame.index), true
	}

	x := frame.a.AsObject()
	y := frame.b.AsObject()
	if equality {
		b, ok := y.TryGetValue(x.KeyAt(frame.index))
		return x.ValueAt(frame.index), b, ok
	}

	key := frame.keys[frame.index]
	a, _ := x.TryGetValue(key)
	b, _ := y.TryGetValue(key)
	return a, b, true
}

// TryAdd is jq's +: null is the identity; numbers add; strings and arrays concatenate; objects
// merge (right wins, insertion order kept).
func (valueOps) TryAdd(a, b Value) (result, err Value, ok bool) {
	kind := a.kind
	if kind == KindNull {
		return b, Null, true
	}
	if b.kind == KindNull {
		return a, Null, true
	}

	if kind == b.kind {
		switch kind {
		case KindNumber:
			return FromNumber(a.num + b.num), Null, true
		case KindString:
			return Value{kind: KindString, ref: a.ref.(string) + b.ref.(string)}, Null, true
		case KindArray:
			return FromArray(a.AsArray().Concat(b.AsArray())), Null, true
		case KindObject:
			return FromObject(a.AsObject().Merge(b.AsObject())), Null, true
		}
	}

	return fail(Errors.CannotBe(a, b, Added))
}

// TrySubtract is jq's -: numbers subtract; arrays drop every element equal to one of the
// right-hand array.
func (valueOps) TrySubtract(a, b Value) (result, err Value, ok bool) {
	kind := a.kind
	if kind == KindNumber && b.kind == KindNumber {
		return FromNumber(a.num - b.num), Null, true
	}

	if kind == KindArray && b.kind == KindArray {
		items := a.AsArray()
		remove := b.AsArray()
		kept := EmptyArray
		for i := 0; i < items.Count(); i++ {
			found := false
			for j := 0; j < remove.Count() && !found; j++ {
				found = ValueOps.AreEqual(items.At(i), remove.At(j))
			}
			if !found {
				kept = kept.Append(items.At(i))
			}
		}
		return FromArray(kept), Null, true
	}

	return fail(Errors.CannotBe(a, b, Subtracted))
}

// TryNegate is jq's unary -: IEEE negation of a number (0 | -. is -0).
func (valueOps) TryNegate(value Value) (result, err Value, ok bool) {
	if value.kind != KindNumber {
		return fail(Errors.CannotNegate(value))
	}
	return FromNumber(-value.num), Null, true
}

// TryMultiply is jq's *: numbers multiply; a string and a number (either order) repeat the
// string; objects merge recursively.
func (valueOps) TryMultiply(a, b Value) (result, err Value, ok bool) {
	kind := a.kind
	other := b.kind
	if kind == KindNumber && other == KindNumber {
		return FromNumber(a.num * b.num), Null, true
	}

	if (kind == KindString && other == KindNumber) || (kind == KindNumber && other == KindString) {
		if kind == KindString {
			return tryRepeat(a.AsString(), b.num)
		}
		return tryRepeat(b.AsString(), a.num)
	}

	if kind == KindObject && other == KindObject {
		return FromObject(deepMerge(a.AsObject(), b.AsObject())), Null, true
	}

	return fail(Errors.CannotBe(a, b, Multiplied))
}

// tryRepeat is s * n: null for a NaN or negative n; otherwise s repeated trunc(n) times ("" below 1).
func tryRepeat(text string, times float64) (result, err Value, ok bool) {
	if math.IsNaN(times) || times < 0 {
		return Null, Null, true
	}

	count := math.MaxInt32
	if times < math.MaxInt32 {
		count = int(times)
	}
	if count == 0 || len(text) == 0 {
		return FromString(""), Null, true
	}

	// The spec's limit is 2^31 - 1 UTF-8 bytes.
	if int64(len(text))*int64(count) > maxStringBytes {
		return fail(Errors.RepeatTooLong())
	}

	return Value{kind: KindString, ref: strings.Repeat(text, count)}, Null, true
}

// mergeFrame is one pair of objects being merged: the result so far, the right side's next key,
// and the key whose values are being merged below.
type mergeFrame struct {
	result *ObjectBuilder
	right  *Object
	next   int
	key    string
}

// deepMerge is * on objects: like a + b, except that a key whose two values are objects gets
// their merge. The objects being merged are on an explicit stack, so nesting depth is not limited
// by the host stack.
func deepMerge(a, b *Object) *Object {
	stack := []*mergeFrame{{result: NewObjectBuilderFrom(a, b.Count()), right: b}}
	for {
		top := stack[len(stack)-1]
		if top.next < top.right.Count() {
			key := top.right.KeyAt(top.next)
			value := top.right.ValueAt(top.next)
			top.next++
			if existing, ok := top.result.TryGetValue(key); ok && value.kind == KindObject && existing.kind == KindObject {
				top.key = key
				right := value.AsObject()
				stack = append(stack, &mergeFrame{result: NewObjectBuilderFrom(existing.AsObject(), right.Count()), right: right})
			} else {
				top.result.Set(key, value)
			}
			continue
		}

		merged := top.result.ToObject()
		stack = stack[:len(stack)-1]
		if len(stack) == 0 {
			return merged
		}
		parent := stack[len(stack)-1]
		parent.result.Set(parent.key, FromObject(merged))
	}
}

// TryDivide is jq's /: numbers divide (a zero divisor is an error, 0/0 too); strings split on the
// right-hand string.
func (valueOps) TryDivide(a, b Value) (result, err Value, ok bool) {
	kind := a.kind
	if kind == KindNumber && b.kind == KindNumber {
		divisor := b.num
		if divisor == 0 {
			return fail(Errors.DivisorIsZero(a, b, Divided))
		}
		return FromNumber(a.num / divisor), Null, true
	}

	if kind == KindString && b.kind == KindString {
		return FromArray(ValueOps.Split(a.AsString(), b.AsString())), Null, true
	}

	return fail(Errors.CannotBe(a, b, Divided))
}

// Split is split/1 and / on strings: the empty string gives []; an empty separator splits into
// code points.
func (valueOps) Split(text, separator string) *Array {
	parts := EmptyArray
	if len(text) == 0 {
		return parts
	}

	if len(separator) == 0 {
		items := make([]Value, 0, len(text))
		for i := 0; i < len(text); {
			next := CodePoints.Advance(text, i, 1)
			items = append(items, Value{kind: KindString, ref: text[i:next]})
			i = next
		}
		return ArrayFromOwned(items)
	}

	start := 0
	for {
		found := strings.Index(text[start:], separator)
		if found < 0 {
			break
		}
		parts = parts.Append(Value{kind: KindString, ref: text[start : start+found]})
		start += found + len(separator)
	}
	return parts.Append(Value{kind: KindString, ref: text[start:]})
}

// TryModulo is jq's %: both numbers truncate to integers (saturating at the 64-bit range), the
// sign follows the dividend; NaN gives NaN; a divisor that truncates to 0 is an error.
func (valueOps) TryModulo(a, b Value) (result, err Value, ok bool) {
	if a.kind != KindNumber || b.kind != KindNumber {
		return fail(Errors.CannotBe(a, b, Remainder))
	}

	x, y := a.num, b.num
	if math.IsNaN(x) || math.IsNaN(y) {
		return FromNumber(math.NaN()), Null, true
	}

	divisor := toInteger(y)
	if divisor == 0 {
		return fail(Errors.DivisorIsZero(a, b, Remainder))
	}

	// -1 divides everything, and MinInt64 % -1 overflows.
	if divisor == -1 {
		return FromNumber(0), Null, true
	}
	return FromNumber(float64(toInteger(x) % divisor)), Null, true
}

// toInteger is truncation toward zero, clamped to [-2^63, 2^63 - 1] (±Infinity clamp to the ends).
func toInteger(value float64) int64 {
	if value <= math.MinInt64 {
		return math.MinInt64
	}
	if value >= math.MaxInt64 {
		return math.MaxInt64
	}
	return int64(value)
}

// TryIndex is .[key] (§7.6): an object by a string (absent: null), an array by a number (NaN and
// out of range: null; negative counts from the end) or by an array (the indices where it occurs),
// an array or a string by a slice object, null by a string, a number or an object (null);
// anything else is "Cannot index T with T (D)".
func (valueOps) TryIndex(target, key Value) (result, err Value, ok bool) {
	kind := target.kind
	switch {
	case key.kind == KindString && kind == KindObject:
		value, _ := target.AsObject().TryGetValue(key.AsString())
		return value, Null, true
	case key.kind == KindNumber && kind == KindArray:
		return element(target.AsArray(), key.num), Null, true
	case key.kind == KindArray && kind == KindArray:
		return FromArray(indices(target.AsArray(), key.AsArray())), Null, true
	case key.kind == KindObject && (kind == KindArray || kind == KindString || kind == KindNull):
		return ValueOps.TryIndexSlice(target, key)
	case (key.kind == KindString || key.kind == KindNumber) && kind == KindNull:
		return Null, Null, true
	default:
		return fail(Errors.CannotIndex(target, key))
	}
}

// element is .[n] on an array: NaN and out of range give null, a negative index counts from the end.
func element(array *Array, index float64) Value {
	if math.IsNaN(index) {
		return Null
	}
	i := math.Trunc(index)
	if i < 0 {
		i += float64(array.Count())
	}
	if i >= 0 && i < float64(array.Count()) {
		return array.At(int(i))
	}
	return Null
}

// indices gives the positions at which needle occurs in items as a contiguous sub-array; none for
// an empty needle.
func indices(items, needle *Array) *Array {
	var found []Value
	for start := 0; needle.Count() > 0 && start+needle.Count() <= items.Count(); start++ {
		i := 0
		for i < needle.Count() && ValueOps.AreEqual(items.At(start+i), needle.At(i)) {
			i++
		}
		if i == needle.Count() {
			found = append(found, FromNumber(float64(start)))
		}
	}
	return ArrayFromOwned(found)
}

// TrySlice is .[start:end] (§7.6) on an array, a string (by code point) or null (gives null). A
// null or NaN start is 0, a null or NaN end the length; a negative bound counts from the end; both
// are clamped, then the start rounds down and the end up.
func (valueOps) TrySlice(input, start, end Value) (result, err Value, ok bool) {
	if input.kind != KindNull && input.kind != KindArray && input.kind != KindString {
		// The key in the message is the slice object that .[s:e] stands for.
		key := EmptyObject.Set("start", start).Set("end", end)
		return fail(Errors.CannotIndex(input, FromObject(key)))
	}
	return trySliceBounds(input, start, end)
}

// TryIndexSlice is .[k] with an object key: on an array or a string the key is a slice when it
// has both start and end (other keys are ignored, as in jq), else the error "Array/string slice
// indices must be integers"; null input gives null whatever the key; any other input is
// "Cannot index T with object (D)".
func (valueOps) TryIndexSlice(input, key Value) (result, err Value, ok bool) {
	kind := input.kind
	if kind == KindNull {
		return trySliceBounds(input, Null, Null)
	}
	if kind != KindArray && kind != KindString {
		return fail(Errors.CannotIndex(input, key))
	}

	slice := key.AsObject()
	start, hasStart := slice.TryGetValue("start")
	end, hasEnd := slice.TryGetValue("end")
	if !hasStart || !hasEnd {
		return fail(Errors.SliceIndicesNotIntegers())
	}
	return trySliceBounds(input, start, end)
}

// trySliceBounds slices an array, a string or null (gives null).
func trySliceBounds(input, start, end Value) (result, err Value, ok bool) {
	kind := input.kind
	if kind == KindNull {
		return Null, Null, true
	}

	var length int
	if kind == KindString {
		length = CodePoints.Length(input.AsString())
	} else {
		length = input.AsArray().Count()
	}
	first, last, err, ok := ValueOps.TrySliceRange(length, start, end)
	if !ok {
		return Null, err, false
	}

	if kind == KindString {
		return Value{kind: KindString, ref: CodePoints.Substring(input.AsString(), first, last)}, Null, true
	}
	return FromArray(input.AsArray().Slice(first, last)), Null, true
}

// TrySliceRange gives the positions [first, last) that the bounds of a slice select in a
// sequence of length items (§7.6): null or NaN start is 0, null or NaN end the length, negative
// bounds count from the end, both are clamped, the start rounds down and the end up. Bounds that
// are not numbers or null: "Array/string slice indices must be integers".
func (valueOps) TrySliceRange(length int, start, end Value) (first, last int, err Value, ok bool) {
	if (start.kind != KindNull && start.kind != KindNumber) || (end.kind != KindNull && end.kind != KindNumber) {
		return 0, 0, Errors.SliceIndicesNotIntegers(), false
	}

	from := 0.0
	if start.kind != KindNull {
		from = start.num
	}
	to := float64(length)
	if end.kind != KindNull {
		to = end.num
	}
	if math.IsNaN(from) {
		from = 0
	}
	if math.IsNaN(to) {
		to = float64(length)
	}
	if from < 0 {
		from += float64(length)
	}
	if to < 0 {
		to += float64(length)
	}
	first = int(math.Floor(clamp(from, 0, float64(length))))
	last = int(math.Ceil(clamp(to, float64(first), float64(length))))
	return first, last, Null, true
}

func clamp(value, low, high float64) float64 {
	if value < low {
		return low
	}
	if value > high {
		return high
	}
	return value
}

// TryExplode is jq's explode: the code points of a string.
func (valueOps) TryExplode(input Value) (result, err Value, ok bool) {
	if input.kind != KindString {
		return fail(Errors.ExplodeNotString())
	}
	return FromArray(CodePoints.Explode(input.AsString())), Null, true
}

// TryImplode is jq's implode: an array of numbers to the string of the code points trunc(n); one
// outside [0, 0x10FFFF] or a surrogate becomes U+FFFD (-0.5 truncates to U+0000). A non-number or
// NaN element is an error that describes the element.
func (valueOps) TryImplode(input Value) (result, err Value, ok bool) {
	if input.kind != KindArray {
		return fail(Errors.ImplodeNotArray())
	}

	codes := input.AsArray()
	var builder strings.Builder
	builder.Grow(codes.Count())
	for i := 0; i < codes.Count(); i++ {
		code := codes.At(i)
		if code.kind != KindNumber || math.IsNaN(code.num) {
			return fail(Errors.CodepointNotNumeric(code))
		}
		number := math.Trunc(code.num)
		if number >= 0 && number <= MaxCodePoint && CodePoints.IsScalar(int(number)) {
			CodePoints.Append(&builder, int(number))
		} else {
			CodePoints.Append(&builder, Replacement)
		}
	}
	return Value{kind: KindString, ref: builder.String()}, Null, true
}

// Sort gives a stably sorted copy of items (jq's sort).
func (valueOps) Sort(items *Array) *Array {
	order := ValueOps.SortOrder(items)
	sorted := make([]Value, len(order))
	for i, at := range order {
		sorted[i] = items.At(at)
	}
	return ArrayFromOwned(sorted)
}

// SortOrder gives the positions of keys in sorted order, equal keys in their original order (a
// stable sort, for sort_by, group_by and unique_by): a bottom-up merge sort.
func (valueOps) SortOrder(keys *Array) []int {
	n := keys.Count()
	order := make([]int, n)
	for i := range order {
		order[i] = i
	}
	merged := make([]int, n)
	for width := 1; width < n; width *= 2 {
		for low := 0; low < n; low += 2 * width {
			middle := min(low+width, n)
			high := min(low+2*width, n)
			left, right, target := low, middle, low
			for left < middle && right < high {
				// Take the left one unless the right one is strictly smaller: stable.
				if ValueOps.Compare(keys.At(order[left]), keys.At(order[right])) <= 0 {
					merged[target] = order[left]
					left++
				} else {
					merged[target] = order[right]
					right++
				}
				target++
			}
			for left < middle {
				merged[target] = order[left]
				left++
				target++
			}
			for right < high {
				merged[target] = order[right]
				right++
				target++
			}
		}
		order, merged = merged, order
	}
	return order
}

func fail(message Value) (result, err Value, ok bool) { return Null, message, false }
