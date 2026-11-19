// The regex engine of this port (the reference uses its host's): what it finds is what the
// simplest matcher of the same programs finds (test/support/backtrackingMatcher.ts), in time
// linear in the input and with memory the size of the pattern.

import assert from "node:assert/strict";
import { test } from "node:test";
import { MaxCaptures, Matcher, tryCompile, tryCreate, tryMatch } from "../../src/builtins/regexEngine.js";
import { MaxProgram, RegexError, tryTranslate } from "../../src/builtins/regexSyntax.js";
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
      const matcher = tryCreate(translation);
      assert.ok(matcher instanceof Matcher, source);
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
          assert.equal(tryMatch(matcher, text, start, spans), found, where);
          if (found) assert.deepEqual(spans, expected, where);
          // Only the match; only whether there is one.
          const match = new Int32Array(2);
          assert.equal(tryMatch(matcher, text, start, match), found, where);
          if (found) assert.deepEqual(match, expected.subarray(0, 2), where);
          assert.equal(tryMatch(matcher, text, start, new Int32Array(0)), found, where);
          compared++;
        }
      }
    }

    assert.ok(compared > 300000, String(compared));
  });
}

// What jq 1.8.2 prints: an iteration that matched nothing ends its repetition, counted or not,
// and a group reports its last participation.
const EmptyIterations: [string, string][] = [
  ['"b" | match("(a*)*") | .captures | map([.offset, .string])', '[[0,""]]'],
  ['"aa" | match("(|a)*", "(|a)+") | .string', '"", ""'],
  ['"ab" | match("(?:a?|b)*") | .string', '"a"'],
  ['"a" | match("(?:(?:a|(c?))(b?))*") | .captures | map([.offset, .string])', '[[1,""],[1,""]]'],
  ['"ca" | match("(c??){2}a", "(c??){0,2}a", "(c??)*a") | .captures | map([.offset, .length])', "[[1,0]], [[1,0]], [[1,0]]"],
  ['"caaa " | match("([^a]??){0,2}[ab]") | .captures | map([.offset, .length])', "[[1,0]]"],
  ['"cabcc" | match("((|$.|[^a]\\\\w*){0,2}(?:a)){1,2}?") | .captures | map([.offset, .length])', "[[0,2],[1,0]]"],
  ['"ab" | match("(?:(a)|b)+") | .captures | map(.string)', '["a"]'],
  ['"aaa" | [match("(a??){3}"; "g") | .length]', "[0,0,0,0]"],
];

for (const [program, expected] of EmptyIterations) {
  test(`empty iterations: ${program}`, () => {
    assert.equal(line(program), expected);
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
  const matcher = tryCompile("^(?:(a)|(b)|[ab]c?)*$", Plain);
  assert.ok(matcher instanceof Matcher);
  assert.equal(matcher.readers, 4);
  assert.equal(matcher.slots, 6);
  const spans = new Int32Array(6);
  assert.ok(tryMatch(matcher, "ab".repeat(500000), 0, spans));
  assert.deepEqual([...spans], [0, 1000000, 999998, 999999, 999999, 1000000]);
  const threads = (matcher as unknown as { current: { pcs: Int32Array; captures: Int32Array } }).current;
  assert.ok(threads.pcs.length <= 8, String(threads.pcs.length));
  assert.equal(threads.captures.length, threads.pcs.length * 6);
  const seen = (matcher as unknown as { seen: Int32Array; stack: Int32Array });
  assert.equal(seen.seen.length, matcher.states);
  assert.equal(seen.stack.length, 2 * matcher.states);
});

test("a pattern too large is an error", () => {
  const failure = (source: string): string => {
    const compiled = tryCompile(source, Plain);
    return compiled instanceof RegexError ? compiled.reason : "compiled";
  };

  // Counted repetitions are written out: at most MaxProgram instructions.
  assert.equal(MaxProgram, 200000);
  assert.equal(failure("(?:a{1000}){200}"), "pattern too large");
  assert.equal(failure("(?:a{1000}){199}"), "compiled");
  // A state is an instruction at a level, and nested loops that can match nothing have levels:
  // at most MaxProgram states more than instructions.
  const nested = (levels: number): string => "(?:".repeat(levels) + "a*" + ")*".repeat(levels);
  assert.equal(failure(nested(100)), "compiled");
  assert.equal(failure(nested(400)), "pattern too large");
  // The captures of the threads: at most MaxCaptures slots.
  assert.equal(MaxCaptures, 1 << 24);
  assert.equal(failure("(a)".repeat(2000)), "compiled");
  assert.equal(failure("(a)".repeat(3000)), "pattern too large");
  assert.equal(line('try test("(?:a{1000}){200}") catch .', '"a"'), '"Regex failure: pattern too large"');
  // Long without repetition is not large: the states are the instructions.
  assert.equal(line('"x" + "ab" * 150000 | . as $s | test($s)'), "true");
  assert.equal(line(`"a" * 300 | [match("${nested(100)}"; "g") | .length]`), "[300,0]");
});
