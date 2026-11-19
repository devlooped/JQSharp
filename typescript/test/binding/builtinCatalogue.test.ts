// The port of dotnet/Tests/Core/Binding/BuiltinCatalogueTests.cs: the native catalogue and the
// prelude against the N and P lists of §9, read from the spec; and the embedded prelude against
// spec/prelude.jq.

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { test } from "node:test";
import { Prelude } from "../../src/binding/prelude.js";
import { PreludeSource } from "../../src/binding/preludeSource.js";
import * as Natives from "../../src/builtins/natives.js";
import { NativeGroup } from "../../src/builtins/natives.js";
import { RepositoryRoot } from "../support/corpus.js";

function specFile(name: string): string {
  return readFileSync(join(RepositoryRoot, "spec", name), "utf8").replace(/\r\n/g, "\n");
}

function signature(item: { name: string; arity: number }): string {
  return `${item.name}/${item.arity}`;
}

function sorted(items: Iterable<string>): string[] {
  return [...items].sort();
}

test("the embedded prelude is spec/prelude.jq", () => {
  // The copy is written by scripts/generate-prelude.mjs (npm run prelude), with LF line ends.
  assert.equal(PreludeSource, specFile("prelude.jq"));
});

test("the prelude binds against the natives", () => {
  const prelude = Prelude.instance;
  assert.equal(prelude.definitions.length, 102);
  // Nested definitions (recurse's r, walk's w) are functions of the prelude too.
  assert.ok(prelude.functions.length > prelude.definitions.length);
  assert.equal(new Set(prelude.definitions.map(signature)).size, prelude.definitions.length);
});

test("a prelude cannot use host variables", () => {
  assert.throws(() => Prelude.load("def f: $x;"), /\$x is not defined at 1:8/);
});

test("the catalogue has unique signatures and indices", () => {
  assert.equal(new Set(Natives.All.map(signature)).size, Natives.All.length);
  for (let i = 0; i < Natives.All.length; i++) {
    assert.equal(Natives.All[i]!.index, i);
    assert.equal(Natives.find(Natives.All[i]!.name, Natives.All[i]!.arity), Natives.All[i]);
  }

  assert.equal(Natives.find("map", 1), null);
});

test("natives and prelude are the builtins of the spec", () => {
  const spec = specBuiltins();
  const natives = new Set(Natives.All.filter((native) => !native.overridesPrelude).map(signature));
  const prelude = new Set(Prelude.instance.definitions.map(signature));
  // An override implements a prelude builtin natively: it stays a prelude builtin of §9.
  for (const native of Natives.All.filter((item) => item.overridesPrelude)) assert.ok(prelude.has(signature(native)), signature(native));
  for (const native of natives) assert.ok(!prelude.has(native), native);
  assert.deepEqual(sorted([...natives, ...prelude]), sorted(spec.all));
  for (const native of natives) assert.ok(spec.native.has(native), `${native} is N in the spec`);
  for (const defined of prelude) assert.ok(spec.prelude.has(defined), `${defined} is P in the spec`);
  assert.deepEqual(sorted(Natives.All.filter((native) => native.group === NativeGroup.Modules).map(signature)), sorted(spec.modules));
});

test("internal builtins start with an underscore", () => {
  assert.deepEqual(sorted(Natives.All.filter((native) => native.isInternal && !native.overridesPrelude).map(signature)), [
    "_group_by_impl/1",
    "_max_by_impl/1",
    "_min_by_impl/1",
    "_sort_by_impl/1",
    "_strindices/1",
  ]);
  assert.deepEqual(sorted(Prelude.instance.definitions.filter((func) => func.name.startsWith("_")).map(signature)), ["_assign/2", "_flatten/1", "_modify/2"]);
});

interface SpecLists {
  all: Set<string>;
  native: Set<string>;
  prelude: Set<string>;
  modules: Set<string>;
}

const Signatures = /`([A-Za-z_][A-Za-z0-9_]*\/[0-9]+)`/g;
// The whole paragraph (group 1) and its list of one-input names (group 2).
const LibraryList = /^\*\*Library functions\*\*(.*?`([a-z0-9 ]+)` \(one input\).*)$/gm;
const FirstCells = /^\| ([^|\n]*) \|/gm;
// One or more signatures at the start of a line, separated by ", ".
const Headings = /^`[A-Za-z_][A-Za-z0-9_]*\/[0-9]+`(, `[A-Za-z_][A-Za-z0-9_]*\/[0-9]+`)*/gm;
// The first sentence of §9.11, which names the members of the optional group.
const ModuleGroup = /A port MAY leave out the whole group:[^.]*\./;
const Formulas = /^\s*([a-z][a-z0-9]*)\(x\):/gm;
const KindRows = /^\| ([^|]*) \| ([NP]) \|/gm;

function signaturesOf(text: string): string[] {
  return [...text.matchAll(Signatures)].map((match) => match[1]!);
}

// Section 9 of spec/jqlite.md, read only where it declares builtins, so that examples in its
// prose cannot add any: the first cell of each table row; the §9.8 library paragraph (its
// signatures and its list of names "(one input)"); the lines that start with signatures (the
// §9.8 formula headings and abs/0); the functions §9.8 defines as `name(x):`; and the group
// sentence of §9.11. Rows whose K column says N or P give the kind; §9.8 is native but abs/0.
function specBuiltins(): SpecLists {
  const text = specFile("jqlite.md");
  const section = sectionOf(text, "## 9. ", "## 10. ");
  const math = sectionOf(section, "### 9.8 ", "### 9.9 ");
  const mathDeclared = declarations(math);
  const library = [...math.matchAll(LibraryList)][0];
  assert.ok(library !== undefined, "The library functions of §9.8 were not found");
  for (const name of library[2]!.split(" ")) mathDeclared.add(name + "/0");
  for (const formula of math.matchAll(Formulas)) mathDeclared.add(formula[1]! + "/0");
  const group = ModuleGroup.exec(sectionOf(section, "### 9.11 ", "### 9.12 "));
  assert.ok(group !== null, "The group sentence of §9.11 was not found");
  const modules = new Set(signaturesOf(group[0]));

  const all = declarations(section);
  for (const item of mathDeclared) all.add(item);
  for (const item of modules) all.add(item);

  const native = new Set(modules);
  const prelude = new Set<string>();
  for (const row of section.matchAll(KindRows)) {
    const kind = row[2] === "N" ? native : prelude;
    for (const item of signaturesOf(row[1]!)) kind.add(item);
  }

  for (const item of mathDeclared) (item === "abs/0" ? prelude : native).add(item);
  return { all, native, prelude, modules };
}

// The signatures in the first cells of table rows, in the library paragraph, and at the start of lines.
function declarations(text: string): Set<string> {
  const declared = new Set<string>();
  for (const cell of text.matchAll(FirstCells)) for (const item of signaturesOf(cell[1]!)) declared.add(item);
  for (const library of text.matchAll(LibraryList)) for (const item of signaturesOf(library[1]!)) declared.add(item);
  for (const heading of text.matchAll(Headings)) for (const item of signaturesOf(heading[0])) declared.add(item);
  return declared;
}

function sectionOf(text: string, start: string, end: string): string {
  const from = text.indexOf("\n" + start);
  const to = text.indexOf("\n" + end, from + 1);
  assert.ok(from >= 0 && to > from, `Section '${start}' not found in the spec`);
  return text.substring(from, to);
}
