"""The syntax layer (dotnet/Tests/Core/Syntax): lexer, parser, nesting limits, the corpus and the prelude parse."""

from __future__ import annotations

import os
import unittest
from typing import Any

from jqlite.syntax import ast as S
from jqlite.syntax.lexer import Lexer
from jqlite.syntax.parser import MAX_NESTING, Parser
from jqlite.syntax.syntax_error import SyntaxError
from jqlite.syntax.token import KEYWORDS, TokenKind, is_keyword
from jqlite.values import value_text

from . import corpus
from .test_values import on_small_stack

# PascalCase names of the token kinds, as the reference's tests print them.
_KIND_NAMES = {value: "".join(part.capitalize() for part in name.split("_"))
               for name, value in vars(TokenKind).items() if name.isupper()}
_KIND_NAMES[TokenKind.EOF] = "Eof"


def lex(source: str) -> list:
    return Lexer.tokenize(source)


def describe(source: str) -> str:
    return " ".join(f"{_KIND_NAMES[t.kind]}:{t.text}@{t.line}:{t.column}" for t in lex(source) if t.kind != TokenKind.EOF)


def kinds(source: str) -> str:
    return " ".join(_KIND_NAMES[t.kind] for t in lex(source) if t.kind != TokenKind.EOF)


def texts(source: str) -> str:
    return " ".join(t.text for t in lex(source) if t.kind != TokenKind.EOF)


def lex_error(source: str) -> str:
    last = lex(source)[-1]
    assert last.kind == TokenKind.ERROR, describe(source)
    return f"{last.text} @{last.line}:{last.column}"


class LexerTests(unittest.TestCase):
    def test_every_punctuation_token_longest_first(self) -> None:
        source = ". .. [ ] { } ( ) | , : ; ? ?// = == != < <= > >= + - * / % += -= *= /= %= // //= |="
        self.assertEqual(
            "Dot DotDot LeftBracket RightBracket LeftBrace RightBrace LeftParen RightParen Pipe Comma Colon Semicolon Question QuestionAlt "
            "Assign Equal NotEqual Less LessEqual Greater GreaterEqual Plus Minus Star Slash Percent "
            "PlusAssign MinusAssign StarAssign SlashAssign PercentAssign Alt AltAssign PipeAssign",
            kinds(source))
        self.assertEqual(source, texts(source))

    def test_adjacent_punctuation_takes_the_longest_match(self) -> None:
        self.assertEqual("QuestionAlt Assign", kinds("?//="))
        self.assertEqual("DotDot Dot", kinds("..."))
        self.assertEqual("Alt Slash", kinds("///"))
        self.assertEqual("PipeAssign Assign", kinds("|=="))

    def test_every_keyword(self) -> None:
        for word, kind in KEYWORDS:
            tokens = [t for t in lex(word) if t.kind != TokenKind.EOF]
            self.assertEqual(1, len(tokens))
            self.assertEqual(kind, tokens[0].kind)
            self.assertEqual(word, tokens[0].text)
            self.assertTrue(is_keyword(tokens[0].kind))
        self.assertEqual(18, len(KEYWORDS))

    def test_names(self) -> None:
        self.assertEqual("Ident:foo@1:1 Ident:a::b@1:5 Ident:_x1@1:10 Ident:null@1:14 Ident:not@1:19", describe("foo a::b _x1 null not"))
        self.assertEqual("Field:a@1:1 Field:end@1:3 Field:e0@1:7 Field:_@1:10", describe(".a.end.e0._"))
        self.assertEqual("Variable:x@1:1 Variable:then@1:4 Variable:a::b@1:10 Variable:__loc__@1:16", describe("$x $then $a::b $__loc__"))
        self.assertEqual("Format:base64@1:1 Format:base64d@1:9 Format:1x@1:18", describe("@base64 @base64d @1x"))

    def test_a_qualified_name_may_have_several_double_colons(self) -> None:
        self.assertEqual("Ident", kinds("a::b::c"))
        self.assertEqual("a::b::c", texts("a::b::c"))
        self.assertEqual("Variable", kinds("$a::b::c"))
        self.assertEqual("a : :", texts("a::"))
        self.assertEqual("Field Colon Colon Ident", kinds(".a::b"))

    def test_numbers(self) -> None:
        cases = [("0", 0.0), ("42", 42.0), (".5", 0.5), ("1.", 1.0), ("1.5", 1.5), ("1e3", 1000.0), ("1E+3", 1000.0), ("1.e3", 1000.0),
                 (".5e1", 5.0), ("25e-1", 2.5), ("0.1", 0.1), ("1e1000", float("inf")), ("1e-1000", 0.0),
                 ("100000000000000000000000000001", 1e29)]
        for source, value in cases:
            tokens = [t for t in lex(source) if t.kind != TokenKind.EOF]
            self.assertEqual(1, len(tokens))
            self.assertEqual(TokenKind.NUMBER, tokens[0].kind)
            self.assertEqual(value, tokens[0].number)
            self.assertIs(float, type(tokens[0].number))
            self.assertEqual(source, tokens[0].text)

    def test_number_boundaries(self) -> None:
        self.assertEqual("Field", kinds(".e0"))
        self.assertEqual("Number Ident", kinds("1e"))
        self.assertEqual("Number Ident Plus", kinds("1e+"))
        self.assertEqual("1.5 .3", texts("1.5.3"))
        self.assertEqual("DotDot Number", kinds("..5"))
        self.assertEqual("Number Ident", kinds("1x"))

    def test_positions_across_line_breaks_and_non_bmp_characters(self) -> None:
        # Columns count code points.
        self.assertEqual("String:😀😀@1:1 Plus:+@1:6 Number:1@2:3 Plus:+@3:1 Field:a@3:3", describe('"😀😀" +\n  1\r\n+ .a'))
        self.assertEqual("String:a\nb@1:1 Ident:x@2:4", describe('"a\nb" x'))

    def test_a_lone_cr_is_white_space_not_a_line_break(self) -> None:
        self.assertEqual("Number:1@1:1 Plus:+@1:3 Number:2@1:5", describe("1\r+\r2"))

    def test_comments(self) -> None:
        self.assertEqual("Number:1@1:1 Plus:+@2:1 Number:2@2:3", describe("1 # a comment\n+ 2 # another"))
        # An odd number of backslashes at the end of the line continues the comment.
        self.assertEqual("1 + 10", texts("1 # c \\\n+ 1\n+ 10"))
        self.assertEqual("1 + 1 + 10", texts("1 # c \\\\\n+ 1\n+ 10"))
        self.assertEqual("1 + 10", texts("1 # c \\\\\\\n+ 1\n+ 10"))
        self.assertEqual("1 + 10", texts("1 # c \\\r\n+ 1\r\n+ 10"))
        self.assertEqual("1 + 1 + 10", texts("1 # c \\ x\n+ 1\n+ 10"))
        # As in jq, a lone CR does not end a comment.
        self.assertEqual("1", texts("1 # c\r+ 1"))
        self.assertEqual("1 + 10", texts("1 # c \\\r+ 1\n+ 10"))

    def test_string_escapes(self) -> None:
        cases = [('"abc"', "abc"), ('"\\"\\\\\\/\\b\\f\\n\\r\\t"', '"\\/\b\f\n\r\t'), ('"\\u00e9\\u00C9"', "éÉ"),
                 ('"\\uD83D\\uDE00"', "😀"), ('"\\ud83d x"', "� x"), ('"\\ude00"', "�"),
                 ('"\\uDE00\\uD83D"', "��"), ('"\\uD83D\\u0041"', "�A"), ('"\\uD83D\\uD83D\\uDE00"', "�😀"),
                 ('"\\u12345"', "ሴ5"), ('"é\t"', "é\t")]
        for source, text in cases:
            tokens = [t for t in lex(source) if t.kind != TokenKind.EOF]
            self.assertEqual(1, len(tokens))
            self.assertEqual(TokenKind.STRING, tokens[0].kind)
            self.assertEqual(text, tokens[0].text)

    def test_a_lone_surrogate_in_the_source_is_u_fffd(self) -> None:
        tokens = [t for t in lex('"a\ud800b\udc00"') if t.kind != TokenKind.EOF]
        self.assertEqual("a�b�", tokens[0].text)
        self.assertEqual("Invalid character '�' @1:2", lex_error("a\ud800"))
        self.assertEqual("String:�@1:1 Plus:+@1:5", describe('"\ud800" +'))

    def test_interpolation_is_split_into_pieces(self) -> None:
        self.assertEqual(
            "StringStart:a@1:1 Number:1@1:5 Plus:+@1:7 LeftParen:(@1:9 Number:2@1:10 RightParen:)@1:11 StringMiddle:b@1:12 "
            "String:c@1:16 StringEnd:d@1:19",
            describe('"a\\(1 + (2))b\\("c")d"'))

    def test_interpolation_nests(self) -> None:
        self.assertEqual("StringStart StringStart String StringEnd StringEnd", kinds('"a\\("b\\("c")")"'))
        self.assertEqual("a b c", texts('"a\\("b\\("c")")"').strip())

    def test_a_parenthesis_outside_interpolation_is_punctuation(self) -> None:
        self.assertEqual("RightParen", kinds(")"))

    def test_errors(self) -> None:
        cases = [('"abc', "Unterminated string @1:1"), ('1 + "a\\(1', "Unterminated string @1:5"),
                 ('"a\\("b" + (1', "Unterminated string @1:1"), ('"a\\', "Unterminated string @1:1"), ('"a\\qb"', "Invalid escape @1:3"),
                 ('"x"\n "\\u12"', "Invalid escape @2:3"), ('"\\u12G4"', "Invalid escape @1:2"), ('"\\u"', "Invalid escape @1:2"),
                 ("1 $ x", "Expected a variable name after '$' @1:3"), ("@", "Expected a format name after '@' @1:1"),
                 ("{aé: 1}", "Invalid character 'é' @1:3"), ("😀", "Invalid character '😀' @1:1"),
                 ("1\f+2", "Invalid character U+000C @1:2"), ("1 ! 2", "Invalid character '!' @1:3")]
        for source, expected in cases:
            self.assertEqual(expected, lex_error(source), source)

    def test_an_error_is_the_last_token(self) -> None:
        self.assertEqual("Number Plus Error", " ".join(_KIND_NAMES[t.kind] for t in lex('1 + "a\\q" + 2')))

    def test_empty_source_is_eof(self) -> None:
        tokens = lex("  # nothing\n")
        self.assertEqual(1, len(tokens))
        self.assertEqual(TokenKind.EOF, tokens[0].kind)
        self.assertEqual((2, 1), (tokens[0].line, tokens[0].column))


