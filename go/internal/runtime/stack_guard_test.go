package runtime_test

import (
	goruntime "runtime"
	"slices"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/devlooped/jqlite/go/internal/runtime"
	"github.com/devlooped/jqlite/go/internal/values"
)

// MaxDepth is the only recursion limit a program meets (spec §11), and a port never crashes below
// it. The reference runs these cases on 1 MB and 256 KB threads, so that they continue on fresh
// stacks; here each runs on a new goroutine twice: with the default number of frames per
// goroutine, and with 50, which makes the evaluation hop to a fresh goroutine hundreds of times.

// hops gives the frames per goroutine of each run: the default (0) and 50.
var hops = []int{0, 50}

func withHops(t *testing.T, frames int, test func()) {
	t.Helper()
	if frames > 0 {
		defer runtime.SetHopEvery(frames)()
	}
	test()
}

func TestNestingToTheParserLimitWorks(t *testing.T) {
	for _, frames := range hops {
		withHops(t, frames, func() {
			for _, n := range []struct{ open, close, expected string }{
				// opening, closing, and the output for 1000 levels around "1"
				{"[", "]", strings.Repeat("[", 1000) + "1" + strings.Repeat("]", 1000)},
				{"{a: ", "}", strings.Repeat(`{"a":`, 1000) + "1" + strings.Repeat("}", 1000)},
				{".[", "]", `error: "Cannot index null with null (null)"`},
				{"\"\\(", ")\"", `"1"`},
				{"reduce . as $x (", ";.)", "1"},
				{"[1 + ", "]", "error"},
				{"1 + (", ")", "1001"},
				{"[label $x | ", "]", strings.Repeat("[", 1000) + "1" + strings.Repeat("]", 1000)},
				{"if . then 1 else ", " end", "1"},
				{"try ", "", "1"},
			} {
				result := onNewGoroutine(t, strings.Repeat(n.open, 1000)+"1"+strings.Repeat(n.close, 1000), nil)
				if n.expected == "error" {
					if !strings.HasPrefix(result, `error: "number (1) and array ([2]) cannot be added`) {
						t.Errorf("%s: %s", n.open, result)
					}
				} else {
					check(t, n.expected, result, n.open)
				}
			}
		})
	}
}

var recursions = []string{
	// N | f makes N + 1 calls.
	"def f: if . == 0 then 0 else (. - 1 | f) + 1 end; N | f",
	"def f: if . == 0 then 0 else [. - 1 | f] | length end; N | f",
	"def f: if . == 0 then 0 else ((. - 1, . - 1) | f) end; label $out | (N | f) | ., break $out",
	"def f(g): if . == 0 then g else (. - 1 | f(g)) + 0 end; N | f(0)",
}

func TestRecursionReachesExactlyMaxDepth(t *testing.T) {
	for _, frames := range hops {
		withHops(t, frames, func() {
			for _, program := range recursions {
				for _, maxDepth := range []int{1000, 5000, 20000} {
					deepest := onNewGoroutine(t, strings.ReplaceAll(program, "N", strconv.Itoa(maxDepth-1)), depth(maxDepth))
					if strings.Contains(deepest, "error") {
						t.Errorf("%s at %d: %s", program, maxDepth, deepest)
					}
					check(t, `error: "Maximum call depth exceeded"`,
						onNewGoroutine(t, strings.ReplaceAll(program, "N", strconv.Itoa(maxDepth)), depth(maxDepth)), program)
				}
			}
		})
	}
}

// A recursive call written inside a deeply nested body costs that nesting at every level: 1,000
// calls through 900 levels of nesting are about 900,000 frames of the interpreter, which one
// goroutine would hold only up to Go's maximum stack. The hops keep each goroutine short.
func TestARecursionThroughDeepNestingHops(t *testing.T) {
	body := strings.Repeat("1 + (", 900) + "(. - 1 | f)" + strings.Repeat(")", 900)
	program := "def f: if . == 0 then 0 else " + body + " end; 999 | f"
	check(t, strconv.Itoa(999*900), onNewGoroutine(t, program, nil), "nested recursion")
}

// The largest MaxDepth tested: 100,000 calls, each on its own level, through both evaluations.
func TestAHundredThousandLevels(t *testing.T) {
	start := time.Now()
	for _, program := range recursions[:3] {
		deepest := onNewGoroutine(t, strings.ReplaceAll(program, "N", "99999"), depth(100000))
		if strings.Contains(deepest, "error") {
			t.Errorf("%s: %s", program, deepest)
		}
	}
	t.Logf("%v", time.Since(start))
}

func TestAStopOfTheHostComesBackAcrossHops(t *testing.T) {
	for _, frames := range hops {
		withHops(t, frames, func() {
			program := compile(t, "def f: if . == 0 then (1, 2, 3) else (. - 1 | f) + 0 end; 900 | f", true)
			result := make(chan []string)
			go func() { result <- run(program, values.Null, nil, 1) }()
			if outputs := <-result; !slices.Equal([]string{"1"}, outputs) {
				t.Errorf("%v", outputs)
			}
		})
	}
}

func TestAnErrorComesBackAcrossHops(t *testing.T) {
	for _, frames := range hops {
		withHops(t, frames, func() {
			check(t, `"deep"`, onNewGoroutine(t, `try (def f: if . == 0 then error("deep") else (. - 1 | f) + 1 end; 900 | f) catch .`, nil), "error")
		})
	}
}

func TestAPanicOfTheHostComesBackAcrossHops(t *testing.T) {
	defer runtime.SetHopEvery(50)()
	program := compile(t, "def f: if . == 0 then (1, 2) else ((. - 1, . - 1) | f) end; 900 | f", true)
	defer func() {
		if recover() != "host" {
			t.Error("the panic did not come back")
		}
	}()
	runtime.Run(program, values.Null, runtime.NewEvaluationOptions(), func(values.Value) bool { panic("host") })
}

// The stack one call level takes, for a single call (EvalOne) and for calls that are not single
// (Eval through callbacks): measured at the deepest point of 50,000 levels, as the stack memory of
// the process. About 1.3 KB; the bound keeps a regression from going unnoticed.
func TestTheStackOfACallLevel(t *testing.T) {
	for _, program := range []string{
		"def f: if . == 0 then debug else (. - 1 | f) + 1 end; N | f",
		"def f: if . == 0 then debug else ((. - 1, . - 1) | f) end; label $out | (N | f) | ., break $out",
	} {
		const n = 50000
		options := depth(n + 1)
		var deepest uint64
		options.Debug = func(values.Value) { deepest = stackInUse() }
		bound := compile(t, strings.ReplaceAll(program, "N", strconv.Itoa(n)), true)
		done := make(chan uint64)
		go func() {
			base := stackInUse()
			runtime.Run(bound, values.Null, options, func(values.Value) bool { return true })
			done <- base
		}()
		base := <-done
		perLevel := (deepest - base) / n
		t.Logf("%d bytes per level: %s", perLevel, program)
		if perLevel > 4096 {
			t.Errorf("%d bytes per level: %s", perLevel, program)
		}
	}
}

func stackInUse() uint64 {
	var stats goruntime.MemStats
	goruntime.ReadMemStats(&stats)
	return stats.StackInuse
}
