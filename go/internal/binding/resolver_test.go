package binding

import (
	"fmt"
	"slices"
	"strings"
	"testing"
)

// Lexical binding (spec §7.5, §7.7, §7.15) stated on the printed bound program, and the binding
// errors of §10.2.

func checkBinds(t *testing.T, cases [][]string) {
	t.Helper()
	for _, c := range cases {
		if actual := bindText(t, c[0]); actual != c[1] {
			t.Errorf("%s:\n expected %s\n got      %s", c[0], c[1], actual)
		}
	}
}

func TestResolverTables(t *testing.T) {
	checkBinds(t, resolverVariablesBindLexically)
	checkBinds(t, resolverFunctionsBindToTheInnermostVisibleDefinition)
	checkBinds(t, resolverParametersAreClosures)
	checkBinds(t, resolverLabelsBindLexically)
	checkBinds(t, resolverPatternsBindEveryVariable)
	checkBinds(t, resolverUnboundVariablesAreHostVariables)
	checkBinds(t, resolverTheLocationIsALiteral)
	checkBinds(t, resolverCallsResolveToUserThenPreludeThenNative)
}

func TestFunctionsAndCallsAreSingleByTheRules(t *testing.T) {
	for _, c := range resolverFunctionsAndCallsAreSingleByTheRules {
		for _, nativeOverrides := range []bool{true, false} {
			bound, err := TryCompile(c[0], nativeOverrides, nil)
			if err != nil {
				t.Fatalf("%s: %s", c[0], err.Message)
			}
			sign := func(single bool) string {
				if single {
					return "+"
				}
				return "-"
			}
			var parts []string
			for _, function := range bound.Functions[preludeCount():] {
				parts = append(parts, function.String()+sign(function.Single))
			}
			parts = append(parts, "body"+sign(bound.Body.AtMostOne()))
			if actual := strings.Join(parts, " "); actual != c[1] {
				t.Errorf("%s (overrides %v): expected %s, got %s", c[0], nativeOverrides, c[1], actual)
			}
		}
	}
}

func TestHostVariablesAreListedOnce(t *testing.T) {
	program, _ := TryCompile("$foo, $bar, ($foo as $bar | $bar), $baz", true, nil)
	if !slices.Equal([]string{"foo", "bar", "baz"}, program.HostVariables) {
		t.Errorf("%v", program.HostVariables)
	}
}

func TestWithoutOverridesBuiltinsBindToThePrelude(t *testing.T) {
	program, _ := TryCompile(".a |= 1, (.b = 2), map_values(.)", false, nil)
	same(t, "(, (prelude:_modify/2^0 (index . \"a\") 1) (prelude:_assign/2^0 (index . \"b\") 2) (prelude:map_values/1^0 .))", printBound(program))

	// Inside the prelude too: map_values calls _modify.
	with := PreludeInstance()
	if with.Functions[with.Find("map_values", 1)].Body.Kind() != NodeCallNative {
		t.Error("map_values does not call the native _modify")
	}
	without := PreludeWithoutOverrides()
	if without.Functions[without.Find("map_values", 1)].Body.Kind() != NodeCallFunction {
		t.Error("map_values does not call the prelude's _modify")
	}
}

func TestEveryConstructBindsItsChildren(t *testing.T) {
	same(t, "(as . $0 (array (, (object (\"a\" $0.0) (\"b\" _) ($0.0 1)) (str @base64 \"x\" $0.0 \"\") (slice (index . $0.0) $0.0 _) (if $0.0 (try $0.0 $0.0) (// (or (and $0.0 $0.0) (neg $0.0)) @csv)) (iter $0.0) (try (index . \"a\")))))",
		bindText(t, ". as $x | [{a: $x, b, ($x): 1}, @base64 \"x\\($x)\", .[$x][$x:], if $x then try $x catch $x else $x and $x or -$x // @csv end, $x[], .a?]"))
}

func TestPreludeFunctionsComeFirstInEveryProgram(t *testing.T) {
	first, _ := TryCompile("def f: 1; f", true, nil)
	second, _ := TryCompile("map(.)", true, nil)
	prelude := PreludeInstance().Functions
	if len(first.Functions) != len(prelude)+1 || len(second.Functions) != len(prelude) {
		t.Fatal("wrong function counts")
	}
	for i := range prelude {
		if prelude[i] != first.Functions[i] || prelude[i] != second.Functions[i] {
			t.Fatalf("function %d differs", i)
		}
	}
}