_SYMBOLS = {S.BinaryOp.ADD: "+", S.BinaryOp.SUBTRACT: "-", S.BinaryOp.MULTIPLY: "*", S.BinaryOp.DIVIDE: "/",
            S.BinaryOp.MODULO: "%", S.BinaryOp.EQUAL: "==", S.BinaryOp.NOT_EQUAL: "!=", S.BinaryOp.LESS: "<",
            S.BinaryOp.LESS_EQUAL: "<=", S.BinaryOp.GREATER: ">", S.BinaryOp.GREATER_EQUAL: ">="}


def _signature(definition: S.Definition) -> str:
    if not definition.params:
        return definition.name
    return f"{definition.name}({';'.join(('$' if p.is_variable else '') + p.name for p in definition.params)})"


def print_node(node: S.Node | None) -> str:
    """Prints an AST as an S-expression (AstPrinter.cs). Recursive: for the trees of ordinary tests only."""
    if node is None:
        return "_"
    k = node.kind
    K = S.NodeKind
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
            parts.append(" " + value_text.to_json(n.texts[i]) + " " + print_node(part))  # type: ignore[attr-defined]
        return "".join(parts) + " " + value_text.to_json(n.texts[-1]) + ")"  # type: ignore[attr-defined]
    if k == K.FORMAT:
        return "@" + n.name  # type: ignore[attr-defined]
    if k == K.INDEX:
        return f"(index {print_node(n.target)} {print_node(n.key)})"  # type: ignore[attr-defined]
    if k == K.SLICE:
        return f"(slice {print_node(n.target)} {print_node(n.frm)} {print_node(n.to)})"  # type: ignore[attr-defined]
    if k == K.ITERATE:
        return f"(iter {print_node(n.target)})"  # type: ignore[attr-defined]
    if k == K.VARIABLE:
        return "$" + n.name  # type: ignore[attr-defined]
    if k == K.CALL:
        if not n.arguments:  # type: ignore[attr-defined]
            return n.name  # type: ignore[attr-defined]
        return f"({n.name} {' '.join(print_node(a) for a in n.arguments)})"  # type: ignore[attr-defined]
    if k == K.PIPE:
        return f"(| {print_node(n.left)} {print_node(n.right)})"  # type: ignore[attr-defined]
    if k == K.COMMA:
        return f"(, {print_node(n.left)} {print_node(n.right)})"  # type: ignore[attr-defined]
    if k == K.BINARY:
        return f"({_SYMBOLS[n.op]} {print_node(n.left)} {print_node(n.right)})"  # type: ignore[attr-defined]
    if k == K.AND:
        return f"(and {print_node(n.left)} {print_node(n.right)})"  # type: ignore[attr-defined]
    if k == K.OR:
        return f"(or {print_node(n.left)} {print_node(n.right)})"  # type: ignore[attr-defined]
    if k == K.ALTERNATIVE:
        return f"(// {print_node(n.left)} {print_node(n.right)})"  # type: ignore[attr-defined]
    if k == K.NEGATE:
        return f"(neg {print_node(n.operand)})"  # type: ignore[attr-defined]
    if k == K.ARRAY_EXPR:
        return "(array)" if n.body is None else f"(array {print_node(n.body)})"  # type: ignore[attr-defined]
    if k == K.OBJECT_EXPR:
        if not n.entries:  # type: ignore[attr-defined]
            return "(object)"
        return "(object " + " ".join(f"({print_node(e.key)} {print_node(e.value)})" for e in n.entries) + ")"  # type: ignore[attr-defined]
    if k == K.IF:
        return f"(if {print_node(n.condition)} {print_node(n.then)} {print_node(n.else_)})"  # type: ignore[attr-defined]
    if k == K.TRY:
        if n.handler is None:  # type: ignore[attr-defined]
            return f"(try {print_node(n.body)})"  # type: ignore[attr-defined]
        return f"(try {print_node(n.body)} {print_node(n.handler)})"  # type: ignore[attr-defined]
    if k == K.REDUCE:
        return f"(reduce {print_node(n.source)} {print_patterns(n.patterns)} {print_node(n.init)} {print_node(n.update)})"  # type: ignore[attr-defined]
    if k == K.FOREACH:
        return (f"(foreach {print_node(n.source)} {print_patterns(n.patterns)} {print_node(n.init)} "  # type: ignore[attr-defined]
                f"{print_node(n.update)} {print_node(n.extract)})")  # type: ignore[attr-defined]
    if k == K.BIND:
        return f"(as {print_node(n.source)} {print_patterns(n.patterns)} {print_node(n.body)})"  # type: ignore[attr-defined]
    if k == K.FUNC_DEF:
        d = n.definition  # type: ignore[attr-defined]
        return f"(def {_signature(d)} {print_node(d.body)} {print_node(n.rest)})"  # type: ignore[attr-defined]
    if k == K.LABEL:
        return f"(label ${n.name} {print_node(n.body)})"  # type: ignore[attr-defined]
    return f"(break ${n.name})"  # type: ignore[attr-defined]


