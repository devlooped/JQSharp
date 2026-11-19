"""The binding layer (dotnet/Tests/Core/Binding): lexical binding printed as hops and slots, single
functions, binding errors, limits, the corpus binds, modules."""

from __future__ import annotations

import os
import unittest

from jqlite.binding import bound as B
from jqlite.binding.prelude import Prelude
from jqlite.binding.resolver import Resolver
from jqlite.runtime.evaluation import EvaluationOptions
from jqlite.syntax.parser import MAX_NESTING, Parser
from jqlite.syntax.syntax_error import SyntaxError
from jqlite.values import value_text

from . import corpus
from .evaluate import MemoryModules, compile, run
from .test_syntax import _SYMBOLS
from .test_values import on_small_stack


def _prelude_count() -> int:
    return len(Prelude.instance().functions)


def _name(program: B.BoundProgram, index: int) -> str:
    function = program.functions[index]
    count = _prelude_count()
    return f"prelude:{function!r}" if index < count else f"{function!r}#{index - count}"


def print_bound(program: B.BoundProgram, node: B.Node | None) -> str:
    """Prints a bound node as an S-expression stating what every reference resolved to (BoundPrinter.cs)."""
    def p(child: B.Node | None) -> str:
        return print_bound(program, child)

    def all_(children: tuple) -> str:
        return "".join(" " + p(child) for child in children)
    if node is None:
        return "_"
    K = B.NodeKind
    k = node.kind
    n: object = node
    if k == K.IDENTITY:
        return "."
    if k == K.LITERAL:
        return value_text.to_json(n.value)  # type: ignore[attr-defined]
    if k == K.TEMPLATE:
        parts = ["(str"]
        if n.format is not None:  # type: ignore[attr-defined]
            parts.append(" @" + n.format)  # type: ignore[attr-defined]
        for i, part in enumerate(n.parts):  # type: ignore[attr-defined]
            parts.append(" " + value_text.to_json(n.texts[i]) + " " + p(part))  # type: ignore[attr-defined]
        return "".join(parts) + " " + value_text.to_json(n.texts[-1]) + ")"  # type: ignore[attr-defined]
    if k == K.FORMAT:
        return "@" + n.name  # type: ignore[attr-defined]
    if k == K.INDEX:
        return f"(index {p(n.target)} {p(n.key)})"  # type: ignore[attr-defined]
    if k == K.SLICE:
        return f"(slice {p(n.target)} {p(n.frm)} {p(n.to)})"  # type: ignore[attr-defined]
    if k == K.ITERATE:
        return f"(iter {p(n.target)})"  # type: ignore[attr-defined]
    if k == K.VARIABLE:
        return f"${n.hops}.{n.slot}"  # type: ignore[attr-defined]
    if k == K.HOST_VARIABLE:
        return "$$" + n.name  # type: ignore[attr-defined]
    if k == K.ENV_VARIABLE:
        return "$ENV"
    if k == K.CALL_FUNCTION:
        name = f"{_name(program, n.function)}^{n.hops}"  # type: ignore[attr-defined]
        return name if not n.arguments else f"({name}{all_(n.arguments)})"  # type: ignore[attr-defined]
    if k == K.CALL_PARAMETER:
        return f"param^{n.hops}.{n.slot}"  # type: ignore[attr-defined]
    if k == K.CALL_NATIVE:
        name = f"native:{n.native!r}"  # type: ignore[attr-defined]
        return name if not n.arguments else f"({name}{all_(n.arguments)})"  # type: ignore[attr-defined]
    if k == K.PIPE:
        return f"(| {p(n.left)} {p(n.right)})"  # type: ignore[attr-defined]
    if k == K.COMMA:
        return f"(,{all_(n.items)})"  # type: ignore[attr-defined]
    if k == K.BINARY:
        return f"({_SYMBOLS[n.op]} {p(n.left)} {p(n.right)})"  # type: ignore[attr-defined]
    if k == K.AND:
        return f"(and {p(n.left)} {p(n.right)})"  # type: ignore[attr-defined]
    if k == K.OR:
        return f"(or {p(n.left)} {p(n.right)})"  # type: ignore[attr-defined]
    if k == K.ALTERNATIVE:
        return f"(// {p(n.left)} {p(n.right)})"  # type: ignore[attr-defined]
    if k == K.NEGATE:
        return f"(neg {p(n.operand)})"  # type: ignore[attr-defined]
    if k == K.ARRAY_EXPR:
        return "(array)" if n.body is None else f"(array {p(n.body)})"  # type: ignore[attr-defined]
    if k == K.OBJECT_EXPR:
        if not n.entries:  # type: ignore[attr-defined]
            return "(object)"
        return "(object " + " ".join(f"({p(e.key)} {p(e.value)})" for e in n.entries) + ")"  # type: ignore[attr-defined]
    if k == K.IF:
        return f"(if {p(n.condition)} {p(n.then)} {p(n.else_)})"  # type: ignore[attr-defined]
    if k == K.TRY:
        return f"(try {p(n.body)})" if n.handler is None else f"(try {p(n.body)} {p(n.handler)})"  # type: ignore[attr-defined]
    if k == K.REDUCE:
        return f"(reduce {p(n.source)} {_binder(n.binder, p)} {p(n.init)} {p(n.update)})"  # type: ignore[attr-defined]
    if k == K.FOREACH:
        return f"(foreach {p(n.source)} {_binder(n.binder, p)} {p(n.init)} {p(n.update)} {p(n.extract)})"  # type: ignore[attr-defined]
    if k == K.BIND:
        return f"(as {p(n.source)} {_binder(n.binder, p)} {p(n.body)})"  # type: ignore[attr-defined]
    if k == K.LABEL:
        return f"(label {p(n.body)})"  # type: ignore[attr-defined]
    return f"(break ^{n.hops})"  # type: ignore[attr-defined]


