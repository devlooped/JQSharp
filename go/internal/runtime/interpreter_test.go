package runtime_test

import (
	"fmt"
	goruntime "runtime"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/devlooped/jqlite/go/internal/binding"
	_ "github.com/devlooped/jqlite/go/internal/builtins"
	"github.com/devlooped/jqlite/go/internal/runtime"
	"github.com/devlooped/jqlite/go/internal/values"
)

// The interpreter (spec §6 to §8, §11, §12) beyond the corpus: signals through every construct,
// laziness, path mode, the depth limit and the host stack, and the host interface. Each case is a
// program, its input and its outputs as one line (see line).

func jsonValue(t testing.TB, text string) values.Value {
	t.Helper()
	v, err, ok := values.ValueText.TryParse(text)
	if !ok {
		t.Fatalf("bad JSON %s: %v", text, err)
	}
	return v
}

func compile(t testing.TB, program string, nativeOverrides bool) *binding.BoundProgram {
	t.Helper()
	bound, err := binding.TryCompile(program, nativeOverrides, nil)
	if err != nil {
		t.Fatalf("'%s': %s at %d:%d", program, err.Message, err.Line, err.Column)
	}
	return bound
}

// run gives the outputs, each as canonical text, then "error: <value>" or "halt …" when the
// evaluation did not complete.
func run(program *binding.BoundProgram, input values.Value, options *runtime.EvaluationOptions, take int) []string {
	if options == nil {
		options = runtime.NewEvaluationOptions()
	}
	var outputs []string
	outcome := runtime.Run(program, input, options, func(v values.Value) bool {
		outputs = append(outputs, values.ValueText.ToJson(v))
		return len(outputs) < take
	})
	switch outcome.Kind {
	case runtime.OutcomeError:
		outputs = append(outputs, "error: "+values.ValueText.ToJson(outcome.Value))
	case runtime.OutcomeHalt:
		text := fmt.Sprintf("halt %d", outcome.Code)
		if outcome.HasValue {
			text += " " + values.ValueText.ToJson(outcome.Value)
		}
		outputs = append(outputs, text)
	case runtime.OutcomeStopped:
		if len(outputs) < take {
			outputs = append(outputs, "stopped")
		}
	}
	return outputs
}

// line gives the outputs separated by ", ".
func line(t testing.TB, program, input string, options *runtime.EvaluationOptions) string {
	return strings.Join(run(compile(t, program, true), jsonValue(t, input), options, 1<<62), ", ")
}

// onNewGoroutine runs the evaluation on a goroutine of its own, which starts with Go's smallest
// stack: the reference runs these on 1 MB and 256 KB threads; a goroutine's stack grows.
func onNewGoroutine(t testing.TB, program string, options *runtime.EvaluationOptions) string {
	bound := compile(t, program, true)
	input := jsonValue(t, "null")
	result := make(chan string)
	go func() { result <- strings.Join(run(bound, input, options, 1<<62), ", ") }()
	return <-result
}

func depth(maxDepth int) *runtime.EvaluationOptions {
	options := runtime.NewEvaluationOptions()
	options.MaxDepth = maxDepth
	return options
}

func check(t *testing.T, expected, actual, context string) {
	t.Helper()
	if expected != actual {
		t.Errorf("%s:\n expected %s\n got      %s", context, expected, actual)
	}
}

func TestSignalsPassThroughEveryConstruct(t *testing.T) {
	for _, c := range signalsPassThroughEveryConstruct {
		check(t, c[1], line(t, c[0], "null", nil), c[0])
	}
}

func TestConsumersThatStopStopTheirProducers(t *testing.T) {
	for _, c := range consumersThatStopStopTheirProducers {
		start := time.Now()
		check(t, c[1], line(t, c[0], "[1,2,3]", nil), c[0])
		if elapsed := time.Since(start); elapsed > 5*time.Second {
			t.Errorf("%s took %v", c[0], elapsed)
		}
	}
}

func TestTheHostStopsTheStream(t *testing.T) {
	outputs := run(compile(t, "range(1000000000)", true), values.Null, nil, 3)
	check(t, "0 1 2", strings.Join(outputs, " "), "range")
}

