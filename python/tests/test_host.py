"""The Python boundary (JqBoundaryTests, JqVariableTests, JqExpressionTests, JqModuleTests,
JqConcurrencyTests, JqDepthTests, JqJsonlStreamingTests): options, exceptions, streaming,
variables, value conversion, resolvers, threads and the depth limit through the public API."""

from __future__ import annotations

import asyncio
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import traceback
import unittest
from typing import Any

import jqlite
from jqlite.syntax import recursion_limit

from . import corpus
from .test_values import on_small_stack


def texts(results: list) -> list[str]:
    return [json.dumps(r, ensure_ascii=False, separators=(",", ":")) for r in results]


class FixedClock:
    def __init__(self, now: float, offset: int, abbreviation: str | None = None) -> None:
        self._now = now
        self._offset = offset
        self._abbreviation = abbreviation

    def now(self) -> float:
        return self._now

    def offset(self, seconds: float) -> int:
        return self._offset

    def abbreviation(self, seconds: float) -> str | None:
        return self._abbreviation


class BoundaryTests(unittest.TestCase):
    def test_options_environment_is_env_and_env(self) -> None:
        options = jqlite.Options(environment={"B": "2", "A": "1"})
        self.assertEqual(['{"B":"2","A":"1"}', '"1"'], texts(jqlite.parse("$ENV, env.A", options).evaluate(None)))

    def test_without_options_env_is_the_process_environment(self) -> None:
        name = next(k for k in os.environ if k.replace("_", "a").isalnum() and k.isascii())
        self.assertEqual([os.environ[name]], jqlite.evaluate(f"$ENV.{name}", None))

    def test_options_clock_gives_now_and_the_local_zone(self) -> None:
        options = jqlite.Options(clock=FixedClock(1425599507.0, -3 * 3600))
        self.assertEqual([1425599507, "2015-03-05T20:51:47 -0300"],
                         jqlite.parse('now, (now | strflocaltime("%Y-%m-%dT%H:%M:%S %z"))', options).evaluate(None))

    def test_options_clock_abbreviation_is_percent_z(self) -> None:
        options = jqlite.Options(clock=FixedClock(0.0, 3600, "CET"))
        self.assertEqual(["CET +0100"], jqlite.parse('0 | strflocaltime("%Z %z")', options).evaluate(None))

    def test_the_system_clock_follows_the_local_zone(self) -> None:
        clock = jqlite.SystemClock()
        self.assertIsInstance(clock.offset(1435708800.0), int)
        # Out of what the C library represents: the offset of now, not a failure.
        self.assertIsInstance(clock.offset(1e15), int)
        self.assertEqual(["number"], jqlite.evaluate("now | type", None))

    def test_options_debug_and_stderr_receive_the_values(self) -> None:
        debugged: list = []
        written: list = []
        options = jqlite.Options(debug=debugged.append, stderr=written.append)
        self.assertEqual([1, 1, 1], jqlite.parse('debug, debug("m", {a: .}), stderr', options).evaluate(1))
        self.assertEqual([1, "m", {"a": 1}], debugged)
        self.assertEqual([1], written)

    def test_without_handlers_debug_and_stderr_only_output_their_input(self) -> None:
        self.assertEqual([1, 1], jqlite.evaluate("debug, stderr", 1))

    def test_halt_error_without_a_code_exits_with_5(self) -> None:
        with self.assertRaises(jqlite.JqHaltError) as raised:
            jqlite.evaluate("halt_error", "bye")
        self.assertEqual(("bye", 5), (raised.exception.message, raised.exception.exit_code))

    def test_options_max_depth_limits_recursion(self) -> None:
        expression = jqlite.parse("def f: if . == 0 then 0 else (. - 1 | f) + 1 end; f", jqlite.Options(max_depth=10))
        self.assertEqual([9], expression.evaluate(9))
        with self.assertRaises(jqlite.JqError) as raised:
            expression.evaluate(10)
        self.assertEqual("Maximum call depth exceeded", raised.exception.message)

    def test_parse_errors_have_a_position(self) -> None:
        for source, message, line, column in [("1 +", "Unexpected end of program at line 1, column 4", 1, 4),
                                              (".a |\n  foo", "foo/0 is not defined at line 2, column 3", 2, 3),
                                              (". as $x | break $x", "$*label-x is not defined at line 1, column 11", 1, 11)]:
            with self.assertRaises(jqlite.JqParseError) as raised:
                jqlite.parse(source)
            e = raised.exception
            self.assertEqual((message, line, column), (e.message, e.line, e.column))
            self.assertIsNone(e.value)

    def test_a_string_error_is_its_message_and_value(self) -> None:
        with self.assertRaises(jqlite.JqError) as raised:
            jqlite.evaluate('error("boom")', None)
        self.assertEqual(("boom", "boom"), (raised.exception.message, raised.exception.value))

    def test_another_error_value_is_its_text_not_a_string(self) -> None:
        with self.assertRaises(jqlite.JqError) as raised:
            jqlite.evaluate("error({a: [1, nan]})", None)
        self.assertEqual('{"a":[1,null]} (not a string)', raised.exception.message)
        self.assertEqual({"a": [1, None]}, raised.exception.value)

    def test_a_builtin_error_is_its_message(self) -> None:
        with self.assertRaises(jqlite.JqError) as raised:
            jqlite.evaluate(". + 1", "a")
        self.assertEqual('string ("a") and number (1) cannot be added', str(raised.exception))

    def test_streaming_gives_results_as_they_come_and_stops_when_asked(self) -> None:
        seen: list = []

        def take(result: object) -> bool:
            seen.append(result)
            return len(seen) < 3
        jqlite.parse("range(1000000000)").stream(None, take)
        self.assertEqual([0, 1, 2], seen)

    def test_streaming_delivers_results_before_an_error(self) -> None:
        seen: list = []
        with self.assertRaises(jqlite.JqError) as raised:
            jqlite.parse('1, 2, error("late"), 3').stream(None, seen.append)
        self.assertEqual([1, 2], seen)
        self.assertEqual("late", raised.exception.message)

    def test_an_exception_of_the_callback_propagates(self) -> None:
        def fail(result: object) -> bool:
            raise KeyError("callback")
        with self.assertRaises(KeyError):
            jqlite.parse("1, 2").stream(None, fail)

    def test_halt_ends_normally(self) -> None:
        self.assertEqual([1], jqlite.evaluate("1, halt, 2", None))

    def test_halt_error_carries_the_value_and_the_code(self) -> None:
        seen: list = []
        with self.assertRaises(jqlite.JqHaltError) as raised:
            jqlite.parse("1, ({a: 1} | halt_error(5)), 2").stream(None, seen.append)
        self.assertEqual([1], seen)
        e = raised.exception
        self.assertEqual((5, {"a": 1}, '{"a":1} (not a string)'), (e.exit_code, e.value, e.message))
        with self.assertRaises(jqlite.JqHaltError) as raised:
            jqlite.evaluate('"bye" | halt_error(0)', None)
        self.assertEqual("bye", raised.exception.message)

    def test_invalid_variable_names_are_rejected_before_evaluation(self) -> None:
        for name in ["$root", "1x", "a-b", "", "ENV", "é"]:
            with self.subTest(name=name):
                with self.assertRaises(ValueError):
                    jqlite.evaluate('error("evaluated")', None, {name: 1})

    def test_variables_are_converted_per_evaluation(self) -> None:
        expression = jqlite.parse("$x + 1")
        self.assertEqual([2], expression.evaluate(None, {"x": 1}))
        self.assertEqual([11], expression.evaluate(None, {"x": 10}))
        with self.assertRaises(jqlite.JqError) as raised:
            expression.evaluate(None)
        self.assertEqual("$x is not defined", raised.exception.message)

    def test_values_cross_the_boundary(self) -> None:
        cases = [
            ("[nan, infinite, -infinite]", None, [None, 1.7976931348623157e308, -1.7976931348623157e308]),
            (".", 100000000000000000000000000001, 1e29), (".", 10 ** 400, 1.7976931348623157e308),
            (". + 1", 9007199254740993, 9007199254740992), (".", -0.0, 0), (".", 1.5, 1.5), (".", 1e21, 1e21), (".", 1e20, 10 ** 20),
            ("explode", "a\ud800b", [97, 65533, 98]), ("keys", {"\udc00": 1}, ["�"]),
            ("tojson", {"b": 1, "a": 2}, '{"b":1,"a":2}'), (". == 1", True, False), ("type", (1, 2), "array"),
        ]
        for program, input, expected in cases:
            with self.subTest(program=program, input=input):
                result = jqlite.evaluate(program, input)
                self.assertEqual([expected], result)
                self.assertIs(type(expected), type(result[0]))

    def test_values_that_are_not_json_are_rejected(self) -> None:
        for value in [object(), {1: 2}, {"a": {1, 2}}]:
            with self.subTest(value=repr(value)):
                with self.assertRaises(TypeError):
                    jqlite.evaluate(".", value)

    def test_results_keep_key_order_and_do_not_share_the_input(self) -> None:
        source = {"z": [1, 2], "a": {"b": None}}
        result = jqlite.evaluate(".", source)[0]
        self.assertEqual(["z", "a"], list(result))
        self.assertEqual(source, result)
        self.assertIsNot(source["z"], result["z"])

    def test_deep_input_and_output_convert_without_recursion(self) -> None:
        value: object = []
        for _ in range(9999):
            value = [value]
        result = on_small_stack(None, lambda: jqlite.evaluate(".", value), 256 * 1024)[0]  # type: ignore[arg-type,index]
        depth = 0
        while result:
            result = result[0]
            depth += 1
        self.assertEqual(9999, depth)

    def test_one_expression_evaluates_concurrently(self) -> None:
        expression = jqlite.parse("[.[] | . * $k] | add")
        results: list = [None] * 32

        def work(i: int) -> None:
            results[i] = expression.evaluate([1, 2, 3], {"k": i})[0]
        threads = [threading.Thread(target=work, args=(i,)) for i in range(len(results))]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        self.assertEqual([6 * i for i in range(len(results))], results)


