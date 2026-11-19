// Package values holds the jqlite value, its arrays and objects, and the operations on values:
// equality, ordering, arithmetic (value_ops.go), the JSON writer and reader (value_text.go) and
// every run-time message (errors.go).
//
// A static class of the reference becomes a package-level variable of an empty struct type, so a
// call reads as in C#: ValueOps.Compare(a, b), CodePoints.Length(s), Errors.CannotIndex(t, k).
package values

// Kind is the kind of a jqlite value, declared in sort order.
type Kind uint8

// The kinds, in their sort order.
const (
	KindNull Kind = iota
	KindFalse
	KindTrue
	KindNumber
	KindString
	KindArray
	KindObject
)

var kindNames = [...]string{"Null", "False", "True", "Number", "String", "Array", "Object"}

func (k Kind) String() string { return kindNames[k] }

// Value is an immutable jqlite value: null, false, true, a number (IEEE-754 double, NaN and
// ±Infinity included), a string, an array or an object. The zero Value is null.
//
// ref holds the string (a Go string or a *jqString), the *Array or the *Object; num holds the
// number.
type Value struct {
	kind Kind
	num  float64
	ref  any
}

// Null, False and True are the three constant values.
var (
	Null  = Value{}
	False = Value{kind: KindFalse}
	True  = Value{kind: KindTrue}
)

// FromBool gives True or False.
func FromBool(value bool) Value {
	if value {
		return True
	}
	return False
}

// FromNumber gives a number value.
func FromNumber(value float64) Value { return Value{kind: KindNumber, num: value} }

// FromString creates a string value; each ill-formed UTF-8 sequence becomes U+FFFD.
func FromString(value string) Value {
	return Value{kind: KindString, ref: CodePoints.Sanitize(value)}
}

// Concat is jq's + on two strings. Both are well-formed, so their concatenation is too and is not
// sanitized again. A long result is a *jqString, which the next concatenation extends in place.
func Concat(left, right Value) Value {
	tail := right.AsString()
	if len(tail) == 0 {
		return left
	}
	if grown, ok := left.ref.(*jqString); ok {
		return Value{kind: KindString, ref: grown.Append(tail)}
	}

	head := left.AsString()
	if len(head) == 0 {
		return right
	}
	if len(head)+len(tail) < stringMinLength {
		return Value{kind: KindString, ref: head + tail}
	}
	return Value{kind: KindString, ref: newJqString(head, tail)}
}

// FromArray gives an array value.
func FromArray(value *Array) Value { return Value{kind: KindArray, ref: value} }

// FromObject gives an object value.
func FromObject(value *Object) Value { return Value{kind: KindObject, ref: value} }

// Kind gives the kind of the value.
func (v Value) Kind() Kind { return v.kind }

// IsTruthy is false for null and false, true for every other value.
func (v Value) IsTruthy() bool { return v.kind > KindFalse }

// TypeName is the name jq's type gives the kind: false and true are both "boolean".
func (v Value) TypeName() string {
	switch v.kind {
	case KindNull:
		return "null"
	case KindFalse, KindTrue:
		return "boolean"
	case KindNumber:
		return "number"
	case KindString:
		return "string"
	case KindArray:
		return "array"
	default:
		return "object"
	}
}

// The As* accessors panic only on a programming error (asking a value for the wrong kind), never
// as part of jqlite semantics: callers check Kind first.

// AsNumber gives the number of a number value.
func (v Value) AsNumber() float64 {
	if v.kind != KindNumber {
		panic(v.wrongKind(KindNumber))
	}
	return v.num
}

// AsString gives the text of a string value.
func (v Value) AsString() string {
	if v.kind != KindString {
		panic(v.wrongKind(KindString))
	}
	if text, ok := v.ref.(string); ok {
		return text
	}
	return v.ref.(*jqString).Text()
}

// AsArray gives the array of an array value.
func (v Value) AsArray() *Array {
	if v.kind != KindArray {
		panic(v.wrongKind(KindArray))
	}
	return v.ref.(*Array)
}

// AsObject gives the object of an object value.
func (v Value) AsObject() *Object {
	if v.kind != KindObject {
		panic(v.wrongKind(KindObject))
	}
	return v.ref.(*Object)
}

// String gives the canonical JSON text of the value, for diagnostics and tests.
func (v Value) String() string { return ValueText.ToJson(v) }

func (v Value) wrongKind(expected Kind) string {
	return "A " + v.kind.String() + " value is not a " + expected.String() + "."
}
