// §9.10 test/2 and match/2: the flags, the search loop and the match objects, with offsets and
// lengths in code points. The pattern goes through RegexSyntax to the engine (RegexEngine), which
// works on UTF-16 units.

import type { Node } from "../binding/bound.js";
import type { Env } from "../runtime/env.js";
import type { After, Emit, Interpreter } from "../runtime/interpreter.js";
import { Path } from "../runtime/path.js";
import * as CodePoints from "../values/codePoints.js";
import * as Errors from "../values/errors.js";
import { JqArray } from "../values/jqArray.js";
import { JqObjectBuilder } from "../values/jqObjectBuilder.js";
import { Fail, type JqValue } from "../values/jqValue.js";
import { add, addClosure } from "./nativeTable.js";
import * as RegexEngine from "./regexEngine.js";
import { RegexError } from "./regexSyntax.js";

export function register(): void {
  add("test", 2, test);
  addClosure("match", 2, match);
}

const NoSpans = new Int32Array(0);

// A search ready to run: the input, the matcher of the pattern and the flags.
class Search {
  constructor(
    readonly input: string,
    readonly matcher: RegexEngine.RegexMatcher,
    readonly global: boolean,
    readonly skipEmpty: boolean,
  ) {}
}

function test(_: Interpreter, input: JqValue, args: JqValue[]): JqValue | Fail {
  const search = tryPrepare(input, args[0]!, args[1]!);
  if (search instanceof Fail) return search;
  // Whether there is a match, and nothing of it; with the n flag, a match that is not empty.
  if (!search.skipEmpty) return search.matcher.tryMatch(search.input, 0, NoSpans);
  return next(search, new CodePointMap(search.input), new Int32Array(2), 0) >= 0;
}

// match(re; flags): flags outermost, re innermost (re fastest); one output per reported match.
function match(interpreter: Interpreter, env: Env | null, args: readonly Node[], input: JqValue, path: Path | null, emit: Emit): void {
  const at = Path.of(path);
  interpreter.forEach(args[1]!, env, input, null, (flags) =>
    interpreter.forEach(args[0]!, env, input, null, (re) => {
      const search = tryPrepare(input, re, flags);
      if (search instanceof Fail) {
        interpreter.fail(search.error);
        return;
      }

      const map = new CodePointMap(search.input);
      const spans = new Int32Array(search.matcher.program.slots);
      let p = 0;
      const more: After = (stop) => {
        if (stop === null) p = next(search, map, spans, p);
        if (stop !== null || p < 0) {
          interpreter.ret(stop);
          return;
        }

        if (search.global) interpreter.after(more);
        interpreter.output(emit, matchObject(search, spans, map), at);
      };
      more(null);
    }),
  );
}

function tryPrepare(input: JqValue, re: JqValue, flags: JqValue): Search | Fail {
  if (typeof input !== "string") return new Fail(Errors.cannotBeMatched(input));
  if (typeof re !== "string") return new Fail(Errors.notAString(re));
  if (typeof flags !== "string" && flags !== null) return new Fail(Errors.notAString(flags));
  let global = false;
  let ignoreCase = false;
  let extended = false;
  let skipEmpty = false;
  let dotAll = false;
  const letters = flags === null ? "" : flags;
  for (let i = 0; i < letters.length; i++) {
    switch (letters[i]) {
      case "g":
        global = true;
        break;
      case "i":
        ignoreCase = true;
        break;
      case "x":
        extended = true;
        break;
      case "n":
        skipEmpty = true;
        break;
      case "m":
      case "p":
        dotAll = true;
        break;
      case "s":
      case "l":
        break;
      default:
        return new Fail(Errors.invalidModifiers(letters));
    }
  }

  const program = RegexEngine.tryCompile(re, { extended, dotAll, ignoreCase });
  if (program instanceof RegexError) return new Fail(Errors.regexFailure(program.reason));
  return new Search(input, new RegexEngine.RegexMatcher(program), global, skipEmpty);
}

/**
 * One turn of the search loop of §9.10, in code points: the next match to report at or after
 * code point `p`, its spans in `spans`. Returns where the search goes on after it, or -1 when
 * there is no match to report (without the g flag, after the first one too).
 */
function next(search: Search, map: CodePointMap, spans: Int32Array, p: number): number {
  while (p >= 0 && p <= map.length) {
    if (!search.matcher.tryMatch(search.input, map.unit(p), spans)) return -1;
    const start = map.codePoint(spans[0]!);
    const end = map.codePoint(spans[1]!);
    p = end > start ? end : end + 1;
    if (!(search.skipEmpty && start === end)) return search.global ? p : map.length + 1;
  }

  return -1;
}

function matchObject(search: Search, spans: Int32Array, map: CodePointMap): JqValue {
  const groups = search.matcher.program.groups;
  const captures: JqValue[] = [];
  for (let g = 0; g < groups.length; g++) {
    const capture = new JqObjectBuilder();
    const start = spans[2 * g + 2]!;
    const end = spans[2 * g + 3]!;
    if (start === end || start < 0) {
      // An empty or absent group has jq's other key order.
      capture.set("offset", start < 0 ? -1 : map.codePoint(start));
      capture.set("string", start < 0 ? null : "");
      capture.set("length", 0);
    } else {
      addSpan(capture, search.input, map, start, end);
    }

    capture.set("name", groups[g]!);
    captures.push(capture.toObject());
  }

  const result = new JqObjectBuilder();
  addSpan(result, search.input, map, spans[0]!, spans[1]!);
  result.set("captures", JqArray.fromOwned(captures));
  return result.toObject();
}

// "offset", "length", "string" of a span given in UTF-16 units.
function addSpan(target: JqObjectBuilder, input: string, map: CodePointMap, start: number, end: number): void {
  const offset = map.codePoint(start);
  target.set("offset", offset);
  target.set("length", map.codePoint(end) - offset);
  target.set("string", input.substring(start, end));
}

/** UTF-16 indices to code point indices and back, for a well-formed string. */
class CodePointMap {
  private readonly toCodePoint: Int32Array | null = null;
  private readonly toUnit: Int32Array | null = null;

  /** The number of code points. */
  readonly length: number;

  constructor(text: string) {
    this.length = CodePoints.length(text);
    if (this.length === text.length) return;
    this.toCodePoint = new Int32Array(text.length + 1);
    this.toUnit = new Int32Array(this.length + 1);
    let codePoint = 0;
    for (let i = 0; i < text.length; i++) {
      this.toCodePoint[i] = codePoint;
      if (!CodePoints.isLowSurrogate(text.charCodeAt(i))) this.toUnit[codePoint++] = i;
    }

    this.toCodePoint[text.length] = this.length;
    this.toUnit[this.length] = text.length;
  }

  codePoint(unit: number): number {
    return this.toCodePoint === null ? unit : this.toCodePoint[unit]!;
  }

  unit(codePoint: number): number {
    return this.toUnit === null ? codePoint : this.toUnit[codePoint]!;
  }
}
