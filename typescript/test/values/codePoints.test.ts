// The port of dotnet/Tests/Core/Values/CodePointTests.cs.

import assert from "node:assert/strict";
import { test } from "node:test";
import * as CodePoints from "../../src/values/codePoints.js";
import * as ValueOps from "../../src/values/valueOps.js";
import { R, V } from "../support/valueHelpers.js";

test("length counts code points", () => {
  for (const [text, length] of [
    ["", 0],
    ["abc", 3],
    ["aé😀b", 4],
    ["😀😀", 2],
  ] as const) {
    assert.equal(CodePoints.length(text), length, text);
  }
});

test("slices", () => {
  const cases: [string, string, string, string][] = [
    ['"aé😀b"', "2", "3", '"😀"'],
    ['"aé😀b"', "-2", "null", '"😀b"'],
    ['"aé😀b"', "null", "2", '"aé"'],
    ['"aé😀b"', "1.5", "2.5", '"é😀"'],
    ['"aé😀b"', "3", "1", '""'],
    ['"aé😀b"', "-100", "100", '"aé😀b"'],
    ['"aé😀b"', "nan", "nan", '"aé😀b"'],
    ["[1,2,3,4]", "1", "3", "[2,3]"],
    ["[1,2,3,4]", "-1", "null", "[4]"],
    ["[1,2,3,4]", "null", "-1", "[1,2,3]"],
    ["[1,2,3,4]", "0.2", "0.2", "[1]"],
    ["[1,2,3,4]", "5", "10", "[]"],
    ["null", "1", "2", "null"],
    ["[0,1,2,3,4,5,6,7,8,9]", "1.2", "3.5", "[1,2,3]"],
    ['"abcdefghi"', "-2", "null", '"hi"'],
    ["null", '"a"', "{}", "null"],
    ["1", "1", "2", 'error: Cannot index number with object ({"start":1,"end":2})'],
    ["1", '"a"', "2", 'error: Cannot index number with object ({"start":"a","end":2})'],
    ["{}", "1", "null", 'error: Cannot index object with object ({"start":1,"end":null})'],
    ["[1]", '"a"', "null", "error: Array/string slice indices must be integers"],
    ['"ab"', "0", "true", "error: Array/string slice indices must be integers"],
  ];
  for (const [input, start, end, expected] of cases) assert.equal(R(ValueOps.trySlice(V(input), V(start), V(end))), expected, `${input}[${start}:${end}]`);
});

// jq 1.8.2: `.[{"start":…,"end":…}]`.
test("slices by object key", () => {
  const cases: [string, string, string][] = [
    ["[1,2,3]", '{"start":1,"end":null}', "[2,3]"],
    ["[1,2,3]", '{"start":1,"end":2,"x":0}', "[2]"],
    ['"abc"', '{"start":null,"end":null}', '"abc"'],
    ['"abc"', '{"start":1}', "error: Array/string slice indices must be integers"],
    ["[1,2,3]", '{"end":2}', "error: Array/string slice indices must be integers"],
    ["[1,2,3]", '{"start":"a","end":2}', "error: Array/string slice indices must be integers"],
    ["null", '{"start":1}', "null"],
    ["null", '{"a":1}', "null"],
    ["1", '{"start":1}', 'error: Cannot index number with object ({"start":1})'],
    ['{"a":1}', '{"start":1}', 'error: Cannot index object with object ({"start":1})'],
  ];
  for (const [input, key, expected] of cases) assert.equal(R(ValueOps.tryIndexSlice(V(input), V(key))), expected, `${input}[${key}]`);
});

test("explode", () => {
  const cases: [string, string][] = [
    ['"aé😀b"', "[97,233,128512,98]"],
    ['""', "[]"],
    ["1", "error: explode input must be a string"],
    ['["a"]', "error: explode input must be a string"],
  ];
  for (const [input, expected] of cases) assert.equal(R(ValueOps.tryExplode(V(input))), expected, input);
});

test("implode", () => {
  const cases: [string, string][] = [
    ["[97,233,128512,98]", '"aé😀b"'],
    ["[]", '""'],
    ["[65.9]", '"A"'],
    ["[1114111]", '"\u{10FFFF}"'],
    // jq 1.8.2: `[-0.5, 97, 1114112, 55296, -1, 97.9] | implode | explode` is [0,97,65533,65533,65533,97].
    ["[-0.5,97,1114112,55296,-1,97.9]", '"\\u0000a\uFFFD\uFFFD\uFFFDa"'],
    ["[57343,57344,1e300,-1e300]", '"\uFFFD\uE000\uFFFD\uFFFD"'],
    ['["a"]', 'error: string ("a") can\'t be imploded, unicode codepoint needs to be numeric'],
    ["[1,[1]]", "error: array ([1]) can't be imploded, unicode codepoint needs to be numeric"],
    ["[nan]", "error: number (null) can't be imploded, unicode codepoint needs to be numeric"],
    ["[null]", "error: null (null) can't be imploded, unicode codepoint needs to be numeric"],
    ['"a"', "error: implode input must be an array"],
    ["{}", "error: implode input must be an array"],
  ];
  for (const [input, expected] of cases) assert.equal(R(ValueOps.tryImplode(V(input))), expected, input);
});

test("lone surrogates become replacement characters", () => {
  const high = "\uD83D";
  const low = "\uDE00";
  const replacement = "\uFFFD";
  assert.equal(CodePoints.sanitize(`a${high}b`), `a${replacement}b`);
  assert.equal(CodePoints.sanitize(low), replacement);
  assert.equal(CodePoints.sanitize(high), replacement);
  assert.equal(CodePoints.sanitize(high + low + high), high + low + replacement);
  assert.equal(CodePoints.sanitize(low + high), replacement + replacement);
});

test("code point order differs from UTF-16 order above the surrogates", () => {
  // U+E000 is below U+1F600 by code point, above its high surrogate by UTF-16 unit.
  assert.equal(CodePoints.compare("\uE000", "😀"), -1);
  assert.equal(CodePoints.compare("😀", "\uFFFF"), 1);
  assert.equal(CodePoints.compare("a", "ab"), -1);
  assert.equal(CodePoints.compare("b", "ab"), 1);
  assert.equal(CodePoints.compare("é", "é"), 0);
});
