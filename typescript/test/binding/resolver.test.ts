// The port of dotnet/Tests/Core/Binding/ResolverTests.cs; the rows of its theories are in
// resolverCases.ts, copied from the reference (the comments above the rows are in the reference).

import assert from "node:assert/strict";
import { test } from "node:test";
import { NodeKind } from "../../src/binding/bound.js";
import { Prelude } from "../../src/binding/prelude.js";
import { Resolver } from "../../src/binding/resolver.js";
import { SyntaxError } from "../../src/syntax/syntaxError.js";
import { bind, error, print } from "../support/boundPrinter.js";
import * as Cases from "./resolverCases.js";

type Rows = readonly (readonly [string, string])[];

function table(name: string, rows: Rows, actual: (source: string) => string): void {
  test(name.replaceAll("_", " "), () => {
    for (const [source, expected] of rows) assert.equal(actual(source), expected, source);
  });
}

table("Variables_bind_lexically", Cases.Variables_bind_lexically, bind);
table("Functions_bind_to_the_innermost_visible_definition", Cases.Functions_bind_to_the_innermost_visible_definition, bind);
table("Parameters_are_closures", Cases.Parameters_are_closures, bind);
table("Labels_bind_lexically", Cases.Labels_bind_lexically, bind);
table("Patterns_bind_every_variable", Cases.Patterns_bind_every_variable, bind);
table("Unbound_variables_are_host_variables", Cases.Unbound_variables_are_host_variables, bind);
table("The_location_is_a_literal", Cases.The_location_is_a_literal, bind);
table("Calls_resolve_to_user_then_prelude_then_native", Cases.Calls_resolve_to_user_then_prelude_then_native, bind);
table("Unknown_names_are_binding_errors", Cases.Unknown_names_are_binding_errors, error);

// A function is single when its body is, its own calls taken as single (spec §7.7).
test("functions and calls are single by the rules", () => {
  for (const [program, expected] of Cases.Functions_and_calls_are_single_by_the_rules) {
    for (const nativeOverrides of [true, false]) {
      const bound = Resolver.tryCompile(program, nativeOverrides);
      assert.ok(!(bound instanceof SyntaxError), program);
      const own = bound.functions.slice(Prelude.instance.functions.length).map((func) => `${func}${func.single ? "+" : "-"}`);
      assert.equal([...own, `body${bound.body.atMostOne ? "+" : "-"}`].join(" "), expected, program);
    }
  }
});

test("host variables are listed once", () => {
  const program = Resolver.tryCompile("$foo, $bar, ($foo as $bar | $bar), $baz");
  assert.ok(!(program instanceof SyntaxError));
  assert.deepEqual(program.hostVariables, ["foo", "bar", "baz"]);
});

test("without overrides builtins bind to the prelude", () => {
  const program = Resolver.tryCompile(".a |= 1, (.b = 2), map_values(.)", false);
  assert.ok(!(program instanceof SyntaxError));
  assert.equal(print(program), '(, (prelude:_modify/2^0 (index . "a") 1) (prelude:_assign/2^0 (index . "b") 2) (prelude:map_values/1^0 .))');
  // Inside the prelude too: map_values calls _modify.
  let mapValues = Prelude.instance.functions[Prelude.instance.find("map_values", 1)!]!.body;
  assert.equal(mapValues.kind, NodeKind.CallNative);
  mapValues = Prelude.withoutOverrides.functions[Prelude.withoutOverrides.find("map_values", 1)!]!.body;
  assert.equal(mapValues.kind, NodeKind.CallFunction);
});

test("every construct binds its children", () => {
  assert.equal(
    bind('. as $x | [{a: $x, b, ($x): 1}, @base64 "x\\($x)", .[$x][$x:], if $x then try $x catch $x else $x and $x or -$x // @csv end, $x[], .a?]'),
    '(as . $0 (array (, (object ("a" $0.0) ("b" _) ($0.0 1)) (str @base64 "x" $0.0 "") (slice (index . $0.0) $0.0 _) (if $0.0 (try $0.0 $0.0) (// (or (and $0.0 $0.0) (neg $0.0)) @csv)) (iter $0.0) (try (index . "a")))))',
  );
});

test("prelude functions come first in every program", () => {
  const first = Resolver.tryCompile("def f: 1; f");
  const second = Resolver.tryCompile("map(.)");
  assert.ok(!(first instanceof SyntaxError) && !(second instanceof SyntaxError));
  const prelude = Prelude.instance.functions;
  assert.equal(first.functions.length, prelude.length + 1);
  assert.equal(second.functions.length, prelude.length);
  for (let i = 0; i < prelude.length; i++) {
    assert.equal(first.functions[i], prelude[i]);
    assert.equal(second.functions[i], prelude[i]);
  }
});

test("syntax errors come from the parser", () => {
  assert.equal(error("1 +"), "Unexpected end of program @1:4");
});

test("module metadata alone is accepted", () => {
  assert.equal(bind("module {a: 1}; 1"), "1");
});

// Not in the reference: the direct nodes of this port (bound.ts).
test("direct nodes are the single ones without calls, up to a nesting", () => {
  const body = (source: string): { direct: boolean; atMostOne: boolean; height: number } => {
    const program = Resolver.tryCompile(source);
    assert.ok(!(program instanceof SyntaxError), source);
    return program.body;
  };

  assert.ok(body(".a + 1").direct);
  assert.ok(body('{a: .b, c: [1], d: "x\\(.e)"} | length').direct);
  assert.ok(body("try (.a | tonumber) catch 0").direct);
  // A long chain is one level; nesting counts.
  assert.equal(body(Array(500).fill("1").join(" + ")).height, 2);
  assert.ok(body("[".repeat(60) + "1" + "]".repeat(60)).direct);
  assert.ok(!body("[".repeat(70) + "1" + "]".repeat(70)).direct);
  assert.ok(body("[".repeat(70) + "1" + "]".repeat(70)).atMostOne);
  // A generator, a call of a function or of a closure native: single or not, never direct.
  assert.ok(!body(".[]").direct);
  assert.ok(!body("[.[]]").direct && body("[.[]]").atMostOne);
  assert.ok(!body("def f: 1; f").direct && body("def f: 1; f").atMostOne);
  assert.ok(!body("getpath([1])").direct && body("getpath([1])").atMostOne);
  assert.ok(!body("reduce .[] as $x (0; . + $x)").direct);
});
