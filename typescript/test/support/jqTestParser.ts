// Reads a file of the conformance corpus (spec/tests/README.md): the format of jq's tests/jq.test.

import { readFileSync } from "node:fs";

export interface JqTestCase {
  readonly program: string;
  readonly input: string;
  readonly expectedOutputs: readonly string[];
  readonly shouldFail: boolean;
  /** The message lines of a %%FAIL case; null with IGNORE MSG. */
  readonly expectedError: string | null;
  readonly lineNumber: number;
}

export function parseFile(filePath: string): JqTestCase[] {
  // Lines end at LF (a CR before it is part of the line ending); a lone CR is part of the line.
  const lines = readFileSync(filePath, "utf8")
    .split("\n")
    .map((line) => (line.endsWith("\r") ? line.substring(0, line.length - 1) : line));
  const cases: JqTestCase[] = [];
  let index = 0;
  while (index < lines.length) {
    if (isSeparator(lines[index]!)) {
      index++;
      continue;
    }

    if (isFailMarker(lines[index]!)) {
      // "%%FAIL IGNORE MSG": the program must not parse, whatever the message.
      const ignoreMessage = lines[index]!.includes("IGNORE MSG");
      index++;
      while (index < lines.length && isSeparator(lines[index]!)) index++;
      if (index >= lines.length) break;
      const program = lines[index]!;
      const lineNumber = index + 1;
      index++;
      const errorLines: string[] = [];
      while (index < lines.length && !isSeparator(lines[index]!) && !isFailMarker(lines[index]!)) errorLines.push(lines[index++]!);
      cases.push({ program, input: "", expectedOutputs: [], shouldFail: true, expectedError: ignoreMessage ? null : errorLines.join("\n"), lineNumber });
      continue;
    }

    const program = lines[index]!;
    const lineNumber = index + 1;
    index++;
    if (index >= lines.length) throw new Error(`Missing input line for test program at line ${lineNumber}.`);
    const input = lines[index]!;
    index++;
    const expectedOutputs: string[] = [];
    while (index < lines.length && !isSeparator(lines[index]!) && !isFailMarker(lines[index]!)) expectedOutputs.push(lines[index++]!);
    cases.push({ program, input, expectedOutputs, shouldFail: false, expectedError: null, lineNumber });
  }

  return cases;
}

function isFailMarker(line: string): boolean {
  return line.trimStart().startsWith("%%FAIL");
}

function isSeparator(line: string): boolean {
  const trimmed = line.trim();
  return trimmed.length === 0 || trimmed.startsWith("#");
}
