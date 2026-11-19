package builtins

import (
	"strings"

	"github.com/devlooped/jqlite/go/internal/values"
)

type containment struct{}

// Containment is §9.4 contains/1: whether the input contains the argument.
var Containment containment

func (containment) Register() { add("contains", 1, contains) }

// contains: at the top level the kinds must be equal; below it a kind mismatch is false.
func contains(_ *interpreter, input value, arguments []value) (value, value, bool) {
	if input.Kind() == arguments[0].Kind() {
		return ok(values.FromBool(check(input, arguments[0])))
	}
	return fail(values.Errors.CannotCheckContainment(input, arguments[0]))
}

// containFrame is a pending check of containers: a contains b. For arrays, every element
// b[item] must be contained by some a[candidate]; for objects, every key of b (b's entry item)
// must be in a with a value that contains b's.
type containFrame struct {
	a, b      value
	item      int
	candidate int
}

// check checks containers with an explicit stack, so that deep values cannot exhaust the host
// stack (§11): each frame asks for one child result at a time, which comes back in answer.
func check(a, b value) bool {
	if scalar, isScalar := tryScalar(a, b); isScalar {
		return scalar
	}

	stack := []*containFrame{{a: a, b: b}}
	answer := false
	answered := false
	for len(stack) > 0 {
		frame := stack[len(stack)-1]
		var done, finished bool
		var childA, childB value
		if frame.b.Kind() == values.KindArray {
			done, finished, childA, childB = stepArray(frame, answered, answer)
		} else {
			done, finished, childA, childB = stepObject(frame, answered, answer)
		}
		answered = false

		if done {
			stack = stack[:len(stack)-1]
			answer, answered = finished, true
		} else if direct, isScalar := tryScalar(childA, childB); isScalar {
			answer, answered = direct, true
		} else {
			stack = append(stack, &containFrame{a: childA, b: childB})
		}
	}
	return answer
}

// stepArray gives the next child check of an array frame, or its result (done).
func stepArray(frame *containFrame, answered, answer bool) (done, result bool, childA, childB value) {
	a := frame.a.AsArray()
	b := frame.b.AsArray()
	if answered {
		if answer {
			frame.item, frame.candidate = frame.item+1, 0
		} else {
			frame.candidate++
		}
	}
	if frame.item == b.Count() {
		return true, true, values.Null, values.Null
	}
	if frame.candidate == a.Count() {
		return true, false, values.Null, values.Null
	}
	return false, false, a.At(frame.candidate), b.At(frame.item)
}

// stepObject gives the next child check of an object frame, or its result (done).
func stepObject(frame *containFrame, answered, answer bool) (done, result bool, childA, childB value) {
	a := frame.a.AsObject()
	b := frame.b.AsObject()
	if answered {
		if !answer {
			return true, false, values.Null, values.Null
		}
		frame.item++
	}
	if frame.item == b.Count() {
		return true, true, values.Null, values.Null
	}
	member, found := a.TryGetValue(b.KeyAt(frame.item))
	if !found {
		return true, false, values.Null, values.Null
	}
	return false, false, member, b.ValueAt(frame.item)
}

// tryScalar gives the result of a check that needs no frame: kinds differ, strings, or other scalars.
func tryScalar(a, b value) (result, isScalar bool) {
	if a.Kind() != b.Kind() {
		return false, true
	}
	switch a.Kind() {
	case values.KindArray, values.KindObject:
		return false, false
	case values.KindString:
		return strings.Contains(a.AsString(), b.AsString()), true
	default:
		return values.ValueOps.AreEqual(a, b), true
	}
}
