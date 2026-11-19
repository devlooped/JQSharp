// The engine of §9.10: runs a program that RegexSyntax translated and finds the next match from
// a position. The reference plugs its host's regex engine in here. This port has its own small
// matcher instead, because JavaScript's RegExp cannot give the captures the spec demands (see
// regexSyntax.ts); nothing here uses the host's regex engine.
//
// The matcher reads the input once, from the start position on, and follows every way the
// program can match at the same time (a Pike machine): a thread is an instruction that waits for
// a code point, with the captures of the way that led to it. The threads are kept in the order a
// backtracking matcher would try them (a greedy quantifier takes all it can first, a lazy one as
// little, the left alternative first), and a state reached a second time at the same position is
// dropped: the first way to it has priority and the same future. So the time is the length of the
// input times the size of the program, and the memory depends on the program alone (see Matcher).
// A search for the leftmost match does not start again at each position: a new thread starts at
// each position, behind the ones already running, until a match is found. Nothing recurses.
//
// A loop whose body can match nothing ends after an iteration that matched nothing (Mark and
// EmptyExit of regexSyntax.ts). Whether an iteration has matched something yet belongs to the
// state of a thread, as its level: of the loops with a mark around its instruction, how many
// have, counted from the outermost (if an outer iteration has matched nothing, the inner ones it
// contains have not either).
//
// The input is read by code point: a match never splits a surrogate pair, and the positions
// reported are UTF-16 indices.
//
// Translated patterns are cached, in a map that is emptied when it reaches CacheLimit entries.

import { MaxProgram, Op, type Options, RegexError, type Translation, tryTranslate } from "./regexSyntax.js";

const CacheLimit = 256;

/** The most capture slots the threads of a program may hold together (instructions that read a code point × slots). */
export const MaxCaptures = 1 << 24;

const cache = new Map<string, Matcher>();

/** The matcher of `pattern` under `options`, or why the pattern is not of the dialect. */
export function tryCompile(pattern: string, options: Options): Matcher | RegexError {
  const key = (options.extended ? "x" : "-") + (options.dotAll ? "m" : "-") + (options.ignoreCase ? "i" : "-") + pattern;
  const cached = cache.get(key);
  if (cached !== undefined) return cached;
  const translation = tryTranslate(pattern, options);
  if (translation instanceof RegexError) return translation;
  const matcher = tryCreate(translation);
  if (matcher instanceof RegexError) return matcher;
  if (cache.size >= CacheLimit) cache.clear();
  cache.set(key, matcher);
  return matcher;
}

/** The matcher of a translated pattern, or the failure when its states or its captures are too many. */
export function tryCreate(translation: Translation): Matcher | RegexError {
  const matcher = new Matcher(translation);
  // A state is an instruction at a level: nested loops that can match nothing multiply them.
  if (matcher.states - translation.ops.length > MaxProgram || matcher.readers * matcher.slots > MaxCaptures) return new RegexError("pattern too large");
  return matcher;
}

/**
 * The first match starting at or after UTF-16 index `start` (anchors and word boundaries see the
 * whole input). `spans` receives start and end of the match, then of each group (-1, -1 for a
 * group that did not participate): 2 · (groups + 1) entries. A shorter `spans` receives the
 * first entries only; an empty one asks only whether there is a match.
 */
export function tryMatch(matcher: Matcher, input: string, start: number, spans: Int32Array): boolean {
  return matcher.run(input, start, spans);
}

// The threads at one position, in priority order: the instruction of each and its captures.
class Threads {
  pcs: Int32Array;
  captures: Int32Array;
  count = 0;

  constructor(
    capacity: number,
    private readonly slots: number,
  ) {
    this.pcs = new Int32Array(capacity);
    this.captures = new Int32Array(capacity * slots);
  }

  // Room for one more thread: there are never more than the program has reading instructions.
  grow(): void {
    const pcs = new Int32Array(this.pcs.length * 2);
    pcs.set(this.pcs);
    this.pcs = pcs;
    const captures = new Int32Array(pcs.length * this.slots);
    captures.set(this.captures);
    this.captures = captures;
  }
}

/**
 * A translated pattern ready to run. What a search needs besides the program is here too, made at
 * the first search and used again by the next ones (a search calls nothing that could start
 * another): one mark per state, a stack of at most two entries per state, and two lists of
 * threads of at most one thread per instruction that reads a code point.
 */
export class Matcher {
  /** One name (or null) per capturing group. */
  readonly groups: readonly (string | null)[];
  /** The capture slots of a thread: 2 · (groups + 1). */
  readonly slots: number;
  /** The states of the program: its instructions, each at every level it can have. */
  readonly states: number;
  /** The instructions that read a code point. */
  readonly readers: number;

