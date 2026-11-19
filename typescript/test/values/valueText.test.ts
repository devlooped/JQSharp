// The port of dotnet/Tests/Core/Values/ValueTextTests.cs. Left out: the test of the current
// culture (JavaScript has no ambient culture that `String(number)` or `Number(text)` follows).

import assert from "node:assert/strict";
import { test } from "node:test";
import * as CodePoints from "../../src/values/codePoints.js";
import * as Errors from "../../src/values/errors.js";
import { JqArray } from "../../src/values/jqArray.js";
import { Fail, type JqValue } from "../../src/values/jqValue.js";
import * as ValueText from "../../src/values/valueText.js";
import { J, V } from "../support/valueHelpers.js";

test("number text", () => {
  const cases: [number, string][] = [
    [0, "0"],
    [1, "1"],
    [-1, "-1"],
    [1.5, "1.5"],
    [-1.5, "-1.5"],
    [0.5, "0.5"],
    [100, "100"],
    [1e15, "1000000000000000"],
    [1e20, "100000000000000000000"],
    [123456789012345680000, "123456789012345680000"],
    [1e21, "1e+21"],
    [-1e21, "-1e+21"],
    [1.5e300, "1.5e+300"],
    [1e-6, "0.000001"],
    [1.5e-6, "0.0000015"],
    [1e-7, "1e-7"],
    [1.23e-18, "1.23e-18"],
    [0.1 + 0.2, "0.30000000000000004"],
    [5e-324, "5e-324"],
    [1.7976931348623157e308, "1.7976931348623157e+308"],
    [9007199254740992, "9007199254740992"],
    [123.456, "123.456"],
    [NaN, "null"],
    [Infinity, "1.7976931348623157e+308"],
    [-Infinity, "-1.7976931348623157e+308"],
    [-0, "-0"],
  ];
  for (const [number, expected] of cases) assert.equal(ValueText.formatNumber(number), expected);
});

test("number text round trips", () => {
  // Doubles of random bits, from a small generator with a fixed seed.
  let seed = 42;
  const next = (): number => {
    seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
    return seed;
  };

  const bits = new Uint32Array(2);
  const double = new Float64Array(bits.buffer);
  for (let i = 0; i < 10000; i++) {
    bits[0] = next();
    bits[1] = next();
    const number = double[0]!;
    if (!Number.isFinite(number)) continue;
    const text = ValueText.formatNumber(number);
    assert.ok(Object.is(ValueText.tryParseNumber(text), number), text);
  }
});

test("string escaping", () => {
  const cases: [string, string][] = [
    ["é<", '"é<"'],
    ['a"b\\c/d', '"a\\"b\\\\c/d"'],
    ["\b\f\n\r\t", '"\\b\\f\\n\\r\\t"'],
    ["\u0000\u0001\u001f", '"\\u0000\\u0001\\u001f"'],
    ["\u007f\u0080", '"\\u007f\u0080"'],
    ["😀", '"😀"'],
    ["\u2028", '"\u2028"'],
  ];
  for (const [text, expected] of cases) assert.equal(J(text), expected);
});

test("reader round trips", () => {
  for (const json of ["null", "true", "false", "[]", "{}", '""', '[1,2.5,"x",null,true,false,{},[[]]]', '{"b":1,"a":[1,{"z":null,"y":"é"}],"c":{"d":[]}}', "-0"]) {
    assert.equal(J(V(json)), json);
  }
});

