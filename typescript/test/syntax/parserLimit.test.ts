// The port of dotnet/Tests/Core/Syntax/ParserLimitTests.cs. The reference parses on a thread
// with a 1 MB stack; here the tests run on the stack Node gives its main thread, which is about
// that size and is what a library gets.

import assert from "node:assert/strict";
import { test } from "node:test";
import { type Node, NodeKind, type Program } from "../../src/syntax/ast.js";
import { MaxNesting, Parser } from "../../src/syntax/parser.js";
import { SyntaxError } from "../../src/syntax/syntaxError.js";
import * as Cases from "./parserLimitCases.js";

const Limit = MaxNesting;

function parses(source: string): Program {
  const program = Parser.tryParse(source);
  if (program instanceof SyntaxError) throw new Error(`${program.message} at ${program.line}:${program.column}`);
  return program;
}

function fails(source: string): string {
  const program = Parser.tryParse(source);
  assert.ok(program instanceof SyntaxError);
  return `${program.message} @${program.line}:${program.column}`;
}

// name, opening repeated, closing repeated (the innermost term is "1"), offset in the opening of
// the token that opens the level
const Nestings: [string, string, string, number][] = [
  ["parentheses", "(", ")", 0],
  ["arrays", "[", "]", 0],
  ["objects", "{a:", "}", 0],
  ["indexes", ".[", "]", 1],
  ["calls", "f(", ")", 1],
  ["interpolations", '"\\(', ')"', 0],
  ["if", "if . then ", " end", 0],
  ["try", "try ", "", 0],
  ["reduce", "reduce . as $x (", ";.)", 0],
  ["foreach", "foreach . as $x (", ";.)", 0],
  ["definitions", "def f: ", "; f", 0],
  ["object keys", "{(", "):1}", 0],
];

// "{(" opens two levels (the object and the parentheses).
function levels(open: string): number {
  return open === "{(" ? Limit / 2 : Limit;
}

// A parenthesized key must not be a number.
function innermost(open: string): string {
  return open === "{(" ? '"a"' : "1";
}

for (const [name, open, close, offset] of Nestings) {
  test(`nesting up to the limit parses: ${name}`, () => {
    const count = levels(open);
    parses(open.repeat(count) + innermost(open) + close.repeat(count));
  });

  test(`nesting beyond the limit fails at the opening token: ${name}`, () => {
    const count = levels(open) + 1;
    const source = open.repeat(count) + innermost(open) + close.repeat(count);
    // The level beyond the limit is opened by the token at offset in the last opening.
    const column = (count - 1) * open.length + offset + 1;
    assert.equal(fails(source), `Program too deeply nested @1:${column}`);
  });
}

test("nested patterns count too", () => {
  parses(". as " + "[".repeat(Limit) + "$x" + "]".repeat(Limit) + " | $x");
  assert.equal(fails(". as " + "[".repeat(Limit + 1) + "$x" + "]".repeat(Limit + 1) + " | $x"), `Program too deeply nested @1:${Limit + 6}`);
});

test("nested metadata counts too", () => {
  assert.equal(fails("module {a: " + "[".repeat(Limit) + "]".repeat(Limit) + "}; 1"), `Program too deeply nested @1:${Limit + 11}`);
});

test("nested metadata within the limit parses", () => {
  parses("module {a: " + "[".repeat(Limit - 1) + "]".repeat(Limit - 1) + "}; 1");
});

test("mixed nesting counts every kind", () => {
  assert.equal(fails("([".repeat(Limit / 2 + 1) + "1" + "])".repeat(Limit / 2 + 1)), `Program too deeply nested @1:${(Limit / 2) * 2 + 1}`);
});

// Sequences of any length parse without recursing per element: prefix, 10,000 elements, suffix.
test("long sequences do not recurse", () => {
  for (const [prefix, element, suffix] of Cases.Long_sequences_do_not_recurse) parses(prefix + element.repeat(10000) + suffix);
});

test("long elif chains do not recurse", () => {
  parses("if . then 1 " + "elif . then 1 ".repeat(10000) + "else 2 end");
});

test("a long chain keeps its associativity", () => {
  let program = parses("1 - ".repeat(10000) + "1");
  // Left-associative: the left spine is 10,000 subtractions deep.
  let depth = 0;
  let node: Node = program.body!;
  while (node.kind === NodeKind.Binary) {
    assert.equal(node.right.kind, NodeKind.Literal);
    node = node.left;
    depth++;
  }

  assert.equal(depth, 10000);
  program = parses("1 // ".repeat(10000) + "1");
  depth = 0;
  node = program.body!;
  while (node.kind === NodeKind.Alternative) {
    assert.equal(node.left.kind, NodeKind.Literal);
    node = node.right;
    depth++;
  }

  assert.equal(depth, 10000);
});

test("a long pipe of bindings nests to the right", () => {
  const program = parses("1 as $x | ".repeat(10000) + "$x");
  let depth = 0;
  let node: Node = program.body!;
  while (node.kind === NodeKind.Bind) {
    node = node.body;
    depth++;
  }

  assert.equal(depth, 10000);
  assert.equal(node.kind, NodeKind.Variable);
});

test("many top-level definitions are a list", () => {
  const program = parses("def f: 1; ".repeat(10000) + "f");
  assert.equal(program.definitions.length, 10000);
  assert.equal(program.body!.kind, NodeKind.Call);
});