  private readonly ops: Int32Array;
  private readonly as: Int32Array;
  private readonly bs: Int32Array;
  private readonly sets: readonly Int32Array[];
  // Per instruction: the loops with a mark around it, and its first state (the one of level 0).
  private readonly depth: Int32Array;
  private readonly base: Int32Array;
  // A match can only start with this UTF-16 unit, or only at the start of the input.
  private readonly literal: string | null;
  private readonly anchored: boolean;

  // Per state: the generation that reached it last. A generation is one position of one search.
  private seen: Int32Array | null = null;
  private generation = 0;
  // Pairs: an instruction and its level, still to follow; or -1 - slot and the value to put back.
  private stack: Int32Array | null = null;
  // The captures of the thread being followed.
  private work: Int32Array | null = null;
  private current: Threads | null = null;
  private next: Threads | null = null;

  constructor(translation: Translation) {
    const ops = translation.ops;
    this.ops = ops;
    this.as = translation.as;
    this.bs = translation.bs;
    this.sets = translation.sets;
    this.groups = translation.groups;
    this.slots = 2 * (translation.groups.length + 1);
    this.depth = new Int32Array(ops.length);
    this.base = new Int32Array(ops.length);
    let depth = 0;
    let states = 0;
    let readers = 0;
    for (let pc = 0; pc < ops.length; pc++) {
      const op = ops[pc]!;
      this.depth[pc] = depth;
      this.base[pc] = states;
      states += depth + 1;
      if (op === Op.Mark) depth++;
      else if (op === Op.EmptyExit) depth--;
      else if (op <= Op.AnyButLineFeed) readers++;
    }

    this.states = states;
    this.readers = readers;
    // The program starts with the instruction that stores the start of the match: what follows
    // it is the first thing every match does.
    this.literal = ops[1] === Op.Char && translation.as[1]! <= 0xffff ? String.fromCharCode(translation.as[1]!) : null;
    this.anchored = ops[1] === Op.Begin;
  }

  run(input: string, start: number, spans: Int32Array): boolean {
    const ops = this.ops;
    const as = this.as;
    const sets = this.sets;
    const depth = this.depth;
    const length = input.length;
    const width = Math.min(spans.length, this.slots);
    if (this.seen === null) {
      this.seen = new Int32Array(this.states);
      this.stack = new Int32Array(2 * this.states);
      this.work = new Int32Array(this.slots);
      const capacity = Math.min(8, this.readers + 1);
      this.current = new Threads(capacity, this.slots);
      this.next = new Threads(capacity, this.slots);
    }

    const work = this.work!;
    let current = this.current!;
    let next = this.next!;
    current.count = 0;
    let matched = false;
    let pos = start;
    this.advance();
    while (true) {
      // The threads at pos are in current. Until a match is found, one more starts here, behind
      // them: a match that starts earlier has priority.
      if (!matched) {
        if (current.count === 0) {
          if (this.anchored && pos > 0) return false;
          if (this.literal !== null) {
            const at = input.indexOf(this.literal, pos);
            if (at < 0) return false;
            if (at !== pos) {
              pos = at;
              this.advance();
            }
          }
        }

        for (let slot = 0; slot < width; slot++) work[slot] = -1;
        if (this.follow(current, 0, 0, input, pos, width, spans)) {
          if (width === 0) return true;
          matched = true;
        }
      }

      if (pos >= length) return matched;
      let c = input.charCodeAt(pos);
      let size = 1;
      if (c >= 0xd800 && c <= 0xdbff) {
        c = input.codePointAt(pos)!;
        if (c > 0xffff) size = 2;
      }

      if (current.count === 0) {
        if (matched) return true;
        pos += size;
        this.advance();
        continue;
      }

      // Every thread reads the code point at pos: the ones that accept it go on after it.
      this.advance();
      next.count = 0;
      const pcs = current.pcs;
      const captures = current.captures;
      const count = current.count;
      for (let i = 0; i < count; i++) {
        const pc = pcs[i]!;
        const op = ops[pc]!;
        if (op === Op.Char ? as[pc] !== c : op === Op.Set ? !inSet(sets[as[pc]!]!, c) : op === Op.AnyButLineFeed && c === 0x0a) continue;
        const from = i * this.slots;
        for (let slot = 0; slot < width; slot++) work[slot] = captures[from + slot]!;
        // It has read a code point: every loop around it has matched something.
        if (this.follow(next, pc + 1, depth[pc + 1]!, input, pos + size, width, spans)) {
          if (width === 0) return true;
          // The threads behind this one have a lower priority than the match.
          matched = true;
          break;
        }
      }

      pos += size;
      const swap = current;
      current = next;
      next = swap;
      this.current = current;
      this.next = swap;
    }
  }

