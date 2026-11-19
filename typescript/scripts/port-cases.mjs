// Copies the rows of the theories of an xUnit test file of the reference (the [InlineData(…)]
// attributes above each test method) into a TypeScript module: one array of rows per method, the
// literals converted from C# to JavaScript. The test files of test/ import these tables, so the
// rows are the reference's own, character for character.
//
// Usage: node scripts/port-cases.mjs <Tests.cs> <cases.ts>
import { readFileSync, writeFileSync } from "node:fs";
import { basename } from "node:path";

const [source, target] = process.argv.slice(2);
const text = readFileSync(source, "utf8").replace(/\r\n/g, "\n");

// One C# argument list, split into its arguments: string literals are kept whole.
function splitArguments(list) {
  const parts = [];
  let current = "";
  let depth = 0;
  for (let i = 0; i < list.length; i++) {
    const c = list[i];
    if (c === '"') {
      const verbatim = current.trimEnd().endsWith("@");
      let j = i + 1;
      for (; j < list.length; j++) {
        if (verbatim) {
          if (list[j] === '"' && list[j + 1] === '"') j++;
          else if (list[j] === '"') break;
        } else if (list[j] === "\\") j++;
        else if (list[j] === '"') break;
      }

      current += list.substring(i, j + 1);
      i = j;
    } else if (c === "(" || c === "[" || c === "{") {
      depth++;
      current += c;
    } else if (c === ")" || c === "]" || c === "}") {
      depth--;
      current += c;
    } else if (c === "," && depth === 0) {
      parts.push(current.trim());
      current = "";
    } else {
      current += c;
    }
  }

  if (current.trim().length > 0) parts.push(current.trim());
  return parts;
}

// A C# literal as a JavaScript one.
function convert(literal) {
  if (literal.startsWith('@"')) {
    const value = literal.substring(2, literal.length - 1).replace(/""/g, '"');
    return JSON.stringify(value);
  }

  if (literal.startsWith('"')) {
    return literal
      .replace(/\\U([0-9A-Fa-f]{8})/g, (_, hex) => `\\u{${hex.replace(/^0+/, "")}}`)
      .replace(/\\x([0-9A-Fa-f]{1,4})/g, (_, hex) => `\\u${hex.padStart(4, "0")}`)
      .replace(/\\0(?![0-9])/g, "\\u0000");
  }

  if (literal === "double.PositiveInfinity") return "Infinity";
  if (literal === "double.NegativeInfinity") return "-Infinity";
  if (literal === "double.NaN") return "NaN";
  if (/^-?[0-9.]+(e[+-]?[0-9]+)?d$/i.test(literal)) return literal.substring(0, literal.length - 1);
  return literal;
}

const tables = [];
const lines = text.split("\n");
let rows = [];
let pending = "";
for (const line of lines) {
  const trimmed = line.trim();
  if (pending.length > 0 || trimmed.startsWith("[InlineData(")) {
    pending += trimmed;
    if (!pending.endsWith(")]")) continue;
    rows.push(splitArguments(pending.substring("[InlineData(".length, pending.length - 2)).map(convert));
    pending = "";
    continue;
  }

  const method = /public (?:async Task|void) (\w+)\(/.exec(trimmed);
  if (method !== null) {
    if (rows.length > 0) tables.push([method[1], rows]);
    rows = [];
  }
}

let output = `// The rows of the theories of ${basename(source)} of the reference, copied by scripts/port-cases.mjs. Do not edit.\n\n`;
output += "/* eslint-disable */\n";
for (const [name, table] of tables) {
  output += `export const ${name} = [\n`;
  for (const row of table) output += `  [${row.join(", ")}],\n`;
  output += "] as const;\n\n";
}

writeFileSync(target, output.trimEnd() + "\n");
console.log(`${tables.length} tables, ${tables.reduce((n, [, t]) => n + t.length, 0)} rows`);