func TestPathsArePartOfEvaluation(t *testing.T) {
	options := runtime.NewEvaluationOptions()
	options.Variables = map[string]values.Value{"v": values.Null}
	for _, c := range pathsArePartOfEvaluation {
		check(t, c[1], line(t, c[0], `{"a":{"b":0},"k":"x","c":[1,2]}`, options), c[0])
	}
}

func TestAssignmentWorksThroughThePrelude(t *testing.T) {
	for _, c := range assignmentWorksThroughThePrelude {
		check(t, c[1], line(t, c[0], `{"a":0,"b":[1,2]}`, nil), c[0])
	}
}

func TestHostVariablesAndENV(t *testing.T) {
	options := runtime.NewEvaluationOptions()
	options.Variables = map[string]values.Value{"x": values.FromNumber(1), "y": values.FromString("two")}
	options.Environment = func() values.Value {
		return values.FromObject(values.EmptyObject.Set("HOME", values.FromString("/home")))
	}
	for _, c := range hostVariablesAndENV {
		check(t, c[1], line(t, c[0], "null", options), c[0])
	}
}

func TestOneProgramRunsConcurrentlyWithDifferentVariables(t *testing.T) {
	program := compile(t, "def f: if . == 0 then $n else (. - 1 | f) end; [range(200) | {a: f}] | map(.a) | add", true)
	results := make([]string, 64)
	var group sync.WaitGroup
	for i := range results {
		group.Go(func() {
			options := runtime.NewEvaluationOptions()
			options.Variables = map[string]values.Value{"n": values.FromNumber(float64(i))}
			results[i] = strings.Join(run(program, values.FromNumber(50), options, 1<<62), ", ")
		})
	}
	group.Wait()
	for i, result := range results {
		check(t, fmt.Sprint(200*i), result, "concurrent")
	}
}

func TestTheCallDepthIsLimited(t *testing.T) {
	for _, maxDepth := range []int{1000, 50} {
		const count = "def f: if . == 0 then 0 else (. - 1 | f) end; "
		// n | f makes n + 1 calls.
		check(t, "0", onNewGoroutine(t, count+fmt.Sprint(maxDepth-1)+" | f", depth(maxDepth)), "deepest")
		check(t, `error: "Maximum call depth exceeded"`, onNewGoroutine(t, count+fmt.Sprint(maxDepth)+" | f", depth(maxDepth)), "too deep")
		check(t, `"Maximum call depth exceeded"`, onNewGoroutine(t, "try ("+count+"100000 | f) catch .", depth(maxDepth)), "caught")
	}
}

func TestASingleCallHasEndedWhenItsOutputIsUsed(t *testing.T) {
	// 20 | fib makes 21,891 calls, 20 deep.
	const fib = "def fib: if . < 2 then . else (. - 1 | fib) + (. - 2 | fib) end; 20 | fib"
	check(t, "6765", line(t, fib, "null", depth(20)), "fib 20")
	check(t, `error: "Maximum call depth exceeded"`, line(t, fib, "null", depth(19)), "fib 19")
	check(t, "6765", line(t, "def fib($n): if $n < 2 then $n else fib($n - 1) + fib($n - 2) end; fib(20)", "null", depth(20)), "fib($n)")

	// A call that is not single stays active while its outputs are consumed: the two calls nest.
	const many = "def g: if . < 2 then ., empty else (. - 1 | g) + (. - 2 | g) end; "
	check(t, "55", line(t, many+"10 | g", "null", nil), "g 10")
	check(t, `error: "Maximum call depth exceeded"`, line(t, many+"15 | g", "null", nil), "g 15")
}

func TestTailRecursionReachesTheLimitWithoutTheHostStack(t *testing.T) {
	check(t, `error: "Maximum call depth exceeded"`,
		onNewGoroutine(t, "def f: if . == 0 then 0 else (. - 1 | f) end; 100000 | f", nil), "tail")
}

func TestLoopNativesDoNotConsumeTheDepth(t *testing.T) {
	for _, c := range loopNativesDoNotConsumeTheDepth {
		check(t, c[1], onNewGoroutine(t, c[0], depth(10)), c[0])
	}
}