def _binder(binder: B.Binder, p: object) -> str:
    if len(binder.patterns) == 1:
        return _pattern(binder.patterns[0], p)
    return "(?// " + " ".join(_pattern(x, p) for x in binder.patterns) + ")"


def _slot(slot: int) -> str:
    return "$_" if slot < 0 else f"${slot}"


def _pattern(pattern: B.Pattern | None, p: object) -> str:
    if pattern is None:
        return "_"
    if pattern.kind == B.PatternKind.VARIABLE:
        return _slot(pattern.slot)  # type: ignore[attr-defined]
    if pattern.kind == B.PatternKind.ARRAY:
        return "[" + " ".join(_pattern(e, p) for e in pattern.elements) + "]"  # type: ignore[attr-defined]
    entries = [f"({p(e.key)} {'_' if e.slot < 0 else _slot(e.slot)} {_pattern(e.value, p)})" for e in pattern.entries]  # type: ignore[attr-defined,operator]
    return "{" + " ".join(entries) + "}"


def print_program(program: B.BoundProgram) -> str:
    parts = [f"(def {_name(program, i)} {print_bound(program, program.functions[i].body)})"
             for i in range(_prelude_count(), len(program.functions))]
    parts.append(print_bound(program, program.body))
    return " ".join(parts)


def bind(source: str) -> str:
    program = Resolver.try_compile(source)
    if type(program) is SyntaxError:
        raise AssertionError(f"'{source}' does not bind: {program.message} at {program.line}:{program.column}")
    return print_program(program)  # type: ignore[arg-type]


def error(source: str) -> str:
    program = Resolver.try_compile(source)
    if type(program) is not SyntaxError:
        raise AssertionError(f"'{source}' binds as {print_program(program)}")  # type: ignore[arg-type]
    return f"{program.message} @{program.line}:{program.column}"


