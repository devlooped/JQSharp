// The regex dialect of §9.10: reads a jqlite pattern, rejects everything outside the dialect,
// and writes an equivalent program for the engine (regexEngine.ts) together with the names of
// its capturing groups in the order of their '('.
//
// The reference writes a pattern text for its host's regex engine. This port does not use
// JavaScript's RegExp: it resets the captures of a group at every iteration of an enclosing
// repetition, where §9.10 says a group reports its last successful participation
// (`"ab" | match("(?:(a)|b)+")` has the capture "a"), and no rewriting of a pattern avoids
// that. So the translation is to the instructions of the small matcher of regexEngine.ts, with
// the meaning the spec demands spelled out: `.`, classes and `\d \w \s` as sets of code points,
// `$` as the end of the input, `\b \B` as ASCII word boundaries. The `i` flag is done here,
// never by the engine: it folds ASCII letters only (RangeSet.addAsciiCases, the one place of the
// rule).
//
// The pattern is read in one pass, without recursion, and each instruction that jumps says how
// far (not where to), so a piece of the program can be copied or moved as it is: a quantifier
// wraps or repeats the instructions of the atom before it.
//
// The limits of §11 are checked here, while the pattern is read (Measure): they bound the program
// (at most five instructions per unit of the pattern's size), so the engine refuses nothing.

import * as CodePoints from "../values/codePoints.js";

/** The largest count of a quantifier, and of the counts of nested quantifiers multiplied. */
export const MaxRepeat = 1000;

/** The largest size of a pattern (§9.10): its items, counted repetitions written out. */
export const MaxSize = 10000;

/** The most groups open at once. */
export const MaxNesting = 100;

/** The most capturing groups. */
export const MaxGroups = 1000;

/** The reason of a pattern beyond MaxSize, MaxNesting or MaxGroups: a fixed text (§10.3). */
export const TooLarge = "the pattern is too large";

/** The flags of §9.10 that change the pattern itself. */
export interface Options {
  readonly extended: boolean;
  readonly dotAll: boolean;
  readonly ignoreCase: boolean;
}

/** The instructions of a program; `a` and `b` are their operands (see regexEngine.ts). */
export const enum Op {
  /** The code point `a`. */
  Char,
  /** A code point of the set `a` of the program. */
  Set,
  /** Any code point. */
  Any,
  /** Any code point but U+000A. */
  AnyButLineFeed,
  /** Go on `a` instructions further; if that fails, `b` instructions further. */
  Split,
  /** Go on `a` instructions further. */
  Jump,
  /** Store the position in slot `a` (2g: the start of group g; 2g + 1: its end; group 0 is the match). */
  Save,
  /** The start of the input. */
  Begin,
  /** The end of the input. */
  End,
  /** An ASCII word boundary. */
  WordBoundary,
  /** Not an ASCII word boundary. */
  NotWordBoundary,
  /** Store the position in mark `a`: where an iteration of a repetition starts. */
  Mark,
  /** Go on `b` instructions further when the position is the one of mark `a` (an iteration that matched nothing ends its repetition); else to the next instruction. */
  EmptyExit,
  /** The pattern matched. */
  Match,
}

/** A translated pattern: the program, its sets, the number of its marks and one name (or null) per capturing group. */
export class Translation {
  constructor(
    readonly ops: Int32Array,
    readonly as: Int32Array,
    readonly bs: Int32Array,
    readonly sets: readonly Int32Array[],
    readonly marks: number,
    readonly groups: readonly (string | null)[],
  ) {}
}

// What precedes a quantifier: only an atom may be repeated.
const enum Previous {
  None,
  Atom,
  Quantifier,
  Anchor,
}

// An open group, or the whole pattern: where it starts, where its current alternative starts, the
// jumps of its earlier alternatives to its end, and whether it can match nothing (for the loops
// around it): `before` is whether every item of the current alternative but the last can, `last`
// whether the last can (null: no item), `any` whether an earlier alternative can.
class Frame {
  alternative: number;
  readonly jumps: number[] = [];
  before = true;
  last: boolean | null = null;
  any = false;

