// Runs a program through the core and gives its outputs as text (dotnet/Tests/Core/Runtime/Evaluate.cs).

import type { BoundProgram } from "../../src/binding/bound.js";
import { Resolver } from "../../src/binding/resolver.js";
import { EvaluationOptions, OutcomeKind } from "../../src/runtime/evaluation.js";
import { Interpreter } from "../../src/runtime/interpreter.js";
import { SyntaxError } from "../../src/syntax/syntaxError.js";
import { Fail, type JqValue } from "../../src/values/jqValue.js";
import * as ValueText from "../../src/values/valueText.js";

export function json(text: string): JqValue {
  const value = ValueText.tryParse(text);
  if (value instanceof Fail) throw new Error(`bad JSON ${text}: ${String(value.error)}`);
  return value;
}

export function compile(program: string): BoundProgram {
  const bound = Resolver.tryCompile(program);
  if (bound instanceof SyntaxError) throw new Error(`'${program}': ${bound.message} at ${bound.line}:${bound.column}`);
  return bound;
}

/** Options with the given depth limit. */
export function depth(maxDepth: number): EvaluationOptions {
  const options = new EvaluationOptions();
  options.maxDepth = maxDepth;
  return options;
}

/** The outputs of a bound program as texts; then `error: …`, `halt code [value]` or `stopped` for how it ended. */
export function runBound(program: BoundProgram, input: JqValue, options: EvaluationOptions | null = null, take: number = Infinity): string[] {
  const outputs: string[] = [];
  const outcome = Interpreter.run(program, input, options ?? new EvaluationOptions(), (output) => {
    outputs.push(ValueText.toJson(output));
    return outputs.length < take;
  });
  switch (outcome.kind) {
    case OutcomeKind.Error:
      outputs.push("error: " + ValueText.toJson(outcome.value));
      break;
    case OutcomeKind.Halt:
      outputs.push(`halt ${outcome.code}` + (outcome.hasValue ? " " + ValueText.toJson(outcome.value) : ""));
      break;
    case OutcomeKind.Stopped:
      if (outputs.length < take) outputs.push("stopped");
      break;
  }

  return outputs;
}

export function run(program: string, input: string = "null", options: EvaluationOptions | null = null, take: number = Infinity): string[] {
  return runBound(compile(program), json(input), options, take);
}

/** The outputs of `run`, joined with ", ". */
export function line(program: string, input: string = "null", options: EvaluationOptions | null = null): string {
  return run(program, input, options).join(", ");
}