class VariableTests(unittest.TestCase):
    def test_env_has_keys(self) -> None:
        self.assertEqual([True], jqlite.evaluate("($ENV | keys | length) > 0", None))

    def test_external_variable_is_available_during_evaluation(self) -> None:
        self.assertEqual([{"name": "Alice", "id": 42}], jqlite.evaluate("{name: .name, id: $root.id}", {"name": "Alice"}, {"root": {"id": 42}}))

    def test_external_variable_missing_at_evaluation_raises(self) -> None:
        with self.assertRaises(jqlite.JqError) as raised:
            jqlite.parse("$root").evaluate(None)
        self.assertEqual("$root is not defined", raised.exception.message)

    def test_external_variable_is_shadowed_by_as_binding(self) -> None:
        self.assertEqual([2], jqlite.evaluate("2 as $x | $x", None, {"x": 1}))

    def test_multiple_external_variables_can_be_bound(self) -> None:
        self.assertEqual([["A", "B"]], jqlite.evaluate("[$a, $b]", None, {"a": "A", "b": "B"}))


class ExpressionTests(unittest.TestCase):
    def test_an_empty_expression_is_the_identity(self) -> None:
        for source in ["", "   ", "# only a comment", "def f: 1;"]:
            self.assertEqual([[1]], jqlite.evaluate(source, [1]))

    def test_parse_rejects_what_is_not_a_string(self) -> None:
        with self.assertRaises(TypeError):
            jqlite.parse(None)  # type: ignore[arg-type]

    def test_parse_raises_for_invalid_expression(self) -> None:
        with self.assertRaises(jqlite.JqParseError):
            jqlite.parse("[invalid!!!")

    def test_parsed_expression_can_be_reused_across_inputs(self) -> None:
        expression = jqlite.parse(".x + .y")
        self.assertEqual([[3], [30], [300]], [expression.evaluate({"x": x, "y": 2 * x}) for x in (1, 10, 100)])

    def test_parsed_expression_handles_multiple_outputs(self) -> None:
        self.assertEqual([10, 20, 30], jqlite.parse(".[]").evaluate([10, 20, 30]))

    def test_programs_on_several_lines(self) -> None:
        self.assertEqual([42], jqlite.evaluate("# comment\n.foo\n", {"foo": 42}))
        self.assertEqual([99], jqlite.evaluate(".foo # pick foo\n| .bar\n", {"foo": {"bar": 99}}))
        self.assertEqual([42], jqlite.evaluate("# comment \\\n# still comment\n.foo\n", {"foo": 42}))
        self.assertEqual([42], jqlite.evaluate("# comment \\\\\n.foo\n", {"foo": 42}))