def print_patterns(patterns: list) -> str:
    return print_pattern(patterns[0]) if len(patterns) == 1 else "(?// " + " ".join(print_pattern(p) for p in patterns) + ")"


def print_pattern(pattern: S.Pattern | None) -> str:
    if pattern is None:
        return "_"
    if pattern.kind == S.PatternKind.VARIABLE:
        return "$" + pattern.name  # type: ignore[attr-defined]
    if pattern.kind == S.PatternKind.ARRAY:
        return "[" + " ".join(print_pattern(e) for e in pattern.elements) + "]"  # type: ignore[attr-defined]
    entries = [f"({print_node(e.key)} {'_' if e.variable is None else '$' + e.variable} {print_pattern(e.value)})"
               for e in pattern.entries]  # type: ignore[attr-defined]
    return "{" + " ".join(entries) + "}"


def print_program(program: S.Program) -> str:
    parts = []
    if program.metadata is not None:
        parts.append(f"(module {value_text.to_json(program.metadata)})")
    for imp in program.imports:
        alias = "" if imp.alias is None else (f" ${imp.alias}" if imp.is_data else f" {imp.alias}")
        meta = "" if imp.metadata is None else " " + value_text.to_json(imp.metadata)
        parts.append(f'({"include" if imp.alias is None else "import"} "{imp.path}"{alias}{meta})')
    for definition in program.definitions:
        parts.append(f"(def {_signature(definition)} {print_node(definition.body)})")
    if program.body is not None:
        parts.append(print_node(program.body))
    return " ".join(parts)


def parse(source: str) -> str:
    program = Parser.try_parse(source)
    if type(program) is SyntaxError:
        raise AssertionError(f"'{source}' does not parse: {program.message} at {program.line}:{program.column}")
    return print_program(program)  # type: ignore[arg-type]


def error(source: str) -> str:
    program = Parser.try_parse(source)
    if type(program) is not SyntaxError:
        raise AssertionError(f"'{source}' parses as {print_program(program)}")  # type: ignore[arg-type]
    return f"{program.message} @{program.line}:{program.column}"