func TestOutputsComeInTheOrderOfTheSpec(t *testing.T) {
	for _, c := range outputsComeInTheOrderOfTheSpec {
		check(t, c[1], line(t, c[0], "[1,2]", nil), c[0])
	}
}

// The native _modify and _assign against their prelude definitions (§8.3): each program is
// compiled twice, with the native overrides and without them, and both must give the same
// outputs, errors included.
func TestNativeAndPreludeAgree(t *testing.T) {
	for _, c := range nativeAndPreludeAgree {
		input := jsonValue(t, c[1])
		native := strings.Join(run(compile(t, c[0], true), input, nil, 1<<62), ", ")
		prelude := strings.Join(run(compile(t, c[0], false), input, nil, 1<<62), ", ")
		check(t, prelude, native, c[0])
	}
}

func TestNativeUpdatesAreLinear(t *testing.T) {
	var before, after goruntime.MemStats
	goruntime.GC()
	goruntime.ReadMemStats(&before)
	check(t, "4000", line(t, "[range(4000)] | .[] |= . + 1 | length", "null", nil), "update")
	goruntime.ReadMemStats(&after)
	// The prelude definition copies the array once per element: 4000 × 128 KB.
	if allocated := after.TotalAlloc - before.TotalAlloc; allocated > 64*1024*1024 {
		t.Errorf("%d bytes", allocated)
	}
}

func TestPathsOfTenThousandKeys(t *testing.T) {
	// A value nested 10,000 arrays deep and the path of its innermost 0.
	const deep = "reduce range(10000) as $i (0; [.]) | [range(10000) | 0] as $p | "
	check(t, "1", onNewGoroutine(t, deep+"getpath($p) |= . + 1 | getpath($p)", nil), "modify")
	check(t, "2", onNewGoroutine(t, deep+"setpath($p; 2) | getpath($p)", nil), "setpath")
	check(t, "[]", onNewGoroutine(t, deep+"delpaths([$p]) | getpath($p[1:])", nil), "delpaths")
	check(t, "[]", onNewGoroutine(t, deep+"del(getpath($p)) | getpath($p[1:])", nil), "del")
	check(t, "3", onNewGoroutine(t, deep+"getpath($p) = 3 | getpath($p)", nil), "assign")
}

// A native of the catalogue without an implementation is a defect of the build: the table refuses
// to load, and this test names it.
func TestEveryNativeOfTheCatalogueIsImplemented(t *testing.T) {
	for _, native := range binding.Natives.All {
		if !runtime.NativeTable.IsImplemented(native) {
			t.Errorf("%v has no implementation", native)
		}
	}
}

// The tables below are the [InlineData] rows of the reference's InterpreterTests and AssignTests.

