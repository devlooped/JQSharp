// A worker of test/host/jqConcurrency.test.ts: parses the program it is given and evaluates it
// on its cases, while the other workers do the same, and posts the text of each result.

import { parentPort, workerData } from "node:worker_threads";
import { Jq, type JsonInput } from "../../src/index.js";
import { JqFileResolver } from "../../src/node.js";

const job = workerData as { program: string; modules: string; cases: { input: JsonInput; variables: Record<string, JsonInput> }[]; rounds: number };
const expression = Jq.parse(job.program, { environment: { JQ_CONCURRENCY: "set" }, resolver: new JqFileResolver(job.modules) });
const results: string[] = [];
for (let round = 0; round < job.rounds; round++) {
  for (const item of job.cases) results.push(JSON.stringify(expression.evaluate(item.input, item.variables)));
}

parentPort!.postMessage(results);