  constructor(
    readonly start: number,
    readonly group: number,
  ) {
    this.alternative = start;
  }

  add(nullable: boolean): void {
    this.before = this.before && this.last !== false;
    this.last = nullable;
  }

  // Ends the current alternative.
  close(): void {
    this.any = this.any || (this.before && this.last !== false);
    this.before = true;
    this.last = null;
  }
}

/** The failure of a translation: the reason. */
export class RegexError {
  constructor(readonly reason: string) {}
}

const Backslash = 0x5c;

/** Translates `pattern`: the program, or why the pattern is outside the dialect. */
export function tryTranslate(pattern: string, options: Options): Translation | RegexError {
  return new Translator(decode(pattern), options).translate();
}

class Translator {
  private readonly ops: number[] = [];
  private readonly as: number[] = [];
  private readonly bs: number[] = [];
  private readonly sets: Int32Array[] = [];
  private readonly groups: (string | null)[] = [];
  private readonly frames: Frame[] = [];
  private readonly measure = new Measure();
  private marks = 0;
  private i = 0;
  private previous = Previous.None;
  // Where the instructions of the last atom start.
  private atom = 0;
  private reason = "";
  // The code point tryEscapedLiteral and tryClassLiteral read; the number tryNumber read; the
  // number of items of the class tryClass read.
  private literalRead = 0;
  private numberRead = 0;
  private itemsRead = 0;

  constructor(
    private readonly points: readonly number[],
    private readonly options: Options,
  ) {}

  translate(): Translation | RegexError {
    const points = this.points;
    const options = this.options;
    const measure = this.measure;
    this.emit(Op.Save, 0);
    let frame = new Frame(this.ops.length, 0);
    this.frames.push(frame);
    while (this.i < points.length) {
      const c = points[this.i]!;
      if (options.extended && (isExtendedSpace(c) || c === 0x23) /* # */) {
        this.i = skipExtended(points, this.i);
        continue;
      }

      switch (c) {
        case Backslash:
          if (!this.tryEscape()) return new RegexError(this.reason);
          if (!measure.add(1)) return new RegexError(TooLarge);
          continue;
        case 0x5b /* [ */:
          if (!this.tryClass()) return new RegexError(this.reason);
          if (!measure.add(this.itemsRead)) return new RegexError(TooLarge);
          this.previous = Previous.Atom;
          continue;
        case 0x2e /* . */:
          this.startAtom();
          this.emit(options.dotAll ? Op.Any : Op.AnyButLineFeed);
          if (!measure.add(1)) return new RegexError(TooLarge);
          this.i++;
          continue;
        case 0x5e /* ^ */:
          this.anchor(Op.Begin);
          if (!measure.add(1)) return new RegexError(TooLarge);
          this.i++;
          continue;
        case 0x24 /* $ */:
          this.anchor(Op.End);
          if (!measure.add(1)) return new RegexError(TooLarge);
          this.i++;
          continue;
        case 0x7c /* | */: {
          // The alternative so far, or the rest: it ends with a jump to the end of the group.
          const next = this.ops.length - frame.alternative + 2;
          this.insert(frame.alternative, Op.Split, 1, next);
          frame.jumps.push(this.ops.length);
          this.emit(Op.Jump);
          frame.alternative = this.ops.length;
          frame.close();
          if (!measure.add(1)) return new RegexError(TooLarge);
          this.previous = Previous.None;
          this.i++;
          continue;
        }
        case 0x28 /* ( */: {
          if (measure.depth === MaxNesting) return new RegexError(TooLarge);
          const start = this.ops.length;
          if (!this.tryGroup()) return new RegexError(this.reason);
          frame = new Frame(start, this.ops.length > start ? this.groups.length : 0);
          frame.alternative = this.ops.length;
          this.frames.push(frame);
          if (!measure.open()) return new RegexError(TooLarge);
          this.previous = Previous.None;
          continue;
        }
        case 0x29 /* ) */: {
          if (this.frames.length === 1) return new RegexError("unmatched close parenthesis");
          measure.close();
          this.end(frame);
          if (frame.group > 0) this.emit(Op.Save, 2 * frame.group + 1);
          this.frames.pop();
          const group = frame;
          frame = this.frames[this.frames.length - 1]!;
          frame.add(group.any);
          this.atom = group.start;
          this.previous = Previous.Atom;
          this.i++;
          continue;
        }
        case 0x2a /* * */:
        case 0x2b /* + */:
        case 0x3f /* ? */:
        case 0x7b /* { */:
          if (this.previous !== Previous.Atom) return new RegexError(this.previous === Previous.Quantifier ? "nested quantifier" : "quantifier without an operand");
          if (!this.tryQuantifier(frame)) return new RegexError(this.reason);
          this.previous = Previous.Quantifier;
          continue;
        case 0x5d /* ] */:
        case 0x7d /* } */:
          return new RegexError(`unescaped '${String.fromCharCode(c)}'`);
        default:
          this.literal(c);
          if (!measure.add(1)) return new RegexError(TooLarge);
          this.i++;
          continue;
      }
    }

    if (this.frames.length > 1) return new RegexError("missing close parenthesis");
    this.end(frame);
    this.emit(Op.Save, 1);
    this.emit(Op.Match);
    return new Translation(Int32Array.from(this.ops), Int32Array.from(this.as), Int32Array.from(this.bs), this.sets, this.marks, this.groups);
  }

