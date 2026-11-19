// The port of dotnet/Tests/JqModuleTests.cs: modules through the public API, with the file
// resolver of "jqlite/node" and with a resolver of the host's own. (The reference also has a
// resolver over resources embedded in an assembly, which has no counterpart here.)

import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { after, test } from "node:test";
import { Jq, type JqModuleKind, JqParseError, type JqResolver, type JsonInput, type JsonValue } from "../../src/index.js";
import { JqFileResolver } from "../../src/node.js";

const directory = mkdtempSync(join(tmpdir(), "jqlite-modules-"));
after(() => rmSync(directory, { recursive: true, force: true }));

function write(name: string, content: string): void {
  const path = join(directory, name);
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, content);
}

function run(expression: string, resolver: JqResolver | undefined, input: JsonInput = null): JsonValue[] {
  return Jq.parse(expression, resolver === undefined ? {} : { resolver }).evaluate(input);
}

function parseError(action: () => unknown): JqParseError {
  try {
    action();
  } catch (e) {
    assert.ok(e instanceof JqParseError, String(e));
    return e;
  }

  throw new assert.AssertionError({ message: "expected a JqParseError" });
}

test("the file resolver adds the extension of the kind", () => {
  write("utils.jq", "def inc: . + 1;");
  write("config.json", '{"key":"value"} 2');
  assert.deepEqual(run('include "utils"; import "config" as $c; inc, $c', new JqFileResolver(directory), 5), [6, [{ key: "value" }, 2]]);
});

test("the file resolver keeps an explicit extension", () => {
  write("ext.jq", "def ext: . + 100;");
  assert.deepEqual(run('include "ext.jq"; import "ext.jq" as e; ext, e::ext', new JqFileResolver(directory), 5), [105, 105]);
});

test("the file resolver resolves from the directory of the importing module", () => {
  write("lib/outer.jq", 'include "inner"; def outer: inner * 2;');
  write("lib/inner.jq", "def inner: . + 10;");
  assert.deepEqual(run('include "lib/outer"; outer', new JqFileResolver(directory), 5), [30]);
});

test("a missing module is a parse error", () => {
  const error = parseError(() => run('include "nonexistent"; .', new JqFileResolver(directory)));
  assert.equal(error.message, "module not found: nonexistent at line 1, column 1");
  assert.deepEqual([error.line, error.column], [1, 1]);
});

test("without a resolver no module exists", () => {
  parseError(() => run('import "something" as m; .', undefined));
  parseError(() => run('import "data" as $d; $d', undefined));
  assert.deepEqual(run('try ("x" | modulemeta) catch .', undefined), ["module not found: x"]);
});

test("an error in a module names it", () => {
  write("bad.jq", "def ok: 1;\ndef broken: (1;");
  const error = parseError(() => run('import "bad" as b; 1', new JqFileResolver(directory)));
  assert.deepEqual([error.line, error.column], [2, 15]);
  assert.ok(error.message.includes(join(directory, "bad.jq")), error.message);
});

test("data modules bind an array of their values", () => {
  write("config.json", '{"key":"value"}');
  assert.deepEqual(run('import "config" as $cfg; $cfg::cfg, $cfg[0].key', new JqFileResolver(directory)), [[{ key: "value" }], "value"]);
});

test("invalid data is a parse error", () => {
  write("bad.json", "not valid json {{{");
  parseError(() => run('import "bad" as $b; $b', new JqFileResolver(directory)));
});

test("each module is read once per parse", () => {
  write("counted.jq", "def counted: . + 1;");
  const files = new JqFileResolver(directory);
  let opens = 0;
  const counting: JqResolver = {
    getCanonicalPath: (path, kind, metadata, fromPath) => files.getCanonicalPath(path, kind, metadata, fromPath),
    open: (canonicalPath) => {
      opens++;
      return files.open(canonicalPath);
    },
  };
  assert.deepEqual(run('include "counted"; import "counted" as c1; import "counted" as c2; c1::counted', counting, 5), [6]);
  assert.equal(opens, 1);
});

test("modulemeta resolves through the resolver at run time", () => {
  write("meta.jq", 'module {"version": "1.0"}; def double: . * 2;');
  const expression = Jq.parse("modulemeta", { resolver: new JqFileResolver(directory) });
  for (let i = 0; i < 16; i++) assert.deepEqual(expression.evaluate("meta"), [{ version: "1.0", deps: [], defs: ["double/0"] }]);
});

test("a resolver of the host names and opens modules", () => {
  const modules = new Map([
    ["mem:greet.jq", 'include "punctuation" {"loud": true}; def greet: "hello " + . + mark;'],
    ["mem:punctuation.jq", 'def mark: "!";'],
    ["mem:numbers.json", "1 2 3"],
  ]);
  const asked: [string, JqModuleKind, JsonValue | undefined, string | null][] = [];
  const memory: JqResolver = {
    getCanonicalPath: (path, kind, metadata, fromPath) => {
      asked.push([path, kind, metadata, fromPath]);
      const name = `mem:${path}${kind === "data" ? ".json" : ".jq"}`;
      return modules.has(name) ? name : null;
    },
    open: (canonicalPath) => modules.get(canonicalPath)!,
  };
  assert.deepEqual(run('include "greet"; import "numbers" as $n; greet, $n', memory, "world"), ["hello world!", [1, 2, 3]]);
  assert.deepEqual(asked, [
    ["greet", "code", undefined, null],
    ["punctuation", "code", { loud: true }, "mem:greet.jq"],
    ["numbers", "data", undefined, null],
  ]);
  assert.equal(parseError(() => run('include "missing"; .', memory)).message, "module not found: missing at line 1, column 1");
});

test("what a resolver throws is the error of the directive", () => {
  const failing: JqResolver = {
    getCanonicalPath: (path) => {
      if (path === "locked") throw new Error("access denied: locked");
      return path;
    },
    open: () => {
      throw new Error("cannot read");
    },
  };
  assert.equal(parseError(() => run('import "locked" as l; 1', failing)).message, "access denied: locked at line 1, column 1");
  assert.equal(parseError(() => run('import "open" as o; 1', failing)).message, "cannot read at line 1, column 1");
  assert.deepEqual(Jq.parse('try ("locked" | modulemeta) catch .', { resolver: failing }).evaluate(null), ["access denied: locked"]);
});