test("reader values", () => {
  const cases: [string, string][] = [
    [' \t\r\n[ 1 , { "a" : 2 } ] \n', '[1,{"a":2}]'],
    ["\uFEFF1", "1"],
    ["1.0", "1"],
    ["1E2", "100"],
    ["1e-2", "0.01"],
    ["1.", "1"],
    [".5", "0.5"],
    ["+1", "1"],
    ["007", "7"],
    ["1e1000", "1.7976931348623157e+308"],
    ["-1e1000", "-1.7976931348623157e+308"],
    ["1e-1000", "0"],
    ["nan", "null"],
    ["NaN", "null"],
    ["[-nan]", "[null]"],
    ["Infinity", "1.7976931348623157e+308"],
    ["-Infinity", "-1.7976931348623157e+308"],
    ['"\\u00e9\\/\\""', '"é/\\""'],
    ['"\\ud83d\\ude00"', '"😀"'],
    ['"\\ud800"', '"\uFFFD"'],
    ['"\\udc00x"', '"\uFFFDx"'],
    ['"\\ud800\\ud800"', '"\uFFFD\uFFFD"'],
    ['"\\ud800\\u0041"', '"\uFFFDA"'],
    ["[nAn,SNaN,-snan,+inf,-INF,iNfInItY]", "[null,null,null,1.7976931348623157e+308,-1.7976931348623157e+308,1.7976931348623157e+308]"],
    ["[1,nan]", "[1,null]"],
    ['{"a":1,"b":2,"a":3}', '{"a":3,"b":2}'],
    ['"a\u007Fb"', '"a\\u007fb"'],
    ['{"a":1,"a":2}', '{"a":2}'],
    ['{"\\ud800":1}', '{"\uFFFD":1}'],
  ];
  for (const [json, expected] of cases) assert.equal(J(V(json)), expected, json);
});

// The reason (implementation-defined, §5.3); every message ends with " (while parsing '<input>')".
test("reader failures", () => {
  const cases: [string, string][] = [
    ["", "Expected JSON value"],
    ["   ", "Expected JSON value"],
    ["1 2", "Unexpected extra JSON values"],
    ["{", "Unfinished JSON term at EOF at line 1, column 1"],
    ["[1,2", "Unfinished JSON term at EOF at line 1, column 4"],
    ['"abc', "Unfinished JSON term at EOF at line 1, column 4"],
    ["[1 2]", "Expected separator between values at line 1, column 4"],
    ["[1,\n2 3]", "Expected separator between values at line 2, column 3"],
    ["tru", "Invalid literal at line 1, column 3"],
    ["nul", "Invalid literal at line 1, column 3"],
    ["Null", "Invalid literal at line 1, column 4"],
    ["NaN1", "Invalid literal at line 1, column 4"],
    ["nanx", "Invalid literal at line 1, column 4"],
    ["[1x]", "Invalid numeric literal at line 1, column 3"],
    ["1e", "Invalid numeric literal at line 1, column 2"],
    ["'a'", "Invalid numeric literal at line 1, column 3"],
    ['"\\q"', "Invalid escape at line 1, column 3"],
    ['"\\u12G4"', "Invalid \\uXXXX escape at line 1, column 6"],
    ['"a\nb"', "Invalid string: control characters from U+0000 through U+001F must be escaped at line 2, column 0"],
    ['"a\u0001"', "Invalid string: control characters from U+0000 through U+001F must be escaped at line 1, column 3"],
    ['["\t"]', "Invalid string: control characters from U+0000 through U+001F must be escaped at line 1, column 3"],
    ["{1:2}", "Object keys must be strings at line 1, column 2"],
    ['{"a" 1}', "Objects must consist of key:value pairs at line 1, column 6"],
    ["[1,]", "Expected another array element at line 1, column 4"],
    ["]", "Unmatched ']' at line 1, column 1"],
    ["[1}", "Unmatched '}' at line 1, column 3"],
    [",", "Expected value before ',' at line 1, column 1"],
  ];
  for (const [json, reason] of cases) {
    const result = ValueText.tryParse(json);
    assert.ok(result instanceof Fail, json);
    assert.equal(result.error, `${reason} (while parsing '${json}')`);
  }
});

test("a sequence of values reads as an array", () => {
  assert.equal(J(ValueText.tryParseSequence("") as JqValue), "[]");
  assert.equal(J(ValueText.tryParseSequence(' 1 "a"\n[2] {"b":3} ') as JqValue), '[1,"a",[2],{"b":3}]');
  assert.ok(ValueText.tryParseSequence("1 ]") instanceof Fail);
});

