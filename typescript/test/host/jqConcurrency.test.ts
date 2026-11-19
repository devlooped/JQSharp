// The counterpart of dotnet/Tests/JqConcurrencyTests.cs. JavaScript threads (workers) share no
// objects, so one expression cannot be evaluated from two threads: what can happen at once is
// (a) evaluations of one expression started while another is running, from its callbacks, and
// (b) workers that each parse and evaluate the same program. Both must give what one evaluation
// alone gives.

import assert from "node:assert/strict";
import { test } from "node:test";
import { Worker } from "node:worker_threads";
import { Jq, type JqOptions, type JsonInput, type JsonValue } from "../../src/index.js";
import { JqFileResolver } from "../../src/node.js";
import { ModulesDirectory } from "../support/corpus.js";

// Functions, closures, recursion, reduce, label and break, limit, a regex, an update, a host
// variable, $ENV and a module: the per-evaluation state of the interpreter and the caches.
const Program = `
  import "a" as f;
  def fact: if . <= 1 then 1 else . * (. - 1 | fact) end;
  def twice(g): g | g;
  {
    fact: ($n | fact),
    twice: ($n | twice(. * 2)),
    sum: (reduce (.xs[] | . * $n) as $x (0; . + $x)),
    first_over: [label $out | .xs[] | if . > $n then ., break $out else empty end],
    limit: [limit(3; .xs[] | . + $n)],
    words: [.text | match("[a-z]+"; "g").string] | join($name),
    update: (.a |= . + $n | .a),
    env: $ENV.JQ_CONCURRENCY,
    module: f::f
  }`;

const Options: JqOptions = { environment: { JQ_CONCURRENCY: "set" }, resolver: new JqFileResolver(ModulesDirectory) };

// The input and the variables of one case: distinct for each n.
function caseOf(n: number): { input: JsonInput; variables: Record<string, JsonInput> } {
  const xs: number[] = [];
  for (let i = 0; i < 7; i++) xs.push(n - 2 + i);
  return { input: { xs, text: `ab${n}cd ef${n}gh`, a: n * 10 }, variables: { n, name: `<${n}>` } };
}

const Cases = [3, 4, 5, 6, 7, 8, 9, 10, 11].map(caseOf);

function expected(): string[] {
  // From a parse of its own, one evaluation after the other: what each case must give.
  const reference = Jq.parse(Program, Options);
  return Cases.map((item) => JSON.stringify(reference.evaluate(item.input, item.variables)));
}

test("the program of these tests uses a module and the environment", () => {
  const first = expected()[0]!;
  assert.ok(first.includes('"module":"a.f"'), first);
  assert.ok(first.includes('"env":"set"'), first);
  assert.ok(first.includes('"fact":6'), first);
});

test("evaluations of one expression started from each other's callbacks give the single result", () => {
  const expression = Jq.parse(Program, Options);
  const outer = Jq.parse("def g: if . == 9 then empty else ., (. + 1 | g) end; 0 | g", Options);
  const reference = expected();
  const actual: string[] = [];
  outer.evaluateEach(null, (index) => {
    // Three evaluations nested in one another, each started by an output of the one before.
    const item = Cases[index as number]!;
    const results: JsonValue[] = [];
    expression.evaluateEach(item.input, (result) => {
      const again = expression.evaluate(item.input, item.variables);
      assert.deepEqual(again, [result]);
      results.push(result);
      return true;
    }, item.variables);
    actual.push(JSON.stringify(results));
    return true;
  });
  assert.deepEqual(actual, reference);
});

test("a handler that evaluates the expression it is called from", () => {
  const seen: JsonValue[] = [];
  const expression: ReturnType<typeof Jq.parse> = Jq.parse("def f: if . < 3 then (. + 1 | debug | f) else . end; f", {
    debug: (value) => {
      if (value === 1) seen.push(...expression.evaluate(2));
    },
  });
  assert.deepEqual(expression.evaluate(0), [3]);
  assert.deepEqual(seen, [3]);
});

test("workers that parse and evaluate the same program at once give the single result", async () => {
  const reference = expected();
  const rounds = 20;
  const workers = Array.from({ length: 8 }, () => {
    return new Promise<string[]>((resolve, reject) => {
      const worker = new Worker(new URL("../support/concurrencyWorker.js", import.meta.url), { workerData: { program: Program, modules: ModulesDirectory, cases: Cases, rounds } });
      worker.once("message", resolve);
      worker.once("error", reject);
    });
  });
  for (const results of await Promise.all(workers)) {
    assert.equal(results.length, rounds * Cases.length);
    for (let i = 0; i < results.length; i++) assert.equal(results[i], reference[i % Cases.length]);
  }
});
