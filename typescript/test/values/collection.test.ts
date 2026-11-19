// The port of dotnet/Tests/Core/Values/CollectionTests.cs. Left out: the size of the value
// struct and the accessor that throws (a value is the host's own primitive here), and the
// appends from several threads (one thread runs at a time; the tests of independent versions
// from one base cover what they checked). The two allocation tests check time instead of bytes.

import assert from "node:assert/strict";
import { test } from "node:test";
import { JqArray } from "../../src/values/jqArray.js";
import { JqObject } from "../../src/values/jqObject.js";
import { JqObjectBuilder } from "../../src/values/jqObjectBuilder.js";
import { JqKind, isTruthy, kindOf, typeName } from "../../src/values/jqValue.js";
import { J, V } from "../support/valueHelpers.js";

test("kinds and truthiness", () => {
  assert.equal(kindOf(null), JqKind.Null);
  assert.equal(kindOf(false), JqKind.False);
  assert.equal(kindOf(true), JqKind.True);
  assert.equal(kindOf(1), JqKind.Number);
  assert.equal(kindOf(""), JqKind.String);
  assert.equal(kindOf(JqArray.Empty), JqKind.Array);
  assert.equal(kindOf(JqObject.Empty), JqKind.Object);
  assert.equal(isTruthy(null), false);
  assert.equal(isTruthy(false), false);
  assert.equal(isTruthy(0), true);
  assert.equal(isTruthy(""), true);
  assert.equal(typeName(true), "boolean");
});

test("appending twice to the same base gives independent arrays", () => {
  const root = JqArray.Empty.append(1).append(2);
  const a = root.append(3);
  const b = root.append(4);
  const c = a.append(5);
  const d = a.concat(JqArray.of(6, 7));
  assert.equal(J(root), "[1,2]");
  assert.equal(J(a), "[1,2,3]");
  assert.equal(J(b), "[1,2,4]");
  assert.equal(J(c), "[1,2,3,5]");
  assert.equal(J(d), "[1,2,3,6,7]");
});

test("the empty array is never appended in place", () => {
  const a = JqArray.Empty.append(1);
  const b = JqArray.Empty.append(2);
  assert.equal(J(a), "[1]");
  assert.equal(J(b), "[2]");
  assert.equal(JqArray.Empty.count, 0);
  assert.equal(J(JqObject.Empty.set("a", 1)), '{"a":1}');
  assert.equal(J(JqObject.Empty.set("b", 2)), '{"b":2}');
  assert.equal(JqObject.Empty.count, 0);
});

test("an array concatenated with itself", () => {
  const a = JqArray.Empty.append(1).append(2);
  assert.equal(J(a.concat(a)), "[1,2,1,2]");
  assert.equal(J(a), "[1,2]");
});

test("slices share storage but never change", () => {
  const array = V("[1,2,3,4,5]") as JqArray;
  const middle = array.slice(1, 3);
  const tail = array.slice(3, 5);
  const grown = middle.append(9);
  const longer = tail.append(8);
  assert.equal(J(array), "[1,2,3,4,5]");
  assert.equal(J(middle), "[2,3]");
  assert.equal(J(grown), "[2,3,9]");
  assert.equal(J(longer), "[4,5,8]");
  assert.equal(J(tail), "[4,5]");
});

test("set item copies one level and pads with null", () => {
  const array = V("[1,[2]]") as JqArray;
  assert.equal(J(array.setItem(1, 7)), "[1,7]");
  assert.equal(J(array.setItem(3, 5)), "[1,[2],null,5]");
  assert.equal(J(array.setItem(2, 6)), "[1,[2],6]");
  assert.equal(J(array), "[1,[2]]");
  assert.equal(array.setItem(0, 0).get(1), array.get(1));
});

test("many versions from one base stay independent", () => {
  const root = JqArray.Empty.append(-1);
  const results: JqArray[] = [];
  for (let i = 0; i < 1000; i++) {
    let array = root.append(i);
    for (let j = 0; j < 10; j++) array = array.append(i * 100 + j);
    results.push(array);
  }

  assert.equal(J(root), "[-1]");
  for (let i = 0; i < results.length; i++) {
    const array = results[i]!;
    assert.equal(array.count, 12);
    assert.equal(array.get(0), -1);
    assert.equal(array.get(1), i);
    for (let j = 0; j < 10; j++) assert.equal(array.get(2 + j), i * 100 + j);
  }
});

