// The host boundary: the port of dotnet/Tests/JqBoundaryTests.cs, JqExpressionTests.cs,
// JqVariableTests.cs and JqDepthTests.cs to this port's public API (Jq, JqExpression, JqOptions,
// JqError), with plain values where the reference has JsonElement.

import assert from "node:assert/strict";
import { test } from "node:test";
import { Jq, type JqClock, JqError, JqExpression, JqHaltError, JqParseError, type JsonValue } from "../../src/index.js";

function texts(results: JsonValue[]): string[] {
  return results.map((result) => JSON.stringify(result));
}

function fixedClock(now: number, offset: (seconds: number) => number): JqClock {
  return { now: () => now, offset };
}

function thrown<T extends Error>(type: new (...args: never[]) => T, action: () => unknown): T {
  try {
    action();
  } catch (e) {
    assert.ok(e instanceof type, `expected ${type.name}, got ${String(e)}`);
    return e;
  }

  throw new assert.AssertionError({ message: `expected ${type.name}, nothing was thrown` });
}

test("parse returns an expression; an empty expression is the identity", () => {
  assert.ok(Jq.parse(".a") instanceof JqExpression);
  for (const expression of ["", "   ", "# only a comment"]) assert.deepEqual(Jq.evaluate(expression, { a: 1 }), [{ a: 1 }]);
});

test("parse rejects what is not a string, and an invalid expression", () => {
  assert.throws(() => Jq.parse(null as unknown as string), TypeError);
  assert.throws(() => Jq.parse("[invalid!!!"), JqParseError);
});

test("a parsed expression is reused across inputs and gives what Jq.evaluate gives", () => {
  const expression = Jq.parse(".a + 1");
  assert.deepEqual(expression.evaluate({ a: 1 }), [2]);
  assert.deepEqual(expression.evaluate({ a: 10 }), [11]);
  assert.deepEqual(expression.evaluate({ a: 41 }), Jq.evaluate(".a + 1", { a: 41 }));
  assert.deepEqual(Jq.parse(".[] | . * 2").evaluate([1, 2, 3]), [2, 4, 6]);
});

test("comments in an expression", () => {
  assert.deepEqual(Jq.evaluate("# a comment\n.a", { a: 1 }), [1]);
  assert.deepEqual(Jq.evaluate(".a # a comment\n| . + 1", { a: 1 }), [2]);
  assert.deepEqual(Jq.evaluate("1 # c \\\n+ 1\n+ 10", null), [11]);
  assert.deepEqual(Jq.evaluate("1 # c \\\\\n+ 1\n+ 10", null), [12]);
});

test("options environment is $ENV and env", () => {
  const expression = Jq.parse("$ENV, env.A", { environment: { B: "2", A: "1" } });
  assert.deepEqual(texts(expression.evaluate(null)), ['{"B":"2","A":"1"}', '"1"']);
  assert.deepEqual(texts(Jq.parse("$ENV", { environment: new Map([["Z", "26"]]) }).evaluate(null)), ['{"Z":"26"}']);
});

test("without options $ENV is the process environment", () => {
  const name = Object.keys(process.env).find((key) => /^[A-Za-z_][A-Za-z0-9_]*$/.test(key))!;
  assert.deepEqual(Jq.parse(`$ENV.${name}`).evaluate(null), [process.env[name]]);
});

test("options clock gives now and the local zone", () => {
  // 2015-03-05T23:51:47Z, in a zone three hours west.
  const expression = Jq.parse('now, (now | strflocaltime("%Y-%m-%dT%H:%M:%S %z %Z"))', { clock: fixedClock(1425599507, () => -3 * 3600) });
  assert.deepEqual(expression.evaluate(null), [1425599507, "2015-03-05T20:51:47 -0300 -03"]);
  const named: JqClock = { now: () => 0, offset: () => 3600, abbreviation: () => "CET" };
  assert.deepEqual(Jq.parse('0 | strflocaltime("%H %Z")', { clock: named }).evaluate(null), ["01 CET"]);
});

test("options clock follows daylight saving time", () => {
  // +01:00, and +02:00 from March to October: January and July.
  const clock = fixedClock(0, (seconds) => (seconds >= 1425171600 && seconds < 1443661200 ? 7200 : 3600));
  assert.deepEqual(Jq.parse('(1420070400, 1435708800) | strflocaltime("%z")', { clock }).evaluate(null), ["+0100", "+0200"]);
});