class ModuleTests(unittest.TestCase):
    """The public resolvers through parse; what modules mean is the corpus's."""

    def setUp(self) -> None:
        self.directory = tempfile.mkdtemp(prefix="jqlite_modules_")

    def tearDown(self) -> None:
        shutil.rmtree(self.directory, ignore_errors=True)

    def write(self, name: str, content: str) -> None:
        path = os.path.join(self.directory, name)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(content)

    def run_with(self, expression: str, resolver: jqlite.Resolver | None, input: object = None) -> list:
        return jqlite.parse(expression, jqlite.Options(resolver=resolver)).evaluate(input)

    def test_file_resolver_adds_the_extension_of_the_kind(self) -> None:
        self.write("utils.jq", "def inc: . + 1;")
        self.write("config.json", '{"key":"value"} 2')
        self.assertEqual([6, [{"key": "value"}, 2]],
                         self.run_with('include "utils"; import "config" as $c; inc, $c', jqlite.FileResolver(self.directory), 5))

    def test_file_resolver_keeps_an_explicit_extension(self) -> None:
        self.write("ext.jq", "def ext: . + 100;")
        self.assertEqual([105, 105], self.run_with('include "ext.jq"; import "ext.jq" as e; ext, e::ext', jqlite.FileResolver(self.directory), 5))

    def test_file_resolver_resolves_from_the_importing_module_directory(self) -> None:
        self.write("lib/outer.jq", 'include "inner"; def outer: inner * 2;')
        self.write("lib/inner.jq", "def inner: . + 10;")
        self.assertEqual([30], self.run_with('include "lib/outer"; outer', jqlite.FileResolver(self.directory), 5))

    def test_a_missing_module_is_a_parse_error(self) -> None:
        with self.assertRaises(jqlite.JqParseError) as raised:
            self.run_with('include "nonexistent"; .', jqlite.FileResolver(self.directory))
        self.assertEqual("module not found: nonexistent at line 1, column 1", raised.exception.message)
        self.assertEqual((1, 1), (raised.exception.line, raised.exception.column))

    def test_without_a_resolver_no_module_exists(self) -> None:
        with self.assertRaises(jqlite.JqParseError):
            self.run_with('import "something" as m; .', None)
        with self.assertRaises(jqlite.JqParseError):
            self.run_with('import "data" as $d; $d', None)
        self.assertEqual(["module not found: x"], self.run_with('try ("x" | modulemeta) catch .', None))

    def test_an_error_in_a_module_names_it(self) -> None:
        self.write("bad.jq", "def ok: 1;\ndef broken: (1;")
        with self.assertRaises(jqlite.JqParseError) as raised:
            self.run_with('import "bad" as b; 1', jqlite.FileResolver(self.directory))
        self.assertEqual((2, 15), (raised.exception.line, raised.exception.column))
        self.assertIn(os.path.join(self.directory, "bad.jq"), raised.exception.message)

    def test_data_modules_bind_an_array_of_their_values(self) -> None:
        self.write("config.json", '{"key":"value"}')
        self.assertEqual([[{"key": "value"}], "value"],
                         self.run_with('import "config" as $cfg; $cfg::cfg, $cfg[0].key', jqlite.FileResolver(self.directory)))

    def test_invalid_data_is_a_parse_error(self) -> None:
        self.write("bad.json", "not valid json {{{")
        with self.assertRaises(jqlite.JqParseError):
            self.run_with('import "bad" as $b; $b', jqlite.FileResolver(self.directory))

    def test_each_module_is_read_once_per_parse(self) -> None:
        self.write("counted.jq", "def counted: . + 1;")
        inner = jqlite.FileResolver(self.directory)

        class Counting(jqlite.Resolver):
            opens = 0

            def get_canonical_path(self, path: str, kind: jqlite.ModuleKind, metadata: object, from_path: str | None) -> str | None:
                return inner.get_canonical_path(path, kind, metadata, from_path)  # type: ignore[arg-type]

            def read(self, canonical_path: str) -> str:
                Counting.opens += 1
                return inner.read(canonical_path)
        self.assertEqual([6], self.run_with('include "counted"; import "counted" as c1; import "counted" as c2; c1::counted', Counting(), 5))
        self.assertEqual(1, Counting.opens)

    def test_modulemeta_resolves_through_the_resolver_at_run_time(self) -> None:
        self.write("meta.jq", 'module {"version": "1.0"}; def double: . * 2;')
        expression = jqlite.parse("modulemeta", jqlite.Options(resolver=jqlite.FileResolver(self.directory)))
        results: list = [None] * 8

        def work(i: int) -> None:
            results[i] = expression.evaluate("meta")[0]
        threads = [threading.Thread(target=work, args=(i,)) for i in range(len(results))]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        for result in results:
            self.assertEqual({"version": "1.0", "deps": [], "defs": ["double/0"]}, result)

    def test_a_custom_resolver_names_and_reads_modules(self) -> None:
        modules = {"mem:greet.jq": 'include "punctuation"; def greet: "hello " + . + mark;', "mem:punctuation.jq": 'def mark: "!";'}

        class Memory(jqlite.Resolver):
            def get_canonical_path(self, path: str, kind: jqlite.ModuleKind, metadata: object, from_path: str | None) -> str | None:
                name = "mem:" + path + (".jq" if kind == jqlite.ModuleKind.CODE else ".json")
                return name if name in modules else None

            def read(self, canonical_path: str) -> str:
                return modules[canonical_path]
        self.assertEqual(["hello world!"], self.run_with('include "greet"; greet', Memory(), "world"))

    def test_a_resolver_error_is_a_parse_error(self) -> None:
        class Failing(jqlite.Resolver):
            def get_canonical_path(self, path: str, kind: jqlite.ModuleKind, metadata: object, from_path: str | None) -> str | None:
                raise OSError("no disk")
        with self.assertRaises(jqlite.JqParseError) as raised:
            self.run_with('import "a" as a; 1', Failing())
        self.assertIn("no disk", raised.exception.message)