class ParserTests(unittest.TestCase):
    def check(self, cases: list[tuple[str, str]], function: object = parse) -> None:
        for source, expected in cases:
            with self.subTest(source=source):
                self.assertEqual(expected, function(source))  # type: ignore[operator]

    def test_precedence_examples_of_the_spec(self) -> None:
        self.check([
            ("1, 2 as $x | $x * 10", "(, 1 (as 2 $x (* $x 10)))"), ("1 + 2 as $x | -$x", "(as (+ 1 2) $x (neg $x))"),
            ("[-1 as $x | 1, $x]", "(array (as (neg 1) $x (, 1 $x)))"), ("1 // 2 == 2", "(// 1 (== 2 2))"),
            ("{} | .a = .b // 1", '(| (object) (// (_assign (index . "a") (index . "b")) 1))'),
            ("[1 // 2 as $x | 10]", "(array (as (// 1 2) $x 10))"),
            ('try (2 * -3 / "a") catch .', '(try (/ (* 2 (neg 3)) "a") .)'),
            ("1 + try 2 catch 3 + 4", "(+ (+ 1 (try 2 3)) 4)"), ("try -.? catch .", "(try (neg (try .)) .)"),
            ("def f: 1; f | f, 2", "(def f 1) (| f (, f 2))"),
        ])

    def test_precedence_examples_that_fail(self) -> None:
        self.check([("reduce 1, 2 as $x (0; . + $x)", "Expected 'as' but found ',' @1:9"),
                    ("try 1 * 2 catch 3", "Unexpected 'catch' @1:11")], error)

    def test_precedence_and_associativity(self) -> None:
        self.check([
            ("1 | 2 | 3", "(| 1 (| 2 3))"), ("1, 2, 3", "(, (, 1 2) 3)"), ("1, 2 | 3, 4", "(| (, 1 2) (, 3 4))"),
            ("1 // 2 // 3", "(// 1 (// 2 3))"), ("1 // 2, 3", "(, (// 1 2) 3)"), (".a = 1 // 2", '(// (_assign (index . "a") 1) 2)'),
            (".a |= . + 1", '(_modify (index . "a") (+ . 1))'), (".a = 1 or 2", '(_assign (index . "a") (or 1 2))'),
            ("1 or 2 or 3", "(or (or 1 2) 3)"), ("1 or 2 and 3", "(or 1 (and 2 3))"), ("1 and 2 or 3", "(or (and 1 2) 3)"),
            ("1 and 2 and 3", "(and (and 1 2) 3)"), ("1 == 2 and 3 != 4", "(and (== 1 2) (!= 3 4))"),
            ("1 + 2 < 3 - 4", "(< (+ 1 2) (- 3 4))"), ("1 <= 2", "(<= 1 2)"), ("1 > 2", "(> 1 2)"), ("1 >= 2", "(>= 1 2)"),
            ("1 - 2 - 3", "(- (- 1 2) 3)"), ("1 - 2 + 3", "(+ (- 1 2) 3)"), ("1 + 2 * 3", "(+ 1 (* 2 3))"),
            ("1 * 2 + 3", "(+ (* 1 2) 3)"), ("1 * 2 / 3 % 4", "(% (/ (* 1 2) 3) 4)"), ("-1 * 2", "(* (neg 1) 2)"),
            ("2 * -3 * 4", "(* (* 2 (neg 3)) 4)"), ("1 - -1", "(- 1 (neg 1))"), ("- - 1", "(neg (neg 1))"),
            ("-.a[0]?", '(neg (try (index (index . "a") 0)))'), ("-1 as $x | $x", "(as (neg 1) $x $x)"),
            ("1 = 2 as $x | $x", "(as (_assign 1 2) $x $x)"), ("1 or 2 as $x | $x", "(as (or 1 2) $x $x)"),
        ])

    def test_assignments_are_rewritten(self) -> None:
        self.check([
            (".a += 1", '(as 1 $*update (_modify (index . "a") (+ . $*update)))'),
            (".a -= 1", '(as 1 $*update (_modify (index . "a") (- . $*update)))'),
            (".a *= 1", '(as 1 $*update (_modify (index . "a") (* . $*update)))'),
            (".a /= 1", '(as 1 $*update (_modify (index . "a") (/ . $*update)))'),
            (".a %= 1", '(as 1 $*update (_modify (index . "a") (% . $*update)))'),
            (".a //= 1", '(as 1 $*update (_modify (index . "a") (// . $*update)))'),
            (".a = (1, 2)", '(_assign (index . "a") (, 1 2))'),
        ])

    def test_non_associative_operators_do_not_chain(self) -> None:
        self.check([
            ("1 == 2 == 3", "Operator '==' cannot be chained: add parentheses @1:8"),
            ("1 == 2 < 3", "Operator '<' cannot be chained: add parentheses @1:8"),
            ("1 < 2 < 3", "Operator '<' cannot be chained: add parentheses @1:7"),
            (".a = .b = 1", "Operator '=' cannot be chained: add parentheses @1:9"),
            (".a = 1 |= 2", "Operator '|=' cannot be chained: add parentheses @1:8"),
        ], error)

    def test_try_operands_are_unary(self) -> None:
        self.check([
            ("try 1", "(try 1)"), ('try error("x") catch . | ascii_upcase', '(| (try (error "x") .) ascii_upcase)'),
            ('[try error("x") + 1]', '(array (+ (try (error "x")) 1))'), ("try 1 catch .[0]", "(try 1 (index . 0))"),
            ("try -1", "(try (neg 1))"), ("try try 1 catch 2 catch 3", "(try (try 1 2) 3)"), ("-try 1 catch 2", "(neg (try 1 2))"),
        ])

    def test_try_operand_errors(self) -> None:
        self.check([('[try -"a" * 0 catch "c"]', "Expected ']' but found 'catch' @1:15"),
                    ("try def f: 1; f", "Unexpected 'def' @1:5")], error)

    def test_binding_sources(self) -> None:
        self.check([
            ("1 as $x | 2 as $y | [$x, $y]", "(as 1 $x (as 2 $y (array (, $x $y))))"), ("1 as $x | 2 | 3", "(as 1 $x (| 2 3))"),
            ("(1 as $x | $x), 2", "(, (as 1 $x $x) 2)"), ("1, 2, 3 as $x | 4, 5 | 6", "(, (, 1 2) (as 3 $x (| (, 4 5) 6)))"),
            ("1 | 2 as $x | 3", "(| 1 (as 2 $x 3))"), ("reduce .[] as $x (0; . + $x)", "(reduce (iter .) $x 0 (+ . $x))"),
            ("reduce .[] / .[] as $x (0; . + $x)", "(reduce (/ (iter .) (iter .)) $x 0 (+ . $x))"),
            ("reduce 1 // 2 as $x (0; $x)", "(reduce (// 1 2) $x 0 $x)"), ("reduce -1 as $x (0; $x)", "(reduce (neg 1) $x 0 $x)"),
            ("reduce (1,2) as $x (0; .+$x) | . * 2", "(| (reduce (, 1 2) $x 0 (+ . $x)) (* . 2))"),
            ("foreach (1,2) as $x (0; . + $x)", "(foreach (, 1 2) $x 0 (+ . $x) .)"),
            ("foreach 1 + 2 as $x (0; $x; [$x, .])", "(foreach (+ 1 2) $x 0 $x (array (, $x .)))"),
            ("[foreach .[] as [$a] ?// $a (0; 1)]", "(array (foreach (iter .) (?// [$a] $a) 0 1 .))"),
        ])

    def test_definitions_and_labels_consume_the_rest_of_the_pipe(self) -> None:
        self.check([
            ("def f: 1; def g: 2; f", "(def f 1) (def g 2) f"), ("def f: 1;", "(def f 1) ."), ("", "."),
            ("1, def f: 2; f, 3", "(, 1 (def f 2 (, f 3)))"), ("[1, 2 | def f: 3; f, 4]", "(array (| (, 1 2) (def f 3 (, f 4))))"),
            ("(def f: 1; f | f), 2", "(, (def f 1 (| f f)) 2)"), ("if def f: 1; f then 2 else 3 end", "(if (def f 1 f) 2 3)"),
            ("def f: def g: 3; g; f", "(def f (def g 3 g)) f"), ("label $out | 1, 2 | 3", "(label $out (| (, 1 2) 3))"),
            ("[1, label $f | 2, 3]", "(array (, 1 (label $f (, 2 3))))"), ("label $f | break $f", "(label $f (break $f))"),
            ("label $if | 1", "(label $if 1)"), ("label $a::b | 1", "(label $a::b 1)"),
        ])

    def test_definitions_and_labels_are_not_operands(self) -> None:
        self.check([
            ("1 + def f: 1; f", "Unexpected 'def' @1:5"), ("1 // def f: 1; f", "Unexpected 'def' @1:6"),
            ("-def f: 1; f", "Unexpected 'def' @1:2"), ("1 + label $f | 1", "Unexpected 'label' @1:5"),
            ("label f | 1", "Expected a label name ($name) but found 'f' @1:7"),
            ("break f", "Expected a label name ($name) but found 'f' @1:7"),
        ], error)

    def test_definitions_and_calls(self) -> None:
        self.check([
            ("def f(a; $b): [a, $b, b]; f(1; 2)", "(def f(a;$b) (array (, (, a $b) b))) (f 1 2)"),
            ("def f($a; $b): $a + $b; 1", "(def f($a;$b) (+ $a $b)) 1"), ("def f(x): x; f(.)", "(def f(x) x) (f .)"),
            ("def true: 1; true", "(def true 1) true"), ("def f::g: 1; f::g", "(def f::g 1) f::g"),
            ("def f(a::b): a::b; f(1)", "(def f(a::b) a::b) (f 1)"), ("f(1; 2, 3; 4 | 5)", "(f 1 (, 2 3) (| 4 5))"), ("a::b", "a::b"),
        ])

    def test_definition_errors(self) -> None:
        self.check([
            ("def if: 1; 1", "Expected a function name but found 'if' @1:5"),
            ("def f(): 1; f", "Expected a parameter name but found ')' @1:7"),
            ("def f(a;): 1; 1", "Expected a parameter name but found ')' @1:9"),
            ("def f(if): 1; 1", "Expected a parameter name but found 'if' @1:7"),
            ("def f: 1", "Expected ';' but found end of program @1:9"), ("def f 1; 1", "Expected ':' but found '1' @1:7"),
        ], error)

    def test_paths_and_postfix(self) -> None:
        self.check([
            (".", "."), ("..", "recurse"), ("..?", "(try recurse)"), ("..|.a?", '(| recurse (try (index . "a")))'),
            (".. .a?", '(try (index recurse "a"))'), ("..[0]?", "(try (index recurse 0))"), (".a", '(index . "a")'),
            ('.a.b."c"[0]', '(index (index (index (index . "a") "b") "c") 0)'), (".a .b", '(index (index . "a") "b")'),
            ('."a"', '(index . "a")'), ('. "a"', '(index . "a")'), ('."a"."b"', '(index (index . "a") "b")'),
            ('."a\\(1)"', '(index . (str "a" 1 ""))'), (".a.[0]", '(index (index . "a") 0)'), (".[0]", "(index . 0)"),
            (". [0]", "(index . 0)"), (".[1 + 1]", "(index . (+ 1 1))"), ('.["a" + "b"]', '(index . (+ "a" "b"))'),
            (".a[.k]", '(index (index . "a") (index . "k"))'), (".[0, 1]", "(index . (, 0 1))"), (".[]", "(iter .)"),
            (".[]?", "(try (iter .))"), (".a[]", '(iter (index . "a"))'), (".[1:2]", "(slice . 1 2)"), (".[1:]", "(slice . 1 _)"),
            (".[:2]", "(slice . _ 2)"), (".[1:$n]", "(slice . 1 $n)"), (".[1:][0]", "(index (slice . 1 _) 0)"),
            (".a?", '(try (index . "a"))'), (".a??", '(try (try (index . "a")))'), (".a?.b?", '(try (index (try (index . "a")) "b"))'),
            (".a[]?.b", '(index (try (iter (index . "a"))) "b")'), (".a? // 1", '(// (try (index . "a")) 1)'),
            ("$x[0]", "(index $x 0)"), ("$x.a", '(index $x "a")'), ('"abc"[1:]', '(slice "abc" 1 _)'), ("1[0]?", "(try (index 1 0))"),
            ("f.a", '(index f "a")'), ("if true then [1] else [2] end[0]", "(index (if true (array 1) (array 2)) 0)"),
            ('{"end":4} | .end', '(| (object ("end" 4)) (index . "end"))'),
        ])

    def test_path_errors(self) -> None:
        self.check([
            (".[:]", "Unexpected ']' @1:4"), (".[1:2:3]", "Expected ']' but found ':' @1:6"),
            (".a?//1", "Unexpected '?//': write '? //' for an optional followed by an alternative @1:3"),
            ("[.[]?//1]", "Unexpected '?//': write '? //' for an optional followed by an alternative @1:5"),
            ("..a", "Unexpected 'a' @1:3"), ("1 . 2", "Unexpected '.' @1:3"),
        ], error)

    def test_terms(self) -> None:
        self.check([
            ("null", "null"), ("true", "true"), ("false", "false"), ("1.5e3", "1500"), ('"a"', '"a"'), ("$__loc__", "$__loc__"),
            ("$ENV.PATH", '(index $ENV "PATH")'), ("[]", "(array)"), ("[1, 2]", "(array (, 1 2))"),
            ("[.[] | . * 2]", "(array (| (iter .) (* . 2)))"), ("(1)", "1"), ("((1, 2))", "(, 1 2)"), ("not", "not"), ("empty", "empty"),
        ])

    def test_strings_and_formats(self) -> None:
        self.check([
            ('"a\\(1)b\\(2)c"', '(str "a" 1 "b" 2 "c")'), ('"\\(1, 2)"', '(str "" (, 1 2) "")'),
            ('"a\\("b\\("c")")"', '(str "a" (str "b" "c" "") "")'), ('"\\(1 + "\\(2)")"', '(str "" (+ 1 (str "" 2 "")) "")'),
            ('"\\((1))"', '(str "" 1 "")'), ('@base64 "x\\(.)y"', '(str @base64 "x" . "y")'),
            ('@json "x\\("y")"', '(str @json "x" "y" "")'), ('@base64 "abc"', '"abc"'), ("@base64", "@base64"), ("@foo", "@foo"),
            ('@foo "x"', '"x"'), ("@text | @json", "(| @text @json)"),
        ])

    def test_string_errors(self) -> None:
        self.check([('"\\()"', "Unexpected ')' @1:4"), ('"\\(1;2)"', "Expected ')' but found ';' @1:5"),
                    ('"a\\(1', "Unterminated string @1:1")], error)

    def test_conditionals(self) -> None:
        self.check([
            ("if 1 then 2 else 3 end", "(if 1 2 3)"), ("if 1 then 2 end", "(if 1 2 .)"),
            ("if 1 then 2 elif 3 then 4 end", "(if 1 2 (if 3 4 .))"), ("if 1 then 2 elif 3 then 4 else 5 end", "(if 1 2 (if 3 4 5))"),
            ("if 1, 2 then 3 | 4 else 5, 6 end", "(if (, 1 2) (| 3 4) (, 5 6))"),
            ("[-if true then 1 else 2 end]", "(array (neg (if true 1 2)))"),
        ])

    def test_conditional_errors(self) -> None:
        self.check([("if 1 then 2 else 3", "Expected 'end' but found end of program @1:19"),
                    ("if 1 2 end", "Expected 'then' but found '2' @1:6")], error)

    def test_object_construction(self) -> None:
        self.check([
            ("{}", "(object)"), ("{a: 1}", '(object ("a" 1))'), ("{if: 1, then: 2}", '(object ("if" 1) ("then" 2))'),
            ('{"a b": 1}', '(object ("a b" 1))'), ('{"a\\(1)": 2}', '(object ((str "a" 1 "") 2))'),
            ('{@base64 "a\\(1)": 2}', '(object ((str @base64 "a" 1 "") 2))'), ("{$k: 1}", "(object ($k 1))"),
            ("{(.a, .b): 1}", '(object ((, (index . "a") (index . "b")) 1))'), ('{("a"): 1}', '(object ("a" 1))'),
            ("{a}", '(object ("a" _))'), ("{if}", '(object ("if" _))'), ("{null}", '(object ("null" _))'), ('{"a"}', '(object ("a" _))'),
            ('{"x\\(1, 2)"}', '(object ((str "x" (, 1 2) "") _))'), ('{@base64 "a"}', '(object ("a" _))'),
            ("{$x}", '(object ("x" $x))'), ("{$__loc__}", '(object ("__loc__" $__loc__))'),
            ('{a, $b, "c": 3}', '(object ("a" _) ("b" $b) ("c" 3))'), ("{a: 1,}", '(object ("a" 1))'),
            ("{a: 1, b: 2}", '(object ("a" 1) ("b" 2))'), ("{a: (1, 2)}", '(object ("a" (, 1 2)))'),
            ("{a: 1 | 2 | 3}", '(object ("a" (| 1 (| 2 3))))'), ("{a: 1, b: 2 | . + 1}", '(object ("a" 1) ("b" (| 2 (+ . 1))))'),
            ("{a: 1 + 2}", '(object ("a" (+ 1 2)))'), ("{a: -1}", '(object ("a" (neg 1)))'),
            ("{a: .x = 1}", '(object ("a" (_assign (index . "x") 1)))'), ("{a: 1 // 2}", '(object ("a" (// 1 2)))'),
            ("{a: reduce .[] as $x (0; 1)}", '(object ("a" (reduce (iter .) $x 0 1)))'), ("{a: if . then 1 end}", '(object ("a" (if . 1 .)))'),
            ("{a: try 1 catch 2}", '(object ("a" (try 1 2)))'), ("{a: .b?}", '(object ("a" (try (index . "b"))))'),
            ("{a: {b: {c: 1}}}", '(object ("a" (object ("b" (object ("c" 1))))))'), ("{a::b: 1}", '(object ("a::b" 1))'),
        ])

    def test_object_errors(self) -> None:
        self.check([
            ("{1: 2}", "May need parentheses around object key expression @1:2"),
            ("{1+2: 3}", "May need parentheses around object key expression @1:2"),
            ("{-1: 2}", "May need parentheses around object key expression @1:2"),
            ("{.a: 2}", "May need parentheses around object key expression @1:2"),
            ("{(1): 2}", "Cannot use number (1) as object key @1:3"), ("{(true): 2}", "Cannot use boolean (true) as object key @1:3"),
            ("{(null): 2}", "Cannot use null (null) as object key @1:3"), ('{("a")}', "Expected ':' but found '}' @1:7"),
            ("{,}", "Unexpected ',' @1:2"), ("{a:1,,}", "Unexpected ',' @1:6"), ("{a 1}", "Expected ',' or '}' but found '1' @1:4"),
            ("{a: 1, b}c", "Unexpected 'c' @1:10"), ("{a: 1 as $x | $x}", "Expected ',' or '}' but found 'as' @1:7"),
            ("{a: 1 | 2 as $x | $x}", "Expected ',' or '}' but found 'as' @1:11"), ("{a: def f: 1; f}", "Unexpected 'def' @1:5"),
            ("{a: label $f | 1}", "Unexpected 'label' @1:5"), ("{a: 1, 2}", "May need parentheses around object key expression @1:8"),
            ("[1, 2,]", "Unexpected ']' @1:7"),
        ], error)

    def test_patterns(self) -> None:
        self.check([
            (". as $x | $x", "(as . $x $x)"), (". as [$a, $b] | 1", "(as . [$a $b] 1)"), (". as [$a, [$b]] | 1", "(as . [$a [$b]] 1)"),
            (". as {$a} | 1", '(as . {("a" $a _)} 1)'), (". as {$a: [$b]} | 1", '(as . {("a" $a [$b])} 1)'),
            ('. as {a: $x, "b": $y, if: $z} | 1', '(as . {("a" _ $x) ("b" _ $y) ("if" _ $z)} 1)'),
            ('. as {"a\\(1)": $x} | 1', '(as . {((str "a" 1 "") _ $x)} 1)'), ('. as {@base64 "a": $x} | 1', '(as . {("a" _ $x)} 1)'),
            (". as {(1 | tostring): $x} | 1", "(as . {((| 1 tostring) _ $x)} 1)"), (". as {$then} | 1", '(as . {("then" $then _)} 1)'),
            (". as [$a, {b: $c}] | 1", '(as . [$a {("b" _ $c)}] 1)'), (". as [$a] ?// $a | $a", "(as . (?// [$a] $a) $a)"),
            (". as [$a]?//[$b] ?// {$c} | 1", '(as . (?// [$a] [$b] {("c" $c _)}) 1)'),
            ("reduce .[] as [$a] ?// $a (0; . + $a)", "(reduce (iter .) (?// [$a] $a) 0 (+ . $a))"), ("1 as $a::b | 1", "(as 1 $a::b 1)"),
        ])

    def test_pattern_errors(self) -> None:
        self.check([
            (". as [] | 1", "Expected a pattern ($name, [...] or {...}) but found ']' @1:7"), (". as {} | 1", "Unexpected '}' @1:7"),
            (". as [$a,] | 1", "Expected a pattern ($name, [...] or {...}) but found ']' @1:10"),
            (". as {(1): $x} | $x", "Cannot use number (1) as object key @1:8"),
            (". as {(true): $x} | $x", "Cannot use boolean (true) as object key @1:8"), (". as {a} | 1", "Expected ':' but found '}' @1:8"),
            (". as 1 | 1", "Expected a pattern ($name, [...] or {...}) but found '1' @1:6"), (". as $x 1", "Expected '|' but found '1' @1:9"),
            ("1 as $ x | $x", "Expected a variable name after '$' @1:6"),
        ], error)

    def test_module_directives(self) -> None:
        program = Parser.try_parse_module(
            'module {a: 1, "b": [1, "x", null, true, {c: false}], if: {},};\n'
            'import "m" as m {search: "./"};\nimport "d" as $d;\ninclude "i";\ninclude "j" {k: []};\n'
            "def f: 1; def g($x): $x;")
        self.assertIsNot(SyntaxError, type(program))
        self.assertEqual(
            '(module {"a":1,"b":[1,"x",null,true,{"c":false}],"if":{}}) (import "m" m {"search":"./"}) (import "d" $d) '
            '(include "i") (include "j" {"k":[]}) (def f 1) (def g($x) $x)', print_program(program))  # type: ignore[arg-type]
        self.assertIsNone(program.body)  # type: ignore[union-attr]
        self.assertEqual(S.Position(2, 1), program.imports[0].position)  # type: ignore[union-attr]

    def test_a_program_with_directives_has_a_body(self) -> None:
        self.assertEqual('(module {}) (import "a" foo::bar) (include "b") (def f 1) f',
                         parse('module {}; import "a" as foo::bar; include "b"; def f: 1; f'))

    def test_directive_errors(self) -> None:
        self.check([
            ("module 1; 1", "Module metadata must be an object @1:8"), ("module [1]; 1", "Module metadata must be an object @1:8"),
            ("module .a; 1", "Module metadata must be constant @1:8"), ("module {a: 1+1}; 1", "Module metadata must be constant @1:8"),
            ('module {a: "x\\(1)"}; 1', "Module metadata must be constant @1:8"), ("module {(1): 1}; 1", "Module metadata must be constant @1:8"),
            ('import "a" as $d {x: [1, -2]}; 1', "Module metadata must be constant @1:18"),
            ('include "a" {a: -1}; 1', "Module metadata must be constant @1:13"),
            ('include "a" {a: 1 2}; 1', "Module metadata must be constant @1:13"),
            ('import "a\\(1)" as foo; 1', "Import path must be constant @1:8"),
            ("import a as foo; 1", "Expected a module path string but found 'a' @1:8"),
            ('import "a" as if; 1', "Expected a module name or a $name but found 'if' @1:15"),
            ('import "a"; 1', "Expected 'as' but found ';' @1:11"), ('import "a" as x 1', "Expected ';' but found '1' @1:17"),
            ('def f: 1; import "a" as foo; 1', "Unexpected 'import' @1:11"), ('1 | import "a" as foo; 1', "Unexpected 'import' @1:5"),
            ('module {"a": 1}; module {}; 1', "Unexpected 'module' @1:18"), ('import "a" as x; module {}; 1', "Unexpected 'module' @1:18"),
        ], error)

    def test_module_errors(self) -> None:
        for source, expected in [("def f: 1; f", "A module contains only definitions @1:11"), ("1", "A module contains only definitions @1:1"),
                                 ("def f: 1", "Expected ';' but found end of program @1:9")]:
            result = Parser.try_parse_module(source)
            self.assertIs(SyntaxError, type(result))
            self.assertEqual(expected, f"{result.message} @{result.line}:{result.column}")  # type: ignore[union-attr]

    def test_an_empty_module_has_no_definitions(self) -> None:
        program = Parser.try_parse_module("# nothing")
        self.assertEqual([], program.definitions)  # type: ignore[union-attr]
        self.assertIsNone(program.body)  # type: ignore[union-attr]

    def test_nodes_carry_the_position_of_their_token(self) -> None:
        program = Parser.try_parse("1 +\n  .a[0] |\n\tf(2)")
        pipe: Any = program.body  # type: ignore[union-attr]
        self.assertEqual(S.NodeKind.PIPE, pipe.kind)
        self.assertEqual(S.Position(2, 9), pipe.position)
        add = pipe.left
        self.assertEqual(S.Position(1, 3), add.position)
        self.assertEqual(S.Position(1, 1), add.left.position)
        index = add.right
        self.assertEqual(S.Position(2, 5), index.position)
        self.assertEqual(S.Position(2, 3), index.target.position)
        call = pipe.right
        self.assertEqual(S.Position(3, 2), call.position)
        self.assertEqual(S.Position(3, 4), call.arguments[0].position)

    def test_syntax_errors(self) -> None:
        self.check([
            ("1 )", "Unexpected ')' @1:3"), ("[1, 2", "Expected ']' but found end of program @1:6"),
            ("(1", "Expected ')' but found end of program @1:3"), ("f(1;", "Unexpected end of program @1:5"), ("|", "Unexpected '|' @1:1"),
            ("1 |", "Unexpected end of program @1:4"), ("1,\n\n  ,", "Unexpected ',' @3:3"), ('"abc', "Unterminated string @1:1"),
            ('1 + "a\\qb"', "Invalid escape @1:7"), ("[invalid!!!", "Invalid character '!' @1:9"), ("then", "Unexpected 'then' @1:1"),
            ("1 as", "Expected a pattern ($name, [...] or {...}) but found end of program @1:5"),
            ("reduce . as $x (0)", "Expected ';' but found ')' @1:18"),
        ], error)


