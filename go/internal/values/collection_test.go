package values

import (
	"fmt"
	"runtime"
	"slices"
	"sync"
	"testing"
	"unsafe"
)

func N(number float64) Value { return FromNumber(number) }

func JA(array *Array) string { return J(FromArray(array)) }

func JO(obj *Object) string { return J(FromObject(obj)) }

// The reference packs a value in 16 bytes; Go's struct is a kind, a float64 and an interface.
func TestValueIsThirtyTwoBytes(t *testing.T) {
	equal(t, uintptr(32), unsafe.Sizeof(Value{}))
}

func TestKindsAndTruthiness(t *testing.T) {
	equal(t, KindNull, Value{}.Kind())
	equal(t, KindFalse, FromBool(false).Kind())
	equal(t, KindTrue, FromBool(true).Kind())
	equal(t, KindNumber, N(1).Kind())
	equal(t, KindString, FromString("").Kind())
	equal(t, KindArray, FromArray(EmptyArray).Kind())
	equal(t, KindObject, FromObject(EmptyObject).Kind())
	equal(t, false, Null.IsTruthy())
	equal(t, false, False.IsTruthy())
	equal(t, true, N(0).IsTruthy())
	equal(t, true, FromString("").IsTruthy())
	equal(t, "boolean", True.TypeName())
	defer func() {
		if recover() == nil {
			t.Error("AsString on a number did not panic")
		}
	}()
	N(1).AsString()
}

func TestAppendingTwiceToTheSameBaseGivesIndependentArrays(t *testing.T) {
	root := EmptyArray.Append(N(1)).Append(N(2))
	a := root.Append(N(3))
	b := root.Append(N(4))
	c := a.Append(N(5))
	d := a.Concat(ArrayOf(N(6), N(7)))

	equal(t, "[1,2]", JA(root))
	equal(t, "[1,2,3]", JA(a))
	equal(t, "[1,2,4]", JA(b))
	equal(t, "[1,2,3,5]", JA(c))
	equal(t, "[1,2,3,6,7]", JA(d))
}

func TestSlicesShareStorageButNeverChange(t *testing.T) {
	array := V(t, "[1,2,3,4,5]").AsArray()
	middle := array.Slice(1, 3)
	tail := array.Slice(3, 5)
	grown := middle.Append(N(9))
	longer := tail.Append(N(8))

	equal(t, "[1,2,3,4,5]", JA(array))
	equal(t, "[2,3]", JA(middle))
	equal(t, "[2,3,9]", JA(grown))
	equal(t, "[4,5,8]", JA(longer))
	equal(t, "[4,5]", JA(tail))
}

func TestSetItemCopiesOneLevelAndPadsWithNull(t *testing.T) {
	array := V(t, "[1,[2]]").AsArray()
	equal(t, "[1,7]", JA(array.SetItem(1, N(7))))
	equal(t, "[1,[2],null,5]", JA(array.SetItem(3, N(5))))
	equal(t, "[1,[2],6]", JA(array.SetItem(2, N(6))))
	equal(t, "[1,[2]]", JA(array))
	equal(t, array.At(1).AsArray(), array.SetItem(0, N(0)).At(1).AsArray())
}

func TestConcurrentAppendsToOneBaseStayIndependent(t *testing.T) {
	root := EmptyArray.Append(N(-1))
	results := make([]*Array, 1000)
	var group sync.WaitGroup
	for i := range results {
		group.Go(func() {
			array := root.Append(N(float64(i)))
			for j := 0; j < 10; j++ {
				array = array.Append(N(float64(i*100 + j)))
			}
			results[i] = array
		})
	}
	group.Wait()

	equal(t, "[-1]", JA(root))
	for i, array := range results {
		equal(t, 12, array.Count())
		equal(t, -1.0, array.At(0).AsNumber())
		equal(t, float64(i), array.At(1).AsNumber())
		for j := 0; j < 10; j++ {
			equal(t, float64(i*100+j), array.At(2+j).AsNumber())
		}
	}
}

func allocated(action func()) uint64 {
	var before, after runtime.MemStats
	runtime.GC()
	runtime.ReadMemStats(&before)
	action()
	runtime.ReadMemStats(&after)
	return after.TotalAlloc - before.TotalAlloc
}

func TestAppendingInALoopDoesNotCopyTheArray(t *testing.T) {
	// reduce range(10000) as $i ([]; . + [$i]); copying on each append would allocate ~1.6 GB.
	array := EmptyArray
	bytes := allocated(func() {
		for i := 0; i < 10000; i++ {
			array = array.Concat(ArrayOf(N(float64(i))))
		}
	})

	equal(t, 10000, array.Count())
	equal(t, 9999.0, array.At(9999).AsNumber())
	if bytes > 8_000_000 {
		t.Errorf("%d bytes", bytes)
	}
}

