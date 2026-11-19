package builtins

import (
	"unicode/utf8"

	"github.com/devlooped/jqlite/go/internal/runtime"
	"github.com/devlooped/jqlite/go/internal/values"
)

type regexNatives struct{}

// RegexNatives are §9.10 test/2 and match/2: the flags, the search loop and the match objects, with
// offsets and lengths in code points. The pattern goes through RegexSyntax to the host engine
// (RegexEngine), which gives byte offsets.
var RegexNatives regexNatives

func (regexNatives) Register() {
	add("test", 2, test)
	addC("match", 2, match)
}

// search is a search ready to run: the input, the compiled pattern and flags.
type search struct {
	input     string
	pattern   *HostPattern
	groups    []string
	global    bool
	skipEmpty bool
}

func test(_ *interpreter, input value, arguments []value) (value, value, bool) {
	s, err, prepared := tryPrepare(input, arguments[0], arguments[1])
	if !prepared {
		return fail(err)
	}
	found := false
	run(s, func([]int, *codePointMap) bool {
		found = true
		return false
	})
	return ok(values.FromBool(found))
}

// match: match(re; flags): flags outermost, re innermost (re fastest); one output per reported match.
func match(in *interpreter, e *env, arguments []node, input value, p *path, out emit) *signal {
	at := runtime.PathOf(p)
	return in.ForEach(arguments[1], e, input, nil, func(flags value, _ *path) *signal {
		return in.ForEach(arguments[0], e, input, nil, func(re value, _ *path) *signal {
			s, err, prepared := tryPrepare(input, re, flags)
			if !prepared {
				return runtime.Fail(err)
			}
			var stop *signal
			run(s, func(spans []int, m *codePointMap) bool {
				stop = runtime.Output(out, matchObject(s, spans, m), at)
				return stop == nil
			})
			return stop
		})
	})
}

func tryPrepare(input, re, flags value) (*search, value, bool) {
	if input.Kind() != values.KindString {
		return nil, values.Errors.CannotBeMatched(input), false
	}
	if re.Kind() != values.KindString {
		return nil, values.Errors.NotAString(re), false
	}
	if flags.Kind() != values.KindString && flags.Kind() != values.KindNull {
		return nil, values.Errors.NotAString(flags), false
	}

	global, ignoreCase, extended, skipEmpty, dotAll := false, false, false, false, false
	letters := ""
	if flags.Kind() == values.KindString {
		letters = flags.AsString()
	}
	for _, letter := range letters {
		switch letter {
		case 'g':
			global = true
		case 'i':
			ignoreCase = true
		case 'x':
			extended = true
		case 'n':
			skipEmpty = true
		case 'm', 'p':
			dotAll = true
		case 's', 'l':
		default:
			return nil, values.Errors.InvalidModifiers(letters), false
		}
	}

	translation, reason, translated := RegexSyntax.TryTranslate(re.AsString(), RegexOptions{extended, dotAll, ignoreCase})
	if !translated {
		return nil, values.Errors.RegexFailure(reason), false
	}
	pattern, reason, compiled := RegexEngine.TryCompile(translation.Pattern)
	if !compiled {
		return nil, values.Errors.RegexFailure(reason), false
	}
	return &search{input.AsString(), pattern, translation.Groups, global, skipEmpty}, values.Null, true
}

// run is the search loop of §9.10, in code points; report gives false to stop.
func run(s *search, report func([]int, *codePointMap) bool) {
	m := newCodePointMap(s.input)
	spans := make([]int, 2*(len(s.groups)+1))
	for p := 0; p <= m.length; {
		if !RegexEngine.TryMatch(s.pattern, s.input, m.unit(p), spans) {
			return
		}
		start := m.codePoint(spans[0])
		end := m.codePoint(spans[1])
		if !(s.skipEmpty && start == end) && (!report(spans, m) || !s.global) {
			return
		}
		if end > start {
			p = end
		} else {
			p = end + 1
		}
	}
}

func matchObject(s *search, spans []int, m *codePointMap) value {
	captures := make([]value, len(s.groups))
	for g, group := range s.groups {
		name := values.Null
		if group != "" {
			name = values.FromString(group)
		}
		capture := values.NewObjectBuilder(4)
		start, end := spans[2*g+2], spans[2*g+3]
		if start < 0 {
			capture.Set("offset", values.FromNumber(-1))
			capture.Set("string", values.Null)
			capture.Set("length", values.FromNumber(0))
		} else if start == end {
			// An empty group has the key order of an absent one, as jq.
			capture.Set("offset", values.FromNumber(float64(m.codePoint(start))))
			capture.Set("string", values.FromString(""))
			capture.Set("length", values.FromNumber(0))
		} else {
			addSpan(capture, s.input, m, start, end)
		}
		capture.Set("name", name)
		captures[g] = values.FromObject(capture.ToObject())
	}

	result := values.NewObjectBuilder(4)
	addSpan(result, s.input, m, spans[0], spans[1])
	result.Set("captures", values.FromArray(values.ArrayFromOwned(captures)))
	return values.FromObject(result.ToObject())
}

// addSpan sets "offset", "length", "string" of a span given in bytes.
func addSpan(target *values.ObjectBuilder, input string, m *codePointMap, start, end int) {
	offset := m.codePoint(start)
	target.Set("offset", values.FromNumber(float64(offset)))
	target.Set("length", values.FromNumber(float64(m.codePoint(end)-offset)))
	target.Set("string", values.FromString(input[start:end]))
}

// codePointMap maps byte offsets to code point indices and back, for a well-formed string.
type codePointMap struct {
	toCodePoint []int
	toUnit      []int
	length      int
}

func newCodePointMap(text string) *codePointMap {
	m := &codePointMap{length: utf8.RuneCountInString(text)}
	if m.length == len(text) {
		return m
	}
	m.toCodePoint = make([]int, len(text)+1)
	m.toUnit = make([]int, m.length+1)
	codePoint := 0
	for i := 0; i < len(text); i++ {
		m.toCodePoint[i] = codePoint
		if utf8.RuneStart(text[i]) {
			m.toUnit[codePoint] = i
			codePoint++
		}
	}
	m.toCodePoint[len(text)] = m.length
	m.toUnit[m.length] = len(text)
	return m
}

func (m *codePointMap) codePoint(unit int) int {
	if m.toCodePoint == nil {
		return unit
	}
	return m.toCodePoint[unit]
}

func (m *codePointMap) unit(codePoint int) int {
	if m.toUnit == nil {
		return codePoint
	}
	return m.toUnit[codePoint]
}
