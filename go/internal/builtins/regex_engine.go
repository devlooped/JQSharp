package builtins

// Host-specific (H): the one file of the core that uses a host regex engine, Go's regexp (RE2,
// linear in the input, on code points of UTF-8 text). It compiles a pattern that RegexSyntax
// translated and finds the next match from a position. regexp has no start position, so a search
// from byte offset p > 0 runs on s[q:], q the start of the code point before p, with the pattern
// ^(?s:.)(?s:.*?)(P): one code point of context (so that \b at p sees what precedes it, and ^ in P
// cannot match), then the leftmost match of P at or after p; the group numbers shift by one.
// Compiled patterns are cached per process in a concurrent map that is emptied when it reaches
// cacheLimit entries. A port plugs its own engine in here.

import (
	"regexp"
	"sync"
	"sync/atomic"
	"unicode/utf8"
)

type regexEngine struct{}

// RegexEngine compiles translated patterns and runs them.
var RegexEngine regexEngine

const cacheLimit = 256

var (
	regexCache     sync.Map
	regexCacheSize atomic.Int64
)

// HostPattern is a pattern compiled by the host engine: as written, and with one code point of
// context before it (compiled on first use).
type HostPattern struct {
	regex   *regexp.Regexp
	pattern string
	context atomic.Pointer[regexp.Regexp]
}

// TryCompile gives a compiled pattern, or false with the engine's reason.
func (regexEngine) TryCompile(pattern string) (*HostPattern, string, bool) {
	if compiled, found := regexCache.Load(pattern); found {
		return compiled.(*HostPattern), "", true
	}
	regex, err := regexp.Compile(pattern)
	if err != nil {
		// The boundary of the host engine: its error becomes the dialect's error.
		return nil, err.Error(), false
	}
	if regexCacheSize.Load() >= cacheLimit {
		regexCache.Clear()
		regexCacheSize.Store(0)
	}
	compiled := &HostPattern{regex: regex, pattern: pattern}
	if actual, loaded := regexCache.LoadOrStore(pattern, compiled); loaded {
		return actual.(*HostPattern), "", true
	}
	regexCacheSize.Add(1)
	return compiled, "", true
}

func (p *HostPattern) withContext() *regexp.Regexp {
	if regex := p.context.Load(); regex != nil {
		return regex
	}
	// P compiled, so P inside a group compiles too.
	regex := regexp.MustCompile(`^(?s:.)(?s:.*?)(` + p.pattern + `)`)
	p.context.Store(regex)
	return regex
}

// TryMatch finds the first match starting at or after byte offset start (anchors and word
// boundaries see the whole input). spans receives start and end of the match, then of each group
// (-1, -1 for a group that did not participate): 2 · (groups + 1) entries, byte offsets.
func (regexEngine) TryMatch(compiled *HostPattern, input string, start int, spans []int) bool {
	if start == 0 {
		found := compiled.regex.FindStringSubmatchIndex(input)
		if found == nil {
			return false
		}
		copy(spans, found)
		return true
	}

	_, size := utf8.DecodeLastRuneInString(input[:start])
	q := start - size
	found := compiled.withContext().FindStringSubmatchIndex(input[q:])
	if found == nil {
		return false
	}
	// Group 1 is P's match, P's groups follow: drop the whole match and shift back to the input.
	for g := 0; g < len(spans); g++ {
		if found[g+2] < 0 {
			spans[g] = -1
		} else {
			spans[g] = found[g+2] + q
		}
	}
	return true
}
