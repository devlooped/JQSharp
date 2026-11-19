// The port of dotnet/Tests/Core/Syntax/CorpusParseTests.cs.

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { test } from "node:test";
import { NodeKind } from "../../src/syntax/ast.js";
import { Parser } from "../../src/syntax/parser.js";
import { SyntaxError } from "../../src/syntax/syntaxError.js";
import { RepositoryRoot, SpecDirectory, corpusFiles } from "../support/corpus.js";
import { parseFile } from "../support/jqTestParser.js";

// %%FAIL cases whose program is syntactically valid: they fail when names are bound.
const BindingFailures = new Set([". as $foo | break $foo", "input", "[inputs]"]);

for (const file of corpusFiles()) {
  test(`every program of ${file} parses`, () => {
    const failures: string[] = [];
    for (const item of parseFile(join(SpecDirectory, file))) {
      const program = Parser.tryParse(item.program);
      const parses = !(program instanceof SyntaxError);
      // The failures of modules.test are all binding errors: missing modules, cycles, names.
      if (item.shouldFail && !BindingFailures.has(item.program) && file !== "modules.test") {
        if (parses) failures.push(`${file}:${item.lineNumber}: '${item.program}' parses but is a %%FAIL case`);
      } else if (!parses) {
        failures.push(`${file}:${item.lineNumber}: '${item.program}': ${program.message} at ${program.line}:${program.column}`);
      }
    }

    assert.deepEqual(failures, []);
  });
}

test("the binding failures are in the corpus", () => {
  const programs = new Set(
    corpusFiles()
      .flatMap((file) => parseFile(join(SpecDirectory, file)))
      .filter((item) => item.shouldFail)
      .map((item) => item.program),
  );
  for (const program of BindingFailures) assert.ok(programs.has(program), program);
});

test("the prelude parses as a module", () => {
  const source = readFileSync(join(RepositoryRoot, "spec", "prelude.jq"), "utf8");
  let program = Parser.tryParseModule(source);
  assert.ok(!(program instanceof SyntaxError));
  assert.equal(program.body, null);
  assert.equal(program.definitions.length, 102);
  assert.ok(program.definitions.some((definition) => definition.name === "_modify" && definition.params.length === 2));
  // And as a program of definitions only, whose body is '.'.
  program = Parser.tryParse(source);
  assert.ok(!(program instanceof SyntaxError));
  assert.equal(program.body!.kind, NodeKind.Identity);
});