var signalsPassThroughEveryConstruct = [][]string{
	{`[try (1, error("x"), 3) catch .]`, `[1,"x"]`},
	{`[.[] | try error catch .]`, `error: "Cannot iterate over null (null)"`},
	{`[.[]?]`, `[]`},
	{`try error({}) catch .`, `{}`},
	{`[(1, 2) | try error catch . + 10]`, `[11,12]`},
	{`try (try error("a") catch error("b")) catch .`, `"b"`},
	{`"x" | try ((try . catch "too much") | error) catch "right"`, `"right"`},
	{`[try ((try (1,2) catch "inner") | if . == 2 then error("consumer") else . end) catch ("outer: " + .)]`, `[1,"outer: consumer"]`},
	{`try ((1, 2)? | error(tostring)) catch .`, `"1"`},
	{`try (([[1]] | .[] as [$a] ?// $a | $a) | error(tostring)) catch .`, `"1"`},
	{`[[[1],2][] as [$a] ?// $a | $a]`, `[1,2]`},
	{`[[[1]][] as [$a] ?// $a | if ($a | type) == "number" then error("n") else $a end]`, `[[1]]`},
	{`try (1 as [$a] ?// {$a} | $a) catch .`, `"Cannot index number with string (\"a\")"`},
	{`[reduce ([1],[5]) as [$a] ?// $a (10; if $a == 1 then error("e") else [., $a] end)]`, `[[[10,[1]],5]]`},
	{`[foreach ([1]) as [$a] ?// $a (10; . + 1; if $a == 1 then ., error("x") else [., $a] end)]`, `[11,[11,[1]]]`},
	{`try (reduce ([1],[2]) as [$a] ?// $a (0; if $a == 1 then error("e") else . + $a end)) catch .`, `"number (0) and array ([1]) cannot be added"`},
	{`[reduce ({"a":1}, [2]) as {a: $a} ?// [$a] (0; . + $a)]`, `[3]`},
	{`try (foreach ([1]) as [$a] ?// $a (0; .; $a) | error(tostring)) catch .`, `"1"`},
	{`[foreach ([1], [2]) as [$a] ?// $a (0; . + (if $a == 2 then error("two") else $a end); .)]`, `error: "number (1) and array ([2]) cannot be added"`},
	{`[5,6] | . as {(.[0] * 0 + 1): $v} | $v`, `6`},
	{`{"a":1} | . as {(.a): $v} | $v`, `error: "Cannot index object with number (1)"`},
	{`null | . as {(0 + 1): $v, ("b"): $w} | [$v, $w]`, `[null,null]`},
	{`try [(1, error("x")) // 3] catch .`, `"x"`},
	{`[empty // 3, (false, null) // 4, (1, null, 2) // 5]`, `[3,4,1,2]`},
	{`[label $f | def g: break $f; (label $f | 1, g, 2), 3]`, `[1]`},
	{`[label $a | label $b | 1, break $a, 2]`, `[1]`},
	{`label $out | try (0, break $out, 1) catch 99`, `0`},
	{`def f: label $x | (1, break $x); [f, f]`, `[1,1]`},
	{`reduce (1,2,3) as $x (0; empty)`, `null`},
	{`[reduce empty as $x (1, 2; .)]`, `[1,2]`},
	{`reduce (1,2) as $x (0; ., 10)`, `10`},
	{`[foreach (1,2) as $x (0; (. + 1, . + 10); .)]`, `[1,10,11,20]`},
	{`[foreach (1,2) as $x (0; empty; .)]`, `[]`},
	{`[foreach (1,2) as $x (0, 1; . + $x)]`, `[1,3,2,4]`},
	{`1, halt, 2`, `1, halt 0`},
	{`try ("bye" | halt_error(5)) catch 0`, `halt 5 "bye"`},
	{`try halt_error("x") catch .`, `"null (null) halt_error/1: number required"`},
}

var consumersThatStopStopTheirProducers = [][]string{
	{`first(range(1000000000))`, `0`},
	{`[limit(3; repeat(1))]`, `[1,1,1]`},
	{`isempty(repeat(1))`, `false`},
	{`[label $f | 1, break $f, error("unreached")]`, `[1]`},
	{`first(range(10; infinite))`, `10`},
	{`[limit(1; 1, error("unreached"))]`, `[1]`},
	{`[.[] | select(. > 1)] | first`, `2`},
	{`any(range(1000000000); . > 2)`, `true`},
}

var pathsArePartOfEvaluation = [][]string{
	{`path(.)`, `[]`},
	{`[path(.a.b, .c[1:2], .c[])]`, `[["a","b"],["c",{"start":1,"end":2}],["c",0],["c",1]]`},
	{`[path(.a[.k])]`, `[["a","x"]]`},
	{`[path(.z?, (.c | try .[] catch .), (.a | .b))]`, `[["z"],["c",0],["c",1],["a","b"]]`},
	{`[path(if .k == "x" then .a else .c end)]`, `[["a"]]`},
	{`[path(.z // .a)]`, `[["a"]]`},
	{`[path(.k as $k | .a[$k])]`, `[["a","x"]]`},
	{`[path(reduce ("a", "b") as $k (.; .[$k]))]`, `[["a","b"]]`},
	{`[path(foreach ("a", "b") as $k (.; .[$k]))]`, `[["a"],["a","b"]]`},
	{`[path(label $x | .a, break $x)]`, `[["a"]]`},
	{`def f(p): p | .b; [path(f(.a))]`, `[["a","b"]]`},
	{`[path(empty), path(getpath(["a", "q"]))]`, `[["a","q"]]`},
	{`[path(first(.c[]), last(.c[]), limit(1; .c[]), skip(1; .c[]))]`, `[["c",0],["c",1],["c",0],["c",1]]`},
	{`[path(.. | select(type == "number"))]`, `[["a","b"],["c",0],["c",1]]`},
	{`[path(.c | recurse(if . == null then empty else .[0]? end))]`, `[["c"],["c",0]]`},
	{`try path(1) catch .`, `"Invalid path expression with result 1"`},
	{`try path($v | .a) catch .`, `"Invalid path expression near attempt to access element \"a\" of null"`},
	{`try path(.k | ascii_upcase | .[]) catch .`, `"Invalid path expression near attempt to iterate through \"X\""`},
	{`try path(.k + "y") catch .`, `"Invalid path expression with result \"xy\""`},
}

