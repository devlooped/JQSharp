// The port of dotnet/Tests/Core/Values/ValueArithmeticTests.cs.

import assert from "node:assert/strict";
import { test } from "node:test";
import { JqObject } from "../../src/values/jqObject.js";
import { Fail, type JqValue } from "../../src/values/jqValue.js";
import * as ValueOps from "../../src/values/valueOps.js";
import { J, R, V } from "../support/valueHelpers.js";

type Operation = (a: JqValue, b: JqValue) => JqValue | Fail;

function table(name: string, operation: Operation, cases: [string, string, string][]): void {
  test(name, () => {
    for (const [a, b, expected] of cases) assert.equal(R(operation(V(a), V(b))), expected, `${a} ${name} ${b}`);
  });
}

table("add", ValueOps.tryAdd, [
  ["null", "1", "1"],
  ["1", "null", "1"],
  ["null", "null", "null"],
  ["null", '{"a":1}', '{"a":1}'],
  ["1", "2", "3"],
  ["0.1", "0.2", "0.30000000000000004"],
  ["1e308", "1e308", "1.7976931348623157e+308"],
  ['"a"', '"b"', '"ab"'],
  ["[1]", "[2,[3]]", "[1,2,[3]]"],
  ["[]", "[]", "[]"],
  ['{"a":1,"b":2}', '{"c":3,"a":4}', '{"a":4,"b":2,"c":3}'],
  ['{"b":1}', '{"a":2}', '{"b":1,"a":2}'],
  ["1", '"a"', 'error: number (1) and string ("a") cannot be added'],
  ["{}", "[]", "error: object ({}) and array ([]) cannot be added"],
  ["true", "true", "error: boolean (true) and boolean (true) cannot be added"],
  ["false", "1", "error: boolean (false) and number (1) cannot be added"],
  ['{"a":"very long string"}', "1", 'error: object ({"a":"very long string"}) and number (1) cannot be added'],
  ["[1,2,3,4,5,6,7,8,9,10,11,12,13]", "1", "error: array ([1,2,3,4,5,6,7,8,9,10,11,...]) and number (1) cannot be added"],
]);

table("subtract", ValueOps.trySubtract, [
  ["5", "3", "2"],
  ["[1,2,3,1,[1]]", "[1,[1]]", "[2,3]"],
  ["[1,2]", "[]", "[1,2]"],
  ['[{"a":1,"b":2}]', '[{"b":2,"a":1}]', "[]"],
  ['"a"', '"a"', 'error: string ("a") and string ("a") cannot be subtracted'],
  ["null", "1", "error: null (null) and number (1) cannot be subtracted"],
  ["{}", "{}", "error: object ({}) and object ({}) cannot be subtracted"],
]);

table("multiply", ValueOps.tryMultiply, [
  ["2", "3", "6"],
  ['"ab"', "3", '"ababab"'],
  ["3", '"ab"', '"ababab"'],
  ['"ab"', "1", '"ab"'],
  ['"ab"', "1.9", '"ab"'],
  ['"ab"', "1.5", '"ab"'],
  ['"x"', "0", '""'],
  ['"ab"', "0.5", '""'],
  ['"ab"', "-0", '""'],
  ["0", '"ab"', '""'],
  ['"ab"', "-0.5", "null"],
  ['"ab"', "-1", "null"],
  ['"ab"', "nan", "null"],
  ['""', "5", '""'],
  ['""', "1e300", '""'],
  ['"é"', "1.5e9", "error: Repeat string result too long"],
  ['"ab"', "1e9", "error: Repeat string result too long"],
  ['{"a":{"b":1},"c":1}', '{"a":{"d":2},"c":{"x":1}}', '{"a":{"b":1,"d":2},"c":{"x":1}}'],
  ['{"a":{"b":{"c":1}}}', '{"a":{"b":{"d":2}},"e":3}', '{"a":{"b":{"c":1,"d":2}},"e":3}'],
  ['{"a":{"b":1}}', '{"a":1}', '{"a":1}'],
  ["[1]", "2", "error: array ([1]) and number (2) cannot be multiplied"],
  ['"a"', '"b"', 'error: string ("a") and string ("b") cannot be multiplied'],
  ["null", "2", "error: null (null) and number (2) cannot be multiplied"],
]);