class ResolverTests(unittest.TestCase):
    """Lexical binding (spec §7.5, §7.7, §7.15) stated on the printed bound program, and the binding errors of §10.2."""

    def check(self, cases: list[tuple[str, str]], function: object = bind) -> None:
        for source, expected in cases:
            with self.subTest(source=source):
                self.assertEqual(expected, function(source))  # type: ignore[operator]

    def test_variables_bind_lexically(self) -> None:
        self.check([
            ("1 as $x | $x", "(as 1 $0 $0.0)"), ("1 as $x | 2 as $x | $x", "(as 1 $0 (as 2 $0 $0.0))"),
            ("1 as $x | 2 as $y | $x, $y", "(as 1 $0 (as 2 $0 (, $1.0 $0.0)))"),
            ("1 as $x | (2 as $x | $x), $x", "(as 1 $0 (, (as 2 $0 $0.0) $0.0))"),
            ("1 as $x | ($x + 1) as $x | $x", "(as 1 $0 (as (+ $0.0 1) $0 $0.0))"),
            ("1 as $x | def f: $x; 2 as $x | f", "(def f/0#0 $0.0) (as 1 $0 (as 2 $0 f/0#0^1))"),
            ("def f: 1; 2 as $x | f", "(def f/0#0 1) (as 2 $0 f/0#0^1)"),
        ])

    def test_functions_bind_to_the_innermost_visible_definition(self) -> None:
        self.check([
            ("def f: f; f", "(def f/0#0 f/0#0^0) f/0#0^0"),
            ("def f: 1; def g: f; def f: 2; g", "(def f/0#0 1) (def g/0#1 f/0#0^0) (def f/0#2 2) g/0#1^0"),
            ("def f: 1; def f(a): 2; f, f(3)", "(def f/0#0 1) (def f/1#1 2) (, f/0#0^0 (f/1#1^0 3))"),
            ("def f: 1; [f, (def f: 2; f), f]", "(def f/0#0 1) (def f/0#1 2) (array (, f/0#0^0 f/0#1^0 f/0#0^0))"),
            ("def f(g): def h: f(g); h; f(.)", "(def f/1#0 h/0#1^0) (def h/0#1 (f/1#0^1 param^0.0)) (f/1#0^0 .)"),
        ])

    def test_functions_and_calls_are_single_by_the_rules(self) -> None:
        cases = [
            ("def fib: if . < 2 then . else (. - 1 | fib) + (. - 2 | fib) end; fib", "fib/0+ body+"),
            ("def r: ., (.[]? | r); r", "r/0- body-"),
            ("def f($a): $a + 1; f(1)", "f/1+ body+"), ("def f($a): $a + 1; f(1, 2)", "f/1+ body-"),
            ("def f($a; b; $c): [b] | $a + $c; f(1; 2, 3; 4)", "f/3+ body+"),
            ("def f($a; b; $c): [b] | $a + $c; f(1; 2; 3, 4)", "f/3+ body-"),
            ("def f($n): if $n < 2 then $n else f($n - 1) + f($n - 2) end; f(9)", "f/1+ body+"),
            ("def f(g): g + 1; f(1)", "f/1- body-"), ("def f(g): [g]; f(1, 2)", "f/1+ body+"),
            ("def f: def g: f; g; f", "f/0- g/0- body-"),
            ("def f: def g: if . < 3 then . + 1 | g else . end; g, (. + 1 | f); f", "f/0- g/0+ body-"),
            ("def f: def g: def h: ., (. + 1 | h); [h], (. + 1 | g); def k: 1 + k; [g] | k, f; [f]", "f/0- g/0- h/0- k/0+ body+"),
            ("def f: if . then empty else last(.[]) end; f", "f/0+ body+"),
            ('def f: getpath(["a"]) | until(. > 3; . + 1) | length; f', "f/0+ body+"),
            ('def f: getpath(["a"], ["b"]); f', "f/0- body-"), ("def f: limit(1; .); f", "f/0- body-"),
            ('def f: if any(.[]; . > 1) then error("x") elif isempty(.[]) then 0 else map(f) | add end; f', "f/0+ body+"),
            ("def f: select(. > 1); f", "f/0- body-"),
            ("def f: .a |= . + 1 | .b = 2; f", "f/0+ body+"), ("def f: .a = (1, 2); f", "f/0- body-"),
        ]
        count = _prelude_count()
        for source, expected in cases:
            for native_overrides in (True, False):
                with self.subTest(source=source, native_overrides=native_overrides):
                    program = Resolver.try_compile(source, native_overrides)
                    own = [f"{f!r}{'+' if f.single else '-'}" for f in program.functions[count:]]  # type: ignore[union-attr]
                    own.append(f"body{'+' if program.body.at_most_one else '-'}")  # type: ignore[union-attr]
                    self.assertEqual(expected, " ".join(own))

    def test_parameters_are_closures(self) -> None:
        self.check([
            ("def f(g): g; f(.)", "(def f/1#0 param^0.0) (f/1#0^0 .)"),
            ("def f(a; b): b, a; f(1; 2)", "(def f/2#0 (, param^0.1 param^0.0)) (f/2#0^0 1 2)"),
            ("def f(a; a): a; f(1; 2)", "(def f/2#0 param^0.1) (f/2#0^0 1 2)"),
            ("def f(g): def h(x): g, x; h(1); f(2)", "(def f/1#0 (h/1#1^0 1)) (def h/1#1 (, param^1.0 param^0.0)) (f/1#0^0 2)"),
            ("def x: 7; def f(x): x; f(1)", "(def x/0#0 7) (def f/1#1 param^0.0) (f/1#1^0 1)"),
            ("def x(a): 7; def f(x): x(0); f(1)", "(def x/1#0 7) (def f/1#1 (x/1#0^1 0)) (f/1#1^0 1)"),
            ("def f(g): def g: 1; g; f(2)", "(def f/1#0 g/0#1^0) (def g/0#1 1) (f/1#0^0 2)"),
            ("def f($a): $a, a; f(1)", "(def f/1#0 (as param^0.0 $0 (, $0.0 param^1.0))) (f/1#0^0 1)"),
            ("def f($a; $b): $a, $b; f(1; 2)", "(def f/2#0 (as param^0.0 $0 (as param^1.1 $0 (, $1.0 $0.0)))) (f/2#0^0 1 2)"),
            ("def f(a; $a): [a, $a]; f(1; 2)", "(def f/2#0 (as param^0.1 $0 (array (, param^1.1 $0.0)))) (f/2#0^0 1 2)"),
            ("def f($a; a): [a, $a]; f(1; 2)", "(def f/2#0 (as param^0.0 $0 (array (, param^1.1 $0.0)))) (f/2#0^0 1 2)"),
            ("def f(x): def x: 5; x; f(1)", "(def f/1#0 x/0#1^0) (def x/0#1 5) (f/1#0^0 1)"),
            ("def f(a::b): a::b; f(3)", "(def f/1#0 param^0.0) (f/1#0^0 3)"),
            ("def f::g: 1; f::g", "(def f::g/0#0 1) f::g/0#0^0"),
        ])

    def test_labels_bind_lexically(self) -> None:
        self.check([
            ("[label $f | def g: break $f; (label $f | 1, g, 2), 3]", "(def g/0#0 (break ^0)) (array (label (, (label (, 1 g/0#0^1 2)) 3)))"),
            ("label $a | label $b | break $a, break $b", "(label (label (, (break ^1) (break ^0))))"),
            ("label $a | 1 as $x | break $a", "(label (as 1 $0 (break ^1)))"),
            ("label $x | 1 as $x | $x, break $x", "(label (as 1 $0 (, $0.0 (break ^1))))"),
            ("label $a::b | break $a::b", "(label (break ^0))"),
        ])

    def test_patterns_bind_every_variable(self) -> None:
        self.check([
            (". as [$a, $b] | $a, $b", "(as . [$1 $0] (, $0.1 $0.0))"),
            (". as {$a, b: [$c]} | $a, $c", '(as . {("a" $0 _) ("b" _ [$1])} (, $0.0 $0.1))'),
            (". as {$a: [$b]} | $a, $b", '(as . {("a" $0 [$1])} (, $0.0 $0.1))'),
            (". as [$a] ?// {a: $b} ?// $a | [$a, $b]", '(as . (?// [$0] {("a" _ $1)} $0) (array (, $0.0 $0.1)))'),
            (". as [$a, $a] | $a", "(as . [$_ $0] $0.0)"),
            (". as {$a, b: $a} | $a", '(as . {("a" $0 _) ("b" _ $_)} $0.0)'),
            (". as [$a, [$a]] | $a", "(as . [$_ [$0]] $0.0)"),
            (". as {$a, b: $a} ?// $c | $a", '(as . (?// {("a" $0 _) ("b" _ $0)} $1) $0.0)'),
            (". as {x: $c} ?// [$a, $a] | $a", '(as . (?// {("x" _ $0)} [$1 $1]) $0.1)'),
            ('"b" as $x | . as {a: $x, ($x): $y} | $y', '(as "b" $0 (as . {("a" _ $0) ($0.0 _ $1)} $0.1))'),
            (". as {a: $x, ($x): $y} | $y", '(as . {("a" _ $0) ($$x _ $1)} $0.1)'),
            ("1 as $x | reduce . as $y ($x; $x, $y)", "(as 1 $0 (reduce . $0 $0.0 (, $1.0 $0.0)))"),
            ("foreach .[] as [$a] (0; $a; [$a, .])", "(foreach (iter .) [$0] 0 $0.0 (array (, $0.0 .)))"),
            ("reduce .[] as $x (.; .) | $x", "(| (reduce (iter .) $0 . .) $$x)"),
        ])

    def test_unbound_variables_are_host_variables(self) -> None:
        self.check([
            ("$foo, $bar, $foo", "(, $$foo $$bar $$foo)"), ("$__prog__", "$$__prog__"), ("$ENV", "$ENV"),
            ("1 as $ENV | $ENV", "(as 1 $0 $0.0)"), ("def env: $ENV; env", "(def env/0#0 $ENV) env/0#0^0"),
        ])

    def test_the_location_is_a_literal(self) -> None:
        self.check([("$__loc__", '{"file":"<top-level>","line":1}'),
                    ("1 as $x |\n\n$__loc__", '(as 1 $0 {"file":"<top-level>","line":3})')])

    def test_host_variables_are_listed_once(self) -> None:
        program = Resolver.try_compile("$foo, $bar, ($foo as $bar | $bar), $baz")
        self.assertEqual(("foo", "bar", "baz"), program.host_variables)  # type: ignore[union-attr]

    def test_calls_resolve_to_user_then_prelude_then_native(self) -> None:
        self.check([
            ("length", "native:length/0"), ("map(.)", "(prelude:map/1^0 .)"),
            ("1 as $x | 2 as $y | map($x)", "(as 1 $0 (as 2 $0 (prelude:map/1^2 $1.0)))"),
            ("def map(f): 1; map(.)", "(def map/1#0 1) (map/1#0^0 .)"), ("def length: 1; length", "(def length/0#0 1) length/0#0^0"),
            ("..", "prelude:recurse/0^0"), ("def recurse: 1; ..", "(def recurse/0#0 1) recurse/0#0^0"),
            (".a |= 1", '(native:_modify/2 (index . "a") 1)'), (".a = 1", '(native:_assign/2 (index . "a") 1)'),
            (".a += 1", '(as 1 $0 (native:_modify/2 (index . "a") (+ . $0.0)))'),
            ("def _modify(a; b): 42; .a |= 1", '(def _modify/2#0 42) (_modify/2#0^0 (index . "a") 1)'),
            ("def true: 1; true", "(def true/0#0 1) true"),
        ])

    def test_without_overrides_builtins_bind_to_the_prelude(self) -> None:
        program = Resolver.try_compile(".a |= 1, (.b = 2), map_values(.)", False)
        self.assertEqual('(, (prelude:_modify/2^0 (index . "a") 1) (prelude:_assign/2^0 (index . "b") 2) (prelude:map_values/1^0 .))',
                         print_program(program))  # type: ignore[arg-type]
        # Inside the prelude too: map_values calls _modify.
        prelude = Prelude.instance()
        self.assertEqual(B.NodeKind.CALL_NATIVE, prelude.functions[prelude.find("map_values", 1)].body.kind)  # type: ignore[index]
        prelude = Prelude.without_overrides()
        self.assertEqual(B.NodeKind.CALL_FUNCTION, prelude.functions[prelude.find("map_values", 1)].body.kind)  # type: ignore[index]

    def test_every_construct_binds_its_children(self) -> None:
        self.assertEqual(
            '(as . $0 (array (, (object ("a" $0.0) ("b" _) ($0.0 1)) (str @base64 "x" $0.0 "") (slice (index . $0.0) $0.0 _) '
            '(if $0.0 (try $0.0 $0.0) (// (or (and $0.0 $0.0) (neg $0.0)) @csv)) (iter $0.0) (try (index . "a")))))',
            bind('. as $x | [{a: $x, b, ($x): 1}, @base64 "x\\($x)", .[$x][$x:], if $x then try $x catch $x else $x and $x or -$x // @csv end, $x[], .a?]'))

    def test_prelude_functions_come_first_in_every_program(self) -> None:
        first = Resolver.try_compile("def f: 1; f")
        second = Resolver.try_compile("map(.)")
        prelude = Prelude.instance().functions
        self.assertEqual(len(prelude) + 1, len(first.functions))  # type: ignore[union-attr]
        self.assertEqual(len(prelude), len(second.functions))  # type: ignore[union-attr]
        for i in range(len(prelude)):
            self.assertIs(prelude[i], first.functions[i])  # type: ignore[union-attr]
            self.assertIs(prelude[i], second.functions[i])  # type: ignore[union-attr]

    def test_unknown_names_are_binding_errors(self) -> None:
        self.check([
            ("foo", "foo/0 is not defined @1:1"), ("1 | foo(1; 2)", "foo/2 is not defined @1:5"), ("map(1; 2)", "map/2 is not defined @1:1"),
            ("1 |\n  foo", "foo/0 is not defined @2:3"), ("foo::bar", "foo::bar/0 is not defined @1:1"),
            ("1 + foo, bar", "foo/0 is not defined @1:5"), ("foo(bar)", "foo/1 is not defined @1:1"), ("map(bar)", "bar/0 is not defined @1:5"),
            ("def f: g; def g: 1; f", "g/0 is not defined @1:8"), ("def f: def g: 3; g; g", "g/0 is not defined @1:21"),
            ("(def f: 1; f), f", "f/0 is not defined @1:16"), ("def f(g): g(1); f(1)", "g/1 is not defined @1:11"),
            ("def f($a): a(1); f(1)", "a/1 is not defined @1:12"), (". as $foo | break $foo", "$*label-foo is not defined @1:13"),
            ("label $f | def g: 1; break $g", "$*label-g is not defined @1:22"), ("def g: break $f; label $f | g", "$*label-f is not defined @1:8"),
            ("$foo::bar", "$foo::bar is not defined @1:1"), ("1 as $x | $x::y", "$x::y is not defined @1:11"),
            (". as {(foo): $x} | $x", "foo/0 is not defined @1:8"), ("reduce . as $x (0; foo)", "foo/0 is not defined @1:20"),
            ("foreach . as $x (0; .; foo)", "foo/0 is not defined @1:24"), ('import "a" as a; 1', "module not found: a @1:1"),
            ('1 as $x | include "a"; 1', "Unexpected 'include' @1:11"),
        ], error)

    def test_syntax_errors_come_from_the_parser(self) -> None:
        self.assertEqual("Unexpected end of program @1:4", error("1 +"))

    def test_module_metadata_alone_is_accepted(self) -> None:
        self.assertEqual("1", bind("module {a: 1}; 1"))


