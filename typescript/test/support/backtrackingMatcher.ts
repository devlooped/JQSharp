// What the programs of regexSyntax.ts mean, as the simplest machine that runs them: a
// backtracking matcher that tries each start position in turn and each alternative in priority
// order, with the positions of the marks as they are stored. It can take time exponential in the
// input, so it is not the engine; test/builtins/regexEngine.test.ts compares the engine with it
// on small patterns and inputs, and gives up on a search that takes it more than `budget` steps.

import { Op, type Translation } from "../../src/builtins/regexSyntax.js";

// What the stack holds, three numbers an entry: an alternative to try (its instruction and
// position), or the value of a slot or of a mark to put back when going back past it.
const Alternative = 0;
const Slot = 1;
const Mark = 2;

/**
 * The first match starting at or after UTF-16 index `start`: its spans, as RegexMatcher.tryMatch
 * gives them; null when the search is not over after `budget` instructions.
 */
export function tryMatch(program: Translation, input: string, start: number, spans: Int32Array, budget: number): boolean | null {
  const steps = { left: budget };
  for (let at = start; at <= input.length; at++) {
    if (run(program, input, at, spans, steps)) return true;
    if (steps.left < 0) return null;
    const unit = input.charCodeAt(at);
    if (unit >= 0xd800 && unit <= 0xdbff) at++;
  }

  return false;
}

// Whether the program matches from `at`; the spans are then in `spans`.
function run(program: Translation, input: string, at: number, spans: Int32Array, steps: { left: number }): boolean {
  const ops = program.ops;
  const as = program.as;
  const bs = program.bs;
  const length = input.length;
  const marks = new Int32Array(program.marks).fill(-1);
  const stack: number[] = [];
  spans.fill(-1);
  let pc = 0;
  let pos = at;
  while (true) {
    if (--steps.left < 0) return false;
    let ok = true;
    const a = as[pc]!;
    switch (ops[pc]) {
      case Op.Char:
      case Op.Set:
      case Op.Any:
      case Op.AnyButLineFeed: {
        const c = pos < length ? input.codePointAt(pos)! : -1;
        if (c >= 0 && (ops[pc] === Op.Char ? c === a : ops[pc] === Op.Set ? inSet(program.sets[a]!, c) : ops[pc] === Op.Any || c !== 0x0a)) {
          pos += c > 0xffff ? 2 : 1;
          pc++;
        } else {
          ok = false;
        }

        break;
      }
      case Op.Split:
        stack.push(Alternative, pc + bs[pc]!, pos);
        pc += a;
        break;
      case Op.Jump:
        pc += a;
        break;
      case Op.Save:
        stack.push(Slot, a, spans[a]!);
        spans[a] = pos;
        pc++;
        break;
      case Op.Begin:
        if (pos === 0) pc++;
        else ok = false;
        break;
      case Op.End:
        if (pos === length) pc++;
        else ok = false;
        break;
      case Op.WordBoundary:
      case Op.NotWordBoundary: {
        const boundary = (pos > 0 && isWord(input.charCodeAt(pos - 1))) !== (pos < length && isWord(input.charCodeAt(pos)));
        if (boundary === (ops[pc] === Op.WordBoundary)) pc++;
        else ok = false;
        break;
      }
      case Op.Mark:
        stack.push(Mark, a, marks[a]!);
        marks[a] = pos;
        pc++;
        break;
      case Op.EmptyExit:
        pc += pos === marks[a] ? bs[pc]! : 1;
        break;
      default:
        return true;
    }

    if (ok) continue;

    // Back to the last alternative, undoing what was stored since.
    while (true) {
      if (stack.length === 0) return false;
      const value = stack.pop()!;
      const index = stack.pop()!;
      const kind = stack.pop()!;
      if (kind === Alternative) {
        pc = index;
        pos = value;
        break;
      }

      if (kind === Slot) spans[index] = value;
      else marks[index] = value;
    }
  }
}

function isWord(unit: number): boolean {
  return (unit >= 0x30 && unit <= 0x39) || (unit >= 0x41 && unit <= 0x5a) || (unit >= 0x61 && unit <= 0x7a) || unit === 0x5f;
}

function inSet(ranges: Int32Array, c: number): boolean {
  for (let i = 0; i < ranges.length; i += 2) {
    if (c >= ranges[i]! && c <= ranges[i + 1]!) return true;
  }

  return false;
}