  // The end of a group or of the pattern: the jumps of its alternatives arrive here.
  private end(frame: Frame): void {
    for (const jump of frame.jumps) this.as[jump] = this.ops.length - jump;
    frame.close();
  }

  private emit(op: Op, a: number = 0, b: number = 0): void {
    this.ops.push(op);
    this.as.push(a);
    this.bs.push(b);
  }

  private insert(at: number, op: Op, a: number, b: number): void {
    this.ops.splice(at, 0, op);
    this.as.splice(at, 0, a);
    this.bs.splice(at, 0, b);
  }

  // An atom starts here: a quantifier after it repeats the instructions from here on.
  private startAtom(): void {
    this.atom = this.ops.length;
    this.previous = Previous.Atom;
    this.frames[this.frames.length - 1]!.add(false);
  }

  private anchor(op: Op): void {
    this.emit(op);
    this.previous = Previous.Anchor;
    this.frames[this.frames.length - 1]!.add(true);
  }

  // \x outside a class: a literal, a shorthand class or an anchor.
  private tryEscape(): boolean {
    const points = this.points;
    if (this.i + 1 === points.length) return this.fail("trailing backslash");
    const c = points[this.i + 1]!;
    switch (c) {
      case 0x62 /* b */:
      case 0x42 /* B */:
        this.anchor(c === 0x62 ? Op.WordBoundary : Op.NotWordBoundary);
        this.i += 2;
        return true;
      case 0x64 /* d */:
      case 0x77 /* w */:
      case 0x73 /* s */:
      case 0x44 /* D */:
      case 0x57 /* W */:
      case 0x53 /* S */: {
        const set = new RangeSet();
        set.addShorthand(c);
        this.startAtom();
        this.class(set, false);
        this.i += 2;
        return true;
      }
    }

    if (!this.tryEscapedLiteral()) return false;
    this.literal(this.literalRead);
    return true;
  }

  // The escapes that stand for one code point: the metacharacters, \t \n \r \f \v and \xHH.
  // Reads the escape at i (which moves past it) into literalRead.
  private tryEscapedLiteral(): boolean {
    const points = this.points;
    const i = this.i;
    if (i + 1 === points.length) return this.fail("trailing backslash");
    const c = points[i + 1]!;
    switch (c) {
      case 0x5c: // \ . * + ? ( ) [ ] { } | ^ $ / -
      case 0x2e:
      case 0x2a:
      case 0x2b:
      case 0x3f:
      case 0x28:
      case 0x29:
      case 0x5b:
      case 0x5d:
      case 0x7b:
      case 0x7d:
      case 0x7c:
      case 0x5e:
      case 0x24:
      case 0x2f:
      case 0x2d:
        this.literalRead = c;
        break;
      case 0x74 /* t */:
        this.literalRead = 0x09;
        break;
      case 0x6e /* n */:
        this.literalRead = 0x0a;
        break;
      case 0x72 /* r */:
        this.literalRead = 0x0d;
        break;
      case 0x66 /* f */:
        this.literalRead = 0x0c;
        break;
      case 0x76 /* v */:
        this.literalRead = 0x0b;
        break;
      case 0x78 /* x */: {
        const high = i + 2 < points.length ? hexValue(points[i + 2]!) : -1;
        const low = i + 3 < points.length ? hexValue(points[i + 3]!) : -1;
        if (high < 0 || low < 0) return this.fail("\\x must be followed by two hex digits");
        this.literalRead = high * 16 + low;
        this.i += 4;
        return true;
      }
      default:
        return this.fail(`invalid escape \\${CodePoints.toText(c)}`);
    }

    this.i += 2;
    return true;
  }

