// The port of dotnet/Tests/Core/Syntax/ParserTests.cs; the rows of its theories are in
// parserCases.ts, copied from the reference.

import assert from "node:assert/strict";
import { test } from "node:test";
import { type Binary, type Call, type Index, NodeKind, type Pipe } from "../../src/syntax/ast.js";
import { Parser } from "../../src/syntax/parser.js";
import { SyntaxError } from "../../src/syntax/syntaxError.js";
import { error, parse, print } from "../support/astPrinter.js";
import * as Cases from "./parserCases.js";

type Rows = readonly (readonly [string, string])[];

function table(name: string, rows: Rows, actual: (source: string) => string): void {
  test(name.replaceAll("_", " "), () => {
    for (const [source, expected] of rows) assert.equal(actual(source), expected, source);
  });
}

// Every row of the examples table of spec §3.1.
table("Precedence_examples_of_the_spec", Cases.Precedence_examples_of_the_spec, parse);
table("Precedence_examples_that_fail", Cases.Precedence_examples_that_fail, error);
// Each level of the table of spec §3.1, with its associativity and its neighbours.
table("Precedence_and_associativity", Cases.Precedence_and_associativity, parse);
table("Assignments_are_rewritten", Cases.Assignments_are_rewritten, parse);
table("Non_associative_operators_do_not_chain", Cases.Non_associative_operators_do_not_chain, error);
table("Try_operands_are_unary", Cases.Try_operands_are_unary, parse);
table("Try_operand_errors", Cases.Try_operand_errors, error);
table("Binding_sources", Cases.Binding_sources, parse);
table("Definitions_and_labels_consume_the_rest_of_the_pipe", Cases.Definitions_and_labels_consume_the_rest_of_the_pipe, parse);
table("Definitions_and_labels_are_not_operands", Cases.Definitions_and_labels_are_not_operands, error);
table("Definitions_and_calls", Cases.Definitions_and_calls, parse);
table("Definition_errors", Cases.Definition_errors, error);
table("Paths_and_postfix", Cases.Paths_and_postfix, parse);
table("Path_errors", Cases.Path_errors, error);
table("Terms", Cases.Terms, parse);
table("Strings_and_formats", Cases.Strings_and_formats, parse);
table("String_errors", Cases.String_errors, error);
table("Conditionals", Cases.Conditionals, parse);
table("Conditional_errors", Cases.Conditional_errors, error);
// Every key form and shorthand of spec §7.11.
table("Object_construction", Cases.Object_construction, parse);
table("Object_errors", Cases.Object_errors, error);
table("Patterns", Cases.Patterns, parse);
table("Pattern_errors", Cases.Pattern_errors, error);
table("Directive_errors", Cases.Directive_errors, error);
table("Syntax_errors", Cases.Syntax_errors, error);
table("Module_errors", Cases.Module_errors, (source) => {
  const module = Parser.tryParseModule(source);
  assert.ok(module instanceof SyntaxError, source);
  return `${module.message} @${module.line}:${module.column}`;
});

test("module directives", () => {
  const program = Parser.tryParseModule(
    'module {a: 1, "b": [1, "x", null, true, {c: false}], if: {},};\n' +
      'import "m" as m {search: "./"};\nimport "d" as $d;\ninclude "i";\ninclude "j" {k: []};\n' +
      "def f: 1; def g($x): $x;",
  );
  assert.ok(!(program instanceof SyntaxError));
  assert.equal(
    print(program),
    '(module {"a":1,"b":[1,"x",null,true,{"c":false}],"if":{}}) (import "m" m {"search":"./"}) (import "d" $d) ' + '(include "i") (include "j" {"k":[]}) (def f 1) (def g($x) $x)',
  );
  assert.equal(program.body, null);
  assert.deepEqual([program.imports[0]!.position.line, program.imports[0]!.position.column], [2, 1]);
});

test("a program with directives has a body", () => {
  assert.equal(parse('module {}; import "a" as foo::bar; include "b"; def f: 1; f'), '(module {}) (import "a" foo::bar) (include "b") (def f 1) f');
});

test("an empty module has no definitions", () => {
  const program = Parser.tryParseModule("# nothing");
  assert.ok(!(program instanceof SyntaxError));
  assert.equal(program.definitions.length, 0);
  assert.equal(program.body, null);
});

test("nodes carry the position of their token", () => {
  const program = Parser.tryParse("1 +\n  .a[0] |\n\tf(2)");
  assert.ok(!(program instanceof SyntaxError));
  const at = (node: { position: { line: number; column: number } }): [number, number] => [node.position.line, node.position.column];
  const pipe = program.body as Pipe;
  assert.equal(pipe.kind, NodeKind.Pipe);
  assert.deepEqual(at(pipe), [2, 9]);
  const add = pipe.left as Binary;
  assert.equal(add.kind, NodeKind.Binary);
  assert.deepEqual(at(add), [1, 3]);
  assert.deepEqual(at(add.left), [1, 1]);
  const index = add.right as Index;
  assert.equal(index.kind, NodeKind.Index);
  assert.deepEqual(at(index), [2, 5]);
  assert.deepEqual(at(index.target), [2, 3]);
  const call = pipe.right as Call;
  assert.equal(call.kind, NodeKind.Call);
  assert.deepEqual(at(call), [3, 2]);
  assert.deepEqual(at(call.arguments[0]!), [3, 4]);
});