class ConcurrencyTests(unittest.TestCase):
    """An expression, the prelude and the modules are immutable and shared; one evaluation owns all
    its state. Many threads evaluating at once must each get what a single thread gets."""

    PROGRAM = """
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
        }
        """

    OPTIONS = jqlite.Options(environment={"JQ_CONCURRENCY": "set"}, resolver=jqlite.FileResolver(corpus.MODULES))

    @staticmethod
    def evaluate(expression: jqlite.Expression, n: int) -> str:
        input = {"xs": list(range(n - 2, n + 5)), "text": f"ab{n}cd ef{n}gh", "a": n * 10}
        return json.dumps(expression.evaluate(input, {"n": n, "name": f"<{n}>"}))

    def test_one_expression_gives_each_thread_the_single_threaded_result(self) -> None:
        expression = jqlite.parse(self.PROGRAM, self.OPTIONS)
        reference = jqlite.parse(self.PROGRAM, self.OPTIONS)
        expected = {n: self.evaluate(reference, n) for n in range(3, 12)}
        self.assertIn('"module":', expected[3])
        self.assertIn('"env": "set"', expected[3])
        wrong: list = []
        barrier = threading.Barrier(16)

        def work(t: int) -> None:
            barrier.wait()
            for i in range(100):
                n = 3 + (t * 7 + i) % 9
                actual = self.evaluate(expression, n)
                if actual != expected[n]:
                    wrong.append(f"thread {t}, round {i}, n = {n}: {actual}")
        threads = [threading.Thread(target=work, args=(t,)) for t in range(16)]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        self.assertEqual([], wrong[:5])

    def test_different_expressions_parse_and_evaluate_at_once(self) -> None:
        wrong: list = []
        barrier = threading.Barrier(16)

        def work(t: int) -> None:
            barrier.wait()
            for i in range(10):
                k = t * 100 + i
                actual = jqlite.parse(f"def g: . + {k}; [limit(3; range(10) | g)] | map(select(. % 2 == 0)) | add // 0").evaluate(0)
                even = sum(x for x in range(k, k + 3) if x % 2 == 0)
                if actual != [even]:
                    wrong.append(f"thread {t}, round {i}: {actual}, expected {even}")
        threads = [threading.Thread(target=work, args=(t,)) for t in range(16)]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        self.assertEqual([], wrong[:5])