LIMIT = MAX_NESTING


def binds(source: str) -> B.BoundProgram:
    """Parses and binds on a thread with a 1 MB C stack."""
    program = on_small_stack(None, lambda: Resolver.try_compile(source))  # type: ignore[arg-type]
    if type(program) is SyntaxError:
        raise AssertionError(f"{program.message} at {program.line}:{program.column}")  # type: ignore[attr-defined]
    return program  # type: ignore[return-value]


class ResolverLimitTests(unittest.TestCase):
    """Binding does not exhaust the host stack on what the parser accepts."""

    def test_nesting_up_to_the_limit_binds(self) -> None:
        cases = [("(", ")"), ("[", "]"), ("{a: ", "}"), ('{("a" + ', "): 1}"), (".[", "]"), ("f(", ")"), ('"\\(', ')"'),
                 ("if . then ", " end"), ("try ", ""), ("reduce . as $x (", ";.)"), ("foreach . as $x (", ";.)"), ("def f: ", "; f"),
                 ("def f(g): ", "; f(.)"), ("[1 + ", "]"), ("[1, 2 as $x | ", "]"), ("(1 | ", ")"), (".[1, ", "]"), ("{a: 1 | 1 + ", "}"),
                 ("if 1, 1 as $x | ", " then . end"), ("try -", ""), ("f(label $x | ", ")"), ('. as [$x] ?// {("a" + ', "): $y} | 1")]
        for open_, close in cases:
            with self.subTest(open=open_):
                levels = LIMIT // 2 if "{(" in open_ else LIMIT
                binds("def f: 1; def f(g): 1; " + open_ * levels + "1" + close * levels)

    def test_long_sequences_do_not_recurse(self) -> None:
        cases = [("", "1 + ", "1"), ("", "1 * ", "1"), ("", "1 // ", "1"), ("", "1 and ", "1"), ("", "1 == 1 or ", "1"), ("", "1 | ", "1"),
                 ("", "1, ", "1"), ("", "1 as $x | ", "$x"), ("", "label $x | ", "break $x"), ("", "def f: 1; ", "f"),
                 ("(", "def f: 1; ", "f)"), ("[1, ", "def f: 1; ", "f]"), ("", "-", "1"), ("", "1 + -", "1"), ("", ".a", ""), ("", ".[0]", ""),
                 ("", ".a?", ""), (". as $a", " ?// $a", " | 1"), ("{a: ", "1 | ", "1}"), ("{", "a: 1, ", "}"), ('"', "\\(1)", '"'),
                 ("[", "1, 1 as $x | ", "$x]"), ("", "1, def f: 1; ", "f"), ("", "1, label $x | ", "1"), ("", "1 // 1 | ", "1"),
                 ("", "1 as $x | label $y | def f: $x; ", "f"), ("", "1 | 1, 1 | ", "1"), ("", "def f(g): g; ", "f(.)"),
                 ("", "reduce . as $x (.; .) | ", "1")]
        for prefix, element, suffix in cases:
            with self.subTest(element=element):
                binds(prefix + element * 10_000 + suffix)

    def test_long_elif_chains_do_not_recurse(self) -> None:
        binds("if . then 1 " + "elif . then 1 " * 10_000 + "else 2 end")

    def test_a_variable_ten_thousand_frames_out(self) -> None:
        node = binds("0 as $a | " + "1 as $x | " * 10_000 + "$a").body
        depth = 0
        while node.kind == B.NodeKind.BIND:
            node = node.body  # type: ignore[attr-defined]
            depth += 1
        self.assertEqual(10_001, depth)
        self.assertEqual((B.NodeKind.VARIABLE, 10_000, 0), (node.kind, node.hops, node.slot))  # type: ignore[attr-defined]

    def test_many_definitions_in_a_row(self) -> None:
        program = binds("def f: 1; " * 10_000 + "f")
        self.assertEqual(_prelude_count() + 10_000, len(program.functions))
        self.assertEqual(B.NodeKind.CALL_FUNCTION, program.body.kind)
        self.assertEqual(len(program.functions) - 1, program.body.function)  # type: ignore[attr-defined]
        self.assertEqual(0, program.body.hops)  # type: ignore[attr-defined]

    def test_many_parameters_and_arguments(self) -> None:
        program = binds("def f(" + "a; " * 10_000 + "b): b; f(" + "1; " * 10_000 + "1)")
        self.assertEqual(10_001, len(program.body.arguments))  # type: ignore[attr-defined]
        body = program.functions[-1].body
        self.assertEqual((B.NodeKind.CALL_PARAMETER, 0, 10_000), (body.kind, body.hops, body.slot))  # type: ignore[attr-defined]


