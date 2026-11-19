// The port of dotnet/Tests/Core/Runtime/StackGuardTests.cs. MaxDepth is the only recursion limit
// a program meets (spec §11): the reference continues on a fresh stack when the host stack runs
// short; this port's interpreter does not recurse on the host stack at all (Interpreter). The
// cases run on the stack of Node's main thread and, for the recursions, on worker threads with
// stacks of 256 KB and 1 MB, as the reference's.

import assert from "node:assert/strict";
import { test } from "node:test";
import { Worker } from "node:worker_threads";
import { compile, depth, line, runBound } from "../support/evaluate.js";

// opening, closing, and the output for 1000 levels around "1" (or around "N" in a key)
const Nestings: [string, string, string][] = [
  ["[", "]", "[".repeat(1000) + "1" + "]".repeat(1000)],
  ["{a: ", "}", '{"a":'.repeat(1000) + "1" + "}".repeat(1000)],
  [".[", "]", 'error: "Cannot index null with null (null)"'],
  ['"\\(', ')"', '"1"'],
  ["reduce . as $x (", ";.)", "1"],
  ["[1 + ", "]", "error"],
  ["1 + (", ")", "1001"],
  ["[label $x | ", "]", "[".repeat(1000) + "1" + "]".repeat(1000)],
  ["if . then 1 else ", " end", "1"],
  ["try ", "", "1"],
];

for (const [open, close, expected] of Nestings) {
  test(`nesting to the parser limit works: ${open.trim()}`, () => {
    const result = line(open.repeat(1000) + "1" + close.repeat(1000));
    if (expected === "error") assert.ok(result.startsWith('error: "number (1) and array ([2]) cannot be added'), result);
    else assert.equal(result, expected);
  });
}

// program with N
const Recursions = [
  "def f: if . == 0 then 0 else (. - 1 | f) + 1 end; N | f",
  "def f: if . == 0 then 0 else [. - 1 | f] | length end; N | f",
  "def f: if . == 0 then 0 else ((. - 1, . - 1) | f) end; label $out | (N | f) | ., break $out",
  "def f(g): if . == 0 then g else (. - 1 | f(g)) + 0 end; N | f(0)",
  // Not in the reference: a function body nested many levels around its recursive call.
  "def f: if . == 0 then 0 else " + "[".repeat(50) + ". - 1 | f" + "]".repeat(50) + " | " + ".[0]".repeat(50) + " end; N | f",
];

// N | f makes N + 1 calls: the deepest that fits, and one more.
function jobs(maxDepth: number): { program: string; maxDepth: number }[] {
  return Recursions.flatMap((program) => [
    { program: program.replace("N", String(maxDepth - 1)), maxDepth },
    { program: program.replace("N", String(maxDepth)), maxDepth },
  ]);
}

function check(results: string[]): void {
  for (let i = 0; i < results.length; i += 2) {
    assert.ok(!results[i]!.includes("error") && !results[i]!.includes("threw"), results[i]);
    assert.equal(results[i + 1], 'error: "Maximum call depth exceeded"');
  }
}

for (const maxDepth of [1000, 5000, 20000, 200000]) {
  test(`recursion reaches exactly MaxDepth ${maxDepth}`, () => {
    check(jobs(maxDepth).map((job) => line(job.program, "null", depth(job.maxDepth))));
  });
}

function onStack(megabytes: number, work: { program: string; maxDepth: number }[]): Promise<string[]> {
  return new Promise((resolve, reject) => {
    const worker = new Worker(new URL("../support/stackWorker.js", import.meta.url), { workerData: work, resourceLimits: { stackSizeMb: megabytes } });
    worker.once("message", resolve);
    worker.once("error", reject);
  });
}

for (const megabytes of [0.25, 1]) {
  test(`recursion reaches exactly MaxDepth on a stack of ${megabytes} MB`, async () => {
    check(await onStack(megabytes, [...jobs(1000), ...jobs(5000), ...jobs(20000)]));
  });
}

