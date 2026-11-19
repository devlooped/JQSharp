// A worker that evaluates programs on its own stack, whose size the test chooses
// (test/runtime/stack.test.ts): it posts the outputs of each program, joined.

import { parentPort, workerData } from "node:worker_threads";
import { depth, line } from "./evaluate.js";

const jobs = workerData as { program: string; maxDepth: number }[];
const results: string[] = [];
for (const job of jobs) {
  try {
    results.push(line(job.program, "null", depth(job.maxDepth)));
  } catch (e) {
    results.push("threw " + (e instanceof Error ? e.name : String(e)));
  }
}

parentPort!.postMessage(results);
