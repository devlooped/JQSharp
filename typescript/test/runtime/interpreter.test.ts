// The port of dotnet/Tests/Core/Runtime/InterpreterTests.cs; the rows of its theories are in
// interpreterCases.ts, copied from the reference. Where the reference runs on a thread with a
// 1 MB stack, the tests run on the stack of Node's main thread; where it evaluates from several
// threads at once, evaluations are nested in one another (one thread runs at a time).

import assert from "node:assert/strict";
import { test } from "node:test";
import { EvaluationOptions, OutcomeKind } from "../../src/runtime/evaluation.js";
import { Interpreter } from "../../src/runtime/interpreter.js";
import { JqObject } from "../../src/values/jqObject.js";
import type { JqValue } from "../../src/values/jqValue.js";
import * as ValueText from "../../src/values/valueText.js";
import { compile, depth, line, run, runBound } from "../support/evaluate.js";
import * as Cases from "./interpreterCases.js";

type Rows = readonly (readonly [string, string])[];

function table(name: string, rows: Rows, input: string = "null", options: () => EvaluationOptions | null = () => null): void {
  test(name.replaceAll("_", " "), () => {
    for (const [program, expected] of rows) assert.equal(line(program, input, options()), expected, program);
  });
}

function variables(values: Record<string, JqValue>): EvaluationOptions {
  const options = new EvaluationOptions();
  options.variables = new Map(Object.entries(values));
  return options;
}

table("Signals_pass_through_every_construct", Cases.Signals_pass_through_every_construct);

test("consumers that stop stop their producers", () => {
  for (const [program, expected] of Cases.Consumers_that_stop_stop_their_producers) {
    const started = performance.now();
    assert.equal(line(program, "[1,2,3]"), expected, program);
    assert.ok(performance.now() - started < 5000, `${program} took too long`);
  }
});

test("the host stops the stream", () => {
  assert.deepEqual(run("range(1000000000)", "null", null, 3), ["0", "1", "2"]);
});

// §8.1, construct by construct.
table("Paths_are_part_of_evaluation", Cases.Paths_are_part_of_evaluation, '{"a":{"b":0},"k":"x","c":[1,2]}', () => variables({ v: null }));

table("Assignment_works_through_the_prelude", Cases.Assignment_works_through_the_prelude, '{"a":0,"b":[1,2]}');

table("Host_variables_and_ENV", Cases.Host_variables_and_ENV, "null", () => {
  const options = variables({ x: 1, y: "two" });
  options.environment = () => JqObject.Empty.set("HOME", "/home");
  return options;
});

test("one program runs in several evaluations at once, with different variables", () => {
  const program = compile("def f: if . == 0 then $n else (. - 1 | f) end; [range(200) | {a: f}] | map(.a) | add");
  const results: string[] = [];
  // Each output of the outer evaluation starts another evaluation of the program, while the
  // outer one and the one before are still running.
  const outer = compile("def g: if . == 64 then empty else ., (. + 1 | g) end; 0 | g");
  const outcome = Interpreter.run(outer, null, new EvaluationOptions(), (i) => {
    results.push(runBound(program, 50, variables({ n: i })).join(", "));
    return true;
  });
  assert.equal(outcome.kind, OutcomeKind.Completed);
  assert.equal(results.length, 64);
  for (let i = 0; i < 64; i++) assert.equal(results[i], String(200 * i));
});

test("an evaluation started from the output of another does not disturb it", () => {
  const outer = compile("[range(5) | . * 2] | .[] | {a: (. + 1)} | try (.a | if . == 5 then error(\"five\") else . end) catch -1");
  const inner = compile("def f: if . == 0 then 0 else (. - 1 | f) + 1 end; try (if . < 0 then error(\"inner\") else f end) catch \"caught\"");
  const seen: string[] = [];
  Interpreter.run(outer, null, new EvaluationOptions(), (output) => {
    seen.push(ValueText.toJson(output) + ":" + runBound(inner, output).join(","));
    return true;
  });
  assert.deepEqual(seen, ["1:1", "3:3", '-1:"caught"', "7:7", "9:9"]);
});