TAIL = "def f: if . == 0 then 0 else (. - 1 | f) end; f"
NON_TAIL = "def f: if . == 0 then 0 else (. - 1 | f) + 1 end; f"
NOT_SINGLE = "def f: if . == 0 then 0, empty else (. - 1 | f) + 1 end; f"


class DepthTests(unittest.TestCase):
    """Runaway recursion through the public API (spec §11): the error is Maximum call depth exceeded
    and the process survives, on a normal thread and on a small one."""

    def test_runaway_recursion_raises_and_the_process_survives(self) -> None:
        for program in (TAIL, NON_TAIL, NOT_SINGLE):
            with self.subTest(program=program):
                for small in (False, True):
                    def run() -> jqlite.JqError:
                        try:
                            jqlite.parse(program).evaluate(100000)
                        except jqlite.JqError as e:
                            return e
                        raise AssertionError("no error")
                    error: Any = on_small_stack(None, run, 256 * 1024) if small else run()  # type: ignore[arg-type]
                    self.assertEqual("Maximum call depth exceeded", error.message)  # type: ignore[union-attr]
                    self.assertEqual("Maximum call depth exceeded", error.value)  # type: ignore[union-attr]

    def test_runaway_recursion_is_caught_by_the_program(self) -> None:
        self.assertEqual(["Maximum call depth exceeded"],
                         jqlite.parse("def f: if . == 0 then 0 else (. - 1 | f) + 1 end; try f catch .").evaluate(100000))

    def test_the_last_call_below_max_depth_succeeds_and_the_next_fails(self) -> None:
        for program, max_depth, deepest, output in [(TAIL, 1000, 999, 0), (NON_TAIL, 1000, 999, 999), (NOT_SINGLE, 1000, 999, 999),
                                                    (TAIL, 3000, 2999, 0), (NON_TAIL, 3000, 2999, 2999), (NOT_SINGLE, 3000, 2999, 2999)]:
            expression = jqlite.parse(program, jqlite.Options(max_depth=max_depth))
            for small in (False, True):
                with self.subTest(program=program, max_depth=max_depth, small=small):
                    def run(n: int) -> object:
                        try:
                            return expression.evaluate(n)
                        except jqlite.JqError as e:
                            return e.message
                    on = (lambda step: on_small_stack(None, step, 256 * 1024)) if small else (lambda step: step())  # type: ignore[arg-type]
                    self.assertEqual([output], on(lambda: run(deepest)))
                    self.assertEqual("Maximum call depth exceeded", on(lambda: run(deepest + 1)))


    def test_the_largest_max_depth_is_honoured_and_a_larger_one_rejected(self) -> None:
        expression = jqlite.parse(NON_TAIL, jqlite.Options(max_depth=100_000))
        self.assertEqual([99_999], on_small_stack(None, lambda: expression.evaluate(99_999), 256 * 1024))  # type: ignore[arg-type]
        for wrong in (100_001, -1, True, 1.5, "10", None):
            with self.subTest(max_depth=wrong), self.assertRaises(ValueError) as raised:
                jqlite.Options(max_depth=wrong)  # type: ignore[arg-type]
            self.assertEqual(f"max_depth must be an int from 0 to 100000, not {wrong!r}", str(raised.exception))
        self.assertEqual(["Maximum call depth exceeded"], jqlite.parse("def f: f; try f catch .", jqlite.Options(max_depth=0)).evaluate(None))


