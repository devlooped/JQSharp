package jqlite_test

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"sync"
	"testing"
	"testing/fstest"
	"time"

	"github.com/devlooped/jqlite/go"
	"github.com/devlooped/jqlite/go/internal/corpus"
)

// The Go boundary: options, errors, streaming, variables and value conversion, depth and
// concurrency through the public API.

func value(t testing.TB, text string) jqlite.Value {
	t.Helper()
	v, err := jqlite.ParseJSON(text)
	if err != nil {
		t.Fatal(err)
	}
	return v
}

func texts(results []jqlite.Value) []string {
	var out []string
	for _, r := range results {
		out = append(out, jqlite.ToJSON(r))
	}
	return out
}

func evaluate(t testing.TB, expression *jqlite.Expression, input string, variables map[string]jqlite.Value) []string {
	t.Helper()
	results, err := expression.Evaluate(value(t, input), variables)
	if err != nil {
		t.Fatal(err)
	}
	return texts(results)
}

func parse(t testing.TB, program string, options jqlite.Options) *jqlite.Expression {
	t.Helper()
	expression, err := jqlite.ParseWithOptions(program, options)
	if err != nil {
		t.Fatal(err)
	}
	return expression
}

func same(t *testing.T, expected, actual []string) {
	t.Helper()
	if !slices.Equal(expected, actual) {
		t.Errorf("expected %q, got %q", expected, actual)
	}
}

func TestOptionsEnvironmentIsENVAndEnv(t *testing.T) {
	expression := parse(t, "$ENV, env.A", jqlite.Options{Environment: []string{"B=2", "A=1"}})
	same(t, []string{`{"B":"2","A":"1"}`, `"1"`}, evaluate(t, expression, "null", nil))
}

