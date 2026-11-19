// Runs the benchmark runner on every case of bench/cases.json and compares its output file with
// bench/cases/<name>.expected, byte for byte. Usage: node scripts/bench-check.mjs [warmup-ms]
// [measure-ms] (after npm run build); with times, it also prints the three measurements.
import { execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const bench = join(root, "..", "bench");
const warmup = process.argv[2] ?? "0";
const measure = process.argv[3] ?? "0";
const cases = JSON.parse(readFileSync(join(bench, "cases.json"), "utf8"));
const work = mkdtempSync(join(tmpdir(), "jqlite-bench-"));
let failed = 0;
try {
  for (const item of cases) {
    const output = join(work, `${item.name}.out`);
    const line = execFileSync(process.execPath, [join(root, "dist", "bench", "crossBench.js"), resolve(bench, item.program), resolve(bench, item.input), String(item.take), warmup, measure, output], {
      encoding: "utf8",
    });
    const same = readFileSync(output).equals(readFileSync(join(bench, "cases", `${item.name}.expected`)));
    if (!same) failed++;
    const result = JSON.parse(line);
    const ms = (ns) => (ns / 1e6).toFixed(3).padStart(10);
    console.log(`${same ? "ok  " : "DIFF"} ${item.name.padEnd(16)} compile ${ms(result.compile_ns)} ms  eval ${ms(result.eval_ns)} ms  text ${ms(result.text_ns)} ms  outputs ${result.outputs}`);
  }
} finally {
  rmSync(work, { recursive: true, force: true });
}

console.log(failed === 0 ? `${cases.length} cases: every output equals its .expected file` : `${failed} of ${cases.length} cases differ`);
process.exitCode = failed === 0 ? 0 : 1;
