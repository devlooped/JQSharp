"""The runtime (dotnet/Tests/Core/Runtime): signals through every construct, laziness, path mode,
the depth limit and the host stack, the host interface, native _modify/_assign against the prelude."""

from __future__ import annotations

import threading
import time
import unittest

from jqlite.runtime.evaluation import MAX_DEPTH, EvaluationOptions
from jqlite.values.jq_object import JqObject

from .evaluate import compile, json, line, on_small_stack_line, on_stack, run
from .test_values import on_small_stack


class InterpreterTests(unittest.TestCase):
    def check(self, cases: list[tuple[str, str]], input: str = "null", options: EvaluationOptions | None = None) -> None:
        for program, expected in cases:
            with self.subTest(program=program):
                self.assertEqual(expected, line(program, input, options))

    def test_signals_pass_through_every_construct(self) -> None:
        self.check([
            ('[try (1, error("x"), 3) catch .]', '[1,"x"]'),
            ("[.[] | try error catch .]", 'error: "Cannot iterate over null (null)"'),
            ("[.[]?]", "[]"), ("try error({}) catch .", "{}"), ("[(1, 2) | try error catch . + 10]", "[11,12]"),
            ('try (try error("a") catch error("b")) catch .', '"b"'),
            ('"x" | try ((try . catch "too much") | error) catch "right"', '"right"'),
            ('[try ((try (1,2) catch "inner") | if . == 2 then error("consumer") else . end) catch ("outer: " + .)]', '[1,"outer: consumer"]'),
            ("try ((1, 2)? | error(tostring)) catch .", '"1"'),
            ("try (([[1]] | .[] as [$a] ?// $a | $a) | error(tostring)) catch .", '"1"'),
            ("[[[1],2][] as [$a] ?// $a | $a]", "[1,2]"),
            ('[[[1]][] as [$a] ?// $a | if ($a | type) == "number" then error("n") else $a end]', "[[1]]"),
            ("try (1 as [$a] ?// {$a} | $a) catch .", '"Cannot index number with string (\\"a\\")"'),
            ('[reduce ([1],[5]) as [$a] ?// $a (10; if $a == 1 then error("e") else [., $a] end)]', "[[[10,[1]],5]]"),
            ('[foreach ([1]) as [$a] ?// $a (10; . + 1; if $a == 1 then ., error("x") else [., $a] end)]', "[11,[11,[1]]]"),
            ('try (reduce ([1],[2]) as [$a] ?// $a (0; if $a == 1 then error("e") else . + $a end)) catch .',
             '"number (0) and array ([1]) cannot be added"'),
            ('[reduce ({"a":1}, [2]) as {a: $a} ?// [$a] (0; . + $a)]', "[3]"),
            ("try (foreach ([1]) as [$a] ?// $a (0; .; $a) | error(tostring)) catch .", '"1"'),
            ('[foreach ([1], [2]) as [$a] ?// $a (0; . + (if $a == 2 then error("two") else $a end); .)]',
             'error: "number (1) and array ([2]) cannot be added"'),
            ("[5,6] | . as {(.[0] * 0 + 1): $v} | $v", "6"),
            ('{"a":1} | . as {(.a): $v} | $v', 'error: "Cannot index object with number (1)"'),
            ('null | . as {(0 + 1): $v, ("b"): $w} | [$v, $w]', "[null,null]"),
            ('try [(1, error("x")) // 3] catch .', '"x"'),
            ("[empty // 3, (false, null) // 4, (1, null, 2) // 5]", "[3,4,1,2]"),
            ("[label $f | def g: break $f; (label $f | 1, g, 2), 3]", "[1]"),
            ("[label $a | label $b | 1, break $a, 2]", "[1]"),
            ("label $out | try (0, break $out, 1) catch 99", "0"),
            ("def f: label $x | (1, break $x); [f, f]", "[1,1]"),
            ("reduce (1,2,3) as $x (0; empty)", "null"), ("[reduce empty as $x (1, 2; .)]", "[1,2]"),
            ("reduce (1,2) as $x (0; ., 10)", "10"), ("[foreach (1,2) as $x (0; (. + 1, . + 10); .)]", "[1,10,11,20]"),
            ("[foreach (1,2) as $x (0; empty; .)]", "[]"), ("[foreach (1,2) as $x (0, 1; . + $x)]", "[1,3,2,4]"),
            ("1, halt, 2", "1, halt 0"), ('try ("bye" | halt_error(5)) catch 0', 'halt 5 "bye"'),
            ('try halt_error("x") catch .', '"null (null) halt_error/1: number required"'),
        ])

    def test_consumers_that_stop_stop_their_producers(self) -> None:
        for program, expected in [
            ("first(range(1000000000))", "0"), ("[limit(3; repeat(1))]", "[1,1,1]"), ("isempty(repeat(1))", "false"),
            ('[label $f | 1, break $f, error("unreached")]', "[1]"), ("first(range(10; infinite))", "10"),
            ('[limit(1; 1, error("unreached"))]', "[1]"), ("[.[] | select(. > 1)] | first", "2"),
            ("any(range(1000000000); . > 2)", "true"),
        ]:
            with self.subTest(program=program):
                start = time.perf_counter()
                self.assertEqual(expected, line(program, "[1,2,3]"))
                self.assertLess(time.perf_counter() - start, 5)

    def test_the_host_stops_the_stream(self) -> None:
        self.assertEqual(["0", "1", "2"], run("range(1000000000)", take=3))

    def test_paths_are_part_of_evaluation(self) -> None:
        self.check([
            ("path(.)", "[]"),
            ("[path(.a.b, .c[1:2], .c[])]", '[["a","b"],["c",{"start":1,"end":2}],["c",0],["c",1]]'),
            ("[path(.a[.k])]", '[["a","x"]]'),
            ("[path(.z?, (.c | try .[] catch .), (.a | .b))]", '[["z"],["c",0],["c",1],["a","b"]]'),
            ('[path(if .k == "x" then .a else .c end)]', '[["a"]]'), ("[path(.z // .a)]", '[["a"]]'),
            ("[path(.k as $k | .a[$k])]", '[["a","x"]]'), ('[path(reduce ("a", "b") as $k (.; .[$k]))]', '[["a","b"]]'),
            ('[path(foreach ("a", "b") as $k (.; .[$k]))]', '[["a"],["a","b"]]'), ("[path(label $x | .a, break $x)]", '[["a"]]'),
            ("def f(p): p | .b; [path(f(.a))]", '[["a","b"]]'), ('[path(empty), path(getpath(["a", "q"]))]', '[["a","q"]]'),
            ("[path(first(.c[]), last(.c[]), limit(1; .c[]), skip(1; .c[]))]", '[["c",0],["c",1],["c",0],["c",1]]'),
            ('[path(.. | select(type == "number"))]', '[["a","b"],["c",0],["c",1]]'),
            ("[path(.c | recurse(if . == null then empty else .[0]? end))]", '[["c"],["c",0]]'),
            ("try path(1) catch .", '"Invalid path expression with result 1"'),
            ("try path($v | .a) catch .", '"Invalid path expression near attempt to access element \\"a\\" of null"'),
            ("try path(.k | ascii_upcase | .[]) catch .", '"Invalid path expression near attempt to iterate through \\"X\\""'),
            ('try path(.k + "y") catch .', '"Invalid path expression with result \\"xy\\""'),
        ], '{"a":{"b":0},"k":"x","c":[1,2]}', EvaluationOptions(variables={"v": None}))

    def test_assignment_works_through_the_prelude(self) -> None:
        self.check([
            (".a = 1", '{"a":1,"b":[1,2]}'), (".b[] |= . * 10", '{"a":0,"b":[10,20]}'), (".b[1] += 5", '{"a":0,"b":[1,7]}'),
            ("[.a += (1, 2)] | map(.a)", "[1,2]"), ('.c //= "d"', '{"a":0,"b":[1,2],"c":"d"}'),
            ("(.a, .z) = 9", '{"a":9,"b":[1,2],"z":9}'), (".b |= map(select(. > 1))", '{"a":0,"b":[2]}'),
            ("(.b[] | select(. == 1)) |= empty", '{"a":0,"b":[2]}'), ("del(.a, .b[0])", '{"b":[2]}'),
            ("to_entries", '[{"key":"a","value":0},{"key":"b","value":[1,2]}]'),
            ("with_entries(.value |= tostring)", '{"a":"0","b":"[1,2]"}'), ("[paths]", '[["a"],["b"],["b",0],["b",1]]'),
            ('def _modify(p; f): "mine"; .a |= 1', '"mine"'),
        ], '{"a":0,"b":[1,2]}')

    def test_host_variables_and_env(self) -> None:
        self.check([
            ("$x", "1"), ("[$x, $y]", '[1,"two"]'), ("$x as $x | $x + 1", "2"), ("$missing", 'error: "$missing is not defined"'),
            ("try $missing catch .", '"$missing is not defined"'), ("$ENV.HOME, env.HOME", '"/home", "/home"'), ("1 as $ENV | $ENV", "1"),
        ], options=EvaluationOptions(variables={"x": 1.0, "y": "two"}, environment=lambda: JqObject.EMPTY.set("HOME", "/home")))

    def test_one_program_runs_concurrently_with_different_variables(self) -> None:
        program = compile("def f: if . == 0 then $n else (. - 1 | f) end; [range(200) | {a: f}] | map(.a) | add")
        results: list = [None] * 32

        def work(i: int) -> None:
            results[i] = ", ".join(run(program, 50.0, EvaluationOptions(variables={"n": float(i)})))
        threads = [threading.Thread(target=work, args=(i,)) for i in range(len(results))]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        for i, result in enumerate(results):
            self.assertEqual(str(200 * i), result)

    def test_the_call_depth_is_limited(self) -> None:
        count = "def f: if . == 0 then 0 else (. - 1 | f) end; "
        for max_depth in (1000, 50):
            options = EvaluationOptions(max_depth=max_depth)
            # n | f makes n + 1 calls.
            self.assertEqual("0", on_small_stack_line(count + str(max_depth - 1) + " | f", options=options))
            self.assertEqual('error: "Maximum call depth exceeded"', on_small_stack_line(count + str(max_depth) + " | f", options=options))
            self.assertEqual('"Maximum call depth exceeded"', on_small_stack_line("try (" + count + "100000 | f) catch .", options=options))

    def test_a_single_call_has_ended_when_its_output_is_used(self) -> None:
        # 20 | fib makes 21,891 calls, 20 deep.
        fib = "def fib: if . < 2 then . else (. - 1 | fib) + (. - 2 | fib) end; 20 | fib"
        self.assertEqual("6765", line(fib, options=EvaluationOptions(max_depth=20)))
        self.assertEqual('error: "Maximum call depth exceeded"', line(fib, options=EvaluationOptions(max_depth=19)))
        self.assertEqual("6765", line("def fib($n): if $n < 2 then $n else fib($n - 1) + fib($n - 2) end; fib(20)",
                                      options=EvaluationOptions(max_depth=20)))
        # A call that is not single stays active while its outputs are consumed: the two calls nest.
        many = "def g: if . < 2 then ., empty else (. - 1 | g) + (. - 2 | g) end; "
        self.assertEqual("55", line(many + "10 | g"))
        self.assertEqual('error: "Maximum call depth exceeded"', line(many + "15 | g"))

    def test_tail_recursion_reaches_the_limit_without_the_host_stack(self) -> None:
        self.assertEqual('error: "Maximum call depth exceeded"',
                         on_small_stack_line("def f: if . == 0 then 0 else (. - 1 | f) end; 100000 | f"))

    def test_loop_natives_do_not_consume_the_depth(self) -> None:
        for program, expected in [
            ("[limit(5000; repeat(1))] | length", "5000"), ("[range(100000)] | length", "100000"), ("last(range(100000))", "99999"),
            ("0 | until(. >= 100000; . + 1)", "100000"), ("[0 | while(. < 100000; . + 1)] | length", "100000"),
            ("[skip(99998; range(100000))]", "[99998,99999]"),
        ]:
            with self.subTest(program=program):
                self.assertEqual(expected, on_small_stack_line(program, options=EvaluationOptions(max_depth=10)))

    def test_outputs_come_in_the_order_of_the_spec(self) -> None:
        self.check([
            ("[1, 2] | [.[] as $x | $x * 10]", "[10,20]"), ("[(1,2) + (10,20)]", "[11,12,21,22]"),
            ("[{a: (1,2), b: (3,4)}]", '[{"a":1,"b":3},{"a":1,"b":4},{"a":2,"b":3},{"a":2,"b":4}]'),
            ('["\\(1,2)-\\(3,4)"]', '["1-3","2-3","1-4","2-4"]'), ("[range(0,1; 3,4)]", "[0,1,2,0,1,2,3,1,2,1,2,3]"),
            ("[.[(0,1)]]", "[1,2]"), ("[range(10)] | [.[(1,2):(3,4)] | length]", "[2,3,1,2]"),
            ("[(true, false) and (true, false)]", "[true,false,false]"), ("[(true, false) or (true, false)]", "[true,true,false]"),
            ("[-(1, 2)]", "[-1,-2]"), ('[{("a", "b"): 1}]', '[{"a":1},{"b":1}]'),
            ('{"x1":"a","x2":"b"} | [{"x\\(1,2)"}]', '[{"x1":"a"},{"x2":"b"}]'), ("[. as [$a, $b] | {$a, $b}]", '[{"a":1,"b":2}]'),
            ('. as {("a", "b"): $v} ?// $v | $v', "[1,2]"), ('{"a":1,"b":2} | [. as {("a", "b"): $v} | $v]', "[1,2]"),
        ], "[1,2]")

    def test_debug_and_stderr_go_to_the_handlers(self) -> None:
        debugged: list = []
        errors: list = []
        options = EvaluationOptions(debug=debugged.append, stderr=errors.append)
        self.assertEqual("1", line('debug, debug("m") | stderr | empty, .', "1", options).split(", ")[-1])
        self.assertEqual([1.0, "m"], debugged[:2])
        # (debug, debug("m")) | stderr | (empty, .): stderr sees both outputs of the debugs.
        self.assertEqual([1.0, 1.0], errors)

    def test_now_needs_a_clock(self) -> None:
        self.assertEqual('"now/0 requires a host clock"', line("try now catch ."))