test("appending in a loop does not copy the array", () => {
  // reduce range(n) as $i ([]; . + [$i]); copying on each append would take minutes.
  const started = performance.now();
  let array = JqArray.Empty;
  for (let i = 0; i < 200000; i++) array = array.concat(JqArray.of(i));
  assert.equal(array.count, 200000);
  assert.equal(array.get(199999), 199999);
  assert.ok(performance.now() - started < 5000);
});

test("adding keys in a loop does not copy the object", () => {
  const started = performance.now();
  let obj = JqObject.Empty;
  for (let i = 0; i < 100000; i++) obj = obj.merge(JqObject.Empty.set("k" + i, i));
  assert.equal(obj.count, 100000);
  assert.equal(obj.get("k1234"), 1234);
  assert.ok(performance.now() - started < 5000);
});

test("object keeps insertion order", () => {
  const obj = JqObject.Empty.set("b", 1).set("a", 2).set("c", 3);
  assert.equal(J(obj), '{"b":1,"a":2,"c":3}');
  assert.equal(J(obj.set("a", 9)), '{"b":1,"a":9,"c":3}');
  assert.equal(J(obj.remove("a")), '{"b":1,"c":3}');
  assert.equal(J(obj.remove("a").set("a", 4)), '{"b":1,"c":3,"a":4}');
  assert.equal(obj.remove("x"), obj);
  assert.equal(J(obj), '{"b":1,"a":2,"c":3}');
  assert.deepEqual(obj.sortedKeys(), ["a", "b", "c"]);
});

test("keys that look like array indexes keep their position", () => {
  const obj = JqObject.Empty.set("b", 1).set("10", 2).set("2", 3);
  assert.equal(J(obj), '{"b":1,"10":2,"2":3}');
  assert.equal(J(V('{"b":1,"10":2,"2":3,"b":4}')), '{"b":4,"10":2,"2":3}');
});

test("object merge keeps insertion order", () => {
  const left = V('{"z":1,"y":2,"x":3}') as JqObject;
  assert.equal(J(left.merge(V('{"w":4,"y":5}') as JqObject)), '{"z":1,"y":5,"x":3,"w":4}');
  assert.equal(J(left.merge(V('{"a":0,"b":1}') as JqObject)), '{"z":1,"y":2,"x":3,"a":0,"b":1}');
  assert.equal(J(left), '{"z":1,"y":2,"x":3}');
});

test("versions of a large object see only their keys", () => {
  const versions: JqObject[] = [JqObject.Empty];
  for (let i = 1; i <= 20; i++) versions.push(versions[i - 1]!.set("k" + i, i));

  // A branch from an older version takes a slot already used by a newer one: it copies.
  const branch = versions[10]!.set("k15", -15);
  for (let i = 1; i <= 20; i++) {
    assert.equal(versions[i]!.count, i);
    assert.ok(versions[i]!.containsKey("k" + i));
    assert.equal(!versions[i]!.containsKey("k20"), i < 20);
    assert.ok(!versions[i]!.containsKey("k" + (i + 1)));
  }

  assert.equal(branch.get("k15"), -15);
  assert.equal(versions[15]!.get("k15"), 15);
  assert.ok(!versions[12]!.containsKey("k15"));
  assert.equal(branch.indexOf("k15"), 10);
});

test("many key adds to one base stay independent", () => {
  let root = JqObject.Empty;
  for (let i = 0; i < 20; i++) root = root.set("base" + i, i);
  const results: JqObject[] = [];
  for (let i = 0; i < 500; i++) {
    let obj = root.set("own" + i, i);
    for (let j = 0; j < 5; j++) obj = obj.set(`own${i}_${j}`, j);
    results.push(obj);
  }

  assert.equal(root.count, 20);
  for (let i = 0; i < results.length; i++) {
    const obj = results[i]!;
    assert.equal(obj.count, 26);
    assert.equal(obj.keyAt(20), "own" + i);
    assert.equal(obj.get("own" + i), i);
    assert.ok(!obj.containsKey("own" + ((i + 1) % results.length)));
    assert.ok(!root.containsKey("own" + i));
  }
});

test("builder replaces in place and removes", () => {
  const builder = new JqObjectBuilder();
  for (let i = 0; i < 12; i++) builder.set("k" + i, i);
  builder.set("k3", 30);
  assert.ok(builder.remove("k0"));
  assert.ok(!builder.remove("missing"));
  const obj = builder.toObject();
  assert.equal(obj.count, 11);
  assert.equal(obj.keyAt(0), "k1");
  assert.equal(obj.get("k3"), 30);
  assert.ok(!obj.containsKey("k0"));
  assert.throws(() => builder.set("x", 1));
});
