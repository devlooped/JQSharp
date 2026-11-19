// The port of dotnet/Tests/Core/Values/ValueOrderingTests.cs.

import assert from "node:assert/strict";
import { test } from "node:test";
import * as CodePoints from "../../src/values/codePoints.js";
import { JqArray } from "../../src/values/jqArray.js";
import type { JqValue } from "../../src/values/jqValue.js";
import * as ValueOps from "../../src/values/valueOps.js";
import { J, V } from "../support/valueHelpers.js";

// In jq's order; no two are equal.
const Ordered = [
  "null",
  "false",
  "true",
  "-1e300",
  "-1",
  "0",
  "0.5",
  "1",
  "1e300",
  '""',
  '"A"',
  '"a"',
  '"ab"',
  '"b"',
  '"é"',
  '""',
  '"￿"',
  '"😀"',
  "[]",
  "[null]",
  "[1]",
  "[1,2]",
  "[1,3]",
  "[2]",
  '["a"]',
  "[[]]",
  "{}",
  '{"a":1}',
  '{"a":2}',
  '{"a":[]}',
  '{"a":1,"b":1}',
  '{"a":2,"b":1}',
  '{"a":1,"c":0}',
  '{"b":0}',
  '{"é":0}',
  '{"😀":0}',
];

test("every pair is ordered", () => {
  for (let i = 0; i < Ordered.length; i++) {
    const a = V(Ordered[i]!);
    assert.equal(ValueOps.compare(a, V(Ordered[i]!)), 0);
    assert.ok(ValueOps.areEqual(a, V(Ordered[i]!)), Ordered[i]);
    for (let j = i + 1; j < Ordered.length; j++) {
      const b = V(Ordered[j]!);
      assert.equal(ValueOps.compare(a, b), -1, `${Ordered[i]} < ${Ordered[j]}`);
      assert.equal(ValueOps.compare(b, a), 1, `${Ordered[j]} > ${Ordered[i]}`);
      assert.ok(!ValueOps.areEqual(a, b), `${Ordered[i]} != ${Ordered[j]}`);
    }
  }
});

test("deep values compare", () => {
  const a = V("[".repeat(10000) + "]".repeat(10000));
  const b = V("[".repeat(10000) + "1" + "]".repeat(10000));
  assert.ok(ValueOps.areEqual(a, V(J(a))));
  assert.equal(ValueOps.compare(a, b), -1);
});

test("objects order by key sets first", () => {
  assert.equal(ValueOps.compare(V('{"a":2,"b":1}'), V('{"a":1,"c":0}')), -1);
});

test("object key order does not matter", () => {
  const a = V('{"a":1,"b":[1,{"x":2,"y":3}]}');
  const b = V('{"b":[1,{"y":3,"x":2}],"a":1}');
  assert.ok(ValueOps.areEqual(a, b));
  assert.equal(ValueOps.compare(a, b), 0);
});

test("strings order by code point, not by UTF-16 unit", () => {
  // U+E000 is one unit 0xE000; U+1F600 is the pair D83D DE00: the order of UTF-16 units puts the pair first.
  assert.ok("😀" < "");
  assert.equal(CodePoints.compare("", "😀"), -1);
  assert.equal(CodePoints.compare("a😀", "a�"), 1);
  assert.equal(CodePoints.compare("😀", "😁"), -1);
  assert.equal(CodePoints.compare("a", "a😀"), -1);
});

test("NaN sorts below numbers, compares equal to NaN but is never equal", () => {
  assert.equal(ValueOps.compare(NaN, -Infinity), -1);
  assert.equal(ValueOps.compare(-1e300, NaN), 1);
  assert.equal(ValueOps.compare(NaN, true), 1);
  assert.equal(ValueOps.compare(NaN, ""), -1);
  // A total order (spec §4.3, §13): [nan < nan, nan <= nan, nan > nan, nan >= nan] is [false,true,false,true].
  assert.equal(ValueOps.compare(NaN, NaN), 0);
  assert.equal(ValueOps.compare(V('[nan,{"a":nan}]'), V('[nan,{"a":nan}]')), 0);
  // == stays IEEE: nan == nan is false, also inside containers.
  assert.ok(!ValueOps.areEqual(NaN, NaN));
  assert.ok(!ValueOps.areEqual(V("[nan]"), V("[nan]")));
  assert.equal(ValueOps.compare(V("[nan]"), V("[0]")), -1);
});

test("sort keeps NaNs together and stable", () => {
  const items = V('[1,nan,"a",-1,nan,null]') as JqArray;
  assert.deepEqual([...ValueOps.sortOrder(items)], [5, 1, 4, 3, 0, 2]);
});

test("equal values", () => {
  const cases: [string, string][] = [
    ["1", "1.0"],
    ["-0", "0"],
    ["1e2", "100"],
    ["null", "null"],
    ['[1,[2,{"a":null}]]', '[1,[2,{"a":null}]]'],
    ['"😀"', '"\\ud83d\\ude00"'],
  ];
  for (const [a, b] of cases) {
    assert.ok(ValueOps.areEqual(V(a), V(b)), `${a} == ${b}`);
    assert.equal(ValueOps.compare(V(a), V(b)), 0);
  }
});

test("unequal values", () => {
  const cases: [string, string][] = [
    ["1", '"1"'],
    ["false", "null"],
    ["[1,2]", "[2,1]"],
    ['{"a":1}', '{"a":1,"b":null}'],
    ['{"a":1}', '{"b":1}'],
  ];
  for (const [a, b] of cases) assert.ok(!ValueOps.areEqual(V(a), V(b)), `${a} != ${b}`);
});

test("sort orders every kind", () => {
  const shuffled = Ordered.map(V).reverse();
  const sorted = ValueOps.sort(JqArray.of(...shuffled));
  assert.equal(J(sorted), "[" + Ordered.map((x) => J(V(x))).join(",") + "]");
});

test("sort order is stable", () => {
  assert.deepEqual([...ValueOps.sortOrder(V("[2,1,2,1,0,2]") as JqArray)], [4, 1, 3, 0, 2, 5]);
  assert.equal(ValueOps.sortOrder(JqArray.Empty).length, 0);
  assert.deepEqual([...ValueOps.sortOrder(V("[7]") as JqArray)], [0]);
});

test("sort matches a reference sort on random input", () => {
  // A small generator with a fixed seed: the same input on every run.
  let seed = 7;
  const next = (): number => {
    seed = (seed * 1103515245 + 12345) % 2147483648;
    return seed % 100;
  };

  const items: JqValue[] = [];
  for (let i = 0; i < 1000; i++) items.push(next());
  const order = ValueOps.sortOrder(JqArray.of(...items));
  for (let i = 1; i < order.length; i++) {
    const c = ValueOps.compare(items[order[i - 1]!]!, items[order[i]!]!);
    assert.ok(c < 0 || (c === 0 && order[i - 1]! < order[i]!));
  }
});