class AssignTests(unittest.TestCase):
    """The native _modify and _assign against their prelude definitions (§8.3): each program is
    compiled twice, with the native overrides and without them, and both must give the same
    outputs, errors included."""

    def test_native_and_prelude_agree(self) -> None:
        cases = [
            ("(.a, .a.b) |= {b: 1}", '{"a":{"b":0}}'), ("(.a.b, .a) |= {x: .}", '{"a":{"b":0}}'), ("(.a, .a) |= . + 1", '{"a":1}'),
            ("(.a[0], .a, .a[0]) |= [.]", '{"a":[1,2]}'),
            ('.. |= (if type == "number" then . + 1 else . end)', '{"a":[1,{"b":2}],"c":3}'),
            ("[..] as $all | .. |= length", '[[1,[2]],{"x":3}]'), (".a |= ., .b |= [., .]", '{"a":[1],"b":{"c":2}}'),
            ("(.a, .a[0]) |= [., .]", '{"a":[1]}'), (". as $in | .a |= $in", '{"a":1}'),
            ("(.[] | select(. >= 2)) |= empty", "[1,5,3,0,7]"), (".[] |= (if . % 2 == 0 then empty else . * 10 end)", "[1,2,3,4,5]"),
            ("(.a, .b, .c) |= (if . == null then empty else . + 1 end)", '{"a":1,"c":3}'), ("(.a.x, .a) |= empty", '{"a":{"x":1}}'),
            ('to_entries |= map(select(.key != "b")) | from_entries', '{"a":1,"b":2}'),
            ('(.a, .b, .c) |= (if . == 2 then error("two") else . end)', '{"a":1,"b":2,"c":3}'),
            ("(.a, .b.x) |= 1", '{"a":0,"b":5}'), ("(.a, 1) |= 1", '{"a":0}'), (".[0] |= 1", "{}"), ("(.a, .[0]) |= 1", "null"),
            ("(.a | .b) |= 1", '{"a":[1]}'), ("(.a, .b) = (1, 2)", "{}"), (".a = empty", "{}"), ("empty = 1", '{"a":0}'),
            (".a = .b", '{"a":0,"b":1}'), ("(.a, .a.b) = 1", "{}"), ('.a[1:3] = ["x"]', '{"a":[0,1,2,3]}'),
            (".[1:3] |= map(. * 10)", "[0,1,2,3,4]"), ('(.[1:3], .[0]) |= (if type == "array" then [] else -1 end)', "[0,1,2,3,4]"),
            (".[2:4][0] |= 9", "[0,1,2,3,4]"), ('.[1:2] |= "x"', '"abc"'), (".a.b[2].c |= 1", "null"), (".[-1] |= . + 1", "[1,2,3]"),
            (".[-5] |= 1", "[1,2,3]"), (".[nan] |= 1", "[1]"), (".[3] |= 1", "[1]"), (".[536870912] |= 1", "[]"),
            (".[] += 1, .a -= 1, .b *= 2, .c /= 2, .d %= 2, .e //= 7", '{"a":1,"b":2,"c":4,"d":5,"e":null}'),
            ("map_values(. + 1)", '{"a":1,"b":2}'), ('walk(if type == "number" then . + 1 else . end)', '[1,[2,{"a":3}]]'),
            ("INDEX(.id)", '[{"id":1},{"id":2}]'), ('sub("b"; "x")', '"abc"'), ("path(.a |= 1)", '{"a":0}'),
            ("path(empty = 1)", '{"a":0}'), ("[path(.a = 1)]", '{"a":0}'), (".a |= (1, 2)", '{"a":0}'),
            ("(.a, .b) |= (.x |= 1)", '{"a":{},"b":null}'),
        ]
        for program, input in cases:
            with self.subTest(program=program):
                native = ", ".join(run(compile(program, True), input))
                prelude = ", ".join(run(compile(program, False), input))
                self.assertEqual(prelude, native)

    def test_native_updates_are_linear(self) -> None:
        # The time of `.[] |= . + 1` on 4,000 and on 32,000 elements: 8 times the work is about 8
        # times the time when the update is linear, 64 when it is quadratic. The bound, 16, sits
        # between them; the prelude definition, which copies the array once per element, is over it.
        def growth(native_overrides: bool) -> float:
            def best(n: int) -> float:
                program = compile(f"[range({n})] | .[] |= . + 1 | length", native_overrides)
                times = []
                for _ in range(3):
                    start = time.perf_counter()
                    self.assertEqual(str(n), line(program))
                    times.append(time.perf_counter() - start)
                return min(times)
            return best(32_000) / best(4_000)
        native = growth(True)
        self.assertLess(native, 16, f"the native update grows {native:.1f} times for 8 times the elements")
        prelude = growth(False)
        self.assertGreater(prelude, 16, f"the measure no longer tells: the quadratic prelude grows only {prelude:.1f} times")

    def test_paths_of_ten_thousand_keys(self) -> None:
        deep = "reduce range(10000) as $i (0; [.]) | [range(10000) | 0] as $p | "
        self.assertEqual("1", on_small_stack_line(deep + "getpath($p) |= . + 1 | getpath($p)"))
        self.assertEqual("2", on_small_stack_line(deep + "setpath($p; 2) | getpath($p)"))
        self.assertEqual("[]", on_small_stack_line(deep + "delpaths([$p]) | getpath($p[1:])"))
        self.assertEqual("[]", on_small_stack_line(deep + "del(getpath($p)) | getpath($p[1:])"))
        self.assertEqual("3", on_small_stack_line(deep + "getpath($p) = 3 | getpath($p)"))