LIMIT = MAX_NESTING


def parse_on_small_stack(source: str) -> object:
    """Parses on a thread with a 1 MB C stack (the default of a Windows main thread)."""
    return on_small_stack(unittest.TestCase(), lambda: Parser.try_parse(source))


def parses(source: str) -> S.Program:
    program = parse_on_small_stack(source)
    if type(program) is SyntaxError:
        raise AssertionError(f"{program.message} at {program.line}:{program.column}")  # type: ignore[attr-defined]
    return program  # type: ignore[return-value]


def fails(source: str) -> str:
    result = parse_on_small_stack(source)
    assert type(result) is SyntaxError, source[:80]
    return f"{result.message} @{result.line}:{result.column}"  # type: ignore[attr-defined]


_NESTINGS = [
    # name, opening repeated, closing repeated (the innermost term is "1"), offset in the opening of
    # the token that opens the level
    ("parentheses", "(", ")", 0), ("arrays", "[", "]", 0), ("objects", "{a:", "}", 0), ("indexes", ".[", "]", 1),
    ("calls", "f(", ")", 1), ("interpolations", '"\\(', ')"', 0), ("if", "if . then ", " end", 0), ("try", "try ", "", 0),
    ("reduce", "reduce . as $x (", ";.)", 0), ("foreach", "foreach . as $x (", ";.)", 0), ("definitions", "def f: ", "; f", 0),
    ("object keys", "{(", "):1}", 0),
]