test("a stop of the host comes back from a deep recursion", () => {
  const program = compile("def f: if . == 0 then (1, 2, 3) else (. - 1 | f) + 0 end; 900 | f");
  assert.deepEqual(runBound(program, null, null, 1), ["1"]);
});

test("an error comes back from a deep recursion", () => {
  assert.equal(line('try (def f: if . == 0 then error("deep") else (. - 1 | f) + 1 end; 900 | f) catch .'), '"deep"');
});

// Not in the reference: what its interpreter leaves to the fresh stacks, and this one does on its
// own stack: generators feeding generators, and long sequences around a generator.
test("long sequences of generators do not use the host stack", () => {
  assert.equal(line("[1] | " + ".[] | [.] | ".repeat(20000) + ".[0]"), "1");
  assert.equal(line("[" + "(1, 2) | ".repeat(14) + "1] | length"), "16384");
  assert.equal(line("[" + "1, ".repeat(100000) + "1] | length"), "100001");
  assert.equal(line("[limit(3; " + "(1, 2) | ".repeat(20000) + ".)]"), "[1,2,1]");
  assert.equal(line('"' + "\\(1)".repeat(20000) + "\\(1, 2)" + '" | length'), "20001, 20001");
  assert.equal(line("{" + "a: 1, ".repeat(20000) + "b: (1, 2)} | .b"), "1, 2");
  assert.equal(line("[" + "1 + ".repeat(20000) + "(1, 2)]"), "[20001,20002]");
  assert.equal(line("0 | " + "try error catch . | ".repeat(20000) + "."), "0");
  assert.equal(line("[" + "label $x | ".repeat(20000) + "(1, break $x, 2)]"), "[1]");
});

// Not in the reference: patterns. With keys of one output a pattern is destructured by a
// recursion per level of nesting, which the parser bounds; with a key of several outputs, in a
// loop, however many variables it has.
const Variables = Array.from({ length: 20000 }, (_, i) => `$v${i}`);

const LongPatterns: [string, string][] = [
  [`[range(20000), {a: 1, b: 2}] | . as [${Variables.join(", ")}, {("a", "b"): $x}] | $x + $v0 + $v19999`, "20000, 20001"],
  [`{a: 1, b: 2, c: [range(20000)]} | . as {("a", "b"): $x, c: [${Variables.join(", ")}]} | $x + $v0 + $v19999`, "20000, 20001"],
  [`[range(20000) | {key: "k\\(.)", value: .}] | from_entries + {a: 1, b: 2} | . as {("a", "b"): $x, ${Variables.map((v, i) => `k${i}: ${v}`).join(", ")}} | $x + $v0 + $v19999`, "20000, 20001"],
];

// The inner pattern in N levels of array pattern, on the inner value in as many arrays.
function deepPattern(levels: number, value: string, inner: string): string {
  return `reduce range(${levels}) as $i (${value}; [.]) | . as ${"[".repeat(levels)}${inner}${"]".repeat(levels)}`;
}

const DeepPatterns: [string, string][] = [
  [`${deepPattern(1000, "7", "$x")} | $x`, "7"],
  [`${deepPattern(996, "[7, {a: 1, b: 2}]", '[$x, {("a", "b"): $y}]')} | [$x, $y]`, "[7,1], [7,2]"],
  [`${deepPattern(997, "7", '{("a", "b"): $y}')} | $y`, 'error: "Cannot index number with string (\\"a\\")"'],
];

test("a long pattern with a key of several outputs does not use the host stack", async () => {
  for (const [program, expected] of LongPatterns) assert.equal(line(program), expected);
  const results = await onStack(0.25, LongPatterns.map(([program]) => ({ program, maxDepth: 1000 })));
  assert.deepEqual(results, LongPatterns.map(([, expected]) => expected));
});

test("patterns nested to the parser limit work", async () => {
  // The parser needs more than 256 KB for 1,000 levels: these run on 1 MB, as Node's main thread has.
  for (const [program, expected] of DeepPatterns) assert.equal(line(program), expected);
  const results = await onStack(1, DeepPatterns.map(([program]) => ({ program, maxDepth: 1000 })));
  assert.deepEqual(results, DeepPatterns.map(([, expected]) => expected));
});
