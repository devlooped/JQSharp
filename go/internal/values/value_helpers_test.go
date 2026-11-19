package values

import "testing"

// V reads JSON text; fails the test when it is not valid.
func V(t testing.TB, json string) Value {
	t.Helper()
	value, err, ok := ValueText.TryParse(json)
	if !ok {
		t.Fatalf("Bad test JSON %s: %s", json, err.AsString())
	}
	return value
}

// J gives the canonical JSON text of a value.
func J(value Value) string { return ValueText.ToJson(value) }

// resultText gives the JSON text of a result, or "error: <message>".
func resultText(result, err Value, ok bool) string {
	if ok {
		return J(result)
	}
	return "error: " + err.AsString()
}

func equal[T comparable](t testing.TB, expected, actual T, context ...any) {
	t.Helper()
	if expected != actual {
		t.Errorf("expected %v, got %v %v", expected, actual, context)
	}
}