def _levels(open_: str) -> int:
    # "{(" opens two levels (the object and the parentheses).
    return LIMIT // 2 if open_ == "{(" else LIMIT


def _innermost(open_: str) -> str:
    # A parenthesized key must not be a number.
    return '"a"' if open_ == "{(" else "1"


class ParserLimitTests(unittest.TestCase):
    """The nesting limit of spec §11, and programs that must not exhaust the host stack."""

    def test_nesting_up_to_the_limit_parses(self) -> None:
        for name, open_, close, _ in _NESTINGS:
            with self.subTest(name=name):
                count = _levels(open_)
                parses(open_ * count + _innermost(open_) + close * count)

    def test_nesting_beyond_the_limit_fails_at_the_opening_token(self) -> None:
        for name, open_, close, offset in _NESTINGS:
            with self.subTest(name=name):
                count = _levels(open_) + 1
                source = open_ * count + _innermost(open_) + close * count
                column = (count - 1) * len(open_) + offset + 1
                self.assertEqual(f"Program too deeply nested @1:{column}", fails(source))

    def test_nested_patterns_count_too(self) -> None:
        parses(". as " + "[" * LIMIT + "$x" + "]" * LIMIT + " | $x")
        self.assertEqual(f"Program too deeply nested @1:{LIMIT + 6}", fails(". as " + "[" * (LIMIT + 1) + "$x" + "]" * (LIMIT + 1) + " | $x"))

    def test_nested_metadata_counts_too(self) -> None:
        self.assertEqual(f"Program too deeply nested @1:{LIMIT + 11}", fails("module {a: " + "[" * LIMIT + "]" * LIMIT + "}; 1"))
        parses("module {a: " + "[" * (LIMIT - 1) + "]" * (LIMIT - 1) + "}; 1")

    def test_mixed_nesting_counts_every_kind(self) -> None:
        self.assertEqual(f"Program too deeply nested @1:{LIMIT // 2 * 2 + 1}", fails("([" * (LIMIT // 2 + 1) + "1" + "])" * (LIMIT // 2 + 1)))

    def test_long_sequences_do_not_recurse(self) -> None:
        cases = [("", "1 + ", "1"), ("", "1 * ", "1"), ("", "1 // ", "1"), ("", "1 and ", "1"), ("", "1 == 1 or ", "1"), ("", "1 | ", "1"),
                 ("", "1, ", "1"), ("", "1 as $x | ", "$x"), ("", "label $x | ", "1"), ("", "def f: 1; ", "f"), ("(", "def f: 1; ", "f)"),
                 ("[1, ", "def f: 1; ", "f]"), ("", "-", "1"), ("", "1 + -", "1"), ("", ".a", ""), ("", ".[0]", ""), ("", ".a?", ""),
                 (". as $a", " ?// $a", " | 1"), ("{a: ", "1 | ", "1}"), ("{", "a: 1, ", "}"), ('"', "\\(1)", '"'), ("f(", "1; ", "1)")]
        for prefix, element, suffix in cases:
            with self.subTest(element=element):
                parses(prefix + element * 10_000 + suffix)

    def test_long_elif_chains_do_not_recurse(self) -> None:
        parses("if . then 1 " + "elif . then 1 " * 10_000 + "else 2 end")

    def test_a_long_chain_keeps_its_associativity(self) -> None:
        node = parses("1 - " * 10_000 + "1").body
        depth = 0
        while node.kind == S.NodeKind.BINARY:  # type: ignore[union-attr]
            self.assertEqual(S.NodeKind.LITERAL, node.right.kind)  # type: ignore[union-attr]
            node = node.left  # type: ignore[union-attr]
            depth += 1
        self.assertEqual(10_000, depth)
        node = parses("1 // " * 10_000 + "1").body
        depth = 0
        while node.kind == S.NodeKind.ALTERNATIVE:  # type: ignore[union-attr]
            self.assertEqual(S.NodeKind.LITERAL, node.left.kind)  # type: ignore[union-attr]
            node = node.right  # type: ignore[union-attr]
            depth += 1
        self.assertEqual(10_000, depth)

    def test_a_long_pipe_of_bindings_nests_to_the_right(self) -> None:
        node = parses("1 as $x | " * 10_000 + "$x").body
        depth = 0
        while node.kind == S.NodeKind.BIND:  # type: ignore[union-attr]
            node = node.body  # type: ignore[union-attr]
            depth += 1
        self.assertEqual(10_000, depth)
        self.assertEqual(S.NodeKind.VARIABLE, node.kind)  # type: ignore[union-attr]

    def test_many_top_level_definitions_are_a_list(self) -> None:
        program = parses("def f: 1; " * 10_000 + "f")
        self.assertEqual(10_000, len(program.definitions))
        self.assertEqual(S.NodeKind.CALL, program.body.kind)  # type: ignore[union-attr]


# %%FAIL cases whose program is syntactically valid: they fail when names are bound.
_BINDING_FAILURES = {". as $foo | break $foo", "input", "[inputs]"}


class CorpusParseTests(unittest.TestCase):
    """Every program of spec/tests/*.test parses, except the %%FAIL cases that fail for a syntax
    reason; spec/prelude.jq parses as a module."""

    def test_every_program_of_the_corpus_parses(self) -> None:
        failures = []
        for case in corpus.all_cases():
            result = Parser.try_parse(case.program)
            parsed = type(result) is not SyntaxError
            if case.should_fail and case.program not in _BINDING_FAILURES and case.file != "modules.test":
                if parsed:
                    failures.append(f"{case.name}: '{case.program}' parses but is a %%FAIL case")
            elif not parsed:
                failures.append(f"{case.name}: '{case.program}': {result.message}")  # type: ignore[union-attr]
        self.assertEqual([], failures)

    def test_the_binding_failures_are_in_the_corpus(self) -> None:
        programs = {case.program for case in corpus.all_cases() if case.should_fail}
        self.assertLessEqual(_BINDING_FAILURES, programs)

    def test_the_prelude_parses_as_a_module(self) -> None:
        with open(os.path.join(corpus.ROOT, "spec", "prelude.jq"), encoding="utf-8") as f:
            source = f.read()
        program = Parser.try_parse_module(source)
        self.assertIsNot(SyntaxError, type(program))
        self.assertIsNone(program.body)  # type: ignore[union-attr]
        self.assertEqual(102, len(program.definitions))  # type: ignore[union-attr]
        self.assertTrue(any(d.name == "_modify" and len(d.params) == 2 for d in program.definitions))  # type: ignore[union-attr]
        program = Parser.try_parse(source)
        self.assertEqual(S.NodeKind.IDENTITY, program.body.kind)  # type: ignore[union-attr]