  // ( (?: or (?<name>: a capturing group starts with the instruction that stores its start.
  private tryGroup(): boolean {
    const points = this.points;
    const i = this.i;
    if (i + 1 < points.length && points[i + 1] === 0x3f /* ? */) {
      if (i + 2 < points.length && points[i + 2] === 0x3a /* : */) {
        this.i += 3;
        return true;
      }

      if (i + 2 < points.length && points[i + 2] === 0x3c /* < */) {
        const start = i + 3;
        let end = start;
        while (end < points.length && points[end] !== 0x3e /* > */) end++;
        if (end === points.length || !isGroupName(points, start, end)) return this.fail("invalid group name");
        const name = text(points, start, end);
        if (this.groups.includes(name)) return this.fail(`duplicate group name ${name}`);
        if (this.groups.length === MaxGroups) return this.fail(TooLarge);
        this.groups.push(name);
        this.emit(Op.Save, 2 * this.groups.length);
        this.i = end + 1;
        return true;
      }

      return this.fail("unsupported group syntax (?");
    }

    if (this.groups.length === MaxGroups) return this.fail(TooLarge);
    this.groups.push(null);
    this.emit(Op.Save, 2 * this.groups.length);
    this.i++;
    return true;
  }

  // * + ? {n} {n,} {n,m}, each optionally followed by ? (lazy).
  private tryQuantifier(frame: Frame): boolean {
    const points = this.points;
    const c = points[this.i]!;
    let min: number;
    // -1: no maximum.
    let max: number;
    // The count of §9.10: how many times the operand is written out.
    let count = 1;
    if (c === 0x7b /* { */) {
      let at = this.i + 1;
      at = this.tryNumber(at);
      if (at < 0) return this.fail("'{' is not a quantifier");
      min = this.numberRead;
      max = min;
      let unbounded = false;
      if (at < points.length && points[at] === 0x2c /* , */) {
        at++;
        if (at < points.length && points[at] === 0x7d /* } */) {
          unbounded = true;
        } else {
          at = this.tryNumber(at);
          if (at < 0) return this.fail("'{' is not a quantifier");
          max = this.numberRead;
        }
      }

      if (at === points.length || points[at] !== 0x7d /* } */) return this.fail("'{' is not a quantifier");
      if (min > MaxRepeat || (!unbounded && (max > MaxRepeat || max < min))) return this.fail("invalid repetition count");
      count = Math.max(unbounded ? min : max, 1);
      if (!this.measure.multiply(count)) return this.fail("invalid repetition count");
      if (unbounded) max = -1;
      this.i = at + 1;
    } else {
      min = c === 0x2b /* + */ ? 1 : 0;
      max = c === 0x3f /* ? */ ? 1 : -1;
      this.i++;
    }

    if (!this.measure.repeat(count)) return this.fail(TooLarge);
    if (this.options.extended) {
      while (this.i < points.length && (isExtendedSpace(points[this.i]!) || points[this.i] === 0x23) /* # */) this.i = skipExtended(points, this.i);
    }

    let lazy = false;
    if (this.i < points.length && points[this.i] === 0x3f /* ? */) {
      lazy = true;
      this.i++;
    }

    return this.repeat(frame, min, max, lazy);
  }

