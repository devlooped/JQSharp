// The port of dotnet/Tests/Core/Binding/ModuleTests.cs.

import assert from "node:assert/strict";
import { test } from "node:test";
import type { IModuleSource } from "../../src/binding/modules.js";
import { Resolver } from "../../src/binding/resolver.js";
import { EvaluationOptions } from "../../src/runtime/evaluation.js";
import { MaxNesting } from "../../src/syntax/parser.js";
import { SyntaxError } from "../../src/syntax/syntaxError.js";
import { ModulesDirectory } from "../support/corpus.js";
import { DirectoryModules, MemoryModules } from "../support/directoryModules.js";
import * as Evaluate from "../support/evaluate.js";

const Corpus = new DirectoryModules(ModulesDirectory);

function error(program: string, modules: IModuleSource | null): string {
  const bound = Resolver.tryCompile(program, true, modules);
  assert.ok(bound instanceof SyntaxError, program);
  return `${bound.message} @${bound.module ?? "main"}:${bound.line}:${bound.column}`;
}

function bind(program: string, modules: IModuleSource) {
  const bound = Resolver.tryCompile(program, true, modules);
  assert.ok(!(bound instanceof SyntaxError), bound instanceof SyntaxError ? bound.message : "");
  return bound;
}

function run(program: string, modules: IModuleSource, input: string = "null"): string {
  return Evaluate.runBound(bind(program, modules), Evaluate.json(input)).join(", ");
}

test("binding errors say where they are", () => {
  const cases: [string, string][] = [
    ['import "double" as d; import "nope" as n; 1', "module not found: nope @main:1:23"],
    ['import "broken" as b; 1', "Expected ')' but found ';' @broken.jq:1:15"],
    ['import "usesmain" as u; 1', "mainf/0 is not defined @usesmain.jq:1:15"],
    ['import "cycle1" as c; 1', "circular import of cycle1.jq @cycle2.jq:1:1"],
    ['import "self" as s; 1', "circular import of self.jq @self.jq:1:1"],
    ['include "config"; 1', "module not found: config @main:1:1"],
    ['import "a" as a; a::zz', "a::zz/0 is not defined @main:1:18"],
    ['import "values" as $d; $d::x', "$d::x is not defined @main:1:24"],
  ];
  for (const [program, expected] of cases) assert.equal(error(program, Corpus), expected, program);
});

test("bad data is a binding error", () => {
  assert.ok(error('import "bad" as $b; $b', Corpus).startsWith("error loading data file bad.json: Unfinished JSON term at EOF"));
});

test("without a source no module exists", () => {
  assert.equal(error('import "a" as a; 1', null), "module not found: a @main:1:1");
  assert.equal(Evaluate.line('try ("a" | modulemeta) catch .'), '"module not found: a"');
});

test("each module is read once", () => {
  const modules = new DirectoryModules(ModulesDirectory);
  assert.equal(run('import "one" as o; import "one" as p; include "one"; o::k + p::k', modules), "2");
  assert.equal(modules.reads, 1);
});

test("a long import chain is no recursion", () => {
  // m0 imports m1, …, m999 imports nothing.
  const sources = new Map<string, string>();
  for (let i = 0; i < 1000; i++) sources.set(`m${i}.jq`, i === 999 ? "def f: 999;" : `import "m${i + 1}" as n; def f: n::f; def g: ${i};`);
  assert.equal(run('import "m0" as m; m::f, m::g', new MemoryModules(sources)), "999, 0");
});

test("modulemeta uses what the program loaded", () => {
  const sources = new Map<string, string>([["x.jq", "def a: 1;"]]);
  const bound = bind('import "x" as x; ["x", "y"][] | modulemeta | .defs', new MemoryModules(sources));
  sources.set("x.jq", "def changed: 1;");
  sources.set("y.jq", "def b: 1; def c: 2;");
  // x as the program loaded it; y, which it does not import, from its source now.
  assert.deepEqual(Evaluate.runBound(bound, null), ['["a/0"]', '["b/0","c/0"]']);
});

test("modules share the host variables", () => {
  const bound = bind('import "v" as v; v::v, $x', new MemoryModules(new Map([["v.jq", "def v: $x;"]])));
  const options = new EvaluationOptions();
  options.variables = new Map([["x", 7]]);
  assert.deepEqual(Evaluate.runBound(bound, null, options), ["7", "7"]);
});

test("qualified aliases nest", () => {
  assert.equal(run('import "a" as x::y; x::y::f', new MemoryModules(new Map([["a.jq", "def f: 1;"]]))), "1");
});

test("a module obeys the nesting limit of its file", () => {
  const deep = "[".repeat(MaxNesting + 1) + "]".repeat(MaxNesting + 1);
  const modules = new MemoryModules(new Map([["deep.jq", `def f: ${deep};`]]));
  // def opens the first level, so the 1000th bracket is one too many.
  assert.equal(error('import "deep" as d; 1', modules), "Program too deeply nested @deep.jq:1:1007");
});