func TestUnknownNamesAreBindingErrors(t *testing.T) {
	for _, c := range resolverUnknownNamesAreBindingErrors {
		same(t, c[1], bindError(t, c[0]))
	}
}

func TestSyntaxErrorsComeFromTheParser(t *testing.T) {
	same(t, "Unexpected end of program @1:4", bindError(t, "1 +"))
}

func TestModuleMetadataAloneIsAccepted(t *testing.T) {
	same(t, "1", bindText(t, "module {a: 1}; 1"))
}

func same(t *testing.T, expected, actual string) {
	t.Helper()
	if expected != actual {
		t.Errorf("\nexpected %s\ngot      %s", expected, actual)
	}
}

var _ = fmt.Sprint

// The tables below are the [InlineData] rows of the reference's ResolverTests.

var resolverVariablesBindLexically = [][]string{
	{`1 as $x | $x`, `(as 1 $0 $0.0)`},
	{`1 as $x | 2 as $x | $x`, `(as 1 $0 (as 2 $0 $0.0))`},
	{`1 as $x | 2 as $y | $x, $y`, `(as 1 $0 (as 2 $0 (, $1.0 $0.0)))`},
	{`1 as $x | (2 as $x | $x), $x`, `(as 1 $0 (, (as 2 $0 $0.0) $0.0))`},
	{`1 as $x | ($x + 1) as $x | $x`, `(as 1 $0 (as (+ $0.0 1) $0 $0.0))`},
	{`1 as $x | def f: $x; 2 as $x | f`, `(def f/0#0 $0.0) (as 1 $0 (as 2 $0 f/0#0^1))`},
	{`def f: 1; 2 as $x | f`, `(def f/0#0 1) (as 2 $0 f/0#0^1)`},
}

var resolverFunctionsBindToTheInnermostVisibleDefinition = [][]string{
	{`def f: f; f`, `(def f/0#0 f/0#0^0) f/0#0^0`},
	{`def f: 1; def g: f; def f: 2; g`, `(def f/0#0 1) (def g/0#1 f/0#0^0) (def f/0#2 2) g/0#1^0`},
	{`def f: 1; def f(a): 2; f, f(3)`, `(def f/0#0 1) (def f/1#1 2) (, f/0#0^0 (f/1#1^0 3))`},
	{`def f: 1; [f, (def f: 2; f), f]`, `(def f/0#0 1) (def f/0#1 2) (array (, f/0#0^0 f/0#1^0 f/0#0^0))`},
	{`def f(g): def h: f(g); h; f(.)`, `(def f/1#0 h/0#1^0) (def h/0#1 (f/1#0^1 param^0.0)) (f/1#0^0 .)`},
}

var resolverFunctionsAndCallsAreSingleByTheRules = [][]string{
	{`def fib: if . < 2 then . else (. - 1 | fib) + (. - 2 | fib) end; fib`, `fib/0+ body+`},
	{`def r: ., (.[]? | r); r`, `r/0- body-`},
	{`def f($a): $a + 1; f(1)`, `f/1+ body+`},
	{`def f($a): $a + 1; f(1, 2)`, `f/1+ body-`},
	{`def f($a; b; $c): [b] | $a + $c; f(1; 2, 3; 4)`, `f/3+ body+`},
	{`def f($a; b; $c): [b] | $a + $c; f(1; 2; 3, 4)`, `f/3+ body-`},
	{`def f($n): if $n < 2 then $n else f($n - 1) + f($n - 2) end; f(9)`, `f/1+ body+`},
	{`def f(g): g + 1; f(1)`, `f/1- body-`},
	{`def f(g): [g]; f(1, 2)`, `f/1+ body+`},
	{`def f: def g: f; g; f`, `f/0- g/0- body-`},
	{`def f: def g: if . < 3 then . + 1 | g else . end; g, (. + 1 | f); f`, `f/0- g/0+ body-`},
	{`def f: def g: def h: ., (. + 1 | h); [h], (. + 1 | g); def k: 1 + k; [g] | k, f; [f]`, `f/0- g/0- h/0- k/0+ body+`},
	{`def f: if . then empty else last(.[]) end; f`, `f/0+ body+`},
	{`def f: getpath(["a"]) | until(. > 3; . + 1) | length; f`, `f/0+ body+`},
	{`def f: getpath(["a"], ["b"]); f`, `f/0- body-`},
	{`def f: limit(1; .); f`, `f/0- body-`},
	{`def f: if any(.[]; . > 1) then error("x") elif isempty(.[]) then 0 else map(f) | add end; f`, `f/0+ body+`},
	{`def f: select(. > 1); f`, `f/0- body-`},
	{`def f: .a |= . + 1 | .b = 2; f`, `f/0+ body+`},
	{`def f: .a = (1, 2); f`, `f/0- body-`},
}