  // Repeats the last atom: its instructions min times, then a loop (no maximum) or max - min
  // optional copies. Each copy is an iteration. When the atom can match nothing and there can be
  // several iterations, each marks where it starts, and one that matched nothing ends the
  // repetition, whatever its counts: `"ca" | match("(c??){2}a")` captures "" at 1, as jq.
  private repeat(frame: Frame, min: number, max: number, lazy: boolean): boolean {
    const start = this.atom;
    const length = this.ops.length - start;
    const nullable = frame.last !== false;
    const marked = nullable && (max < 0 || max > 1);
    const iteration = length + (marked ? 2 : 0);
    const size = min * iteration + (max < 0 ? iteration + 2 : (max - min) * (iteration + 1));

    const ops = this.ops.splice(start, length);
    const as = this.as.splice(start, length);
    const bs = this.bs.splice(start, length);
    const end = start + size;
    const mark = marked ? this.marks++ : 0;
    const copy = (): void => {
      if (marked) this.emit(Op.Mark, mark);
      for (let k = 0; k < length; k++) this.emit(ops[k]!, as[k]!, bs[k]!);
      if (marked) this.emit(Op.EmptyExit, mark, end - this.ops.length);
    };

    for (let n = 0; n < min; n++) copy();
    if (max < 0) {
      // again: split (iteration, end); iteration; jump again; end:
      const again = this.ops.length;
      this.emit(Op.Split, lazy ? end - again : 1, lazy ? 1 : end - again);
      copy();
      this.emit(Op.Jump, again - this.ops.length);
    } else {
      // split (iteration, end); iteration; split (iteration, end); iteration; …; end:
      for (let n = max - min; n > 0; n--) {
        const out = end - this.ops.length;
        this.emit(Op.Split, lazy ? out : 1, lazy ? 1 : out);
        copy();
      }
    }

    frame.last = min === 0 ? true : nullable;
    return true;
  }

  // The number at `at`, into numberRead: the index after it, or -1 when there is none.
  private tryNumber(at: number): number {
    const points = this.points;
    let value = 0;
    const start = at;
    while (at < points.length && points[at]! >= 0x30 && points[at]! <= 0x39) {
      value = Math.min(value * 10 + (points[at]! - 0x30), MaxRepeat + 1);
      at++;
    }

    this.numberRead = value;
    return at > start ? at : -1;
  }

  // [...] or [^...]: literals, escapes, ranges of literals and shorthands; itemsRead is their number.
  private tryClass(): boolean {
    const points = this.points;
    let items = 0;
    let at = this.i + 1;
    const negated = at < points.length && points[at] === 0x5e; /* ^ */
    if (negated) at++;
    const first = at;
    const set = new RangeSet();
    let previousLiteral = -1;
    while (true) {
      if (at === points.length) return this.fail("missing ']'");
      const c = points[at]!;
      if (c === 0x5d /* ] */) break;
      if (c === 0x5b /* [ */) return this.fail("unescaped '[' in a class");

      if (c === 0x2d /* - */ && at !== first && at + 1 < points.length && points[at + 1] !== 0x5d) {
        // A range: the item before must be a single literal, the one after a literal too.
        if (previousLiteral < 0) return this.fail("'-' must be first, last, escaped or between two literals");
        at = this.tryClassLiteral(at + 1);
        if (at < 0) return false;
        const high = this.literalRead;
        if (high < previousLiteral) return this.fail("range out of order");
        set.add(previousLiteral, high);
        previousLiteral = -1;
        continue;
      }

      if (c === Backslash && at + 1 < points.length && isShorthand(points[at + 1]!)) {
        set.addShorthand(points[at + 1]!);
        previousLiteral = -1;
        items++;
        at += 2;
        continue;
      }

      at = this.tryClassLiteral(at);
      if (at < 0) return false;
      set.add(this.literalRead, this.literalRead);
      previousLiteral = this.literalRead;
      items++;
    }

    if (at === first) return this.fail("empty class");
    // The i flag folds before a negation applies: [^a] matches neither a nor A.
    if (this.options.ignoreCase) set.addAsciiCases();
    this.startAtom();
    this.class(set, negated);
    this.itemsRead = items;
    this.i = at + 1;
    return true;
  }

