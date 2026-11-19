// What the tests share about the conformance corpus: where it is, the host of spec §14.2 and
// the comparison rule.

import { readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import type { IClock } from "../../src/runtime/evaluation.js";
import type { JqArray } from "../../src/values/jqArray.js";
import type { JqObject } from "../../src/values/jqObject.js";
import { Fail, JqKind, type JqValue, kindOf } from "../../src/values/jqValue.js";
import * as ValueText from "../../src/values/valueText.js";

/** The root of the repository: the tests run from typescript/dist/test. */
export const RepositoryRoot = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "..", "..");

export const SpecDirectory = join(RepositoryRoot, "spec", "tests");

export const ModulesDirectory = join(SpecDirectory, "modules");

/** The files of the corpus, in name order. */
export function corpusFiles(): string[] {
  return readdirSync(SpecDirectory)
    .filter((name) => name.endsWith(".test"))
    .sort();
}

/** UTC+02:00 without DST or abbreviation, for the core. */
export const FixedZoneClock: IClock = {
  now: () => Date.now() / 1000,
  offset: () => 7200,
  abbreviation: () => null,
};

/** The outputs, as text, against the expected lines: each read back and compared structurally (spec §14.2). Null when they agree. */
export function compare(expected: readonly string[], actual: readonly string[]): string | null {
  if (actual.length !== expected.length || !expected.every((line, i) => textEquals(line, actual[i]!))) {
    return `expected [${expected.join(", ")}] but got [${actual.join(", ")}]`;
  }

  return null;
}

function textEquals(expected: string, actual: string): boolean {
  const left = ValueText.tryParse(expected);
  const right = ValueText.tryParse(actual);
  return !(left instanceof Fail) && !(right instanceof Fail) && same(left, right);
}

// Numbers as doubles (-0 equals 0), strings by code points, arrays in order, objects with the
// same keys in the same order. Without recursion: values of the corpus are up to 10,000 deep.
function same(first: JqValue, second: JqValue): boolean {
  const pending: JqValue[] = [first, second];
  while (pending.length > 0) {
    const right = pending.pop()!;
    const left = pending.pop()!;
    const kind = kindOf(left);
    if (kind !== kindOf(right)) return false;
    switch (kind) {
      case JqKind.Number:
      case JqKind.String:
        if (left !== right) return false;
        break;
      case JqKind.Array: {
        const a = left as JqArray;
        const b = right as JqArray;
        if (a.count !== b.count) return false;
        for (let i = 0; i < a.count; i++) pending.push(a.get(i), b.get(i));
        break;
      }
      case JqKind.Object: {
        const x = left as JqObject;
        const y = right as JqObject;
        if (x.count !== y.count) return false;
        for (let i = 0; i < x.count; i++) {
          if (x.keyAt(i) !== y.keyAt(i)) return false;
          pending.push(x.valueAt(i), y.valueAt(i));
        }

        break;
      }
    }
  }

  return true;
}

const FixedText =
  /^(\S+\/\d+ is not defined|\$\*label-\S+ is not defined|\$\S+::\S+ is not defined|Cannot use .+ as object key|May need parentheses around object key expression|Module metadata must be constant|Module metadata must be an object|Import path must be constant|Invalid escape)$/;

/**
 * The message lines of a %%FAIL case that are fixed texts of spec §10.2 must be contained in the
 * error; the other lines are jq's own wording and are not compared (spec §14.1). Null when none
 * is missing.
 */
export function missingFixedText(expectedError: string | null, message: string): string | null {
  for (const line of (expectedError ?? "").split("\n")) {
    if (FixedText.test(line) && !message.includes(line)) return `expected an error containing '${line}', got '${message}'`;
  }

  return null;
}