var resolverParametersAreClosures = [][]string{
	{`def f(g): g; f(.)`, `(def f/1#0 param^0.0) (f/1#0^0 .)`},
	{`def f(a; b): b, a; f(1; 2)`, `(def f/2#0 (, param^0.1 param^0.0)) (f/2#0^0 1 2)`},
	{`def f(a; a): a; f(1; 2)`, `(def f/2#0 param^0.1) (f/2#0^0 1 2)`},
	{`def f(g): def h(x): g, x; h(1); f(2)`, `(def f/1#0 (h/1#1^0 1)) (def h/1#1 (, param^1.0 param^0.0)) (f/1#0^0 2)`},
	{`def x: 7; def f(x): x; f(1)`, `(def x/0#0 7) (def f/1#1 param^0.0) (f/1#1^0 1)`},
	{`def x(a): 7; def f(x): x(0); f(1)`, `(def x/1#0 7) (def f/1#1 (x/1#0^1 0)) (f/1#1^0 1)`},
	{`def f(g): def g: 1; g; f(2)`, `(def f/1#0 g/0#1^0) (def g/0#1 1) (f/1#0^0 2)`},
	{`def f($a): $a, a; f(1)`, `(def f/1#0 (as param^0.0 $0 (, $0.0 param^1.0))) (f/1#0^0 1)`},
	{`def f($a; $b): $a, $b; f(1; 2)`, `(def f/2#0 (as param^0.0 $0 (as param^1.1 $0 (, $1.0 $0.0)))) (f/2#0^0 1 2)`},
	{`def f(a; $a): [a, $a]; f(1; 2)`, `(def f/2#0 (as param^0.1 $0 (array (, param^1.1 $0.0)))) (f/2#0^0 1 2)`},
	{`def f($a; a): [a, $a]; f(1; 2)`, `(def f/2#0 (as param^0.0 $0 (array (, param^1.1 $0.0)))) (f/2#0^0 1 2)`},
	{`def f(x): def x: 5; x; f(1)`, `(def f/1#0 x/0#1^0) (def x/0#1 5) (f/1#0^0 1)`},
	{`def f(a::b): a::b; f(3)`, `(def f/1#0 param^0.0) (f/1#0^0 3)`},
	{`def f::g: 1; f::g`, `(def f::g/0#0 1) f::g/0#0^0`},
}

var resolverLabelsBindLexically = [][]string{
	{`[label $f | def g: break $f; (label $f | 1, g, 2), 3]`, `(def g/0#0 (break ^0)) (array (label (, (label (, 1 g/0#0^1 2)) 3)))`},
	{`label $a | label $b | break $a, break $b`, `(label (label (, (break ^1) (break ^0))))`},
	{`label $a | 1 as $x | break $a`, `(label (as 1 $0 (break ^1)))`},
	{`label $x | 1 as $x | $x, break $x`, `(label (as 1 $0 (, $0.0 (break ^1))))`},
	{`label $a::b | break $a::b`, `(label (break ^0))`},
}

var resolverPatternsBindEveryVariable = [][]string{
	{`. as [$a, $b] | $a, $b`, `(as . [$1 $0] (, $0.1 $0.0))`},
	{`. as {$a, b: [$c]} | $a, $c`, `(as . {("a" $0 _) ("b" _ [$1])} (, $0.0 $0.1))`},
	{`. as {$a: [$b]} | $a, $b`, `(as . {("a" $0 [$1])} (, $0.0 $0.1))`},
	{`. as [$a] ?// {a: $b} ?// $a | [$a, $b]`, `(as . (?// [$0] {("a" _ $1)} $0) (array (, $0.0 $0.1)))`},
	{`. as [$a, $a] | $a`, `(as . [$_ $0] $0.0)`},
	{`. as {$a, b: $a} | $a`, `(as . {("a" $0 _) ("b" _ $_)} $0.0)`},
	{`. as [$a, [$a]] | $a`, `(as . [$_ [$0]] $0.0)`},
	{`. as {$a, b: $a} ?// $c | $a`, `(as . (?// {("a" $0 _) ("b" _ $0)} $1) $0.0)`},
	{`. as {x: $c} ?// [$a, $a] | $a`, `(as . (?// {("x" _ $0)} [$1 $1]) $0.1)`},
	{`"b" as $x | . as {a: $x, ($x): $y} | $y`, `(as "b" $0 (as . {("a" _ $0) ($0.0 _ $1)} $0.1))`},
	{`. as {a: $x, ($x): $y} | $y`, `(as . {("a" _ $0) ($$x _ $1)} $0.1)`},
	{`1 as $x | reduce . as $y ($x; $x, $y)`, `(as 1 $0 (reduce . $0 $0.0 (, $1.0 $0.0)))`},
	{`foreach .[] as [$a] (0; $a; [$a, .])`, `(foreach (iter .) [$0] 0 $0.0 (array (, $0.0 .)))`},
	{`reduce .[] as $x (.; .) | $x`, `(| (reduce (iter .) $0 . .) $$x)`},
}