  // The literal of a class at `at`, into literalRead: the index after it, or -1 after a failure.
  private tryClassLiteral(at: number): number {
    const literal = this.points[at]!;
    if (literal === Backslash) {
      const i = this.i;
      this.i = at;
      const ok = this.tryEscapedLiteral();
      const next = this.i;
      this.i = i;
      return ok ? next : -1;
    }

    if (literal === 0x5b || literal === 0x5d) {
      this.fail(`unescaped '${String.fromCharCode(literal)}' in a class`);
      return -1;
    }

    this.literalRead = literal;
    return at + 1;
  }

  // A class as a set of the program: a negated class is its complement.
  private class(set: RangeSet, negated: boolean): void {
    const ranges = (negated ? set.complement() : set).ranges;
    this.sets.push(Int32Array.from(ranges));
    this.emit(Op.Set, this.sets.length - 1);
  }

  // A code point; under the i flag an ASCII letter becomes the class of its two cases.
  private literal(c: number): void {
    this.startAtom();
    if (this.options.ignoreCase && ((c >= 0x41 && c <= 0x5a) || (c >= 0x61 && c <= 0x7a))) {
      const set = new RangeSet();
      set.add(c, c);
      set.addAsciiCases();
      this.class(set, false);
      return;
    }

    this.emit(Op.Char, c);
  }

  private fail(reason: string): boolean {
    this.reason = reason;
    return false;
  }
}

// The limits of §11 on the part of a pattern read so far: its size, the groups open, and the
// counts of the counted quantifiers nested in one another.
class Measure {
  // Per open group: the size when it opened.
  private readonly starts: number[] = [];
  // For the pattern and per open group: the largest product of the counts nested in it so far.
  private readonly products: number[] = [1];
  private size = 0;
  // The last item, the operand of a quantifier: its size and the largest product of the counts nested in it.
  private atom = 0;
  private atomProduct = 1;

  /** The groups open. */
  get depth(): number {
    return this.starts.length;
  }

  /** An item of this size (a literal, a class, an anchor, a '|'); false when the pattern is too large. */
  add(itemSize: number): boolean {
    this.atom = itemSize;
    this.atomProduct = 1;
    this.size += itemSize;
    return this.size <= MaxSize;
  }

  /** A group opens: it counts 1; false when the pattern is too large. */
  open(): boolean {
    this.starts.push(this.size);
    this.products.push(1);
    return this.add(1);
  }

  /** A group closes: it is the last item, with all it holds. */
  close(): void {
    this.atom = this.size - this.starts.pop()!;
    this.atomProduct = this.products.pop()!;
    const outer = this.products.length - 1;
    this.products[outer] = Math.max(this.products[outer]!, this.atomProduct);
  }

  /** A counted quantifier on the last item; false when the nested counts multiply above MaxRepeat. */
  multiply(count: number): boolean {
    const product = count * this.atomProduct;
    const outer = this.products.length - 1;
    this.products[outer] = Math.max(this.products[outer]!, product);
    return product <= MaxRepeat;
  }

  /** A quantifier on the last item: it counts 1, and the item once more for each count above 1; false when the pattern is too large. */
  repeat(count: number): boolean {
    this.size += this.atom * (count - 1) + 1;
    return this.size <= MaxSize;
  }
}

function isShorthand(c: number): boolean {
  return c === 0x64 || c === 0x77 || c === 0x73 || c === 0x44 || c === 0x57 || c === 0x53;
}

// With the x flag: whitespace and a # comment up to the next line feed.
function skipExtended(points: readonly number[], i: number): number {
  if (points[i] !== 0x23 /* # */) return i + 1;
  while (i < points.length && points[i] !== 0x0a) i++;
  return i;
}

function isExtendedSpace(c: number): boolean {
  return (c >= 0x09 && c <= 0x0d) || c === 0x20;
}

function isGroupName(points: readonly number[], start: number, end: number): boolean {
  if (end === start || (points[start]! >= 0x30 && points[start]! <= 0x39)) return false;
  for (let i = start; i < end; i++) {
    const c = points[i]!;
    if (!((c >= 0x61 && c <= 0x7a) || (c >= 0x41 && c <= 0x5a) || (c >= 0x30 && c <= 0x39) || c === 0x5f)) return false;
  }

  return true;
}

