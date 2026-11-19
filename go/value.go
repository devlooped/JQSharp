package jqlite

import (
	"encoding/json"
	"errors"
	"fmt"
	"reflect"
	"slices"

	"github.com/devlooped/jqlite/go/internal/values"
)

// Value is a jqlite value: null, false, true, a number (float64), a string, an array or an object
// whose keys keep their insertion order. Values are immutable and safe to share between
// goroutines. The zero Value is null.
//
// Go has no standard ordered JSON value, so the host boundary exposes the engine's own value type,
// with conversions from and to JSON text (ParseJSON, ToJSON) and from and to Go's any (FromAny,
// ToAny).
type Value = values.Value

// Kind is the kind of a Value, in jq's sort order.
type Kind = values.Kind

// Array is the array of a Value (Count, At); Object is its object (Count, KeyAt, ValueAt,
// TryGetValue, in insertion order).
type (
	Array  = values.Array
	Object = values.Object
)

// The kinds of values.
const (
	KindNull   = values.KindNull
	KindFalse  = values.KindFalse
	KindTrue   = values.KindTrue
	KindNumber = values.KindNumber
	KindString = values.KindString
	KindArray  = values.KindArray
	KindObject = values.KindObject
)

// Null, False and True are the three constant values.
var (
	Null  = values.Null
	False = values.False
	True  = values.True
)

// Bool gives true or false.
func Bool(b bool) Value { return values.FromBool(b) }

// Number gives a number value.
func Number(n float64) Value { return values.FromNumber(n) }

// String gives a string value; each ill-formed UTF-8 sequence becomes U+FFFD (spec §4.1).
func String(s string) Value { return values.FromString(s) }

// NewArray gives an array of the items.
func NewArray(items ...Value) Value { return values.FromArray(values.ArrayOf(items...)) }

// Member is a key and its value, for NewObject.
type Member struct {
	Key   string
	Value Value
}

// NewObject gives an object of the members, in their order; a repeated key keeps its first
// position and its last value.
func NewObject(members ...Member) Value {
	builder := values.NewObjectBuilder(len(members))
	for _, m := range members {
		builder.Set(values.CodePoints.Sanitize(m.Key), m.Value)
	}
	return values.FromObject(builder.ToObject())
}

// ParseJSON reads exactly one JSON value (spec §5.3: NaN, Infinity and their relatives are read
// too, lone surrogate escapes become U+FFFD). The error's message is jq's, as fromjson gives it.
func ParseJSON(text string) (Value, error) {
	v, err, ok := values.ValueText.TryParse(text)
	if !ok {
		return Null, errors.New(err.AsString())
	}
	return v, nil
}

// ToJSON gives the canonical text of a value (spec §5.1): compact, NaN as null, ±Infinity as
// ±1.7976931348623157e+308, numbers in their shortest form.
func ToJSON(v Value) string { return values.ValueText.ToJson(v) }

// FromAny converts a Go value: nil, bool, the integer and floating-point types, string,
// json.Number, a Value, []any (and any slice), map[string]any (and any map with string keys, keys
// sorted, since Go maps have no order). Anything else is an error.
func FromAny(v any) (Value, error) {
	switch x := v.(type) {
	case nil:
		return Null, nil
	case Value:
		return x, nil
	case bool:
		return Bool(x), nil
	case string:
		return String(x), nil
	case float64:
		return Number(x), nil
	case float32:
		return Number(float64(x)), nil
	case int:
		return Number(float64(x)), nil
	case int8:
		return Number(float64(x)), nil
	case int16:
		return Number(float64(x)), nil
	case int32:
		return Number(float64(x)), nil
	case int64:
		return Number(float64(x)), nil
	case uint:
		return Number(float64(x)), nil
	case uint8:
		return Number(float64(x)), nil
	case uint16:
		return Number(float64(x)), nil
	case uint32:
		return Number(float64(x)), nil
	case uint64:
		return Number(float64(x)), nil
	case json.Number:
		n, ok := values.ValueText.TryParseNumber(string(x))
		if !ok {
			return Null, fmt.Errorf("jqlite: %q is not a number", string(x))
		}
		return Number(n), nil
	case []any:
		items := make([]Value, len(x))
		for i, item := range x {
			converted, err := FromAny(item)
			if err != nil {
				return Null, err
			}
			items[i] = converted
		}
		return values.FromArray(values.ArrayFromOwned(items)), nil
	case map[string]any:
		keys := make([]string, 0, len(x))
		for key := range x {
			keys = append(keys, key)
		}
		slices.Sort(keys)
		members := make([]Member, len(keys))
		for i, key := range keys {
			converted, err := FromAny(x[key])
			if err != nil {
				return Null, err
			}
			members[i] = Member{key, converted}
		}
		return NewObject(members...), nil
	}
	return fromReflected(reflect.ValueOf(v))
}

// fromReflected converts the slices and string-keyed maps of other element types.
func fromReflected(r reflect.Value) (Value, error) {
	switch r.Kind() {
	case reflect.Slice, reflect.Array:
		items := make([]any, r.Len())
		for i := range items {
			items[i] = r.Index(i).Interface()
		}
		return FromAny(items)
	case reflect.Map:
		if r.Type().Key().Kind() == reflect.String {
			m := make(map[string]any, r.Len())
			for _, key := range r.MapKeys() {
				m[key.String()] = r.MapIndex(key).Interface()
			}
			return FromAny(m)
		}
	case reflect.Pointer:
		if r.IsNil() {
			return Null, nil
		}
		return FromAny(r.Elem().Interface())
	}
	return Null, fmt.Errorf("jqlite: cannot convert a %v to a value", r.Type())
}

// ToAny converts a value to Go's any: nil, bool, float64, string, []any and map[string]any (Go
// maps have no order: an object's key order is lost; ToJSON keeps it). NaN and ±Infinity stay
// float64 as they are.
func ToAny(v Value) any {
	switch v.Kind() {
	case KindNull:
		return nil
	case KindFalse:
		return false
	case KindTrue:
		return true
	case KindNumber:
		return v.AsNumber()
	case KindString:
		return v.AsString()
	case KindArray:
		array := v.AsArray()
		items := make([]any, array.Count())
		for i := range items {
			items[i] = ToAny(array.At(i))
		}
		return items
	default:
		obj := v.AsObject()
		m := make(map[string]any, obj.Count())
		for i := 0; i < obj.Count(); i++ {
			m[obj.KeyAt(i)] = ToAny(obj.ValueAt(i))
		}
		return m
	}
}