test("the system clock is the default", () => {
  const [now, zone] = Jq.evaluate('now, (now | strflocaltime("%z"))', null) as [number, string];
  assert.ok(Math.abs(now - Date.now() / 1000) < 60);
  const minutes = -new Date().getTimezoneOffset();
  const expected = (minutes < 0 ? "-" : "+") + String(Math.trunc(Math.abs(minutes) / 60)).padStart(2, "0") + String(Math.abs(minutes) % 60).padStart(2, "0");
  assert.equal(zone, expected);
});

test("options debug and stderr receive the values", () => {
  const debugged: string[] = [];
  const written: string[] = [];
  const expression = Jq.parse('debug, debug("m", {a: .}), stderr', { debug: (value) => debugged.push(JSON.stringify(value)), stderr: (value) => written.push(JSON.stringify(value)) });
  assert.deepEqual(expression.evaluate(1), [1, 1, 1]);
  assert.deepEqual(debugged, ["1", '"m"', '{"a":1}']);
  assert.deepEqual(written, ["1"]);
});

test("without handlers debug and stderr only output their input", () => {
  assert.deepEqual(Jq.evaluate("debug, stderr", 1), [1, 1]);
});

test("halt_error without a code exits with 5", () => {
  const error = thrown(JqHaltError, () => Jq.evaluate("halt_error", "bye"));
  assert.deepEqual([error.message, error.exitCode], ["bye", 5]);
});

test("options maxDepth limits recursion", () => {
  const expression = Jq.parse("def f: if . == 0 then 0 else (. - 1 | f) + 1 end; f", { maxDepth: 10 });
  assert.deepEqual(expression.evaluate(9), [9]);
  assert.equal(thrown(JqError, () => expression.evaluate(10)).message, "Maximum call depth exceeded");
});

test("parse errors have a position", () => {
  const cases: [string, string, number, number][] = [
    ["1 +", "Unexpected end of program at line 1, column 4", 1, 4],
    [".a |\n  foo", "foo/0 is not defined at line 2, column 3", 2, 3],
    [". as $x | break $x", "$*label-x is not defined at line 1, column 11", 1, 11],
  ];
  for (const [expression, message, line, column] of cases) {
    const error = thrown(JqParseError, () => Jq.parse(expression));
    assert.deepEqual([error.message, error.line, error.column], [message, line, column]);
    assert.equal(error.value, undefined);
    assert.ok(error instanceof JqError);
  }
});

test("a string error is its message and value", () => {
  const error = thrown(JqError, () => Jq.evaluate('error("boom")', null));
  assert.equal(error.message, "boom");
  assert.equal(error.value, "boom");
});

test("another error value is its text, not a string", () => {
  const error = thrown(JqError, () => Jq.evaluate("error({a: [1, nan]})", null));
  assert.equal(error.message, '{"a":[1,null]} (not a string)');
  assert.deepEqual(error.value, { a: [1, null] });
});

test("a builtin error is its message", () => {
  assert.equal(thrown(JqError, () => Jq.evaluate(". + 1", "a")).message, 'string ("a") and number (1) cannot be added');
});

test("streaming gives results as they come and stops when asked", () => {
  const seen: JsonValue[] = [];
  Jq.parse("range(1000000000)").evaluateEach(null, (result) => {
    seen.push(result);
    return seen.length < 3;
  });
  assert.deepEqual(seen, [0, 1, 2]);
});

test("streaming delivers results before an error", () => {
  const seen: JsonValue[] = [];
  const error = thrown(JqError, () =>
    Jq.parse('1, 2, error("late"), 3').evaluateEach(null, (result) => {
      seen.push(result);
      return true;
    }),
  );
  assert.deepEqual(seen, [1, 2]);
  assert.equal(error.message, "late");
});

test("an exception of the callback propagates", () => {
  assert.throws(
    () =>
      Jq.parse("1, 2").evaluateEach(null, () => {
        throw new RangeError("mine");
      }),
    /mine/,
  );
});

test("halt ends normally", () => {
  assert.deepEqual(Jq.evaluate("1, halt, 2", null), [1]);
});