function hexValue(c: number): number {
  return c >= 0x30 && c <= 0x39 ? c - 0x30 : c >= 0x61 && c <= 0x66 ? c - 0x61 + 10 : c >= 0x41 && c <= 0x46 ? c - 0x41 + 10 : -1;
}

// The code points of a well-formed string.
function decode(source: string): number[] {
  const points: number[] = [];
  for (let i = 0; i < source.length; i++) {
    const c = source.codePointAt(i)!;
    if (c > 0xffff) i++;
    points.push(c);
  }

  return points;
}

function text(points: readonly number[], start: number, end: number): string {
  let result = "";
  for (let i = start; i < end; i++) result += CodePoints.toText(points[i]!);
  return result;
}

/** A set of code points as sorted, disjoint, non-adjacent ranges. */
class RangeSet {
  // Pairs: low, high, low, high, …
  private pairs: number[] = [];
  private normalized = true;

  /** The ranges, as pairs low, high in ascending order. */
  get ranges(): readonly number[] {
    this.normalize();
    return this.pairs;
  }

  add(low: number, high: number): void {
    this.pairs.push(low, high);
    this.normalized = false;
  }

  // The ASCII classes of §9.10 and their complements.
  addShorthand(c: number): void {
    const set = new RangeSet();
    switch (c) {
      case 0x64 /* d */:
      case 0x44 /* D */:
        set.add(0x30, 0x39);
        break;
      case 0x77 /* w */:
      case 0x57 /* W */:
        set.add(0x30, 0x39);
        set.add(0x41, 0x5a);
        set.add(0x61, 0x7a);
        set.add(0x5f, 0x5f);
        break;
      default:
        set.add(0x09, 0x0d);
        set.add(0x20, 0x20);
        break;
    }

    const ranges = c === 0x44 || c === 0x57 || c === 0x53 ? set.complement().ranges : set.ranges;
    for (let i = 0; i < ranges.length; i += 2) this.add(ranges[i]!, ranges[i + 1]!);
  }

  /**
   * The i flag of §9.10: adds the other case of every ASCII letter in the set (A-Z and a-z, 32
   * apart); no other code point folds.
   */
  addAsciiCases(): void {
    const added: number[] = [];
    const ranges = this.ranges;
    for (let i = 0; i < ranges.length; i += 2) {
      const low = ranges[i]!;
      const high = ranges[i + 1]!;
      const upperLow = Math.max(low, 0x41);
      const upperHigh = Math.min(high, 0x5a);
      if (upperLow <= upperHigh) added.push(upperLow + 32, upperHigh + 32);
      const lowerLow = Math.max(low, 0x61);
      const lowerHigh = Math.min(high, 0x7a);
      if (lowerLow <= lowerHigh) added.push(lowerLow - 32, lowerHigh - 32);
    }

    for (let i = 0; i < added.length; i += 2) this.add(added[i]!, added[i + 1]!);
  }

  complement(): RangeSet {
    const complement = new RangeSet();
    let next = 0;
    const ranges = this.ranges;
    for (let i = 0; i < ranges.length; i += 2) {
      if (ranges[i]! > next) complement.add(next, ranges[i]! - 1);
      next = ranges[i + 1]! + 1;
    }

    if (next <= CodePoints.Max) complement.add(next, CodePoints.Max);
    return complement;
  }

  private normalize(): void {
    if (this.normalized) return;
    const ranges: [number, number][] = [];
    for (let i = 0; i < this.pairs.length; i += 2) ranges.push([this.pairs[i]!, this.pairs[i + 1]!]);
    ranges.sort((a, b) => a[0] - b[0]);
    const merged: number[] = [];
    for (const [low, high] of ranges) {
      const last = merged.length - 1;
      if (last > 0 && low <= merged[last]! + 1) merged[last] = Math.max(merged[last]!, high);
      else merged.push(low, high);
    }

    this.pairs = merged;
    this.normalized = true;
  }
}