func TestWithoutOptionsENVIsTheProcessEnvironment(t *testing.T) {
	for _, entry := range os.Environ() {
		name, v, _ := strings.Cut(entry, "=")
		if name == "" || strings.ContainsFunc(name, func(r rune) bool {
			return !(r == '_' || (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9'))
		}) || (name[0] >= '0' && name[0] <= '9') {
			continue
		}
		same(t, []string{jqlite.ToJSON(jqlite.String(v))}, evaluate(t, jqlite.MustParse("$ENV."+name), "null", nil))
		return
	}
}

func TestENVHasKeys(t *testing.T) {
	same(t, []string{"true"}, evaluate(t, jqlite.MustParse("($ENV | keys | length) > 0"), "null", nil))
}

func TestOptionsGiveNowAndTheLocalZone(t *testing.T) {
	options := jqlite.Options{
		Now:      func() time.Time { return time.Date(2015, 3, 5, 23, 51, 47, 0, time.UTC) },
		Location: time.FixedZone("", -3*60*60),
	}
	expression := parse(t, `now, (now | strflocaltime("%Y-%m-%dT%H:%M:%S %z %Z"))`, options)
	same(t, []string{"1425599507", `"2015-03-05T20:51:47 -0300 -03"`}, evaluate(t, expression, "null", nil))
}

func TestOptionsLocationFollowsDaylightSavingTime(t *testing.T) {
	zone, err := time.LoadLocation("Europe/Paris")
	if err != nil {
		t.Skip("no time zone database: ", err)
	}
	expression := parse(t, `(1420070400, 1435708800) | strflocaltime("%z %Z")`, jqlite.Options{Location: zone})
	// January: +01:00; July: +02:00.
	same(t, []string{`"+0100 CET"`, `"+0200 CEST"`}, evaluate(t, expression, "null", nil))
}

func TestOptionsDebugAndStderrReceiveTheValues(t *testing.T) {
	var debugged, written []string
	options := jqlite.Options{
		Debug:  func(v jqlite.Value) { debugged = append(debugged, jqlite.ToJSON(v)) },
		Stderr: func(v jqlite.Value) { written = append(written, jqlite.ToJSON(v)) },
	}
	same(t, []string{"1", "1", "1"}, evaluate(t, parse(t, `debug, debug("m", {a: .}), stderr`, options), "1", nil))
	same(t, []string{"1", `"m"`, `{"a":1}`}, debugged)
	same(t, []string{"1"}, written)
	same(t, []string{"1", "1"}, evaluate(t, jqlite.MustParse("debug, stderr"), "1", nil))
}

func TestHaltErrorWithoutACodeExitsWith5(t *testing.T) {
	_, err := jqlite.Evaluate("halt_error", value(t, `"bye"`), nil)
	var halt *jqlite.HaltError
	if !errors.As(err, &halt) || halt.Message != "bye" || halt.ExitCode != 5 {
		t.Errorf("%#v", err)
	}
}

func TestOptionsMaxDepthLimitsRecursion(t *testing.T) {
	expression := parse(t, "def f: if . == 0 then 0 else (. - 1 | f) + 1 end; f", jqlite.Options{MaxDepth: 10})
	same(t, []string{"9"}, evaluate(t, expression, "9", nil))
	_, err := expression.Evaluate(jqlite.Number(10), nil)
	if err == nil || err.Error() != "Maximum call depth exceeded" {
		t.Errorf("%v", err)
	}
}

func TestParseErrorsHaveAPosition(t *testing.T) {
	for _, c := range []struct {
		expression, message string
		line, column        int
	}{
		{"1 +", "Unexpected end of program at line 1, column 4", 1, 4},
		{".a |\n  foo", "foo/0 is not defined at line 2, column 3", 2, 3},
		{". as $x | break $x", "$*label-x is not defined at line 1, column 11", 1, 11},
	} {
		_, err := jqlite.Parse(c.expression)
		var parseError *jqlite.ParseError
		if !errors.As(err, &parseError) || parseError.Message != c.message || parseError.Line != c.line || parseError.Column != c.column {
			t.Errorf("%s: %#v", c.expression, err)
		}
	}
}

func TestErrorsCarryTheirMessageAndValue(t *testing.T) {
	for _, c := range []struct{ program, input, message, value string }{
		{`error("boom")`, "null", "boom", `"boom"`},
		{`error({a: [1, nan]})`, "null", `{"a":[1,null]} (not a string)`, `{"a":[1,null]}`},
		{`. + 1`, `"a"`, `string ("a") and number (1) cannot be added`, `"string (\"a\") and number (1) cannot be added"`},
	} {
		_, err := jqlite.Evaluate(c.program, value(t, c.input), nil)
		var jqError *jqlite.Error
		if !errors.As(err, &jqError) || jqError.Message != c.message || jqlite.ToJSON(jqError.Value) != c.value {
			t.Errorf("%s: %#v", c.program, err)
		}
	}
}

func TestStreamingGivesResultsAsTheyComeAndStopsWhenAsked(t *testing.T) {
	var seen []float64
	err := jqlite.MustParse("range(1000000000)").EvaluateFunc(jqlite.Null, nil, func(v jqlite.Value) bool {
		seen = append(seen, v.AsNumber())
		return len(seen) < 3
	})
	if err != nil || !slices.Equal([]float64{0, 1, 2}, seen) {
		t.Errorf("%v %v", err, seen)
	}
}

func TestStreamingDeliversResultsBeforeAnError(t *testing.T) {
	var seen []string
	err := jqlite.MustParse(`1, 2, error("late"), 3`).EvaluateFunc(jqlite.Null, nil, func(v jqlite.Value) bool {
		seen = append(seen, jqlite.ToJSON(v))
		return true
	})
	same(t, []string{"1", "2"}, seen)
	if err == nil || err.Error() != "late" {
		t.Errorf("%v", err)
	}
}

func TestResultsIsARangeOverFunction(t *testing.T) {
	var seen []string
	var last error
	for v, err := range jqlite.MustParse(`1, 2, error("late"), 3`).Results(jqlite.Null, nil) {
		if err != nil {
			last = err
			break
		}
		seen = append(seen, jqlite.ToJSON(v))
	}
	same(t, []string{"1", "2"}, seen)
	if last == nil || last.Error() != "late" {
		t.Errorf("%v", last)
	}

	seen = nil
	for v := range jqlite.MustParse("range(1000000000)").Results(jqlite.Null, nil) {
		seen = append(seen, jqlite.ToJSON(v))
		if len(seen) == 3 {
			break
		}
	}
	same(t, []string{"0", "1", "2"}, seen)
}

func TestAPanicOfTheCallbackPropagates(t *testing.T) {
	defer func() {
		if recover() != "callback" {
			t.Error("the panic did not propagate")
		}
	}()
	_ = jqlite.MustParse("1, 2").EvaluateFunc(jqlite.Null, nil, func(jqlite.Value) bool { panic("callback") })
}

func TestHaltEndsNormally(t *testing.T) {
	same(t, []string{"1"}, evaluate(t, jqlite.MustParse("1, halt, 2"), "null", nil))
}

func TestHaltErrorCarriesTheValueAndTheCode(t *testing.T) {
	var seen []string
	err := jqlite.MustParse("1, ({a: 1} | halt_error(5)), 2").EvaluateFunc(jqlite.Null, nil, func(v jqlite.Value) bool {
		seen = append(seen, jqlite.ToJSON(v))
		return true
	})
	same(t, []string{"1"}, seen)
	var halt *jqlite.HaltError
	if !errors.As(err, &halt) || halt.ExitCode != 5 || jqlite.ToJSON(halt.Value) != `{"a":1}` || halt.Message != `{"a":1} (not a string)` {
		t.Errorf("%#v", err)
	}
	_, err = jqlite.Evaluate(`"bye" | halt_error(0)`, jqlite.Null, nil)
	if !errors.As(err, &halt) || halt.Message != "bye" {
		t.Errorf("%#v", err)
	}
}

func TestInvalidVariableNamesAreRejectedBeforeEvaluation(t *testing.T) {
	for _, name := range []string{"$root", "1x", "a-b", "", "ENV"} {
		_, err := jqlite.Evaluate(`error("evaluated")`, jqlite.Null, map[string]jqlite.Value{name: jqlite.Number(1)})
		var variableError *jqlite.VariableError
		if !errors.As(err, &variableError) || variableError.Name != name {
			t.Errorf("%q: %v", name, err)
		}
	}
}

func TestVariables(t *testing.T) {
	expression := jqlite.MustParse("$x + 1")
	same(t, []string{"2"}, evaluate(t, expression, "null", map[string]jqlite.Value{"x": jqlite.Number(1)}))
	same(t, []string{"11"}, evaluate(t, expression, "null", map[string]jqlite.Value{"x": jqlite.Number(10)}))
	if _, err := expression.Evaluate(jqlite.Null, nil); err == nil || err.Error() != "$x is not defined" {
		t.Errorf("%v", err)
	}
	root := value(t, `{"id":42}`)
	same(t, []string{`{"name":"Alice","id":42}`},
		evaluate(t, jqlite.MustParse("{name: .name, id: $root.id}"), `{"name":"Alice"}`, map[string]jqlite.Value{"root": root}))
	same(t, []string{"2"}, evaluate(t, jqlite.MustParse("2 as $x | $x"), "null", map[string]jqlite.Value{"x": jqlite.Number(1)}))
	same(t, []string{`["A","B"]`}, evaluate(t, jqlite.MustParse("[$a, $b]"), "null",
		map[string]jqlite.Value{"a": jqlite.String("A"), "b": jqlite.String("B")}))
}

func TestValuesCrossTheBoundary(t *testing.T) {
	for _, c := range [][3]string{
		// Non-finite numbers come out as jq prints them; large numbers stay doubles.
		{"[nan, infinite, -infinite]", "null", "[null,1.7976931348623157e+308,-1.7976931348623157e+308]"},
		{".", "100000000000000000000000000001", "1e+29"},
		{".", "1E400", "1.7976931348623157e+308"},
		{". + 1", "9007199254740993", "9007199254740992"},
		{".", "-0", "-0"},
		// A lone surrogate becomes U+FFFD on the way in (spec §4.1).
		{". | explode", `"a\ud800b"`, "[97,65533,98]"},
		{"keys", `{"\udc00":1}`, "[\"�\"]"},
		{"tojson", `{"b":1,"a":2,"b":3}`, `"{\"b\":3,\"a\":2}"`},
	} {
		results := evaluate(t, jqlite.MustParse(c[0]), c[1], nil)
		same(t, []string{c[2]}, results)
	}
	// Ill-formed UTF-8 from Go becomes U+FFFD, one per maximal subpart.
	same(t, []string{"[97,65533,65533,98]"}, texts(must(jqlite.Evaluate("explode", jqlite.String("a\xC0\x80b"), nil))))
}

func must(results []jqlite.Value, err error) []jqlite.Value {
	if err != nil {
		panic(err)
	}
	return results
}

func TestValuesFromAndToAny(t *testing.T) {
	v, err := jqlite.FromAny(map[string]any{"b": []any{1, 2.5, "x", nil, true}, "a": map[string]int{"z": 1}})
	if err != nil {
		t.Fatal(err)
	}
	same(t, []string{`{"a":{"z":1},"b":[1,2.5,"x",null,true]}`}, []string{jqlite.ToJSON(v)})
	back := jqlite.ToAny(v).(map[string]any)
	if back["b"].([]any)[1] != 2.5 || back["a"].(map[string]any)["z"] != 1.0 {
		t.Errorf("%v", back)
	}
	if _, err := jqlite.FromAny(struct{}{}); err == nil {
		t.Error("a struct converted")
	}
	same(t, []string{`{"b":1,"a":2}`}, []string{jqlite.ToJSON(jqlite.NewObject(jqlite.Member{Key: "b", Value: jqlite.Number(1)}, jqlite.Member{Key: "a", Value: jqlite.Number(2)}))})
}

func TestDeepInputAndOutputConvertWithoutRecursion(t *testing.T) {
	text := strings.Repeat("[", 10000) + strings.Repeat("]", 10000)
	same(t, []string{text}, evaluate(t, jqlite.MustParse("."), text, nil))
}

func TestModulesThroughAFileSystem(t *testing.T) {
	files := fstest.MapFS{
		"lib/greet.jq":      {Data: []byte(`import "sub/helper" as h; def greet: "hello " + h::name;`)},
		"lib/sub/helper.jq": {Data: []byte(`def name: "world";`)},
		"lib/data.json":     {Data: []byte(`{"a":1} {"a":2}`)},
	}
	resolver := &jqlite.FSResolver{FS: files, Root: "lib"}
	expression, err := jqlite.ParseWithResolver(`import "greet" as g; import "data" as $d; g::greet, ($d | map(.a)), ("greet" | modulemeta | .deps[0].relpath)`, resolver)
	if err != nil {
		t.Fatal(err)
	}
	same(t, []string{`"hello world"`, "[1,2]", `"sub/helper"`}, evaluate(t, expression, "null", nil))

	_, err = jqlite.ParseWithResolver(`import "nope" as n; 1`, resolver)
	if err == nil || err.Error() != "module not found: nope at line 1, column 1" {
		t.Errorf("%v", err)
	}
	_, err = jqlite.Parse(`import "greet" as g; 1`)
	if err == nil || err.Error() != "module not found: greet at line 1, column 1" {
		t.Errorf("%v", err)
	}
}

func TestModulesThroughTheFileResolver(t *testing.T) {
	resolver := jqlite.NewFileResolver(filepath.Join(corpus.TestsDirectory(), "modules"))
	expression, err := jqlite.ParseWithResolver(`import "funcs" as f; 1 | f::addtwo`, resolver)
	if err != nil {
		t.Fatal(err)
	}
	same(t, []string{"3"}, evaluate(t, expression, "null", nil))
	_, err = jqlite.ParseWithResolver(`import "broken" as b; 1`, resolver)
	var parseError *jqlite.ParseError
	if !errors.As(err, &parseError) || !strings.HasSuffix(parseError.Module, "broken.jq") || !strings.Contains(parseError.Message, "broken.jq, line 1, column 15") {
		t.Errorf("%#v", err)
	}
}

// Runaway recursion through the public API (spec §11): the error is "Maximum call depth exceeded"
// and the process survives, on the test's goroutine and on a new one (whose stack starts small and
// grows).

const (
	tailRecursion    = "def f: if . == 0 then 0 else (. - 1 | f) end; f"
	nonTailRecursion = "def f: if . == 0 then 0 else (. - 1 | f) + 1 end; f"
)

func onNewGoroutine[T any](run func() T) T {
	result := make(chan T)
	go func() { result <- run() }()
	return <-result
}

func TestRunawayRecursionFailsAndTheProcessSurvives(t *testing.T) {
	for _, program := range []string{tailRecursion, nonTailRecursion} {
		expression := jqlite.MustParse(program)
		for _, fresh := range []bool{false, true} {
			run := func() error {
				_, err := expression.Evaluate(jqlite.Number(100000), nil)
				return err
			}
			var err error
			if fresh {
				err = onNewGoroutine(run)
			} else {
				err = run()
			}
			var jqError *jqlite.Error
			if !errors.As(err, &jqError) || jqError.Message != "Maximum call depth exceeded" || jqlite.ToJSON(jqError.Value) != `"Maximum call depth exceeded"` {
				t.Errorf("%s: %v", program, err)
			}
		}
	}
	same(t, []string{`"Maximum call depth exceeded"`}, evaluate(t, jqlite.MustParse(nonTailRecursion[:len(nonTailRecursion)-1]+"try f catch ."), "100000", nil))
}

func TestTheLastCallBelowMaxDepthSucceedsAndTheNextFails(t *testing.T) {
	for _, c := range []struct {
		program  string
		maxDepth int
		deepest  int
		output   string
	}{
		{tailRecursion, 0, 999, "0"},
		{nonTailRecursion, 0, 999, "999"},
		{tailRecursion, 3000, 2999, "0"},
		{nonTailRecursion, 3000, 2999, "2999"},
	} {
		expression := parse(t, c.program, jqlite.Options{MaxDepth: c.maxDepth})
		for _, fresh := range []bool{false, true} {
			run := func(n int) ([]jqlite.Value, error) {
				if fresh {
					type result struct {
						values []jqlite.Value
						err    error
					}
					r := onNewGoroutine(func() result {
						v, err := expression.Evaluate(jqlite.Number(float64(n)), nil)
						return result{v, err}
					})
					return r.values, r.err
				}
				return expression.Evaluate(jqlite.Number(float64(n)), nil)
			}
			results, err := run(c.deepest)
			if err != nil || len(results) != 1 || jqlite.ToJSON(results[0]) != c.output {
				t.Errorf("%s at %d: %v %v", c.program, c.deepest, texts(results), err)
			}
			if _, err := run(c.deepest + 1); err == nil || err.Error() != "Maximum call depth exceeded" {
				t.Errorf("%s at %d: %v", c.program, c.deepest+1, err)
			}
		}
	}
}

// An expression, the prelude and the modules are immutable and shared; one evaluation owns all
// its state. Many goroutines evaluating at once, started together, must each get what a single
// goroutine gets. No timing is involved: a shared mutable cell shows up as a wrong result.

const (
	goroutines = 32
	rounds     = 500
)

// Variables ($n, $name), a recursive function, a closure parameter, reduce, label/break, limit, a
// regex builtin, an update assignment, $ENV and a module import: each piece of shared state.
const concurrentProgram = `
import "funcs" as f;
def fact: if . <= 1 then 1 else . * (. - 1 | fact) end;
def twice(g): g | g;
{
  fact: ($n | fact),
  twice: ($n | twice(. * 2)),
  sum: (reduce (.xs[] | . * $n) as $x (0; . + $x)),
  first_over: [label $out | .xs[] | if . > $n then ., break $out else empty end],
  limit: [limit(3; .xs[] | . + $n)],
  words: [.text | match("[a-z]+"; "g").string] | join($name),
  update: (.a |= . + $n | .a),
  env: $ENV.JQ_CONCURRENCY,
  module: ($n | f::addtwo)
}`

func concurrentOptions() jqlite.Options {
	return jqlite.Options{
		Environment: []string{"JQ_CONCURRENCY=set"},
		Resolver:    jqlite.NewFileResolver(filepath.Join(corpus.TestsDirectory(), "modules")),
	}
}

// concurrentCase gives the input and the variables of one case: distinct for each n.
func concurrentCase(t testing.TB, n int) (jqlite.Value, map[string]jqlite.Value) {
	xs := make([]string, 7)
	for i := range xs {
		xs[i] = fmt.Sprint(n - 2 + i)
	}
	input := value(t, fmt.Sprintf(`{"xs":[%s],"text":"ab%dcd ef%dgh","a":%d}`, strings.Join(xs, ","), n, n, n*10))
	return input, map[string]jqlite.Value{"n": jqlite.Number(float64(n)), "name": jqlite.String(fmt.Sprintf("<%d>", n))}
}

func concurrentEvaluate(t testing.TB, expression *jqlite.Expression, n int) string {
	input, variables := concurrentCase(t, n)
	results, err := expression.Evaluate(input, variables)
	if err != nil {
		return err.Error()
	}
	return strings.Join(texts(results), "\n")
}

// onManyGoroutines runs work on goroutines that start together; gives what they report as wrong.
func onManyGoroutines(work func(g int, wrong func(string))) []string {
	var mu sync.Mutex
	var wrong []string
	report := func(text string) {
		mu.Lock()
		defer mu.Unlock()
		wrong = append(wrong, text)
	}
	start := make(chan struct{})
	var group sync.WaitGroup
	for g := 0; g < goroutines; g++ {
		group.Go(func() {
			<-start
			work(g, report)
		})
	}
	close(start)
	group.Wait()
	return wrong
}

func TestOneExpressionGivesEachGoroutineTheSingleThreadedResult(t *testing.T) {
	expression := parse(t, concurrentProgram, concurrentOptions())

	// Single-threaded, from a parse of its own: what each case must give.
	reference := parse(t, concurrentProgram, concurrentOptions())
	expected := map[int]string{}
	for n := 3; n < 12; n++ {
		expected[n] = concurrentEvaluate(t, reference, n)
	}
	if !strings.Contains(expected[3], `"module":`) || !strings.Contains(expected[3], `"env":"set"`) {
		t.Fatal(expected[3])
	}

	wrong := onManyGoroutines(func(g int, wrong func(string)) {
		for i := 0; i < rounds; i++ {
			n := 3 + (g*7+i)%9
			if actual := concurrentEvaluate(t, expression, n); actual != expected[n] {
				wrong(fmt.Sprintf("goroutine %d, round %d, n = %d: %s", g, i, n, actual))
			}
		}
	})
	if len(wrong) > 0 {
		t.Error(wrong[:min(5, len(wrong))])
	}
}

func TestDifferentExpressionsParseAndEvaluateAtOnce(t *testing.T) {
	wrong := onManyGoroutines(func(g int, wrong func(string)) {
		for i := 0; i < 20; i++ {
			k := g*100 + i
			expression, err := jqlite.Parse(fmt.Sprintf("def g: . + %d; [limit(3; range(10) | g)] | map(select(. %% 2 == 0)) | add // 0", k))
			if err != nil {
				wrong(err.Error())
				continue
			}
			results, err := expression.Evaluate(jqlite.Number(0), nil)
			even := 0
			for x := k; x < k+3; x++ {
				if x%2 == 0 {
					even += x
				}
			}
			if err != nil || len(results) != 1 || jqlite.ToJSON(results[0]) != fmt.Sprint(even) {
				wrong(fmt.Sprintf("goroutine %d, round %d: %v %v, expected %d", g, i, texts(results), err, even))
			}
		}
	})
	if len(wrong) > 0 {
		t.Error(wrong[:min(5, len(wrong))])
	}
}
