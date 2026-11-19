// The runner of the cross-language benchmark (bench/README.md): the same command line, the same
// three measurements and the same line of output in every port. It drives the core engine, so
// the numbers do not include the host boundary (here, plain values).

import { readFileSync, writeFileSync } from "node:fs";
import { Resolver } from "../src/binding/resolver.js";
import { EvaluationOptions, OutcomeKind } from "../src/runtime/evaluation.js";
import { Interpreter } from "../src/runtime/interpreter.js";
import { SyntaxError } from "../src/syntax/syntaxError.js";
import { Fail, type JqValue } from "../src/values/jqValue.js";
import * as ValueText from "../src/values/valueText.js";

const Rounds = 10;

function readText(path: string): string {
  const text = readFileSync(path, "utf8");
  return text.charCodeAt(0) === 0xfeff ? text.substring(1) : text;
}

function run(args: string[]): number {
  if (args.length !== 6) {
    console.error("usage: crossBench <program.jq> <input.json> <take> <warmup-ms> <measure-ms> <output-file>");
    return 2;
  }

  const source = readText(args[0]!);
  const inputText = readText(args[1]!);
  const take = Number.parseInt(args[2]!, 10);
  const warmupMs = Number.parseInt(args[3]!, 10);
  const measureMs = Number.parseInt(args[4]!, 10);

  const program = Resolver.tryCompile(source);
  if (program instanceof SyntaxError) {
    console.error(`${program.message} at ${program.line}:${program.column}`);
    return 1;
  }

  const input = ValueText.tryParse(inputText);
  if (input instanceof Fail) {
    console.error(`bad input: ${ValueText.toJson(input.error)}`);
    return 1;
  }

  const options = new EvaluationOptions();

  // The check run: the outputs as canonical text, one per line.
  let text = "";
  let count = 0;
  const outcome = Interpreter.run(program, input, options, (output) => {
    text += ValueText.toJson(output) + "\n";
    count++;
    return take === 0 || count < take;
  });
  if (outcome.kind === OutcomeKind.Error || (outcome.kind === OutcomeKind.Halt && outcome.hasValue)) {
    console.error(`error: ${ValueText.toJson(outcome.value)}`);
    return 1;
  }

  writeFileSync(args[5]!, text, "utf8");

  // compile: the program text to a bound program (the prelude is loaded already).
  const compileNs = measure(warmupMs, measureMs, () => {
    Resolver.tryCompile(source);
  });

  // eval: the bound program on the input value, outputs collected as values.
  const evalNs = measure(warmupMs, measureMs, () => {
    const outputs: JqValue[] = [];
    Interpreter.run(program, input, options, (output) => {
      outputs.push(output);
      return take === 0 || outputs.length < take;
    });
  });

  // text: the input text read, the program evaluated, the outputs written as text.
  const textNs = measure(warmupMs, measureMs, () => {
    const value = ValueText.tryParse(inputText) as JqValue;
    let written = "";
    let outputs = 0;
    Interpreter.run(program, value, options, (output) => {
      written += ValueText.toJson(output) + "\n";
      outputs++;
      return take === 0 || outputs < take;
    });
    // The string is built: a rope is flattened when its length is not all that is asked of it.
    sink = written.charCodeAt(written.length - 1);
  });

  const result = { impl: "typescript", runtime: `Node.js ${process.versions.node}`, compile_ns: round(compileNs), eval_ns: round(evalNs), text_ns: round(textNs), outputs: count };
  console.log(JSON.stringify(result));
  return 0;
}

// What the text operation leaves, so that the engine cannot drop the work.
export let sink = 0;

function round(ns: number): number {
  return Math.round(ns * 10) / 10;
}

// Nanoseconds per operation: the median of ten rounds of equal size, sized from the warm-up.
function measure(warmupMs: number, measureMs: number, operation: () => void): number {
  const started = performance.now();
  let warmups = 0;
  do {
    operation();
    warmups++;
  } while (performance.now() - started < warmupMs);

  const perOperationMs = (performance.now() - started) / warmups;
  const batch = Math.max(1, Math.floor(measureMs / Rounds / perOperationMs));
  const rounds: number[] = [];
  for (let n = 0; n < Rounds; n++) {
    const start = process.hrtime.bigint();
    for (let i = 0; i < batch; i++) operation();
    rounds.push(Number(process.hrtime.bigint() - start) / batch);
  }

  rounds.sort((a, b) => a - b);
  return (rounds[Rounds / 2 - 1]! + rounds[Rounds / 2]!) / 2;
}

process.exitCode = run(process.argv.slice(2));
