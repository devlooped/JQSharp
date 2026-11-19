// The port of dotnet/Tests/Core/Binding/CorpusBindTests.cs.

import assert from "node:assert/strict";
import { join } from "node:path";
import { test } from "node:test";
import { Resolver } from "../../src/binding/resolver.js";
import { Parser } from "../../src/syntax/parser.js";
import { SyntaxError } from "../../src/syntax/syntaxError.js";
import { ModulesDirectory, SpecDirectory, corpusFiles } from "../support/corpus.js";
import { DirectoryModules } from "../support/directoryModules.js";
import { parseFile } from "../support/jqTestParser.js";

// The module files of modules.test (spec/tests/README.md).
const Modules = new DirectoryModules(ModulesDirectory);

for (const file of corpusFiles()) {
  test(`every program of ${file} binds`, () => {
    const failures: string[] = [];
    for (const item of parseFile(join(SpecDirectory, file))) {
      const program = Parser.tryParse(item.program);
      if (program instanceof SyntaxError) {
        if (!item.shouldFail) failures.push(`${file}:${item.lineNumber}: '${item.program}' does not parse: ${program.message}`);
        continue;
      }

      const bound = Resolver.tryBind(program, true, Modules);
      const binds = !(bound instanceof SyntaxError);
      if (item.shouldFail && binds) failures.push(`${file}:${item.lineNumber}: '${item.program}' binds but is a %%FAIL case`);
      else if (!item.shouldFail && !binds) failures.push(`${file}:${item.lineNumber}: '${item.program}': ${bound.message} at ${bound.line}:${bound.column}`);
      else if (item.shouldFail && !binds && item.expectedError !== null && !bound.message.includes(item.expectedError)) {
        failures.push(`${file}:${item.lineNumber}: '${item.program}': '${bound.message}' does not contain '${item.expectedError}'`);
      }
    }

    assert.deepEqual(failures, []);
  });
}

test("some failure of the corpus is a binding error", () => {
  const bindingFailures = corpusFiles()
    .flatMap((file) => parseFile(join(SpecDirectory, file)))
    .filter((item) => item.shouldFail && !(Parser.tryParse(item.program) instanceof SyntaxError))
    .map((item) => item.program);
  assert.ok(bindingFailures.includes(". as $foo | break $foo"));
});