test("halt_error carries the value and the code", () => {
  const seen: JsonValue[] = [];
  const error = thrown(JqHaltError, () =>
    Jq.parse("1, ({a: 1} | halt_error(5)), 2").evaluateEach(null, (result) => {
      seen.push(result);
      return true;
    }),
  );
  assert.deepEqual(seen, [1]);
  assert.equal(error.exitCode, 5);
  assert.deepEqual(error.value, { a: 1 });
  assert.equal(error.message, '{"a":1} (not a string)');
  assert.equal(thrown(JqHaltError, () => Jq.evaluate('"bye" | halt_error(0)', null)).message, "bye");
});

test("invalid variable names are rejected before evaluation", () => {
  for (const name of ["$root", "1x", "a-b", "", "ENV"]) {
    assert.throws(() => Jq.evaluate('error("evaluated")', null, { [name]: 1 }), TypeError, name);
    assert.throws(() => Jq.evaluate('error("evaluated")', null, new Map([[name, 1]])), TypeError, name);
  }
});

test("variables are given per evaluation", () => {
  const expression = Jq.parse("$x + 1");
  assert.deepEqual(expression.evaluate(null, { x: 1 }), [2]);
  assert.deepEqual(expression.evaluate(null, new Map([["x", 10]])), [11]);
  assert.equal(thrown(JqError, () => expression.evaluate(null)).message, "$x is not defined");
  assert.deepEqual(Jq.evaluate("try $x catch .", null), ["$x is not defined"]);
  // A binding of the expression shadows a host variable; several variables at once.
  assert.deepEqual(Jq.evaluate("1 as $x | $x", null, { x: 99 }), [1]);
  assert.deepEqual(Jq.evaluate("[$a, $b, $root.name]", null, { a: 1, b: [2], root: { name: "r" } }), [[1, [2], "r"]]);
});

test("values cross the boundary", () => {
  // Non-finite numbers come out as jq prints them; large numbers stay doubles.
  assert.deepEqual(Jq.evaluate("[nan, infinite, -infinite]", null), [[null, 1.7976931348623157e308, -1.7976931348623157e308]]);
  assert.deepEqual(Jq.evaluate(".", 1e29), [1e29]);
  assert.deepEqual(Jq.evaluate(". + 1", 9007199254740992), [9007199254740992]);
  assert.ok(Object.is(Jq.evaluate(".", -0)[0], -0));
  assert.deepEqual(Jq.evaluate("[., isnan, isinfinite]", NaN), [[null, true, false]]);
  assert.deepEqual(Jq.evaluate("isinfinite", Infinity), [true]);
  // A lone surrogate becomes U+FFFD on the way in (spec §4.1), in strings and in keys.
  assert.deepEqual(Jq.evaluate("explode", "a\ud800b"), [[97, 65533, 98]]);
  assert.deepEqual(Jq.evaluate("keys", { "\udc00": 1 }), [["�"]]);
  assert.deepEqual(Jq.evaluate("[keys, .[]]", { "\udc00x": 1, "\udc01x": 2 }), [[["�x"], 2]]);
  // What is not JSON: a property without a value is left out, and null in an array.
  assert.deepEqual(Jq.evaluate(".", { a: undefined, b: [undefined, 1] } as never), [{ b: [null, 1] }]);
  assert.throws(() => Jq.evaluate(".", { a: 1n } as never), TypeError);
  assert.throws(() => Jq.evaluate(".", (() => 1) as never), TypeError);
  // A key that would be the prototype is an ordinary key.
  const proto = Jq.evaluate('{"__proto__": 1, a: 2}', null)[0] as Record<string, JsonValue>;
  assert.deepEqual(Object.keys(proto), ["__proto__", "a"]);
  assert.equal(Object.getPrototypeOf(proto), Object.prototype);
});

