// Random patterns of the dialect of §9.10 and random inputs for them, from a seed: the same
// seed gives the same cases on every run.

/** A generator of the numbers 0 .. n - 1 (a linear congruential generator, exact in 32 bits). */
export function random(seed: number): (n: number) => number {
  let state = seed >>> 0;
  return (n) => {
    state = (Math.imul(state, 1664525) + 1013904223) >>> 0;
    return Math.floor((state / 0x100000000) * n);
  };
}

const Atoms = ["a", "a", "b", "b", "c", ".", "[ab]", "[^a]", "\\w", "\\s"];
const Anchors = ["^", "$", "\\b", "\\B"];
const Quantifiers = ["*", "+", "?", "*?", "+?", "??", "{2}", "{0,2}", "{1,}", "{1,2}?", "{2,}?"];
const Letters = ["a", "a", "b", "b", "c", " ", "\u{1F600}"];

/** A pattern: alternatives of sequences of atoms, groups and anchors, with quantifiers; `depth` bounds the nesting of groups. */
export function pattern(next: (n: number) => number, depth: number): string {
  let text = "";
  const alternatives = 1 + (next(4) === 0 ? 1 + next(2) : 0);
  for (let a = 0; a < alternatives; a++) {
    if (a > 0) text += "|";
    const items = next(4);
    for (let i = 0; i < items; i++) {
      const kind = next(10);
      if (kind === 0) {
        text += Anchors[next(Anchors.length)]!;
        continue;
      }

      if (kind <= 3 && depth > 0) text += (next(3) === 0 ? "(?:" : "(") + pattern(next, depth - 1) + ")";
      else text += Atoms[next(Atoms.length)]!;
      if (next(2) === 0) text += Quantifiers[next(Quantifiers.length)]!;
    }
  }

  return text;
}

/** An input of at most `length` code points. */
export function input(next: (n: number) => number, length: number): string {
  let text = "";
  for (let n = next(length + 1); n > 0; n--) text += Letters[next(Letters.length)]!;
  return text;
}
