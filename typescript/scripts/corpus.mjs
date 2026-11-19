// A quick look at the conformance corpus while developing: runs every case through the core,
// with and without the native overrides, and prints the failures (the tests do the same, one
// test per case). Usage: node scripts/corpus.mjs [file-name-filter] (after npm run build).
import { join } from "node:path";
import { ModulesDirectory, SpecDirectory, corpusFiles } from "../dist/test/support/corpus.js";
import { DirectoryModules } from "../dist/test/support/directoryModules.js";
import { parseFile } from "../dist/test/support/jqTestParser.js";
import { runCore } from "../dist/test/support/runCore.js";

const filter = process.argv[2] ?? "";
const modules = new DirectoryModules(ModulesDirectory);
let passed = 0;
let failed = 0;
for (const file of corpusFiles()) {
  if (!file.includes(filter)) continue;
  for (const test of parseFile(join(SpecDirectory, file))) {
    for (const overrides of [true, false]) {
      let failure;
      try {
        failure = runCore(test, overrides, modules);
      } catch (e) {
        failure = `threw ${e instanceof Error ? e.stack : e}`;
      }

      if (failure === null) {
        passed++;
      } else {
        failed++;
        if (failed <= Number(process.env.MAX_FAILURES ?? 40)) console.log(`${file}:${test.lineNumber} (${overrides ? "overrides" : "prelude"}): ${test.program}\n    ${failure}`);
      }
    }
  }
}

console.log(`${passed} passed, ${failed} failed`);
process.exitCode = failed === 0 ? 0 : 1;