test("key order at the boundary", () => {
  // JavaScript enumerates the keys of a plain object that look like array indexes first.
  assert.deepEqual(Jq.evaluate("keys_unsorted", { b: 1, "10": 2, a: 3, "2": 4 }), [["2", "10", "b", "a"]]);
  // A Map gives its entries in order.
  const ordered = new Map<string, JsonValue>([
    ["b", 1],
    ["10", 2],
    ["a", 3],
    ["2", 4],
  ]);
  assert.deepEqual(Jq.evaluate("keys_unsorted", ordered), [["b", "10", "a", "2"]]);
  // Text in, text out: the order of jqlite, whatever the keys.
  assert.deepEqual(Jq.parse(". + {c: 5, \"1\": 6}").evaluateText('{"b":1,"10":2,"a":3,"2":4}'), ['{"b":1,"10":2,"a":3,"2":4,"c":5,"1":6}']);
  // The same result as a plain object: every key is there, the index-like ones first.
  const plain = Jq.parse(". + {c: 5, \"1\": 6}").evaluate(ordered)[0] as Record<string, JsonValue>;
  assert.deepEqual(Object.keys(plain), ["1", "2", "10", "b", "a", "c"]);
  assert.deepEqual(plain, { b: 1, "10": 2, a: 3, "2": 4, c: 5, "1": 6 });
});

test("text in and text out", () => {
  const expression = Jq.parse(".[] | [., . * 2]");
  assert.deepEqual(expression.evaluateText(" [1, 2e0,\n-0] "), ["[1,2]", "[2,4]", "[-0,-0]"]);
  assert.equal(thrown(JqError, () => expression.evaluateText("[1,")).message, "Unfinished JSON term at EOF at line 1, column 3 (while parsing '[1,')");
  assert.deepEqual(Jq.parse("[nan, infinite]").evaluateText("null"), ["[null,1.7976931348623157e+308]"]);
});

test("deep input and output convert without recursion", () => {
  let input: JsonValue = [];
  for (let i = 1; i < 5000; i++) input = [input];
  let result = Jq.evaluate(".", input)[0]!;
  let depth = 0;
  while (Array.isArray(result) && result.length === 1) {
    result = result[0]!;
    depth++;
  }

  assert.equal(depth, 4999);
  assert.deepEqual(result, []);
  const text = "[".repeat(10000) + "]".repeat(10000);
  assert.deepEqual(Jq.parse(".").evaluateText(text), [text]);
});

test("JSON Lines: each value is evaluated alone", async () => {
  const results: JsonValue[] = [];
  for await (const result of Jq.evaluateAsync("if . == 2 then halt else ., . * 10 end", [1, 2, 3])) results.push(result);
  assert.deepEqual(results, [1, 10, 3, 30]);
  async function* lines(): AsyncGenerator<JsonValue> {
    yield { a: 1 };
    yield { a: 2 };
  }

  const fields: JsonValue[] = [];
  for await (const result of Jq.evaluateAsync(Jq.parse(".a + $k"), lines(), { k: 100 })) fields.push(result);
  assert.deepEqual(fields, [101, 102]);
});

// N | f makes N + 1 calls of f.
const Tail = "def f: if . == 0 then 0 else (. - 1 | f) end; f";
const NonTail = "def f: if . == 0 then 0 else (. - 1 | f) + 1 end; f";

test("runaway recursion throws and the process survives", () => {
  for (const program of [Tail, NonTail]) {
    const error = thrown(JqError, () => Jq.parse(program).evaluate(100000));
    assert.equal(error.message, "Maximum call depth exceeded");
    assert.equal(error.value, "Maximum call depth exceeded");
  }
});

test("runaway recursion is caught by the program", () => {
  assert.deepEqual(Jq.parse("def f: if . == 0 then 0 else (. - 1 | f) + 1 end; try f catch .").evaluate(100000), ["Maximum call depth exceeded"]);
});

test("the last call below MaxDepth succeeds and the next fails", () => {
  // program, maxDepth (undefined: the default, 1000), the deepest N that fits, its output
  const boundaries: [string, number | undefined, number, number][] = [
    [Tail, undefined, 999, 0],
    [NonTail, undefined, 999, 999],
    [Tail, 3000, 2999, 0],
    [NonTail, 3000, 2999, 2999],
    [NonTail, 1000000, 999999, 999999],
  ];
  for (const [program, maxDepth, deepest, output] of boundaries) {
    const expression = maxDepth === undefined ? Jq.parse(program) : Jq.parse(program, { maxDepth });
    assert.deepEqual(expression.evaluate(deepest), [output]);
    assert.equal(thrown(JqError, () => expression.evaluate(deepest + 1)).message, "Maximum call depth exceeded");
  }
});