var assignmentWorksThroughThePrelude = [][]string{
	{`.a = 1`, `{"a":1,"b":[1,2]}`},
	{`.b[] |= . * 10`, `{"a":0,"b":[10,20]}`},
	{`.b[1] += 5`, `{"a":0,"b":[1,7]}`},
	{`[.a += (1, 2)] | map(.a)`, `[1,2]`},
	{`.c //= "d"`, `{"a":0,"b":[1,2],"c":"d"}`},
	{`(.a, .z) = 9`, `{"a":9,"b":[1,2],"z":9}`},
	{`.b |= map(select(. > 1))`, `{"a":0,"b":[2]}`},
	{`(.b[] | select(. == 1)) |= empty`, `{"a":0,"b":[2]}`},
	{`del(.a, .b[0])`, `{"b":[2]}`},
	{`to_entries`, `[{"key":"a","value":0},{"key":"b","value":[1,2]}]`},
	{`with_entries(.value |= tostring)`, `{"a":"0","b":"[1,2]"}`},
	{`[paths]`, `[["a"],["b"],["b",0],["b",1]]`},
	{`def _modify(p; f): "mine"; .a |= 1`, `"mine"`},
}

var hostVariablesAndENV = [][]string{
	{`$x`, `1`},
	{`[$x, $y]`, `[1,"two"]`},
	{`$x as $x | $x + 1`, `2`},
	{`$missing`, `error: "$missing is not defined"`},
	{`try $missing catch .`, `"$missing is not defined"`},
	{`$ENV.HOME, env.HOME`, `"/home", "/home"`},
	{`1 as $ENV | $ENV`, `1`},
}

var loopNativesDoNotConsumeTheDepth = [][]string{
	{`[limit(5000; repeat(1))] | length`, `5000`},
	{`[range(100000)] | length`, `100000`},
	{`last(range(100000))`, `99999`},
	{`0 | until(. >= 100000; . + 1)`, `100000`},
	{`[0 | while(. < 100000; . + 1)] | length`, `100000`},
	{`[skip(99998; range(100000))]`, `[99998,99999]`},
}

var outputsComeInTheOrderOfTheSpec = [][]string{
	{`[1, 2] | [.[] as $x | $x * 10]`, `[10,20]`},
	{`[(1,2) + (10,20)]`, `[11,12,21,22]`},
	{`[{a: (1,2), b: (3,4)}]`, `[{"a":1,"b":3},{"a":1,"b":4},{"a":2,"b":3},{"a":2,"b":4}]`},
	{`["\(1,2)-\(3,4)"]`, `["1-3","2-3","1-4","2-4"]`},
	{`[range(0,1; 3,4)]`, `[0,1,2,0,1,2,3,1,2,1,2,3]`},
	{`[.[(0,1)]]`, `[1,2]`},
	{`[range(10)] | [.[(1,2):(3,4)] | length]`, `[2,3,1,2]`},
	{`[(true, false) and (true, false)]`, `[true,false,false]`},
	{`[(true, false) or (true, false)]`, `[true,true,false]`},
	{`[-(1, 2)]`, `[-1,-2]`},
	{`[{("a", "b"): 1}]`, `[{"a":1},{"b":1}]`},
	{`{"x1":"a","x2":"b"} | [{"x\(1,2)"}]`, `[{"x1":"a"},{"x2":"b"}]`},
	{`[. as [$a, $b] | {$a, $b}]`, `[{"a":1,"b":2}]`},
	{`. as {("a", "b"): $v} ?// $v | $v`, `[1,2]`},
	{`{"a":1,"b":2} | [. as {("a", "b"): $v} | $v]`, `[1,2]`},
}

