package values

import (
	"math"
	"math/rand"
	"slices"
	"strings"
	"testing"
)

// In jq's order; no two are equal.
var ordered = []string{
	"null",
	"false",
	"true",
	"-1e300",
	"-1",
	"0",
	"0.5",
	"1",
	"1e300",
	`""`,
	`"A"`,
	`"a"`,
	`"ab"`,
	`"b"`,
	`"é"`,
	"\"\"",
	"\"￿\"",
	`"😀"`,
	"[]",
	"[null]",
	"[1]",
	"[1,2]",
	"[1,3]",
	"[2]",
	`["a"]`,
	"[[]]",
	"{}",
	`{"a":1}`,
	`{"a":2}`,
	`{"a":[]}`,
	`{"a":1,"b":1}`,
	`{"a":2,"b":1}`,
	`{"a":1,"c":0}`,
	`{"b":0}`,
	`{"é":0}`,
	`{"😀":0}`,
}

func TestEveryPairIsOrdered(t *testing.T) {
	for i := range ordered {
		a := V(t, ordered[i])
		equal(t, 0, ValueOps.Compare(a, V(t, ordered[i])))
		equal(t, true, ValueOps.AreEqual(a, V(t, ordered[i])), ordered[i])
		for j := i + 1; j < len(ordered); j++ {
			b := V(t, ordered[j])
			equal(t, -1, ValueOps.Compare(a, b), ordered[i]+" < "+ordered[j])
			equal(t, 1, ValueOps.Compare(b, a), ordered[j]+" > "+ordered[i])
			equal(t, false, ValueOps.AreEqual(a, b), ordered[i]+" != "+ordered[j])
		}
	}
}

func TestDeepValuesCompare(t *testing.T) {
	a := V(t, strings.Repeat("[", 10000)+strings.Repeat("]", 10000))
	b := V(t, strings.Repeat("[", 10000)+"1"+strings.Repeat("]", 10000))
	equal(t, true, ValueOps.AreEqual(a, V(t, J(a))))
	equal(t, -1, ValueOps.Compare(a, b))
}

func TestObjectsOrderByKeySetsFirst(t *testing.T) {
	equal(t, -1, ValueOps.Compare(V(t, `{"a":2,"b":1}`), V(t, `{"a":1,"c":0}`)))
}

func TestObjectKeyOrderDoesNotMatter(t *testing.T) {
	a := V(t, `{"a":1,"b":[1,{"x":2,"y":3}]}`)
	b := V(t, `{"b":[1,{"y":3,"x":2}],"a":1}`)
	equal(t, true, ValueOps.AreEqual(a, b))
	equal(t, 0, ValueOps.Compare(a, b))
}

func TestStringsOrderByCodePoint(t *testing.T) {
	equal(t, -1, CodePoints.Compare("", "😀"))
	equal(t, 1, CodePoints.Compare("a😀", "a�"))
	equal(t, -1, CodePoints.Compare("😀", "😁"))
	equal(t, -1, CodePoints.Compare("a", "a😀"))
}

func TestNanSortsBelowNumbersComparesEqualToNanButIsNeverEqual(t *testing.T) {
	nan := N(math.NaN())
	equal(t, -1, ValueOps.Compare(nan, N(math.Inf(-1))))
	equal(t, 1, ValueOps.Compare(N(-1e300), nan))
	equal(t, 1, ValueOps.Compare(nan, True))
	equal(t, -1, ValueOps.Compare(nan, FromString("")))
	// A total order (spec §4.3, §13): [nan < nan, nan <= nan, nan > nan, nan >= nan] is [false,true,false,true].
	equal(t, 0, ValueOps.Compare(nan, nan))
	equal(t, 0, ValueOps.Compare(V(t, `[nan,{"a":nan}]`), V(t, `[nan,{"a":nan}]`)))
	// == stays IEEE: nan == nan is false, also inside containers.
	equal(t, false, ValueOps.AreEqual(nan, nan))
	equal(t, false, ValueOps.AreEqual(V(t, "[nan]"), V(t, "[nan]")))
	equal(t, -1, ValueOps.Compare(V(t, "[nan]"), V(t, "[0]")))
}

func TestSortKeepsNansTogetherAndStable(t *testing.T) {
	items := V(t, `[1,nan,"a",-1,nan,null]`).AsArray()
	equal(t, true, slices.Equal([]int{5, 1, 4, 3, 0, 2}, ValueOps.SortOrder(items)))
}

func TestEqualValues(t *testing.T) {
	for _, c := range [][2]string{
		{"1", "1.0"},
		{"-0", "0"},
		{"1e2", "100"},
		{"null", "null"},
		{`[1,[2,{"a":null}]]`, `[1,[2,{"a":null}]]`},
		{`"😀"`, `"😀"`},
	} {
		equal(t, true, ValueOps.AreEqual(V(t, c[0]), V(t, c[1])), c)
		equal(t, 0, ValueOps.Compare(V(t, c[0]), V(t, c[1])), c)
	}
}

func TestUnequalValues(t *testing.T) {
	for _, c := range [][2]string{
		{"1", `"1"`},
		{"false", "null"},
		{"[1,2]", "[2,1]"},
		{`{"a":1}`, `{"a":1,"b":null}`},
		{`{"a":1}`, `{"b":1}`},
	} {
		equal(t, false, ValueOps.AreEqual(V(t, c[0]), V(t, c[1])), c)
	}
}

func TestSortOrdersEveryKind(t *testing.T) {
	shuffled := make([]Value, len(ordered))
	expected := make([]string, len(ordered))
	for i, text := range ordered {
		shuffled[len(ordered)-1-i] = V(t, text)
		expected[i] = J(V(t, text))
	}
	sorted := ValueOps.Sort(ArrayOf(shuffled...))
	equal(t, "["+strings.Join(expected, ",")+"]", JA(sorted))
}

func TestSortOrderIsStable(t *testing.T) {
	keys := V(t, "[2,1,2,1,0,2]").AsArray()
	equal(t, true, slices.Equal([]int{4, 1, 3, 0, 2, 5}, ValueOps.SortOrder(keys)))
	equal(t, 0, len(ValueOps.SortOrder(EmptyArray)))
	equal(t, true, slices.Equal([]int{0}, ValueOps.SortOrder(V(t, "[7]").AsArray())))
}

func TestSortMatchesAReferenceSortOnRandomInput(t *testing.T) {
	random := rand.New(rand.NewSource(7))
	items := make([]Value, 1000)
	for i := range items {
		items[i] = N(float64(random.Intn(100)))
	}

	order := ValueOps.SortOrder(ArrayOf(items...))
	for i := 1; i < len(order); i++ {
		c := ValueOps.Compare(items[order[i-1]], items[order[i]])
		if !(c < 0 || (c == 0 && order[i-1] < order[i])) {
			t.Fatalf("not sorted at %d", i)
		}
	}
}
