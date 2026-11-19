package builtins

import "strings"

// regexFuzz gives random patterns of the flavor of §9.10 and random inputs for them, from a seed:
// the same seed gives the same cases on every run (and the same as the reference's RegexFuzz).
type regexFuzz struct{ state uint32 }

var (
	fuzzAtoms       = []string{"a", "a", "b", "b", "c", ".", "[ab]", "[^a]", `\w`, `\s`}
	fuzzAnchors     = []string{"^", "$", `\b`, `\B`}
	fuzzQuantifiers = []string{"*", "+", "?", "*?", "+?", "??", "{2}", "{0,2}", "{1,}", "{1,2}?", "{2,}?"}
	fuzzLetters     = []string{"a", "a", "b", "b", "c", " ", "\U0001F600"}
)

func newRegexFuzz(seed uint32) *regexFuzz { return &regexFuzz{seed} }

// Next gives a number 0 .. n - 1 (a linear congruential generator, exact in 32 bits).
func (f *regexFuzz) Next(n int) int {
	f.state = f.state*1664525 + 1013904223
	return int(uint64(f.state) * uint64(n) >> 32)
}

// Pattern gives alternatives of sequences of atoms, groups and anchors, with quantifiers; depth
// bounds the nesting of groups.
func (f *regexFuzz) Pattern(depth int) string {
	var text strings.Builder
	alternatives := 1
	if f.Next(4) == 0 {
		alternatives += 1 + f.Next(2)
	}
	for a := 0; a < alternatives; a++ {
		if a > 0 {
			text.WriteByte('|')
		}
		items := f.Next(4)
		for i := 0; i < items; i++ {
			kind := f.Next(10)
			if kind == 0 {
				text.WriteString(fuzzAnchors[f.Next(len(fuzzAnchors))])
				continue
			}

			if kind <= 3 && depth > 0 {
				if f.Next(3) == 0 {
					text.WriteString("(?:")
				} else {
					text.WriteString("(")
				}
				text.WriteString(f.Pattern(depth - 1))
				text.WriteByte(')')
			} else {
				text.WriteString(fuzzAtoms[f.Next(len(fuzzAtoms))])
			}
			if f.Next(2) == 0 {
				text.WriteString(fuzzQuantifiers[f.Next(len(fuzzQuantifiers))])
			}
		}
	}

	return text.String()
}

// Input gives an input of at most length code points.
func (f *regexFuzz) Input(length int) string {
	var text strings.Builder
	for n := f.Next(length + 1); n > 0; n-- {
		text.WriteString(fuzzLetters[f.Next(len(fuzzLetters))])
	}
	return text.String()
}
