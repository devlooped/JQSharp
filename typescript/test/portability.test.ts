// The portability rules of AGENTS.md, checked on the sources (the counterpart of
// dotnet/Tests/PortabilityTests.cs): what the core (src/values, syntax, binding, runtime,
// builtins) must not use, with the files allowed each exception and why; and where the package
// may use Node.

import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { join, relative } from "node:path";
import { test } from "node:test";
import { RepositoryRoot } from "./support/corpus.js";

const Source = join(RepositoryRoot, "typescript", "src");
const CoreFolders = ["values", "syntax", "binding", "runtime", "builtins"];

interface Rule {
  name: string;
  pattern: RegExp;
  allowed: Record<string, string>;
}

const CoreRules: Rule[] = [
  { name: "the host's JSON", pattern: /\bJSON\s*\./, allowed: {} },
  { name: "generators and iterators", pattern: /\bfunction\s*\*|\byield\b|Symbol\s*\.\s*(iterator|asyncIterator)|\basync\b|\bawait\b/, allowed: {} },
  // The regex engine of this port is its own (regexEngine.ts): the host's is used nowhere.
  { name: "the host's regex", pattern: /\bRegExp\b|(?<!this)\.(match|matchAll|search)\s*\(|(^|[=(,:&|?{;]|\breturn)\s*\/(?![/*\s])/, allowed: {} },
  { name: "the host's dates and locales", pattern: /\bDate\b|\bIntl\b|\btoLocale\w*|\blocaleCompare\b/, allowed: {} },
  { name: "reflection", pattern: /\bReflect\b|\bProxy\b|(?<![.\w])eval\s*\((?!node: )|\bgetOwnPropertyNames\b|\bdefineProperty\b|\bgetPrototypeOf\b/, allowed: {} },
  { name: "Node or browser APIs", pattern: /\brequire\s*\(|\bprocess\b|\bglobalThis\b|\bwindow\b|\bdocument\b|\bBuffer\b|\bTextDecoder\b|\bTextEncoder\b|\bsetTimeout\b|\bconsole\b/, allowed: {} },
  {
    name: "throw",
    pattern: /\bthrow\b/,
    allowed: {
      "binding/prelude.ts": "the embedded prelude does not bind: a build defect",
      "builtins/nativeTable.ts": "start-up checks of the registrations against the catalogue",
      "runtime/interpreter.ts": "a step that schedules nothing, a node kind in the wrong place: defects of the interpreter",
      "values/jqArray.ts": "index guards of the host caller",
      "values/jqArrayBuilder.ts": "index guard, and use after toArray(): programming errors",
      "values/jqObject.ts": "index guards of the host caller",
      "values/jqObjectBuilder.ts": "use after toObject(): a programming error",
    },
  },
  { name: "try and catch", pattern: /\btry\s*\{|\bcatch\s*[({]|\bfinally\b/, allowed: {} },
];

// The source without comments and with the text of its strings blanked: line breaks stay.
function code(source: string): string {
  let text = "";
  let i = 0;
  const blank = (count: number): void => {
    for (let n = 0; n < count && i < source.length; n++, i++) text += source[i] === "\n" || source[i] === "\r" ? source[i] : " ";
  };

  while (i < source.length) {
    const c = source[i]!;
    const next = source[i + 1];
    if (c === "/" && next === "/") {
      while (i < source.length && source[i] !== "\n") blank(1);
    } else if (c === "/" && next === "*") {
      const end = source.indexOf("*/", i + 2);
      blank((end < 0 ? source.length : end + 2) - i);
    } else if (c === '"' || c === "'" || c === "`") {
      text += c;
      i++;
      while (i < source.length && source[i] !== c) blank(source[i] === "\\" ? 2 : 1);
      if (i < source.length) {
        text += c;
        i++;
      }
    } else {
      text += c;
      i++;
    }
  }

  return text;
}

function files(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true, recursive: true })
    .filter((entry) => entry.isFile() && entry.name.endsWith(".ts"))
    .map((entry) => relative(Source, join(entry.parentPath, entry.name)).replaceAll("\\", "/"))
    .sort();
}

function matches(file: string, pattern: RegExp): number[] {
  const lines = code(readFileSync(join(Source, file), "utf8")).split("\n");
  const found: number[] = [];
  lines.forEach((line, index) => {
    if (pattern.test(line)) found.push(index + 1);
  });
  return found;
}

function coreFiles(): string[] {
  return CoreFolders.flatMap((folder) => files(join(Source, folder)));
}

for (const rule of CoreRules) {
  test(`the core does not use ${rule.name}`, () => {
    const violations: string[] = [];
    for (const file of coreFiles()) {
      // The generated text of the prelude is one string constant.
      if (file in rule.allowed || file === "binding/preludeSource.ts") continue;
      for (const line of matches(file, rule.pattern)) violations.push(`${file}:${line}`);
    }

    assert.deepEqual(violations, [], `${rule.name} is not allowed in the core outside ${Object.keys(rule.allowed).join(", ") || "nowhere"}`);
  });
}

test("the allowances of the core are still used", () => {
  // An allowance nothing uses any more is removed, so the lists stay the truth.
  for (const rule of CoreRules) {
    for (const file of Object.keys(rule.allowed)) assert.ok(matches(file, rule.pattern).length > 0, `${file} no longer uses ${rule.name}: remove it from the allowed files.`);
  }
});

test("the core has the files of the reference", () => {
  const expected = [
    "binding/bound.ts", "binding/modules.ts", "binding/prelude.ts", "binding/preludeSource.ts", "binding/resolver.ts",
    "builtins/assignNatives.ts", "builtins/calendar.ts", "builtins/collectionNatives.ts", "builtins/containment.ts", "builtins/coreNatives.ts",
    "builtins/dateNatives.ts", "builtins/formats.ts", "builtins/loopNatives.ts", "builtins/mathExtra.ts", "builtins/mathNatives.ts",
    "builtins/moduleNatives.ts", "builtins/nativeTable.ts", "builtins/natives.ts", "builtins/pathNatives.ts", "builtins/regexEngine.ts",
    "builtins/regexNatives.ts", "builtins/regexSyntax.ts", "builtins/strftime.ts", "builtins/stringNatives.ts",
    "runtime/env.ts", "runtime/evaluation.ts", "runtime/interpreter.ts", "runtime/path.ts", "runtime/signal.ts",
    "syntax/ast.ts", "syntax/lexer.ts", "syntax/parser.ts", "syntax/syntaxError.ts", "syntax/token.ts",
    "values/codePoints.ts", "values/errors.ts", "values/jqArray.ts", "values/jqArrayBuilder.ts", "values/jqObject.ts", "values/jqObjectBuilder.ts",
    "values/jqValue.ts", "values/utf8.ts", "values/valueOps.ts", "values/valueText.ts",
  ];
  // The reference's files, minus Runtime/StackGuard.cs (the interpreter does not use the host
  // stack), plus the generated text of the prelude.
  assert.deepEqual(coreFiles().sort(), expected.sort());
});

test("a layer imports only the layers before it", () => {
  // Values < Syntax < Binding < Runtime < Builtins, and the two links of Builtins: the catalogue
  // (natives.ts) used by Binding, and the table and the formats called by the interpreter.
  const order = ["values", "syntax", "binding", "runtime", "builtins"];
  const violations: string[] = [];
  for (const file of coreFiles()) {
    const layer = order.indexOf(file.split("/")[0]!);
    const source = readFileSync(join(Source, file), "utf8");
    for (const match of source.matchAll(/from "\.\.\/(\w+)\/(\w+)\.js"/g)) {
      const target = order.indexOf(match[1]!);
      if (target < 0) violations.push(`${file} imports ${match[1]}/${match[2]} from outside the core`);
      if (target <= layer) continue;
      const allowed =
        (match[1] === "builtins" && match[2] === "natives" && file.startsWith("binding/")) ||
        (match[1] === "builtins" && (match[2] === "nativeTable" || match[2] === "formats") && file === "runtime/interpreter.ts") ||
        // Modules.cs holds a part of the Resolver class: the two files of the binding layer import each other.
        match[1] === file.split("/")[0];
      if (!allowed) violations.push(`${file} imports ${match[1]}/${match[2]}`);
    }
  }

  assert.deepEqual(violations, []);
});

test("only the Node entry uses Node", () => {
  const node = files(Source).filter((file) => matches(file, /\bprocess\b|\brequire\s*\(/).length > 0 || /from "node:/.test(readFileSync(join(Source, file), "utf8")));
  // jqExpression.ts reads the process environment through globalThis, where there is one.
  assert.deepEqual(node, ["jqExpression.ts", "jqFileResolver.ts"]);
  assert.ok(/globalThis as \{ process\?:/.test(readFileSync(join(Source, "jqExpression.ts"), "utf8")));
});

test("only the adapter converts host values", () => {
  // The host's JSON (JSON.parse, JSON.stringify) is used nowhere in the package: text goes
  // through the core's reader and writer, plain values through json/jsonValueAdapter.ts.
  const users = files(Source).filter((file) => matches(file, /\bJSON\s*\./).length > 0);
  assert.deepEqual(users, []);
  const converters = files(Source).filter((file) => matches(file, /\bObject\s*\.\s*(keys|entries)\b|\bArray\s*\.\s*isArray\b/).length > 0);
  // jqExpression.ts reads the names of the variables and of the environment, not values.
  assert.deepEqual(converters, ["jqExpression.ts", "json/jsonValueAdapter.ts"]);
});

test("code is what remains without comments and strings", () => {
  const source = ['const a = "throw"; // throw', "/* throw", "   yield */", "const b = 'he said \\'catch\\'';", "const c = `x ${1} catch`;", "throw new Error(); // throw"].join("\n");
  const lines = code(source).split("\n");
  assert.equal(lines.length, 6);
  assert.equal(lines.join("\n").match(/\bthrow\b/g)!.length, 1);
  assert.ok(!lines.join("\n").includes("yield"));
  assert.ok(!lines.join("\n").includes("catch"));
  assert.ok(lines[5]!.includes("throw new Error();"));
});
