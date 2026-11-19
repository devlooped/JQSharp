// The port of dotnet/Tests/Core/Binding/ResolverLimitTests.cs, on the stack Node gives its main
// thread (the reference binds on a thread with a 1 MB stack).

import assert from "node:assert/strict";
import { test } from "node:test";
import { type BoundProgram, type CallFunction, type CallParameter, type Node, NodeKind, type Variable } from "../../src/binding/bound.js";
import { Prelude } from "../../src/binding/prelude.js";
import { Resolver } from "../../src/binding/resolver.js";
import { MaxNesting } from "../../src/syntax/parser.js";
import { SyntaxError } from "../../src/syntax/syntaxError.js";
import * as Cases from "./resolverLimitCases.js";

const Limit = MaxNesting;

function binds(source: string): BoundProgram {
  const bound = Resolver.tryCompile(source);
  if (bound instanceof SyntaxError) throw new Error(`${bound.message} at ${bound.line}:${bound.column}`);
  return bound;
}

// Opening and closing repeated Limit times around "1"; each opening is one level of nesting and
// costs the binder the most frames that level can (a construct, then a sequence inside it).
test("nesting up to the limit binds", () => {
  for (const [open, close] of Cases.Nesting_up_to_the_limit_binds) {
    const levels = open.includes("{(") ? Limit / 2 : Limit;
    binds("def f: 1; def f(g): 1; " + open.repeat(levels) + "1" + close.repeat(levels));
  }
});

// The sequences of ParserLimitTests: prefix, 10,000 elements, suffix.
test("long sequences do not recurse", () => {
  for (const [prefix, element, suffix] of Cases.Long_sequences_do_not_recurse) binds(prefix + element.repeat(10000) + suffix);
});

test("long elif chains do not recurse", () => {
  binds("if . then 1 " + "elif . then 1 ".repeat(10000) + "else 2 end");
});

test("a variable ten thousand frames out", () => {
  const program = binds("0 as $a | " + "1 as $x | ".repeat(10000) + "$a");
  let node: Node = program.body;
  let depth = 0;
  while (node.kind === NodeKind.Bind) {
    node = node.body;
    depth++;
  }

  assert.equal(depth, 10001);
  assert.equal(node.kind, NodeKind.Variable);
  assert.deepEqual([(node as Variable).hops, (node as Variable).slot], [10000, 0]);
});

test("many definitions in a row", () => {
  const program = binds("def f: 1; ".repeat(10000) + "f");
  assert.equal(program.functions.length, Prelude.instance.functions.length + 10000);
  const call = program.body as CallFunction;
  assert.equal(call.kind, NodeKind.CallFunction);
  assert.equal(call.function, program.functions.length - 1);
  assert.equal(call.hops, 0);
});

test("many parameters and arguments", () => {
  const program = binds("def f(" + "a; ".repeat(10000) + "b): b; f(" + "1; ".repeat(10000) + "1)");
  assert.equal((program.body as CallFunction).arguments.length, 10001);
  const body = program.functions[program.functions.length - 1]!.body as CallParameter;
  assert.equal(body.kind, NodeKind.CallParameter);
  assert.deepEqual([body.hops, body.slot], [0, 10000]);
});
