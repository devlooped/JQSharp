// Copies spec/prelude.jq, the builtins written in jqlite, into the module the engine embeds:
// src/binding/preludeSource.ts. TypeScript has no way to import a text file that every bundler
// and runtime share, so the text is a generated string constant; test/prelude.test.ts fails when
// it differs from spec/prelude.jq. Line ends are written as LF, whatever the checkout has.
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const source = readFileSync(join(root, "..", "spec", "prelude.jq"), "utf8").replace(/\r\n/g, "\n");
const lines = source.split("\n").map((line) => "  " + JSON.stringify(line) + ",");
const newline = JSON.stringify("\n");
const text = [
  "// Generated from spec/prelude.jq by scripts/generate-prelude.mjs (npm run prelude). Do not edit.",
  "",
  "/** The text of spec/prelude.jq, unchanged, with LF line ends. */",
  "export const PreludeSource: string = [",
  ...lines,
  `].join(${newline});`,
  "",
].join("\n");
const target = join(root, "src", "binding", "preludeSource.ts");
let current = null;
try {
  current = readFileSync(target, "utf8");
} catch {
  // not generated yet
}

if (current !== text) writeFileSync(target, text);