  // The next position of the search: no state has been reached at it yet.
  private advance(): void {
    if (this.generation === 0x7fffffff) {
      this.seen!.fill(0);
      this.generation = 0;
    }

    this.generation++;
  }

  // Follows a thread from an instruction at `pos`, with the captures in `work`, through the
  // instructions that read nothing: each reading instruction it reaches first becomes a thread of
  // `threads`, in priority order. True when it reached the end of the program: the match is then
  // in `spans`, and what it had still to follow has a lower priority.
  private follow(threads: Threads, pc: number, level: number, input: string, pos: number, width: number, spans: Int32Array): boolean {
    const ops = this.ops;
    const as = this.as;
    const bs = this.bs;
    const depth = this.depth;
    const base = this.base;
    const seen = this.seen!;
    const stack = this.stack!;
    const work = this.work!;
    const generation = this.generation;
    const slots = this.slots;
    let top = 0;
    while (true) {
      const op = ops[pc]!;
      // What an instruction that reads does next does not depend on the level.
      const state = op <= Op.AnyButLineFeed ? base[pc]! : base[pc]! + level;
      let alive = seen[state] !== generation;
      if (alive) {
        seen[state] = generation;
        switch (op) {
          case Op.Char:
          case Op.Set:
          case Op.Any:
          case Op.AnyButLineFeed: {
            const index = threads.count;
            if (index === threads.pcs.length) threads.grow();
            threads.pcs[index] = pc;
            const captures = threads.captures;
            const to = index * slots;
            for (let slot = 0; slot < width; slot++) captures[to + slot] = work[slot]!;
            threads.count = index + 1;
            alive = false;
            break;
          }
          case Op.Split:
            stack[top++] = pc + bs[pc]!;
            stack[top++] = level;
            pc += as[pc]!;
            break;
          case Op.Jump:
            pc += as[pc]!;
            break;
          case Op.Save: {
            const slot = as[pc]!;
            if (slot < width) {
              stack[top++] = -1 - slot;
              stack[top++] = work[slot]!;
              work[slot] = pos;
            }

            pc++;
            break;
          }
          case Op.Begin:
            if (pos === 0) pc++;
            else alive = false;
            break;
          case Op.End:
            if (pos === input.length) pc++;
            else alive = false;
            break;
          case Op.WordBoundary:
          case Op.NotWordBoundary: {
            const boundary = (pos > 0 && isWord(input.charCodeAt(pos - 1))) !== (pos < input.length && isWord(input.charCodeAt(pos)));
            if (boundary === (op === Op.WordBoundary)) pc++;
            else alive = false;
            break;
          }
          case Op.Mark:
            // An iteration starts: it has matched nothing yet, so the level stays.
            pc++;
            break;
          case Op.EmptyExit: {
            // The iteration of the innermost loop ends: it matched something if the level counts it.
            const inner = depth[pc]!;
            pc += level < inner ? bs[pc]! : 1;
            if (level === inner) level = inner - 1;
            break;
          }
          default:
            for (let slot = 0; slot < width; slot++) spans[slot] = work[slot]!;
            return true;
        }
      }

      if (alive) continue;

      // This way ended: back to the last one left aside, putting back the captures stored since.
      while (true) {
        if (top === 0) return false;
        const value = stack[--top]!;
        const entry = stack[--top]!;
        if (entry >= 0) {
          pc = entry;
          level = value;
          break;
        }

        work[-1 - entry] = value;
      }
    }
  }
}

// The ASCII word characters of §9.10: a UTF-16 unit of a code point above U+FFFF is not one.
function isWord(unit: number): boolean {
  return (unit >= 0x30 && unit <= 0x39) || (unit >= 0x41 && unit <= 0x5a) || (unit >= 0x61 && unit <= 0x7a) || unit === 0x5f;
}

// Whether a code point is in a set: sorted pairs low, high.
function inSet(ranges: Int32Array, c: number): boolean {
  if (ranges.length <= 8) {
    for (let i = 0; i < ranges.length; i += 2) {
      if (c < ranges[i]!) return false;
      if (c <= ranges[i + 1]!) return true;
    }

    return false;
  }

  let low = 0;
  let high = ranges.length / 2 - 1;
  while (low <= high) {
    const middle = (low + high) >> 1;
    if (c < ranges[2 * middle]!) high = middle - 1;
    else if (c > ranges[2 * middle + 1]!) low = middle + 1;
    else return true;
  }

  return false;
}
