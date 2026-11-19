// Runs one case of the corpus through the core (Resolver, Interpreter), by the rules of
// spec/tests/README.md.

import type { IModuleSource } from "../../src/binding/modules.js";
import { Resolver } from "../../src/binding/resolver.js";
import { EvaluationOptions, OutcomeKind } from "../../src/runtime/evaluation.js";
import { Interpreter } from "../../src/runtime/interpreter.js";
import { SyntaxError } from "../../src/syntax/syntaxError.js";
import { Fail } from "../../src/values/jqValue.js";
import * as ValueText from "../../src/values/valueText.js";
import { FixedZoneClock, compare, missingFixedText } from "./corpus.js";
import type { JqTestCase } from "./jqTestParser.js";

/** Why the case fails, or null when it passes. */
export function runCore(test: JqTestCase, nativeOverrides: boolean, modules: IModuleSource): string | null {
  const bound = Resolver.tryCompile(test.program, nativeOverrides, modules);
  if (bound instanceof SyntaxError) return test.shouldFail ? missingFixedText(test.expectedError, bound.message) : `${bound.message} at ${bound.line}:${bound.column}`;
  if (test.shouldFail) return "the program parses and binds but must not";
  const value = ValueText.tryParse(test.input);
  if (value instanceof Fail) return `bad input: ${ValueText.toJson(value.error)}`;
  const outputs: string[] = [];
  const options = new EvaluationOptions();
  options.maxDepth = 1000;
  options.clock = FixedZoneClock;
  const outcome = Interpreter.run(bound, value, options, (output) => {
    outputs.push(ValueText.toJson(output));
    return true;
  });
  if (outcome.kind === OutcomeKind.Error) return `error ${ValueText.toJson(outcome.value)} after [${outputs.join(", ")}]`;
  if (outcome.kind === OutcomeKind.Halt && (outcome.hasValue || outcome.code !== 0)) return `halt_error ${outcome.code} after [${outputs.join(", ")}]`;
  return compare(test.expectedOutputs, outputs);
}
