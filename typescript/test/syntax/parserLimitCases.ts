// The rows of the theories of ParserLimitTests.cs of the reference, copied by scripts/port-cases.mjs. Do not edit.

/* eslint-disable */
export const Long_sequences_do_not_recurse = [
  ["", "1 + ", "1"],
  ["", "1 * ", "1"],
  ["", "1 // ", "1"],
  ["", "1 and ", "1"],
  ["", "1 == 1 or ", "1"],
  ["", "1 | ", "1"],
  ["", "1, ", "1"],
  ["", "1 as $x | ", "$x"],
  ["", "label $x | ", "1"],
  ["", "def f: 1; ", "f"],
  ["(", "def f: 1; ", "f)"],
  ["[1, ", "def f: 1; ", "f]"],
  ["", "-", "1"],
  ["", "1 + -", "1"],
  ["", ".a", ""],
  ["", ".[0]", ""],
  ["", ".a?", ""],
  [". as $a", " ?// $a", " | 1"],
  ["{a: ", "1 | ", "1}"],
  ["{", "a: 1, ", "}"],
  ["\"", "\\(1)", "\""],
  ["f(", "1; ", "1)"],
] as const;
