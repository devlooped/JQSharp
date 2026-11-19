"""The regex engine (RegexEngineTests.cs): what it finds is what the simplest matcher of the same
programs finds (backtracking_matcher.py), in time linear in the input and with memory the size of
the pattern. The corpus shows what the programs mean (spec §9.10).

Pure Python runs the engine many times slower than .NET, so the reference's numbers are scaled
here to keep the suite short: 10,000 random patterns per seed instead of 20,000, the inputs of six
linear-time cases 2 to 40 times shorter, and the input of the program of §11 with the most states
10 times shorter. The bound on time stays the reference's 2 seconds, so linearity is still checked:
a quadratic or exponential engine would take minutes on these inputs, the engine takes a few
hundred milliseconds at most.
"""

from __future__ import annotations

import time
import unittest

from jqlite.builtins import regex_engine
from jqlite.builtins.regex_syntax import MAX_GROUPS, MAX_NESTING, MAX_SIZE, Options, try_translate

from . import backtracking_matcher
from .evaluate import line
from .regex_fuzz import RegexFuzz

_PLAIN = Options(extended=False, dot_all=False, ignore_case=False)


class RegexEngineTests(unittest.TestCase):
    def test_random_patterns_match_as_the_backtracking_matcher_does(self) -> None:
        for seed in (1, 7, 12345):
            with self.subTest(seed=seed):
                fuzz = RegexFuzz(seed)
                compared = 0
                for _ in range(10000):
                    source = fuzz.pattern(3)
                    translation = try_translate(source, _PLAIN._replace(dot_all=fuzz.next(2) == 0))
                    self.assertNotIsInstance(translation, str, source)
                    matcher = regex_engine.RegexMatcher(regex_engine.RegexProgram(translation))  # type: ignore[arg-type]
                    width = 2 * (len(translation.groups) + 1)  # type: ignore[union-attr]
                    for _ in range(4):
                        text = fuzz.input(7)
                        for start in range(len(text) + 1):
                            expected = [0] * width
                            found = backtracking_matcher.try_match(translation, text, start, expected, 200000)  # type: ignore[arg-type]
                            if found is None:
                                continue
                            where = f"{source} on {text!r} from {start}"
                            spans = [0] * width
                            self.assertEqual(found, matcher.try_match(text, start, spans), where)
                            if found:
                                self.assertEqual(expected, spans, where)
                            # Only the match; only whether there is one.
                            match = [0, 0]
                            self.assertEqual(found, matcher.try_match(text, start, match), where)
                            if found:
                                self.assertEqual(expected[:2], match, where)
                            self.assertEqual(found, matcher.try_match(text, start, []), where)
                            compared += 1
                self.assertGreater(compared, 150000)

    # Patterns a backtracking matcher takes exponential or quadratic time for, and long inputs.
    LINEAR_TIME = [
        ('"a" * 28 + "c" | test("^(a*)*b$")', "false"),
        ('"a" * 5000 + "c" | test("^(a*)*b$")', "false"),
        ('"ab" * 50000 | test("^(a|b)*$")', "true"),
        ('"a" * 100000 | test("a*b")', "false"),
        ('"a" * 100000 | [match("a*b")] | length', "0"),
        ('"x" * 50000 | test("(x+x+)+y")', "false"),
        ('"a" * 40 | test("(a?){40}a{40}")', "true"),
        ('"ab" * 50000 | match("^(a|b)*$") | .captures[0].offset', "99999"),
        ('"a" * 50000 | [match("(a|aa)*?$"; "g") | .length]', "[50000,0]"),
    ]

    def test_linear_time(self) -> None:
        for program, expected in self.LINEAR_TIME:
            with self.subTest(program=program):
                clock = time.perf_counter()
                self.assertEqual(expected, line(program))
                elapsed = (time.perf_counter() - clock) * 1000
                self.assertLess(elapsed, 2000, f"{elapsed:.0f} ms")

    def test_the_memory_of_a_search_is_the_size_of_the_pattern(self) -> None:
        # The threads are kept in lists that grow with their number: after a long input they hold
        # no more than one thread per instruction that reads a code point.
        program = regex_engine.try_compile("^(?:(a)|(b)|[ab]c?)*$", _PLAIN)
        self.assertIsInstance(program, regex_engine.RegexProgram)
        assert isinstance(program, regex_engine.RegexProgram)
        self.assertEqual(4, program.readers)
        self.assertEqual(6, program.slots)
        matcher = regex_engine.RegexMatcher(program)
        spans = [0] * 6
        self.assertTrue(matcher.try_match("ab" * 50000, 0, spans))
        self.assertEqual([0, 100000, 99998, 99999, 99999, 100000], spans)
        threads = matcher._current
        assert threads is not None
        self.assertLessEqual(len(threads.pcs), 8)
        self.assertEqual(len(threads.pcs) * 6, len(threads.captures))
        self.assertEqual(program.states, len(matcher._seen))
        self.assertEqual(2 * program.states, len(matcher._stack))

    def test_the_largest_patterns_of_spec_11_are_programs_the_matcher_runs(self) -> None:
        # The limits are the translation's, and the corpus has their boundaries. What is the
        # engine's: within them a program has at most five instructions per unit of the pattern's
        # size, and a state is an instruction at a level (nested loops that can match nothing have
        # levels).
        def compiled(source: str) -> regex_engine.RegexProgram:
            program = regex_engine.try_compile(source, _PLAIN)
            assert isinstance(program, regex_engine.RegexProgram), program
            return program

        def nested(levels: int, inner: str) -> str:
            return "(?:" * levels + inner + ")*" * levels

        self.assertEqual([10000, 100, 1000], [MAX_SIZE, MAX_NESTING, MAX_GROUPS])
        # The most instructions for a size: an empty group repeated, each copy optional and with its mark.
        copies = try_translate("(){0,1000}" * 9 + "(){0,990}", _PLAIN)
        assert not isinstance(copies, str), copies
        self.assertEqual(5 * (MAX_SIZE - 10) + 3, len(copies.ops))
        self.assertTrue(regex_engine.RegexMatcher(regex_engine.RegexProgram(copies)).try_match("a", 0, [0, 0]))
        # The most states: every instruction inside a hundred loops that can match nothing.
        deep = compiled(nested(MAX_NESTING, "a?" * 4900))
        self.assertTrue(1_000_001 <= deep.states <= 1_999_999, deep.states)
        self.assertTrue(regex_engine.RegexMatcher(deep).try_match("a" * 10 + "b", 0, [0, 0]))
        self.assertEqual("[300,0]", line(f'"a" * 300 | [match("{nested(MAX_NESTING, "a*")}"; "g") | .length]'))
        # The most capture slots: a thousand groups, each thread with its own.
        groups = compiled("(a)" * MAX_GROUPS)
        self.assertEqual(2 * (MAX_GROUPS + 1), groups.slots)
        spans = [0] * groups.slots
        self.assertTrue(regex_engine.RegexMatcher(groups).try_match("a" * (MAX_GROUPS + 5), 0, spans))
        self.assertEqual([0, 1000, 0, 1, 999, 1000], spans[:4] + spans[-2:])
        # Past a limit the translation fails, with the fixed reason.
        self.assertEqual("the pattern is too large", regex_engine.try_compile("(a)" * (MAX_GROUPS + 1), _PLAIN))
