// Runs every case of the jqlite conformance corpus (spec/tests/*.test), by the rules of
// spec/tests/README.md: through the public API (Jq.parse, JqExpression), and through the core
// with every prelude builtin bound to its definition, to check that the natives that override
// some change nothing. Every case must pass both ways. A third run gives the public API plain
// values instead of text, where only the order of object keys is not compared (JavaScript moves
// the keys that look like array indexes to the front of a plain object).

import assert from "node:assert/strict";
import { join } from "node:path";
import { describe, test } from "node:test";
import { Jq, JqError, type JqOptions, JqParseError, type JsonValue } from "../src/index.js";
import { JqFileResolver } from "../src/node.js";
import { ModulesDirectory, SpecDirectory, compare, corpusFiles, missingFixedText } from "./support/corpus.js";
import { DirectoryModules } from "./support/directoryModules.js";
import { type JqTestCase, parseFile } from "./support/jqTestParser.js";
import { runCore } from "./support/runCore.js";

// The host of spec §14.2: no environment variables, a zone at UTC+02:00 without DST or
// abbreviation, the default depth, and the module files of modules/.
const Options: JqOptions = {
  environment: {},
  clock: { now: () => Date.now() / 1000, offset: () => 7200 },
  maxDepth: 1000,
  resolver: new JqFileResolver(ModulesDirectory),
};

const CorpusModules = new DirectoryModules(ModulesDirectory);

function run(test: JqTestCase, evaluate: (expression: ReturnType<typeof Jq.parse>, input: string) => string[]): string | null {
  let expression;
  try {
    expression = Jq.parse(test.program, Options);
  } catch (e) {
    if (!(e instanceof JqParseError)) throw e;
    return test.shouldFail ? missingFixedText(test.expectedError, e.message) : e.message;
  }

  if (test.shouldFail) return "the program parses and binds but must not";
  let outputs: string[];
  try {
    outputs = evaluate(expression, test.input);
  } catch (e) {
    if (!(e instanceof JqError)) throw e;
    return `${e.name} ${e.message}`;
  }

  return compare(test.expectedOutputs, outputs);
}

// The keys of every object in code point order, so that two texts compare whatever the key order.
function sorted(value: JsonValue): JsonValue {
  return JSON.parse(
    JSON.stringify(value, (_key, item: JsonValue) => {
      if (item === null || typeof item !== "object" || Array.isArray(item)) return item;
      const ordered: { [key: string]: JsonValue } = {};
      for (const key of Object.keys(item).sort()) ordered["k:" + key] = item[key]!;
      return ordered;
    }),
  ) as JsonValue;
}

let plainCases = 0;

function runPlain(test: JqTestCase): string | null {
  let input: JsonValue;
  let expected: JsonValue[];
  try {
    input = JSON.parse(test.input) as JsonValue;
    expected = test.expectedOutputs.map((line) => sorted(JSON.parse(line) as JsonValue));
  } catch {
    // Not for the host's JSON: values nested deeper than it reads, or numbers it does not have.
    return null;
  }

  plainCases++;
  return run({ ...test, expectedOutputs: expected.map((value) => JSON.stringify(value)) }, (expression) => expression.evaluate(input).map((value) => JSON.stringify(sorted(value))));
}

for (const file of corpusFiles()) {
  const cases = parseFile(join(SpecDirectory, file));
  describe(`corpus ${file}`, () => {
    for (const item of cases) {
      test(`${file}:${item.lineNumber} through the public API`, () => {
        assert.equal(
          run(item, (expression, input) => expression.evaluateText(input)),
          null,
          item.program,
        );
      });
      test(`${file}:${item.lineNumber} through the core without native overrides`, () => {
        assert.equal(runCore(item, false, CorpusModules), null, item.program);
      });
    }
  });
}

describe("corpus through plain values", () => {
  for (const file of corpusFiles()) {
    test(file, () => {
      const failures: string[] = [];
      for (const item of parseFile(join(SpecDirectory, file))) {
        if (item.shouldFail) continue;
        let failure: string | null;
        try {
          failure = runPlain(item);
        } catch (e) {
          failure = String(e);
        }

        if (failure !== null) failures.push(`${file}:${item.lineNumber}: ${item.program}\n    ${failure}`);
      }

      assert.deepEqual(failures, []);
    });
  }

  test("most cases can be given as plain values", () => {
    assert.ok(plainCases > 900, `${plainCases} cases`);
  });
});