test("the call depth is limited", () => {
  for (const maxDepth of [1000, 50]) {
    const options = depth(maxDepth);
    const count = "def f: if . == 0 then 0 else (. - 1 | f) end; ";
    // n | f makes n + 1 calls.
    assert.equal(line(count + (maxDepth - 1) + " | f", "null", options), "0");
    assert.equal(line(count + maxDepth + " | f", "null", options), 'error: "Maximum call depth exceeded"');
    assert.equal(line("try (" + count + "100000 | f) catch .", "null", options), '"Maximum call depth exceeded"');
  }
});

test("a single call has ended when its output is used", () => {
  // 20 | fib makes 21,891 calls, 20 deep.
  const fib = "def fib: if . < 2 then . else (. - 1 | fib) + (. - 2 | fib) end; 20 | fib";
  assert.equal(line(fib, "null", depth(20)), "6765");
  assert.equal(line(fib, "null", depth(19)), 'error: "Maximum call depth exceeded"');
  assert.equal(line("def fib($n): if $n < 2 then $n else fib($n - 1) + fib($n - 2) end; fib(20)", "null", depth(20)), "6765");
  // A call that is not single stays active while its outputs are consumed: the two calls nest.
  const many = "def g: if . < 2 then ., empty else (. - 1 | g) + (. - 2 | g) end; ";
  assert.equal(line(many + "10 | g"), "55");
  assert.equal(line(many + "15 | g"), 'error: "Maximum call depth exceeded"');
});

test("tail recursion reaches the limit without the host stack", () => {
  assert.equal(line("def f: if . == 0 then 0 else (. - 1 | f) end; 100000 | f"), 'error: "Maximum call depth exceeded"');
});

// Natives marked *loop* do not consume the call depth.
table("Loop_natives_do_not_consume_the_depth", Cases.Loop_natives_do_not_consume_the_depth, "null", () => depth(10));

table("Outputs_come_in_the_order_of_the_spec", Cases.Outputs_come_in_the_order_of_the_spec, "[1,2]");

// Not in the reference: the general evaluation of the nodes this port does not evaluate directly
// gives what the direct one gives (the corpus has both; these are the deep ones).
test("nodes nested deeper than the direct evaluation goes", () => {
  const nest = (open: string, close: string, levels: number): string => open.repeat(levels) + "1" + close.repeat(levels);
  assert.equal(line(nest("[", "]", 100) + " | flatten"), "[1]");
  assert.equal(line(nest("{a:", "}", 100) + " | [paths] | length"), "100");
  assert.equal(line(nest("(1 + ", ")", 100)), "101");
  assert.equal(line(nest("-(", ")", 101)), "-1");
  assert.equal(line(nest("(1 | ", ")", 100)), "1");
  assert.equal(line(nest("(. as $x | ", ")", 100)), "1");
  assert.equal(line(nest("(. as [$x] ?// $x | ", ")", 100)), "1");
  assert.equal(line(nest("(true and ", ")", 100)), "true");
  assert.equal(line(nest("(null // ", ")", 100)), "1");
  assert.equal(line(nest("(label $x | ", ")", 100)), "1");
  assert.equal(line(nest("(try ", ")", 100)), "1");
  assert.equal(line(nest("(if . then 2 else ", " end)", 100)), "1");
  assert.equal(line(nest('"\\(', ')"', 100)), '"1"');
  assert.equal(line("[0,0] | " + nest(".[", "]", 100)), "0");
  assert.equal(line(nest("(0 | tostring | tonumber | . + ", ")", 100)), "1");
  assert.equal(line(nest("[", "]", 100).replace("1", "error(\"deep\")") + " | 0"), 'error: "deep"');
  assert.equal(line("try " + nest("[", "]", 100).replace("1", "error(\"deep\")") + " catch ."), '"deep"');
  assert.equal(line(nest("[", "]", 100).replace("1", "empty") + " | flatten"), "[]");
  assert.equal(line("[path(" + nest("(. | ", ")", 100).replace("1", ".a") + ")]"), '[["a"]]');
});