var resolverUnboundVariablesAreHostVariables = [][]string{
	{`$foo, $bar, $foo`, `(, $$foo $$bar $$foo)`},
	{`$__prog__`, `$$__prog__`},
	{`$ENV`, `$ENV`},
	{`1 as $ENV | $ENV`, `(as 1 $0 $0.0)`},
	{`def env: $ENV; env`, `(def env/0#0 $ENV) env/0#0^0`},
}

var resolverTheLocationIsALiteral = [][]string{
	{`$__loc__`, `{"file":"<top-level>","line":1}`},
	{"1 as $x |\n\n$__loc__", `(as 1 $0 {"file":"<top-level>","line":3})`},
}

var resolverCallsResolveToUserThenPreludeThenNative = [][]string{
	{`length`, `native:length/0`},
	{`map(.)`, `(prelude:map/1^0 .)`},
	{`1 as $x | 2 as $y | map($x)`, `(as 1 $0 (as 2 $0 (prelude:map/1^2 $1.0)))`},
	{`def map(f): 1; map(.)`, `(def map/1#0 1) (map/1#0^0 .)`},
	{`def length: 1; length`, `(def length/0#0 1) length/0#0^0`},
	{`..`, `prelude:recurse/0^0`},
	{`def recurse: 1; ..`, `(def recurse/0#0 1) recurse/0#0^0`},
	{`.a |= 1`, `(native:_modify/2 (index . "a") 1)`},
	{`.a = 1`, `(native:_assign/2 (index . "a") 1)`},
	{`.a += 1`, `(as 1 $0 (native:_modify/2 (index . "a") (+ . $0.0)))`},
	{`def _modify(a; b): 42; .a |= 1`, `(def _modify/2#0 42) (_modify/2#0^0 (index . "a") 1)`},
	{`def true: 1; true`, `(def true/0#0 1) true`},
}

var resolverUnknownNamesAreBindingErrors = [][]string{
	{`foo`, `foo/0 is not defined @1:1`},
	{`1 | foo(1; 2)`, `foo/2 is not defined @1:5`},
	{`map(1; 2)`, `map/2 is not defined @1:1`},
	{"1 |\n  foo", `foo/0 is not defined @2:3`},
	{`foo::bar`, `foo::bar/0 is not defined @1:1`},
	{`1 + foo, bar`, `foo/0 is not defined @1:5`},
	{`foo(bar)`, `foo/1 is not defined @1:1`},
	{`map(bar)`, `bar/0 is not defined @1:5`},
	{`def f: g; def g: 1; f`, `g/0 is not defined @1:8`},
	{`def f: def g: 3; g; g`, `g/0 is not defined @1:21`},
	{`(def f: 1; f), f`, `f/0 is not defined @1:16`},
	{`def f(g): g(1); f(1)`, `g/1 is not defined @1:11`},
	{`def f($a): a(1); f(1)`, `a/1 is not defined @1:12`},
	{`. as $foo | break $foo`, `$*label-foo is not defined @1:13`},
	{`label $f | def g: 1; break $g`, `$*label-g is not defined @1:22`},
	{`def g: break $f; label $f | g`, `$*label-f is not defined @1:8`},
	{`$foo::bar`, `$foo::bar is not defined @1:1`},
	{`1 as $x | $x::y`, `$x::y is not defined @1:11`},
	{`. as {(foo): $x} | $x`, `foo/0 is not defined @1:8`},
	{`reduce . as $x (0; foo)`, `foo/0 is not defined @1:20`},
	{`foreach . as $x (0; .; foo)`, `foo/0 is not defined @1:24`},
	{`import "a" as a; 1`, `module not found: a @1:1`},
	{`1 as $x | include "a"; 1`, `Unexpected 'include' @1:11`},
}