table("divide", ValueOps.tryDivide, [
  ["6", "4", "1.5"],
  ["-1", "3", "-0.3333333333333333"],
  ["1", "0", "error: number (1) and number (0) cannot be divided because the divisor is zero"],
  ["0", "-0", "error: number (0) and number (-0) cannot be divided because the divisor is zero"],
  ['"a,b,c"', '","', '["a","b","c"]'],
  ['"a,b,"', '","', '["a","b",""]'],
  ['"a, b"', '", "', '["a","b"]'],
  ['"abc"', '"x"', '["abc"]'],
  ['""', '","', "[]"],
  ['"aé😀"', '""', '["a","é","😀"]'],
  ["[]", "1", "error: array ([]) and number (1) cannot be divided"],
  ['"a"', "1", 'error: string ("a") and number (1) cannot be divided'],
]);

table("modulo", ValueOps.tryModulo, [
  ["5", "3", "2"],
  ["-5", "3", "-2"],
  ["5", "-3", "2"],
  ["5.9", "3.9", "2"],
  ["5", "-1", "0"],
  ["-4", "2", "0"],
  ["-9223372036854775808", "-1", "0"],
  ["1e300", "7", "0"],
  ["nan", "1", "null"],
  ["1", "nan", "null"],
  ["1e300", "-1e300", "9223372036854776000"],
  ["-1e300", "1e300", "-1"],
  ["1", "0", "error: number (1) and number (0) cannot be divided (remainder) because the divisor is zero"],
  ["5", "0.5", "error: number (5) and number (0.5) cannot be divided (remainder) because the divisor is zero"],
  ['"a"', "1", 'error: string ("a") and number (1) cannot be divided (remainder)'],
  ["1", '"a"', 'error: number (1) and string ("a") cannot be divided (remainder)'],
]);

test("remainder of infinities", () => {
  // [(infinite, -infinite) % (1, -1, infinite)] is [0,0,0,0,0,-1] (spec §4.4).
  const results: string[] = [];
  for (const a of [Infinity, -Infinity]) {
    for (const b of [1, -1, Infinity]) results.push(R(ValueOps.tryModulo(a, b)));
  }

  assert.deepEqual(results, ["0", "0", "0", "0", "0", "-1"]);
});

test("negation", () => {
  const cases: [string, string][] = [
    ["1", "-1"],
    ["0", "-0"],
    ["-0", "0"],
    ["nan", "null"],
    ["null", "error: null (null) cannot be negated"],
    ['"a"', 'error: string ("a") cannot be negated'],
    ['"very-long-long-long-long-string"', 'error: string ("very-long-long-long-long...") cannot be negated'],
  ];
  for (const [a, expected] of cases) assert.equal(R(ValueOps.tryNegate(V(a))), expected, a);
});

test("operands never change", () => {
  const a = V('{"a":1,"b":{"c":2}}');
  const b = V('{"b":{"d":3},"e":4}');
  assert.ok(!(ValueOps.tryAdd(a, b) instanceof Fail));
  assert.ok(!(ValueOps.tryMultiply(a, b) instanceof Fail));
  assert.equal(J(a), '{"a":1,"b":{"c":2}}');
  assert.equal(J(b), '{"b":{"d":3},"e":4}');
});

test("deep merge of ten thousand levels does not recurse", () => {
  // {"a":{"a":…{"x":1}}} * {"a":{"a":…{"y":2}}}, 10,000 levels.
  const nest = (key: string, depth: number): JqValue => {
    let value: JqValue = JqObject.Empty.set(key, depth);
    for (let i = 0; i < depth; i++) value = JqObject.Empty.set("a", value);
    return value;
  };

  let result = ValueOps.tryMultiply(nest("x", 10000), nest("y", 10000));
  assert.ok(!(result instanceof Fail));
  for (let i = 0; i < 10000; i++) result = (result as JqObject).valueAt(0);
  assert.equal(J(result as JqValue), '{"x":10000,"y":10000}');
});