class CorpusBindTests(unittest.TestCase):
    """Every program of spec/tests/*.test binds, and every %%FAIL case fails to parse or to bind,
    with the fixed message of spec §10.2 it names."""

    def test_every_program_of_the_corpus_binds(self) -> None:
        failures = []
        for case in corpus.all_cases():
            program = Parser.try_parse(case.program)
            if type(program) is SyntaxError:
                if not case.should_fail:
                    failures.append(f"{case.name}: does not parse: {program.message}")
                continue
            result = Resolver.try_bind(program, True, corpus.CORPUS_MODULES)  # type: ignore[arg-type]
            bound_ok = type(result) is not SyntaxError
            if case.should_fail and bound_ok:
                failures.append(f"{case.name}: '{case.program}' binds but is a %%FAIL case")
            elif not case.should_fail and not bound_ok:
                failures.append(f"{case.name}: '{case.program}': {result.message}")  # type: ignore[union-attr]
            elif case.should_fail and case.expected_error is not None and case.expected_error not in result.message:  # type: ignore[union-attr]
                failures.append(f"{case.name}: '{result.message}' does not contain '{case.expected_error}'")  # type: ignore[union-attr]
        self.assertEqual([], failures)

    def test_some_failure_of_the_corpus_is_a_binding_error(self) -> None:
        binding_failures = [c.program for c in corpus.all_cases() if c.should_fail and type(Parser.try_parse(c.program)) is not SyntaxError]
        self.assertIn(". as $foo | break $foo", binding_failures)