STACKS = (1024 * 1024, 256 * 1024)


class StackGuardTests(unittest.TestCase):
    """MaxDepth is the only recursion limit a program meets (spec §11): on a small host stack, deep
    evaluations work up to MaxDepth and fail with its error past it, never with a host failure."""

    def test_nesting_to_the_parser_limit_works(self) -> None:
        cases = [
            ("[", "]", "[" * 1000 + "1" + "]" * 1000), ("{a: ", "}", '{"a":' * 1000 + "1" + "}" * 1000),
            (".[", "]", 'error: "Cannot index null with null (null)"'), ('"\\(', ')"', '"1"'), ("reduce . as $x (", ";.)", "1"),
            ("[1 + ", "]", "error"), ("1 + (", ")", "1001"), ("[label $x | ", "]", "[" * 1000 + "1" + "]" * 1000),
            ("if . then 1 else ", " end", "1"), ("try ", "", "1"),
        ]
        for open_, close, expected in cases:
            for stack in STACKS:
                with self.subTest(open=open_, stack=stack):
                    result = on_stack(stack, open_ * 1000 + "1" + close * 1000)
                    if expected == "error":
                        self.assertTrue(result.startswith('error: "number (1) and array ([2]) cannot be added'), result[:80])
                    else:
                        self.assertEqual(expected, result)

    def test_recursion_reaches_exactly_max_depth(self) -> None:
        recursions = [
            ("def f: if . == 0 then 0 else (. - 1 | f) + 1 end; N | f", 1024 * 1024),
            ("def f: if . == 0 then 0 else (. - 1 | f) + 1 end; N | f", 256 * 1024),
            ("def f: if . == 0 then 0 else [. - 1 | f] | length end; N | f", 256 * 1024),
            ("def f: if . == 0 then 0 else ((. - 1, . - 1) | f) end; label $out | (N | f) | ., break $out", 1024 * 1024),
            ("def f: if . == 0 then 0 else ((. - 1, . - 1) | f) end; label $out | (N | f) | ., break $out", 256 * 1024),
            ("def f(g): if . == 0 then g else (. - 1 | f(g)) + 0 end; N | f(0)", 256 * 1024),
        ]
        for program, stack in recursions:
            for max_depth in (1000, 5000):
                with self.subTest(program=program, stack=stack, max_depth=max_depth):
                    options = EvaluationOptions(max_depth=max_depth)
                    deepest = on_stack(stack, program.replace("N", str(max_depth - 1)), options=options)
                    self.assertNotIn("error", deepest)
                    self.assertEqual('error: "Maximum call depth exceeded"',
                                     on_stack(stack, program.replace("N", str(max_depth)), options=options))

    def test_the_largest_max_depth_runs_on_a_small_stack(self) -> None:
        # MAX_DEPTH levels of calls on a 256 KB thread: CPython's Python-to-Python calls do not use the C stack.
        options = EvaluationOptions(max_depth=MAX_DEPTH)
        n = str(MAX_DEPTH - 1)
        self.assertEqual(n, on_stack(256 * 1024, f"def f: if . == 0 then 0 else (. - 1 | f) + 1 end; {n} | f", options=options))
        # Not single: every call stays active while its output is consumed.
        self.assertEqual(n, on_stack(256 * 1024, f"def f: if . == 0 then 0, empty else (. - 1 | f) + 1 end; {n} | f", options=options))
        self.assertEqual('error: "Maximum call depth exceeded"',
                         on_stack(256 * 1024, f"def f: if . == 0 then 0 else (. - 1 | f) + 1 end; {MAX_DEPTH} | f", options=options))

    def test_a_stop_of_the_host_comes_back_from_deep_recursion(self) -> None:
        program = compile("def f: if . == 0 then (1, 2, 3) else (. - 1 | f) + 0 end; 900 | f")
        for stack in STACKS:
            self.assertEqual(["1"], on_small_stack(None, lambda: run(program, None, take=1), stack))  # type: ignore[arg-type]

    def test_an_error_comes_back_from_deep_recursion(self) -> None:
        for stack in STACKS:
            self.assertEqual('"deep"', on_stack(stack, 'try (def f: if . == 0 then error("deep") else (. - 1 | f) + 1 end; 900 | f) catch .'))

    def test_values_ten_thousand_levels_deep_are_read_written_and_compared(self) -> None:
        deep = "[" * 10000 + "]" * 10000
        self.assertEqual("true", on_stack(256 * 1024, f'"{deep}" | fromjson | (tojson | fromjson) == . and (. < [[1]]) == false'))
        self.assertEqual("true", on_stack(256 * 1024, "reduce range(10000) as $i (0; [.]) | . == . and (tojson | length) == 20001"))
        self.assertEqual("true", on_stack(256 * 1024, f'"{deep}" | fromjson | contains(.)'))
        self.assertEqual(deep, json(deep) and on_stack(256 * 1024, f'"{deep}" | fromjson | tojson')[1:-1])
