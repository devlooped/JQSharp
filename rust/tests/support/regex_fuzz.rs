//! Random patterns of the flavor of spec §9.10 and random inputs for them, from a seed: the same
//! seed gives the same cases on every run.

const ATOMS: [&str; 10] = ["a", "a", "b", "b", "c", ".", "[ab]", "[^a]", r"\w", r"\s"];
const ANCHORS: [&str; 4] = ["^", "$", r"\b", r"\B"];
const QUANTIFIERS: [&str; 11] = ["*", "+", "?", "*?", "+?", "??", "{2}", "{0,2}", "{1,}", "{1,2}?", "{2,}?"];
const LETTERS: [&str; 7] = ["a", "a", "b", "b", "c", " ", "\u{1F600}"];

pub struct RegexFuzz {
    state: u32,
}

impl RegexFuzz {
    pub fn new(seed: u32) -> RegexFuzz {
        RegexFuzz { state: seed }
    }

    /// A number 0 .. n - 1 (a linear congruential generator, exact in 32 bits).
    pub fn next(&mut self, n: usize) -> usize {
        self.state = self.state.wrapping_mul(1664525).wrapping_add(1013904223);
        ((self.state as u64 * n as u64) >> 32) as usize
    }

    /// A pattern: alternatives of sequences of atoms, groups and anchors, with quantifiers; `depth`
    /// bounds the nesting of groups.
    pub fn pattern(&mut self, depth: usize) -> String {
        let mut text = String::new();
        let alternatives = 1 + if self.next(4) == 0 { 1 + self.next(2) } else { 0 };
        for a in 0..alternatives {
            if a > 0 {
                text.push('|');
            }
            let items = self.next(4);
            for _ in 0..items {
                let kind = self.next(10);
                if kind == 0 {
                    text.push_str(ANCHORS[self.next(ANCHORS.len())]);
                    continue;
                }

                if kind <= 3 && depth > 0 {
                    text.push_str(if self.next(3) == 0 { "(?:" } else { "(" });
                    let inner = self.pattern(depth - 1);
                    text.push_str(&inner);
                    text.push(')');
                } else {
                    text.push_str(ATOMS[self.next(ATOMS.len())]);
                }
                if self.next(2) == 0 {
                    text.push_str(QUANTIFIERS[self.next(QUANTIFIERS.len())]);
                }
            }
        }

        text
    }

    /// An input of at most `length` code points.
    pub fn input(&mut self, length: usize) -> String {
        let mut text = String::new();
        for _ in 0..self.next(length + 1) {
            text.push_str(LETTERS[self.next(LETTERS.len())]);
        }
        text
    }
}
