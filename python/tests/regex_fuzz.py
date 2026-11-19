"""Random patterns of the flavor of §9.10 and random inputs for them, from a seed: the same seed
gives the same cases on every run (RegexFuzz.cs)."""

from __future__ import annotations

_ATOMS = ["a", "a", "b", "b", "c", ".", "[ab]", "[^a]", "\\w", "\\s"]
_ANCHORS = ["^", "$", "\\b", "\\B"]
_QUANTIFIERS = ["*", "+", "?", "*?", "+?", "??", "{2}", "{0,2}", "{1,}", "{1,2}?", "{2,}?"]
_LETTERS = ["a", "a", "b", "b", "c", " ", "\U0001F600"]


class RegexFuzz:
    def __init__(self, seed: int) -> None:
        self._state = seed

    def next(self, n: int) -> int:
        """A number 0 .. n - 1 (a linear congruential generator, exact in 32 bits)."""
        self._state = (self._state * 1664525 + 1013904223) & 0xFFFFFFFF
        return (self._state * n) >> 32

    def pattern(self, depth: int) -> str:
        """A pattern: alternatives of sequences of atoms, groups and anchors, with quantifiers;
        `depth` bounds the nesting of groups."""
        text: list[str] = []
        alternatives = 1 + (1 + self.next(2) if self.next(4) == 0 else 0)
        for a in range(alternatives):
            if a > 0:
                text.append("|")
            for _ in range(self.next(4)):
                kind = self.next(10)
                if kind == 0:
                    text.append(_ANCHORS[self.next(len(_ANCHORS))])
                    continue
                if kind <= 3 and depth > 0:
                    text.append("(?:" if self.next(3) == 0 else "(")
                    text.append(self.pattern(depth - 1))
                    text.append(")")
                else:
                    text.append(_ATOMS[self.next(len(_ATOMS))])
                if self.next(2) == 0:
                    text.append(_QUANTIFIERS[self.next(len(_QUANTIFIERS))])
        return "".join(text)

    def input(self, length: int) -> str:
        """An input of at most `length` code points."""
        return "".join(_LETTERS[self.next(len(_LETTERS))] for _ in range(self.next(length + 1)))