test("reader depth limit", () => {
  const ok = "[".repeat(10000) + "]".repeat(10000);
  const deep = ValueText.tryParse(ok);
  assert.ok(!(deep instanceof Fail));
  assert.equal(J(deep), ok);
  const tooDeep = ValueText.tryParse("[".repeat(10001));
  assert.ok(tooDeep instanceof Fail);
  assert.ok((tooDeep.error as string).startsWith("Exceeds depth limit for parsing"));
});

// Arrays nested `levels` deep around `inner`.
function nest(levels: number, inner: JqValue): JqValue {
  let value = inner;
  for (let i = 0; i < levels; i++) value = JqArray.of(value);
  return value;
}

test("writer skips values deeper than 10001 levels", () => {
  const skipped = "<skipped: too deep>";
  // jq 1.8.2: `reduce range(n) as $i (x; [.]) | tojson`.
  assert.equal(J(nest(10000, 1)), "[".repeat(10000) + "1" + "]".repeat(10000));
  assert.equal(J(nest(10001, 1)), "[".repeat(10001) + skipped + "]".repeat(10001));
  assert.equal(J(nest(10000, JqArray.Empty)), "[".repeat(10001) + "]".repeat(10001));
  assert.equal(J(nest(10001, JqArray.Empty)), "[".repeat(10001) + skipped + "]".repeat(10001));
  assert.equal(J(nest(10000, V("[1,[2]]"))), "[".repeat(10001) + skipped + "," + skipped + "]".repeat(10001));
});

test("number reading", () => {
  const cases: [string, number][] = [
    ["12", 12],
    ["-1.5e3", -1500],
    ["0.1", 0.1],
    [".5", 0.5],
    ["5.", 5],
    ["+3", 3],
    ["inf", Infinity],
    ["-INFINITY", -Infinity],
  ];
  for (const [text, expected] of cases) assert.equal(ValueText.tryParseNumber(text), expected, text);
});

test("number reading failures", () => {
  for (const text of ["", " 1", "1 ", "0x10", ".", "-", "1e+", "1,5", "abc"]) assert.equal(ValueText.tryParseNumber(text), null, text);
});

test("NaN words read as NaN", () => {
  for (const text of ["nan", "NaN", "-nan", "+NAN", "snan", "-sNaN"]) assert.ok(Number.isNaN(ValueText.tryParseNumber(text)), text);
});

test("error dump", () => {
  const cases: [string, string][] = [
    ["[1,2]", "[1,2]"],
    ["[1,2,3,4,5,6,7,8,9,10,11,12,13]", "[1,2,3,4,5,6,7,8,9,10,11,...]"],
    ['{"aaaaaaaaaaaaaaaaaaaaaaaaaa":1}', '{"aaaaaaaaaaaaaaaaaaaaaaa...}'],
    // A cut value ends with "..." and its own closing delimiter, not those of what it contains.
    ['"very-long-long-long-long-string"', '"very-long-long-long-long..."'],
    ['["very-long-long-long-long-string"]', '["very-long-long-long-lon...]'],
    // 27 characters: 29 bytes with the quotes, not cut; one more is cut.
    ['"abcdefghijklmnopqrstuvwxyz0"', '"abcdefghijklmnopqrstuvwxyz0"'],
    ['"abcdefghijklmnopqrstuvwxyz01"', '"abcdefghijklmnopqrstuvwx..."'],
    // Two-byte é: the quote and 12 of them make 25 bytes.
    ['"ééééééééééééééééééé"', '"éééééééééééé..."'],
    ['"a😀😀😀😀😀😀😀"', '"a😀😀😀😀😀..."'],
  ];
  for (const [json, expected] of cases) {
    const dump = Errors.dump(V(json));
    assert.equal(dump, expected);
    assert.ok(CodePoints.utf8ByteCount(dump) <= 29);
  }
});