class RecursionLimitTests(unittest.TestCase):
    """jqlite raises the recursion limit only while it parses or evaluates, and the last one to end
    puts back the limit it found: importing, parsing and evaluating leave the process as it was."""

    def test_importing_parsing_and_evaluating_leave_the_limit_alone(self) -> None:
        script = ("import sys; before = sys.getrecursionlimit(); import jqlite; after_import = sys.getrecursionlimit(); "
                  "e = jqlite.parse('[' * 1000 + ']' * 1000); e.evaluate(None); "
                  "jqlite.evaluate('def f: if . == 0 then 0 else (. - 1 | f) + 1 end; 4999 | f', None, options=jqlite.Options(max_depth=5000)); "
                  "print(before, after_import, sys.getrecursionlimit())")
        folder = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        result = subprocess.run([sys.executable, "-c", script], cwd=folder, capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        before, after_import, after = result.stdout.split()
        self.assertEqual((before, before), (after_import, after))

    def test_a_lowered_limit_does_not_stop_jqlite_and_is_put_back(self) -> None:
        previous = sys.getrecursionlimit()
        depth = len(traceback.extract_stack())
        sys.setrecursionlimit(depth + 100)
        try:
            nested = jqlite.parse("[" * 1000 + "1" + "]" * 1000)
            self.assertEqual(depth + 100, sys.getrecursionlimit())
            self.assertEqual([4999], jqlite.parse(NON_TAIL, jqlite.Options(max_depth=5000)).evaluate(4999))
            self.assertEqual(1, len(nested.evaluate(None)))
            self.assertEqual(depth + 100, sys.getrecursionlimit())
        finally:
            sys.setrecursionlimit(previous)

    def test_errors_stops_and_callbacks_put_the_limit_back(self) -> None:
        previous = sys.getrecursionlimit()
        with self.assertRaises(jqlite.JqError):
            jqlite.evaluate("def f: if . == 0 then error(\"x\") else (. - 1 | f) end; 900 | f", None)
        with self.assertRaises(jqlite.JqParseError):
            jqlite.parse("[" * 1001 + "]" * 1001)
        seen: list[int] = []

        def take_two(result: int) -> bool:
            seen.append(result)
            return len(seen) < 2
        jqlite.parse("def f: if . == 0 then range(10) else (. - 1 | f) end; 900 | f").stream(None, take_two)
        limits: list[int] = []
        jqlite.parse("def f: if . == 0 then 1 else (. - 1 | f) end; 900 | f | debug",
                     jqlite.Options(debug=lambda v: limits.append(sys.getrecursionlimit()))).evaluate(None)
        self.assertEqual([0, 1], seen)
        self.assertGreater(limits[0], previous)
        self.assertEqual(previous, sys.getrecursionlimit())
        self.assertEqual(0, recursion_limit.active())

    def test_threads_that_overlap_put_the_limit_back_when_the_last_ends(self) -> None:
        previous = sys.getrecursionlimit()
        expression = jqlite.parse(NON_TAIL, jqlite.Options(max_depth=3000))
        wrong: list[str] = []

        def work(t: int) -> None:
            for i in range(5):
                actual = expression.evaluate(2000 + t + i)
                if actual != [2000 + t + i]:
                    wrong.append(f"thread {t}: {actual}")
        threads = [threading.Thread(target=work, args=(t,)) for t in range(8)]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        self.assertEqual([], wrong)
        self.assertEqual(previous, sys.getrecursionlimit())
        self.assertEqual(0, recursion_limit.active())


class JsonLinesTests(unittest.TestCase):
    LINES = ['{"name":"Alice","age":30,"active":true,"items":[1,2]}', '{"name":"Bob","age":25,"active":false,"items":[3,4]}']

    def test_each_value_is_evaluated_in_turn(self) -> None:
        values = [json.loads(line) for line in self.LINES]
        self.assertEqual(["Alice", "Bob"], list(jqlite.evaluate_each(".name", values)))
        self.assertEqual([1, 2, 3, 4], list(jqlite.evaluate_each(".items[]", values)))
        self.assertEqual(["Alice"], list(jqlite.evaluate_each("select(.active) | .name", values)))

    def test_each_value_is_evaluated_alone(self) -> None:
        self.assertEqual([1, 3], list(jqlite.evaluate_each(jqlite.parse("if . == 2 then halt else . end"), [1, 2, 3])))

    def test_an_asynchronous_source(self) -> None:
        async def source():  # type: ignore[no-untyped-def]
            for line in self.LINES:
                yield json.loads(line)

        async def collect() -> list:
            return [result async for result in jqlite.evaluate_each_async(".age * 2", source())]
        self.assertEqual([60, 50], asyncio.run(collect()))