var nativeAndPreludeAgree = [][]string{
	{`(.a, .a.b) |= {b: 1}`, `{"a":{"b":0}}`},
	{`(.a.b, .a) |= {x: .}`, `{"a":{"b":0}}`},
	{`(.a, .a) |= . + 1`, `{"a":1}`},
	{`(.a[0], .a, .a[0]) |= [.]`, `{"a":[1,2]}`},
	{`.. |= (if type == "number" then . + 1 else . end)`, `{"a":[1,{"b":2}],"c":3}`},
	{`[..] as $all | .. |= length`, `[[1,[2]],{"x":3}]`},
	{`.a |= ., .b |= [., .]`, `{"a":[1],"b":{"c":2}}`},
	{`(.a, .a[0]) |= [., .]`, `{"a":[1]}`},
	{`. as $in | .a |= $in`, `{"a":1}`},
	{`(.[] | select(. >= 2)) |= empty`, `[1,5,3,0,7]`},
	{`.[] |= (if . % 2 == 0 then empty else . * 10 end)`, `[1,2,3,4,5]`},
	{`(.a, .b, .c) |= (if . == null then empty else . + 1 end)`, `{"a":1,"c":3}`},
	{`(.a.x, .a) |= empty`, `{"a":{"x":1}}`},
	{`to_entries |= map(select(.key != "b")) | from_entries`, `{"a":1,"b":2}`},
	{`(.a, .b, .c) |= (if . == 2 then error("two") else . end)`, `{"a":1,"b":2,"c":3}`},
	{`(.a, .b.x) |= 1`, `{"a":0,"b":5}`},
	{`(.a, 1) |= 1`, `{"a":0}`},
	{`.[0] |= 1`, `{}`},
	{`(.a, .[0]) |= 1`, `null`},
	{`(.a | .b) |= 1`, `{"a":[1]}`},
	{`(.a, .b) = (1, 2)`, `{}`},
	{`.a = empty`, `{}`},
	{`empty = 1`, `{"a":0}`},
	{`.a = .b`, `{"a":0,"b":1}`},
	{`(.a, .a.b) = 1`, `{}`},
	{`.a[1:3] = ["x"]`, `{"a":[0,1,2,3]}`},
	{`.[1:3] |= map(. * 10)`, `[0,1,2,3,4]`},
	{`(.[1:3], .[0]) |= (if type == "array" then [] else -1 end)`, `[0,1,2,3,4]`},
	{`.[2:4][0] |= 9`, `[0,1,2,3,4]`},
	{`.[1:2] |= "x"`, `"abc"`},
	{`.a.b[2].c |= 1`, `null`},
	{`.[-1] |= . + 1`, `[1,2,3]`},
	{`.[-5] |= 1`, `[1,2,3]`},
	{`.[nan] |= 1`, `[1]`},
	{`.[3] |= 1`, `[1]`},
	{`.[536870912] |= 1`, `[]`},
	{`.[] += 1, .a -= 1, .b *= 2, .c /= 2, .d %= 2, .e //= 7`, `{"a":1,"b":2,"c":4,"d":5,"e":null}`},
	{`map_values(. + 1)`, `{"a":1,"b":2}`},
	{`walk(if type == "number" then . + 1 else . end)`, `[1,[2,{"a":3}]]`},
	{`INDEX(.id)`, `[{"id":1},{"id":2}]`},
	{`sub("b"; "x")`, `"abc"`},
	{`path(.a |= 1)`, `{"a":0}`},
	{`path(empty = 1)`, `{"a":0}`},
	{`[path(.a = 1)]`, `{"a":0}`},
	{`.a |= (1, 2)`, `{"a":0}`},
	{`(.a, .b) |= (.x |= 1)`, `{"a":{},"b":null}`},
}