func TestAddingKeysInALoopDoesNotCopyTheObject(t *testing.T) {
	obj := EmptyObject
	bytes := allocated(func() {
		for i := 0; i < 10000; i++ {
			obj = obj.Merge(EmptyObject.Set(fmt.Sprint("k", i), N(float64(i))))
		}
	})

	equal(t, 10000, obj.Count())
	value, ok := obj.TryGetValue("k1234")
	equal(t, true, ok)
	equal(t, 1234.0, value.AsNumber())
	if bytes > 16_000_000 {
		t.Errorf("%d bytes", bytes)
	}
}

func TestObjectKeepsInsertionOrder(t *testing.T) {
	obj := EmptyObject.Set("b", N(1)).Set("a", N(2)).Set("c", N(3))
	equal(t, `{"b":1,"a":2,"c":3}`, JO(obj))
	equal(t, `{"b":1,"a":9,"c":3}`, JO(obj.Set("a", N(9))))
	equal(t, `{"b":1,"c":3}`, JO(obj.Remove("a")))
	equal(t, `{"b":1,"c":3,"a":4}`, JO(obj.Remove("a").Set("a", N(4))))
	equal(t, obj, obj.Remove("x"))
	equal(t, `{"b":1,"a":2,"c":3}`, JO(obj))
	equal(t, true, slices.Equal([]string{"a", "b", "c"}, obj.SortedKeys()))
}

func TestObjectMergeKeepsInsertionOrder(t *testing.T) {
	left := V(t, `{"z":1,"y":2,"x":3}`).AsObject()
	equal(t, `{"z":1,"y":5,"x":3,"w":4}`, JO(left.Merge(V(t, `{"w":4,"y":5}`).AsObject())))
	equal(t, `{"z":1,"y":2,"x":3,"a":0,"b":1}`, JO(left.Merge(V(t, `{"a":0,"b":1}`).AsObject())))
	equal(t, `{"z":1,"y":2,"x":3}`, JO(left))
}

func TestVersionsOfALargeObjectSeeOnlyTheirKeys(t *testing.T) {
	versions := make([]*Object, 21)
	versions[0] = EmptyObject
	for i := 1; i <= 20; i++ {
		versions[i] = versions[i-1].Set(fmt.Sprint("k", i), N(float64(i)))
	}

	// A branch from an older version takes a slot already used by a newer one: it copies.
	branch := versions[10].Set("k15", N(-15))

	for i := 1; i <= 20; i++ {
		equal(t, i, versions[i].Count())
		equal(t, true, versions[i].ContainsKey(fmt.Sprint("k", i)))
		equal(t, i < 20, !versions[i].ContainsKey("k20"))
		equal(t, false, versions[i].ContainsKey(fmt.Sprint("k", i+1)))
	}

	v, _ := branch.TryGetValue("k15")
	equal(t, -15.0, v.AsNumber())
	v, _ = versions[15].TryGetValue("k15")
	equal(t, 15.0, v.AsNumber())
	equal(t, false, versions[12].ContainsKey("k15"))
	equal(t, 10, branch.IndexOf("k15"))
}

func TestConcurrentKeyAddsToOneBaseStayIndependent(t *testing.T) {
	root := EmptyObject
	for i := 0; i < 20; i++ {
		root = root.Set(fmt.Sprint("base", i), N(float64(i)))
	}

	results := make([]*Object, 500)
	var group sync.WaitGroup
	for i := range results {
		group.Go(func() {
			obj := root.Set(fmt.Sprint("own", i), N(float64(i)))
			for j := 0; j < 5; j++ {
				obj = obj.Set(fmt.Sprintf("own%d_%d", i, j), N(float64(j)))
			}
			results[i] = obj
		})
	}
	group.Wait()

	equal(t, 20, root.Count())
	for i, obj := range results {
		equal(t, 26, obj.Count())
		equal(t, fmt.Sprint("own", i), obj.KeyAt(20))
		value, ok := obj.TryGetValue(fmt.Sprint("own", i))
		equal(t, true, ok)
		equal(t, float64(i), value.AsNumber())
		equal(t, false, obj.ContainsKey(fmt.Sprint("own", (i+1)%len(results))))
		equal(t, false, root.ContainsKey(fmt.Sprint("own", i)))
	}
}

func TestBuilderReplacesInPlaceAndRemoves(t *testing.T) {
	builder := NewObjectBuilder(4)
	for i := 0; i < 12; i++ {
		builder.Set(fmt.Sprint("k", i), N(float64(i)))
	}
	builder.Set("k3", N(30))
	equal(t, true, builder.Remove("k0"))
	equal(t, false, builder.Remove("missing"))
	obj := builder.ToObject()

	equal(t, 11, obj.Count())
	equal(t, "k1", obj.KeyAt(0))
	v, _ := obj.TryGetValue("k3")
	equal(t, 30.0, v.AsNumber())
	equal(t, false, obj.ContainsKey("k0"))
	defer func() {
		if recover() == nil {
			t.Error("Set after ToObject did not panic")
		}
	}()
	builder.Set("x", N(1))
}