def _module_error(program: str, modules: object) -> str:
    result = Resolver.try_compile(program, True, modules)  # type: ignore[arg-type]
    assert type(result) is SyntaxError, program
    return f"{result.message} @{result.module or 'main'}:{result.line}:{result.column}"


def _module_run(program: str, modules: object, input: str = "null") -> str:
    return ", ".join(run(compile(program, True, modules), input))


class ModuleTests(unittest.TestCase):
    """The modules group (spec §9.11) beyond the corpus: errors and where they are, loading once, limits, modulemeta."""

    def test_binding_errors_say_where_they_are(self) -> None:
        cases = [('import "double" as d; import "nope" as n; 1', "module not found: nope @main:1:23"),
                 ('import "broken" as b; 1', "Expected ')' but found ';' @broken.jq:1:15"),
                 ('import "usesmain" as u; 1', "mainf/0 is not defined @usesmain.jq:1:15"),
                 ('import "cycle1" as c; 1', "circular import of cycle1.jq @cycle2.jq:1:1"),
                 ('import "self" as s; 1', "circular import of self.jq @self.jq:1:1"),
                 ('include "config"; 1', "module not found: config @main:1:1"),
                 ('import "a" as a; a::zz', "a::zz/0 is not defined @main:1:18"),
                 ('import "values" as $d; $d::x', "$d::x is not defined @main:1:24")]
        for program, expected in cases:
            with self.subTest(program=program):
                self.assertEqual(expected, _module_error(program, corpus.CORPUS_MODULES))

    def test_bad_data_is_a_binding_error(self) -> None:
        self.assertTrue(_module_error('import "bad" as $b; $b', corpus.CORPUS_MODULES)
                        .startswith("error loading data file bad.json: Unfinished JSON term at EOF"))

    def test_without_a_source_no_module_exists(self) -> None:
        self.assertEqual("module not found: a @main:1:1", _module_error('import "a" as a; 1', None))
        self.assertEqual(['"module not found: a"'], run('try ("a" | modulemeta) catch .'))

    def test_each_module_is_read_once(self) -> None:
        modules = corpus.DirectoryModules(corpus.MODULES)
        self.assertEqual("2", _module_run('import "one" as o; import "one" as p; include "one"; o::k + p::k', modules))
        self.assertEqual(1, modules.reads)

    def test_a_long_import_chain_is_no_recursion(self) -> None:
        sources = {f"m{i}.jq": ("def f: 999;" if i == 999 else f'import "m{i + 1}" as n; def f: n::f; def g: {i};') for i in range(1000)}
        modules = MemoryModules(sources)
        self.assertEqual("999, 0", on_small_stack(None, lambda: _module_run('import "m0" as m; m::f, m::g', modules)))  # type: ignore[arg-type]

    def test_modulemeta_uses_what_the_program_loaded(self) -> None:
        sources = {"x.jq": "def a: 1;"}
        program = compile('import "x" as x; ["x", "y"][] | modulemeta | .defs', True, MemoryModules(sources))
        sources["x.jq"] = "def changed: 1;"
        sources["y.jq"] = "def b: 1; def c: 2;"
        # x as the program loaded it; y, which it does not import, from its source now.
        self.assertEqual(['["a/0"]', '["b/0","c/0"]'], run(program, None))

    def test_modules_share_the_host_variables(self) -> None:
        program = compile('import "v" as v; v::v, $x', True, MemoryModules({"v.jq": "def v: $x;"}))
        self.assertEqual(["7", "7"], run(program, None, EvaluationOptions(variables={"x": 7.0})))

    def test_qualified_aliases_nest(self) -> None:
        self.assertEqual("1", _module_run('import "a" as x::y; x::y::f', MemoryModules({"a.jq": "def f: 1;"})))

    def test_a_module_obeys_the_nesting_limit_of_its_file(self) -> None:
        deep = "[" * (MAX_NESTING + 1) + "]" * (MAX_NESTING + 1)
        modules = MemoryModules({"deep.jq": f"def f: {deep};"})
        self.assertEqual("Program too deeply nested @deep.jq:1:1007", _module_error('import "deep" as d; 1', modules))

    def test_the_corpus_modules_folder_exists(self) -> None:
        self.assertTrue(os.path.isdir(corpus.MODULES))
