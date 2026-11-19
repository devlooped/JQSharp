// The regex engine: what it finds is what the simplest matcher of the same programs finds
// (test/support/backtrackingMatcher.ts), in time linear in the input and with memory the size of
// the pattern. The corpus shows what the programs mean (spec §9.10).

import assert from "node:assert/strict";
import { test } from "node:test";
import { RegexMatcher, RegexProgram, tryCompile } from "../../src/builtins/regexEngine.js";
import { MaxGroups, MaxNesting, MaxSize, RegexError, tryTranslate } from "../../src/builtins/regexSyntax.js";
import * as Backtracking from "../support/backtrackingMatcher.js";
import { line } from "../support/evaluate.js";
import { input, pattern, random } from "../support/regexFuzz.js";

const Plain = { extended: false, dotAll: false, ignoreCase: false };

for (const seed of [1, 7, 12345]) {
  test(`random patterns match as the backtracking matcher does (seed ${seed})`, () => {
    const next = random(seed);
    let compared = 0;
    for (let n = 0; n < 20000; n++) {
      const source = pattern(next, 3);
      const translation = tryTranslate(source, { extended: false, dotAll: next(2) === 0, ignoreCase: false });
      assert.ok(!(translation instanceof RegexError), source);
      const matcher = new RegexMatcher(new RegexProgram(translation));
      const width = 2 * (translation.groups.length + 1);
      for (let k = 0; k < 4; k++) {
        const text = input(next, 7);
        for (let start = 0; start <= text.length; start++) {
          // A search starts between code points.
          if (start > 0 && text.charCodeAt(start) >= 0xdc00 && text.charCodeAt(start) <= 0xdfff) continue;
          const expected = new Int32Array(width);
          const found = Backtracking.tryMatch(translation, text, start, expected, 200000);
          if (found === null) continue;
          const where = `${JSON.stringify(source)} on ${JSON.stringify(text)} from ${start}`;
          const spans = new Int32Array(width);
          assert.equal(matcher.tryMatch(text, start, spans), found, where);
          if (found) assert.deepEqual(spans, expected, where);
          // Only the match; only whether there is one.
          const match = new Int32Array(2);
          assert.equal(matcher.tryMatch(text, start, match), found, where);
          if (found) assert.deepEqual(match, expected.subarray(0, 2), where);
          assert.equal(matcher.tryMatch(text, start, new Int32Array(0)), found, where);
          compared++;
        }
      }
    }

    assert.ok(compared > 300000, String(compared));
  });
}

// Patterns a backtracking matcher takes exponential or quadratic time for, and long inputs.
const Linear: [string, string][] = [
  ['"a" * 28 + "c" | test("^(a*)*b$")', "false"],
  ['"a" * 5000 + "c" | test("^(a*)*b$")', "false"],
  ['"ab" * 2000000 | test("^(a|b)*$")', "true"],
  ['"a" * 200000 | test("a*b")', "false"],
  ['"a" * 200000 | [match("a*b")] | length', "0"],
  ['"x" * 100000 | test("(x+x+)+y")', "false"],
  ['"a" * 40 | test("(a?){40}a{40}")', "true"],
  ['"ab" * 2000000 | match("^(a|b)*$") | .captures[0].offset', "3999999"],
  ['"a" * 100000 | [match("(a|aa)*?$"; "g") | .length]', "[100000,0]"],
];

for (const [program, expected] of Linear) {
  test(`linear time: ${program}`, () => {
    const start = performance.now();
    assert.equal(line(program), expected);
    const elapsed = performance.now() - start;
    assert.ok(elapsed < 2000, `${elapsed} ms`);
  });
}

test("the memory of a search is the size of the pattern", () => {
  // The threads are kept in arrays that grow with their number: after a long input they hold
  // no more than one thread per instruction that reads a code point.
  const program = tryCompile("^(?:(a)|(b)|[ab]c?)*$", Plain);
  assert.ok(program instanceof RegexProgram);
  assert.equal(program.readers, 4);
  assert.equal(program.slots, 6);
  const matcher = new RegexMatcher(program);
  const spans = new Int32Array(6);
  assert.ok(matcher.tryMatch("ab".repeat(500000), 0, spans));
  assert.deepEqual([...spans], [0, 1000000, 999998, 999999, 999999, 1000000]);
  const threads = (matcher as unknown as { current: { pcs: Int32Array; captures: Int32Array } }).current;
  assert.ok(threads.pcs.length <= 8, String(threads.pcs.length));
  assert.equal(threads.captures.length, threads.pcs.length * 6);
  const seen = (matcher as unknown as { seen: Int32Array; stack: Int32Array });
  assert.equal(seen.seen.length, program.states);
  assert.equal(seen.stack.length, 2 * program.states);
});

test("the largest patterns of spec §11 are programs the matcher runs", () => {
  // The limits are the translation's, and the corpus has their boundaries. What is the engine's:
  // within them a program has at most five instructions per unit of the pattern's size, and a
  // state is an instruction at a level (nested loops that can match nothing have levels).
  const compiled = (source: string): RegexProgram => {
    const program = tryCompile(source, Plain);
    assert.ok(program instanceof RegexProgram, source.slice(0, 40));
    return program;
  };

  assert.deepEqual([MaxSize, MaxNesting, MaxGroups], [10000, 100, 1000]);
  // The most instructions for a size: an empty group repeated, each copy optional and with its mark.
  const copies = tryTranslate("(){0,1000}".repeat(9) + "(){0,990}", Plain);
  assert.ok(!(copies instanceof RegexError));
  assert.equal(copies.ops.length, 5 * (MaxSize - 10) + 3);
  assert.ok(new RegexMatcher(new RegexProgram(copies)).tryMatch("a", 0, new Int32Array(2)));
  // The most states: every instruction inside a hundred loops that can match nothing.
  const nested = (levels: number, inner: string): string => "(?:".repeat(levels) + inner + ")*".repeat(levels);
  const deep = compiled(nested(MaxNesting, "a?".repeat(4900)));
  assert.ok(deep.states > 1000000 && deep.states < 2000000, String(deep.states));
  assert.ok(new RegexMatcher(deep).tryMatch("a".repeat(100) + "b", 0, new Int32Array(2)));
  assert.equal(line(`"a" * 300 | [match("${nested(MaxNesting, "a*")}"; "g") | .length]`), "[300,0]");
  // The most capture slots: a thousand groups, each thread with its own.
  const groups = compiled("(a)".repeat(MaxGroups));
  assert.equal(groups.slots, 2 * (MaxGroups + 1));
  const spans = new Int32Array(groups.slots);
  assert.ok(new RegexMatcher(groups).tryMatch("a".repeat(MaxGroups + 5), 0, spans));
  assert.deepEqual([...spans.subarray(0, 4), ...spans.subarray(groups.slots - 2)], [0, 1000, 0, 1, 999, 1000]);
  // Past a limit the translation fails, with the fixed reason.
  const failure = tryCompile("(a)".repeat(MaxGroups + 1), Plain);
  assert.ok(failure instanceof RegexError);
  assert.equal(failure.reason, "the pattern is too large");
});
