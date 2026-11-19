// The port of dotnet/Tests/Core/Runtime/AssignTests.cs; the rows are in assignCases.ts, copied
// from the reference.

import assert from "node:assert/strict";
import { test } from "node:test";
import type { BoundProgram } from "../../src/binding/bound.js";
import { Resolver } from "../../src/binding/resolver.js";
import { SyntaxError } from "../../src/syntax/syntaxError.js";
import { json, line, runBound } from "../support/evaluate.js";
import * as Cases from "./assignCases.js";

function compile(program: string, nativeOverrides: boolean): BoundProgram {
  const bound = Resolver.tryCompile(program, nativeOverrides);
  if (bound instanceof SyntaxError) throw new Error(bound.message);
  return bound;
}

function outputs(program: BoundProgram, input: string): string {
  return runBound(program, json(input)).join(", ");
}

test("native and prelude agree", () => {
  for (const [program, input] of Cases.Native_and_prelude_agree) {
    const native = outputs(compile(program, true), input);
    const prelude = outputs(compile(program, false), input);
    assert.equal(native, prelude, `${program} on ${input}`);
  }
});

test("native updates are linear", () => {
  // The prelude definition copies the array once per element; the native one does not.
  const started = performance.now();
  assert.equal(outputs(compile("[range(200000)] | .[] |= . + 1 | length", true), "null"), "200000");
  assert.ok(performance.now() - started < 5000);
});

test("updates that add keys in a loop are linear", () => {
  // A copy per step is 100,000 × up to 100,000 entries.
  for (const program of [
    'reduce range(100000) as $i ({}; .["k\\($i)"] = $i) | length',
    'reduce range(100000) as $i ({}; .["k\\($i)"] |= $i) | length',
    "[range(100000)] | INDEX(.) | length",
    "reduce range(100000) as $i ([]; .[$i] = $i) | length",
  ]) {
    const started = performance.now();
    assert.equal(outputs(compile(program, true), "null"), "100000", program);
    assert.ok(performance.now() - started < 5000, program);
  }
});

test("paths of ten thousand keys", () => {
  // A value nested 10,000 arrays deep and the path of its innermost 0.
  const deep = "reduce range(10000) as $i (0; [.]) | [range(10000) | 0] as $p | ";
  assert.equal(line(deep + "getpath($p) |= . + 1 | getpath($p)"), "1");
  assert.equal(line(deep + "setpath($p; 2) | getpath($p)"), "2");
  assert.equal(line(deep + "delpaths([$p]) | getpath($p[1:])"), "[]");
  assert.equal(line(deep + "del(getpath($p)) | getpath($p[1:])"), "[]");
  assert.equal(line(deep + "getpath($p) = 3 | getpath($p)"), "3");
});
