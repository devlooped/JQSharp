//! The syntax layer: what `dotnet/Tests/Core/Syntax` covers. Tokens and positions; the precedence
//! table and examples of spec §3.1 printed as trees; the nesting limit and long sequences on a
//! 1 MB stack; the corpus and the prelude as parser tests.

mod support;

use jqlite::syntax::ast::{NodeKind, Position};
use jqlite::syntax::lexer::Lexer;
use jqlite::syntax::parser::{MAX_NESTING, Parser};
use jqlite::syntax::token::{self, Token, TokenKind};

use support::ast_printer::{parse, parse_error as error, print_program};
use support::on_thread;

const MEGABYTE: usize = 1024 * 1024;

// ---- LexerTests ----

fn lex(source: &str) -> Vec<Token> {
    Lexer::tokenize(source)
}

fn tokens(source: &str) -> Vec<Token> {
    lex(source).into_iter().filter(|token| token.kind != TokenKind::Eof).collect()
}

// Kind, text and position of each token, without the final Eof.
fn describe(source: &str) -> String {
    tokens(source).iter().map(|t| format!("{:?}:{}@{}:{}", t.kind, t.text, t.line, t.column)).collect::<Vec<_>>().join(" ")
}

fn kinds(source: &str) -> String {
    tokens(source).iter().map(|t| format!("{:?}", t.kind)).collect::<Vec<_>>().join(" ")
}

fn texts(source: &str) -> String {
    tokens(source).iter().map(|t| t.text.clone()).collect::<Vec<_>>().join(" ")
}

fn lex_error(source: &str) -> String {
    let all = lex(source);
    let last = &all[all.len() - 1];
    assert_eq!(last.kind, TokenKind::Error, "{source}");
    format!("{} @{}:{}", last.text, last.line, last.column)
}

fn single(source: &str) -> Token {
    let mut all = tokens(source);
    assert_eq!(all.len(), 1, "{source}");
    all.remove(0)
}

#[test]
fn every_punctuation_token_longest_first() {
    let source = ". .. [ ] { } ( ) | , : ; ? ?// = == != < <= > >= + - * / % += -= *= /= %= // //= |=";
    assert_eq!(
        kinds(source),
        "Dot DotDot LeftBracket RightBracket LeftBrace RightBrace LeftParen RightParen Pipe Comma Colon Semicolon Question QuestionAlt \
         Assign Equal NotEqual Less LessEqual Greater GreaterEqual Plus Minus Star Slash Percent \
         PlusAssign MinusAssign StarAssign SlashAssign PercentAssign Alt AltAssign PipeAssign"
    );
    assert_eq!(texts(source), source);
}

#[test]
fn adjacent_punctuation_takes_the_longest_match() {
    assert_eq!(kinds("?//="), "QuestionAlt Assign");
    assert_eq!(kinds("..."), "DotDot Dot");
    assert_eq!(kinds("///"), "Alt Slash");
    assert_eq!(kinds("|=="), "PipeAssign Assign");
}

#[test]
fn every_keyword() {
    for (word, kind) in token::KEYWORDS {
        let token = single(word);
        assert_eq!(token.kind, kind);
        assert_eq!(token.text, word);
        assert!(token::is_keyword(token.kind));
    }
    assert_eq!(token::KEYWORDS.len(), 18);
}

#[test]
fn names() {
    assert_eq!(describe("foo a::b _x1 null not"), "Ident:foo@1:1 Ident:a::b@1:5 Ident:_x1@1:10 Ident:null@1:14 Ident:not@1:19");
    assert_eq!(describe(".a.end.e0._"), "Field:a@1:1 Field:end@1:3 Field:e0@1:7 Field:_@1:10");
    assert_eq!(describe("$x $then $a::b $__loc__"), "Variable:x@1:1 Variable:then@1:4 Variable:a::b@1:10 Variable:__loc__@1:16");
    assert_eq!(describe("@base64 @base64d @1x"), "Format:base64@1:1 Format:base64d@1:9 Format:1x@1:18");
}

#[test]
fn a_qualified_name_may_have_several_double_colons() {
    assert_eq!(kinds("a::b::c"), "Ident");
    assert_eq!(texts("a::b::c"), "a::b::c");
    assert_eq!(kinds("$a::b::c"), "Variable");
    assert_eq!(texts("a::"), "a : :");
    assert_eq!(kinds(".a::b"), "Field Colon Colon Ident");
}

#[test]
fn numbers() {
    let cases = [
        ("0", 0.0),
        ("42", 42.0),
        (".5", 0.5),
        ("1.", 1.0),
        ("1.5", 1.5),
        ("1e3", 1000.0),
        ("1E+3", 1000.0),
        ("1.e3", 1000.0),
        (".5e1", 5.0),
        ("25e-1", 2.5),
        ("0.1", 0.1),
        ("1e1000", f64::INFINITY),
        ("1e-1000", 0.0),
        ("100000000000000000000000000001", 1e29),
    ];
    for (source, value) in cases {
        let token = single(source);
        assert_eq!(token.kind, TokenKind::Number);
        assert_eq!(token.number, value, "{source}");
        assert_eq!(token.text, source);
    }
}

#[test]
fn number_boundaries() {
    assert_eq!(kinds(".e0"), "Field");
    assert_eq!(kinds("1e"), "Number Ident");
    assert_eq!(kinds("1e+"), "Number Ident Plus");
    assert_eq!(texts("1.5.3"), "1.5 .3");
    assert_eq!(kinds("..5"), "DotDot Number");
    assert_eq!(kinds("1x"), "Number Ident");
}

#[test]
fn positions_across_line_breaks_and_non_bmp_characters() {
    // "😀😀" is 4 code points; columns count code points.
    assert_eq!(describe("\"😀😀\" +\n  1\r\n+ .a"), "String:😀😀@1:1 Plus:+@1:6 Number:1@2:3 Plus:+@3:1 Field:a@3:3");
    assert_eq!(describe("\"a\nb\" x"), "String:a\nb@1:1 Ident:x@2:4");
}

#[test]
fn a_lone_cr_is_white_space_not_a_line_break() {
    assert_eq!(describe("1\r+\r2"), "Number:1@1:1 Plus:+@1:3 Number:2@1:5");
}

#[test]
fn comments() {
    assert_eq!(describe("1 # a comment\n+ 2 # another"), "Number:1@1:1 Plus:+@2:1 Number:2@2:3");
    // An odd number of backslashes at the end of the line continues the comment.
    assert_eq!(texts("1 # c \\\n+ 1\n+ 10"), "1 + 10");
    assert_eq!(texts("1 # c \\\\\n+ 1\n+ 10"), "1 + 1 + 10");
    assert_eq!(texts("1 # c \\\\\\\n+ 1\n+ 10"), "1 + 10");
    assert_eq!(texts("1 # c \\\r\n+ 1\r\n+ 10"), "1 + 10");
    assert_eq!(texts("1 # c \\ x\n+ 1\n+ 10"), "1 + 1 + 10");
    // As in jq, a lone CR does not end a comment.
    assert_eq!(texts("1 # c\r+ 1"), "1");
    assert_eq!(texts("1 # c \\\r+ 1\n+ 10"), "1 + 10");
}

#[test]
fn string_escapes() {
    let cases = [
        ("\"abc\"", "abc"),
        ("\"\\\"\\\\\\/\\b\\f\\n\\r\\t\"", "\"\\/\u{8}\u{c}\n\r\t"),
        ("\"\\u00e9\\u00C9\"", "éÉ"),
        ("\"\\uD83D\\uDE00\"", "😀"),
        ("\"\\ud83d x\"", "\u{FFFD} x"),
        ("\"\\ude00\"", "\u{FFFD}"),
        ("\"\\uDE00\\uD83D\"", "\u{FFFD}\u{FFFD}"),
        ("\"\\uD83D\\u0041\"", "\u{FFFD}A"),
        ("\"\\uD83D\\uD83D\\uDE00\"", "\u{FFFD}😀"),
        ("\"\\u12345\"", "\u{1234}5"),
        ("\"é\t\"", "é\t"),
    ];
    for (source, text) in cases {
        let token = single(source);
        assert_eq!(token.kind, TokenKind::String, "{source}");
        assert_eq!(token.text, text, "{source}");
    }
}

// The reference also checks a lone surrogate in the source text itself, which a Rust string
// cannot hold: the source is well-formed before the lexer sees it.

#[test]
fn interpolation_is_split_into_pieces() {
    assert_eq!(
        describe("\"a\\(1 + (2))b\\(\"c\")d\""),
        "StringStart:a@1:1 Number:1@1:5 Plus:+@1:7 LeftParen:(@1:9 Number:2@1:10 RightParen:)@1:11 StringMiddle:b@1:12 String:c@1:16 StringEnd:d@1:19"
    );
}

#[test]
fn interpolation_nests() {
    assert_eq!(kinds("\"a\\(\"b\\(\"c\")\")\""), "StringStart StringStart String StringEnd StringEnd");
    assert_eq!(texts("\"a\\(\"b\\(\"c\")\")\"").trim(), "a b c");
}

#[test]
fn a_parenthesis_outside_interpolation_is_punctuation() {
    assert_eq!(kinds(")"), "RightParen");
}

#[test]
fn lexical_errors() {
    let cases = [
        ("\"abc", "Unterminated string @1:1"),
        ("1 + \"a\\(1", "Unterminated string @1:5"),
        ("\"a\\(\"b\" + (1", "Unterminated string @1:1"),
        ("\"a\\", "Unterminated string @1:1"),
        ("\"a\\qb\"", "Invalid escape @1:3"),
        ("\"x\"\n \"\\u12\"", "Invalid escape @2:3"),
        ("\"\\u12G4\"", "Invalid escape @1:2"),
        ("\"\\u\"", "Invalid escape @1:2"),
        ("1 $ x", "Expected a variable name after '$' @1:3"),
        ("@", "Expected a format name after '@' @1:1"),
        ("{aé: 1}", "Invalid character 'é' @1:3"),
        ("😀", "Invalid character '😀' @1:1"),
        ("1\u{c}+2", "Invalid character U+000C @1:2"),
        ("1 ! 2", "Invalid character '!' @1:3"),
    ];
    for (source, expected) in cases {
        assert_eq!(lex_error(source), expected, "{source}");
    }
}

#[test]
fn an_error_is_the_last_token() {
    let all: Vec<String> = lex("1 + \"a\\q\" + 2").iter().map(|t| format!("{:?}", t.kind)).collect();
    assert_eq!(all.join(" "), "Number Plus Error");
}

#[test]
fn empty_source_is_eof() {
    let all = lex("  # nothing\n");
    assert_eq!(all.len(), 1);
    assert_eq!(all[0].kind, TokenKind::Eof);
    assert_eq!((all[0].line, all[0].column), (2, 1));
}

// ---- ParserTests ----

fn check_parse(cases: &[(&str, &str)]) {
    for (source, expected) in cases {
        assert_eq!(parse(source), *expected, "{source}");
    }
}

fn check_error(cases: &[(&str, &str)]) {
    for (source, expected) in cases {
        assert_eq!(error(source), *expected, "{source}");
    }
}

// Every row of the examples table of spec §3.1.
#[test]
fn precedence_examples_of_the_spec() {
    check_parse(&[
        ("1, 2 as $x | $x * 10", "(, 1 (as 2 $x (* $x 10)))"),
        ("1 + 2 as $x | -$x", "(as (+ 1 2) $x (neg $x))"),
        ("[-1 as $x | 1, $x]", "(array (as (neg 1) $x (, 1 $x)))"),
        ("1 // 2 == 2", "(// 1 (== 2 2))"),
        ("{} | .a = .b // 1", "(| (object) (// (_assign (index . \"a\") (index . \"b\")) 1))"),
        ("[1 // 2 as $x | 10]", "(array (as (// 1 2) $x 10))"),
        ("try (2 * -3 / \"a\") catch .", "(try (/ (* 2 (neg 3)) \"a\") .)"),
        ("1 + try 2 catch 3 + 4", "(+ (+ 1 (try 2 3)) 4)"),
        ("try -.? catch .", "(try (neg (try .)) .)"),
        ("def f: 1; f | f, 2", "(def f 1) (| f (, f 2))"),
    ]);
}

#[test]
fn precedence_examples_that_fail() {
    check_error(&[("reduce 1, 2 as $x (0; . + $x)", "Expected 'as' but found ',' @1:9"), ("try 1 * 2 catch 3", "Unexpected 'catch' @1:11")]);
}

// Each level of the table of spec §3.1, with its associativity and its neighbours.
#[test]
fn precedence_and_associativity() {
    check_parse(&[
        ("1 | 2 | 3", "(| 1 (| 2 3))"),
        ("1, 2, 3", "(, (, 1 2) 3)"),
        ("1, 2 | 3, 4", "(| (, 1 2) (, 3 4))"),
        ("1 // 2 // 3", "(// 1 (// 2 3))"),
        ("1 // 2, 3", "(, (// 1 2) 3)"),
        (".a = 1 // 2", "(// (_assign (index . \"a\") 1) 2)"),
        (".a |= . + 1", "(_modify (index . \"a\") (+ . 1))"),
        (".a = 1 or 2", "(_assign (index . \"a\") (or 1 2))"),
        ("1 or 2 or 3", "(or (or 1 2) 3)"),
        ("1 or 2 and 3", "(or 1 (and 2 3))"),
        ("1 and 2 or 3", "(or (and 1 2) 3)"),
        ("1 and 2 and 3", "(and (and 1 2) 3)"),
        ("1 == 2 and 3 != 4", "(and (== 1 2) (!= 3 4))"),
        ("1 + 2 < 3 - 4", "(< (+ 1 2) (- 3 4))"),
        ("1 <= 2", "(<= 1 2)"),
        ("1 > 2", "(> 1 2)"),
        ("1 >= 2", "(>= 1 2)"),
        ("1 - 2 - 3", "(- (- 1 2) 3)"),
        ("1 - 2 + 3", "(+ (- 1 2) 3)"),
        ("1 + 2 * 3", "(+ 1 (* 2 3))"),
        ("1 * 2 + 3", "(+ (* 1 2) 3)"),
        ("1 * 2 / 3 % 4", "(% (/ (* 1 2) 3) 4)"),
        ("-1 * 2", "(* (neg 1) 2)"),
        ("2 * -3 * 4", "(* (* 2 (neg 3)) 4)"),
        ("1 - -1", "(- 1 (neg 1))"),
        ("- - 1", "(neg (neg 1))"),
        ("-.a[0]?", "(neg (try (index (index . \"a\") 0)))"),
        ("-1 as $x | $x", "(as (neg 1) $x $x)"),
        ("1 = 2 as $x | $x", "(as (_assign 1 2) $x $x)"),
        ("1 or 2 as $x | $x", "(as (or 1 2) $x $x)"),
    ]);
}

#[test]
fn assignments_are_rewritten() {
    check_parse(&[
        (".a += 1", "(as 1 $*update (_modify (index . \"a\") (+ . $*update)))"),
        (".a -= 1", "(as 1 $*update (_modify (index . \"a\") (- . $*update)))"),
        (".a *= 1", "(as 1 $*update (_modify (index . \"a\") (* . $*update)))"),
        (".a /= 1", "(as 1 $*update (_modify (index . \"a\") (/ . $*update)))"),
        (".a %= 1", "(as 1 $*update (_modify (index . \"a\") (% . $*update)))"),
        (".a //= 1", "(as 1 $*update (_modify (index . \"a\") (// . $*update)))"),
        (".a = (1, 2)", "(_assign (index . \"a\") (, 1 2))"),
    ]);
}

#[test]
fn non_associative_operators_do_not_chain() {
    check_error(&[
        ("1 == 2 == 3", "Operator '==' cannot be chained: add parentheses @1:8"),
        ("1 == 2 < 3", "Operator '<' cannot be chained: add parentheses @1:8"),
        ("1 < 2 < 3", "Operator '<' cannot be chained: add parentheses @1:7"),
        (".a = .b = 1", "Operator '=' cannot be chained: add parentheses @1:9"),
        (".a = 1 |= 2", "Operator '|=' cannot be chained: add parentheses @1:8"),
    ]);
}

#[test]
fn try_operands_are_unary() {
    check_parse(&[
        ("try 1", "(try 1)"),
        ("try error(\"x\") catch . | ascii_upcase", "(| (try (error \"x\") .) ascii_upcase)"),
        ("[try error(\"x\") + 1]", "(array (+ (try (error \"x\")) 1))"),
        ("try 1 catch .[0]", "(try 1 (index . 0))"),
        ("try -1", "(try (neg 1))"),
        ("try try 1 catch 2 catch 3", "(try (try 1 2) 3)"),
        ("-try 1 catch 2", "(neg (try 1 2))"),
    ]);
}

#[test]
fn try_operand_errors() {
    check_error(&[("[try -\"a\" * 0 catch \"c\"]", "Expected ']' but found 'catch' @1:15"), ("try def f: 1; f", "Unexpected 'def' @1:5")]);
}

#[test]
fn binding_sources() {
    check_parse(&[
        ("1 as $x | 2 as $y | [$x, $y]", "(as 1 $x (as 2 $y (array (, $x $y))))"),
        ("1 as $x | 2 | 3", "(as 1 $x (| 2 3))"),
        ("(1 as $x | $x), 2", "(, (as 1 $x $x) 2)"),
        ("1, 2, 3 as $x | 4, 5 | 6", "(, (, 1 2) (as 3 $x (| (, 4 5) 6)))"),
        ("1 | 2 as $x | 3", "(| 1 (as 2 $x 3))"),
        ("reduce .[] as $x (0; . + $x)", "(reduce (iter .) $x 0 (+ . $x))"),
        ("reduce .[] / .[] as $x (0; . + $x)", "(reduce (/ (iter .) (iter .)) $x 0 (+ . $x))"),
        ("reduce 1 // 2 as $x (0; $x)", "(reduce (// 1 2) $x 0 $x)"),
        ("reduce -1 as $x (0; $x)", "(reduce (neg 1) $x 0 $x)"),
        ("reduce (1,2) as $x (0; .+$x) | . * 2", "(| (reduce (, 1 2) $x 0 (+ . $x)) (* . 2))"),
        ("foreach (1,2) as $x (0; . + $x)", "(foreach (, 1 2) $x 0 (+ . $x) .)"),
        ("foreach 1 + 2 as $x (0; $x; [$x, .])", "(foreach (+ 1 2) $x 0 $x (array (, $x .)))"),
        ("[foreach .[] as [$a] ?// $a (0; 1)]", "(array (foreach (iter .) (?// [$a] $a) 0 1 .))"),
    ]);
}

#[test]
fn definitions_and_labels_consume_the_rest_of_the_pipe() {
    check_parse(&[
        ("def f: 1; def g: 2; f", "(def f 1) (def g 2) f"),
        ("def f: 1;", "(def f 1) ."),
        ("", "."),
        ("1, def f: 2; f, 3", "(, 1 (def f 2 (, f 3)))"),
        ("[1, 2 | def f: 3; f, 4]", "(array (| (, 1 2) (def f 3 (, f 4))))"),
        ("(def f: 1; f | f), 2", "(, (def f 1 (| f f)) 2)"),
        ("if def f: 1; f then 2 else 3 end", "(if (def f 1 f) 2 3)"),
        ("def f: def g: 3; g; f", "(def f (def g 3 g)) f"),
        ("label $out | 1, 2 | 3", "(label $out (| (, 1 2) 3))"),
        ("[1, label $f | 2, 3]", "(array (, 1 (label $f (, 2 3))))"),
        ("label $f | break $f", "(label $f (break $f))"),
        ("label $if | 1", "(label $if 1)"),
        ("label $a::b | 1", "(label $a::b 1)"),
    ]);
}

#[test]
fn definitions_and_labels_are_not_operands() {
    check_error(&[
        ("1 + def f: 1; f", "Unexpected 'def' @1:5"),
        ("1 // def f: 1; f", "Unexpected 'def' @1:6"),
        ("-def f: 1; f", "Unexpected 'def' @1:2"),
        ("1 + label $f | 1", "Unexpected 'label' @1:5"),
        ("label f | 1", "Expected a label name ($name) but found 'f' @1:7"),
        ("break f", "Expected a label name ($name) but found 'f' @1:7"),
    ]);
}

#[test]
fn definitions_and_calls() {
    check_parse(&[
        ("def f(a; $b): [a, $b, b]; f(1; 2)", "(def f(a;$b) (array (, (, a $b) b))) (f 1 2)"),
        ("def f($a; $b): $a + $b; 1", "(def f($a;$b) (+ $a $b)) 1"),
        ("def f(x): x; f(.)", "(def f(x) x) (f .)"),
        ("def true: 1; true", "(def true 1) true"),
        ("def f::g: 1; f::g", "(def f::g 1) f::g"),
        ("def f(a::b): a::b; f(1)", "(def f(a::b) a::b) (f 1)"),
        ("f(1; 2, 3; 4 | 5)", "(f 1 (, 2 3) (| 4 5))"),
        ("a::b", "a::b"),
    ]);
}

#[test]
fn definition_errors() {
    check_error(&[
        ("def if: 1; 1", "Expected a function name but found 'if' @1:5"),
        ("def f(): 1; f", "Expected a parameter name but found ')' @1:7"),
        ("def f(a;): 1; 1", "Expected a parameter name but found ')' @1:9"),
        ("def f(if): 1; 1", "Expected a parameter name but found 'if' @1:7"),
        ("def f: 1", "Expected ';' but found end of program @1:9"),
        ("def f 1; 1", "Expected ':' but found '1' @1:7"),
    ]);
}

#[test]
fn paths_and_postfix() {
    check_parse(&[
        (".", "."),
        ("..", "recurse"),
        ("..?", "(try recurse)"),
        ("..|.a?", "(| recurse (try (index . \"a\")))"),
        (".. .a?", "(try (index recurse \"a\"))"),
        ("..[0]?", "(try (index recurse 0))"),
        (".a", "(index . \"a\")"),
        (".a.b.\"c\"[0]", "(index (index (index (index . \"a\") \"b\") \"c\") 0)"),
        (".a .b", "(index (index . \"a\") \"b\")"),
        (".\"a\"", "(index . \"a\")"),
        (". \"a\"", "(index . \"a\")"),
        (".\"a\".\"b\"", "(index (index . \"a\") \"b\")"),
        (".\"a\\(1)\"", "(index . (str \"a\" 1 \"\"))"),
        (".a.[0]", "(index (index . \"a\") 0)"),
        (".[0]", "(index . 0)"),
        (". [0]", "(index . 0)"),
        (".[1 + 1]", "(index . (+ 1 1))"),
        (".[\"a\" + \"b\"]", "(index . (+ \"a\" \"b\"))"),
        (".a[.k]", "(index (index . \"a\") (index . \"k\"))"),
        (".[0, 1]", "(index . (, 0 1))"),
        (".[]", "(iter .)"),
        (".[]?", "(try (iter .))"),
        (".a[]", "(iter (index . \"a\"))"),
        (".[1:2]", "(slice . 1 2)"),
        (".[1:]", "(slice . 1 _)"),
        (".[:2]", "(slice . _ 2)"),
        (".[1:$n]", "(slice . 1 $n)"),
        (".[1:][0]", "(index (slice . 1 _) 0)"),
        (".a?", "(try (index . \"a\"))"),
        (".a??", "(try (try (index . \"a\")))"),
        (".a?.b?", "(try (index (try (index . \"a\")) \"b\"))"),
        (".a[]?.b", "(index (try (iter (index . \"a\"))) \"b\")"),
        (".a? // 1", "(// (try (index . \"a\")) 1)"),
        ("$x[0]", "(index $x 0)"),
        ("$x.a", "(index $x \"a\")"),
        ("\"abc\"[1:]", "(slice \"abc\" 1 _)"),
        ("1[0]?", "(try (index 1 0))"),
        ("f.a", "(index f \"a\")"),
        ("if true then [1] else [2] end[0]", "(index (if true (array 1) (array 2)) 0)"),
        ("{\"end\":4} | .end", "(| (object (\"end\" 4)) (index . \"end\"))"),
    ]);
}

#[test]
fn path_errors() {
    check_error(&[
        (".[:]", "Unexpected ']' @1:4"),
        (".[1:2:3]", "Expected ']' but found ':' @1:6"),
        (".a?//1", "Unexpected '?//': write '? //' for an optional followed by an alternative @1:3"),
        ("[.[]?//1]", "Unexpected '?//': write '? //' for an optional followed by an alternative @1:5"),
        ("..a", "Unexpected 'a' @1:3"),
        ("1 . 2", "Unexpected '.' @1:3"),
    ]);
}

#[test]
fn terms() {
    check_parse(&[
        ("null", "null"),
        ("true", "true"),
        ("false", "false"),
        ("1.5e3", "1500"),
        ("\"a\"", "\"a\""),
        ("$__loc__", "$__loc__"),
        ("$ENV.PATH", "(index $ENV \"PATH\")"),
        ("[]", "(array)"),
        ("[1, 2]", "(array (, 1 2))"),
        ("[.[] | . * 2]", "(array (| (iter .) (* . 2)))"),
        ("(1)", "1"),
        ("((1, 2))", "(, 1 2)"),
        ("not", "not"),
        ("empty", "empty"),
    ]);
}

#[test]
fn strings_and_formats() {
    check_parse(&[
        ("\"a\\(1)b\\(2)c\"", "(str \"a\" 1 \"b\" 2 \"c\")"),
        ("\"\\(1, 2)\"", "(str \"\" (, 1 2) \"\")"),
        ("\"a\\(\"b\\(\"c\")\")\"", "(str \"a\" (str \"b\" \"c\" \"\") \"\")"),
        ("\"\\(1 + \"\\(2)\")\"", "(str \"\" (+ 1 (str \"\" 2 \"\")) \"\")"),
        ("\"\\((1))\"", "(str \"\" 1 \"\")"),
        ("@base64 \"x\\(.)y\"", "(str @base64 \"x\" . \"y\")"),
        ("@json \"x\\(\"y\")\"", "(str @json \"x\" \"y\" \"\")"),
        ("@base64 \"abc\"", "\"abc\""),
        ("@base64", "@base64"),
        ("@foo", "@foo"),
        ("@foo \"x\"", "\"x\""),
        ("@text | @json", "(| @text @json)"),
    ]);
}

#[test]
fn string_errors() {
    check_error(&[("\"\\()\"", "Unexpected ')' @1:4"), ("\"\\(1;2)\"", "Expected ')' but found ';' @1:5"), ("\"a\\(1", "Unterminated string @1:1")]);
}

#[test]
fn conditionals() {
    check_parse(&[
        ("if 1 then 2 else 3 end", "(if 1 2 3)"),
        ("if 1 then 2 end", "(if 1 2 .)"),
        ("if 1 then 2 elif 3 then 4 end", "(if 1 2 (if 3 4 .))"),
        ("if 1 then 2 elif 3 then 4 else 5 end", "(if 1 2 (if 3 4 5))"),
        ("if 1, 2 then 3 | 4 else 5, 6 end", "(if (, 1 2) (| 3 4) (, 5 6))"),
        ("[-if true then 1 else 2 end]", "(array (neg (if true 1 2)))"),
    ]);
}

#[test]
fn conditional_errors() {
    check_error(&[("if 1 then 2 else 3", "Expected 'end' but found end of program @1:19"), ("if 1 2 end", "Expected 'then' but found '2' @1:6")]);
}

// Every key form and shorthand of spec §7.11.
#[test]
fn object_construction() {
    check_parse(&[
        ("{}", "(object)"),
        ("{a: 1}", "(object (\"a\" 1))"),
        ("{if: 1, then: 2}", "(object (\"if\" 1) (\"then\" 2))"),
        ("{\"a b\": 1}", "(object (\"a b\" 1))"),
        ("{\"a\\(1)\": 2}", "(object ((str \"a\" 1 \"\") 2))"),
        ("{@base64 \"a\\(1)\": 2}", "(object ((str @base64 \"a\" 1 \"\") 2))"),
        ("{$k: 1}", "(object ($k 1))"),
        ("{(.a, .b): 1}", "(object ((, (index . \"a\") (index . \"b\")) 1))"),
        ("{(\"a\"): 1}", "(object (\"a\" 1))"),
        ("{a}", "(object (\"a\" _))"),
        ("{if}", "(object (\"if\" _))"),
        ("{null}", "(object (\"null\" _))"),
        ("{\"a\"}", "(object (\"a\" _))"),
        ("{\"x\\(1, 2)\"}", "(object ((str \"x\" (, 1 2) \"\") _))"),
        ("{@base64 \"a\"}", "(object (\"a\" _))"),
        ("{$x}", "(object (\"x\" $x))"),
        ("{$__loc__}", "(object (\"__loc__\" $__loc__))"),
        ("{a, $b, \"c\": 3}", "(object (\"a\" _) (\"b\" $b) (\"c\" 3))"),
        ("{a: 1,}", "(object (\"a\" 1))"),
        ("{a: 1, b: 2}", "(object (\"a\" 1) (\"b\" 2))"),
        ("{a: (1, 2)}", "(object (\"a\" (, 1 2)))"),
        ("{a: 1 | 2 | 3}", "(object (\"a\" (| 1 (| 2 3))))"),
        ("{a: 1, b: 2 | . + 1}", "(object (\"a\" 1) (\"b\" (| 2 (+ . 1))))"),
        ("{a: 1 + 2}", "(object (\"a\" (+ 1 2)))"),
        ("{a: -1}", "(object (\"a\" (neg 1)))"),
        ("{a: .x = 1}", "(object (\"a\" (_assign (index . \"x\") 1)))"),
        ("{a: 1 // 2}", "(object (\"a\" (// 1 2)))"),
        ("{a: reduce .[] as $x (0; 1)}", "(object (\"a\" (reduce (iter .) $x 0 1)))"),
        ("{a: if . then 1 end}", "(object (\"a\" (if . 1 .)))"),
        ("{a: try 1 catch 2}", "(object (\"a\" (try 1 2)))"),
        ("{a: .b?}", "(object (\"a\" (try (index . \"b\"))))"),
        ("{a: {b: {c: 1}}}", "(object (\"a\" (object (\"b\" (object (\"c\" 1))))))"),
        ("{a::b: 1}", "(object (\"a::b\" 1))"),
    ]);
}

#[test]
fn object_errors() {
    check_error(&[
        ("{1: 2}", "May need parentheses around object key expression @1:2"),
        ("{1+2: 3}", "May need parentheses around object key expression @1:2"),
        ("{-1: 2}", "May need parentheses around object key expression @1:2"),
        ("{.a: 2}", "May need parentheses around object key expression @1:2"),
        ("{(1): 2}", "Cannot use number (1) as object key @1:3"),
        ("{(true): 2}", "Cannot use boolean (true) as object key @1:3"),
        ("{(null): 2}", "Cannot use null (null) as object key @1:3"),
        ("{(\"a\")}", "Expected ':' but found '}' @1:7"),
        ("{,}", "Unexpected ',' @1:2"),
        ("{a:1,,}", "Unexpected ',' @1:6"),
        ("{a 1}", "Expected ',' or '}' but found '1' @1:4"),
        ("{a: 1, b}c", "Unexpected 'c' @1:10"),
        ("{a: 1 as $x | $x}", "Expected ',' or '}' but found 'as' @1:7"),
        ("{a: 1 | 2 as $x | $x}", "Expected ',' or '}' but found 'as' @1:11"),
        ("{a: def f: 1; f}", "Unexpected 'def' @1:5"),
        ("{a: label $f | 1}", "Unexpected 'label' @1:5"),
        ("{a: 1, 2}", "May need parentheses around object key expression @1:8"),
        ("[1, 2,]", "Unexpected ']' @1:7"),
    ]);
}

#[test]
fn patterns() {
    check_parse(&[
        (". as $x | $x", "(as . $x $x)"),
        (". as [$a, $b] | 1", "(as . [$a $b] 1)"),
        (". as [$a, [$b]] | 1", "(as . [$a [$b]] 1)"),
        (". as {$a} | 1", "(as . {(\"a\" $a _)} 1)"),
        (". as {$a: [$b]} | 1", "(as . {(\"a\" $a [$b])} 1)"),
        (". as {a: $x, \"b\": $y, if: $z} | 1", "(as . {(\"a\" _ $x) (\"b\" _ $y) (\"if\" _ $z)} 1)"),
        (". as {\"a\\(1)\": $x} | 1", "(as . {((str \"a\" 1 \"\") _ $x)} 1)"),
        (". as {@base64 \"a\": $x} | 1", "(as . {(\"a\" _ $x)} 1)"),
        (". as {(1 | tostring): $x} | 1", "(as . {((| 1 tostring) _ $x)} 1)"),
        (". as {$then} | 1", "(as . {(\"then\" $then _)} 1)"),
        (". as [$a, {b: $c}] | 1", "(as . [$a {(\"b\" _ $c)}] 1)"),
        (". as [$a] ?// $a | $a", "(as . (?// [$a] $a) $a)"),
        (". as [$a]?//[$b] ?// {$c} | 1", "(as . (?// [$a] [$b] {(\"c\" $c _)}) 1)"),
        ("reduce .[] as [$a] ?// $a (0; . + $a)", "(reduce (iter .) (?// [$a] $a) 0 (+ . $a))"),
        ("1 as $a::b | 1", "(as 1 $a::b 1)"),
    ]);
}

#[test]
fn pattern_errors() {
    check_error(&[
        (". as [] | 1", "Expected a pattern ($name, [...] or {...}) but found ']' @1:7"),
        (". as {} | 1", "Unexpected '}' @1:7"),
        (". as [$a,] | 1", "Expected a pattern ($name, [...] or {...}) but found ']' @1:10"),
        (". as {(1): $x} | $x", "Cannot use number (1) as object key @1:8"),
        (". as {(true): $x} | $x", "Cannot use boolean (true) as object key @1:8"),
        (". as {a} | 1", "Expected ':' but found '}' @1:8"),
        (". as 1 | 1", "Expected a pattern ($name, [...] or {...}) but found '1' @1:6"),
        (". as $x 1", "Expected '|' but found '1' @1:9"),
        ("1 as $ x | $x", "Expected a variable name after '$' @1:6"),
    ]);
}

#[test]
fn module_directives() {
    let program = Parser::try_parse_module(
        "module {a: 1, \"b\": [1, \"x\", null, true, {c: false}], if: {},};\n\
         import \"m\" as m {search: \"./\"};\nimport \"d\" as $d;\ninclude \"i\";\ninclude \"j\" {k: []};\n\
         def f: 1; def g($x): $x;",
    )
    .expect("the module parses");
    assert_eq!(
        print_program(&program),
        "(module {\"a\":1,\"b\":[1,\"x\",null,true,{\"c\":false}],\"if\":{}}) (import \"m\" m {\"search\":\"./\"}) (import \"d\" $d) \
         (include \"i\") (include \"j\" {\"k\":[]}) (def f 1) (def g($x) $x)"
    );
    assert!(program.body.is_none());
    assert_eq!(program.imports[0].position, Position::new(2, 1));
}

#[test]
fn a_program_with_directives_has_a_body() {
    assert_eq!(parse("module {}; import \"a\" as foo::bar; include \"b\"; def f: 1; f"), "(module {}) (import \"a\" foo::bar) (include \"b\") (def f 1) f");
}

#[test]
fn directive_errors() {
    check_error(&[
        ("module 1; 1", "Module metadata must be an object @1:8"),
        ("module [1]; 1", "Module metadata must be an object @1:8"),
        ("module .a; 1", "Module metadata must be constant @1:8"),
        ("module {a: 1+1}; 1", "Module metadata must be constant @1:8"),
        ("module {a: \"x\\(1)\"}; 1", "Module metadata must be constant @1:8"),
        ("module {(1): 1}; 1", "Module metadata must be constant @1:8"),
        ("import \"a\" as $d {x: [1, -2]}; 1", "Module metadata must be constant @1:18"),
        ("include \"a\" {a: -1}; 1", "Module metadata must be constant @1:13"),
        ("include \"a\" {a: 1 2}; 1", "Module metadata must be constant @1:13"),
        ("import \"a\\(1)\" as foo; 1", "Import path must be constant @1:8"),
        ("import a as foo; 1", "Expected a module path string but found 'a' @1:8"),
        ("import \"a\" as if; 1", "Expected a module name or a $name but found 'if' @1:15"),
        ("import \"a\"; 1", "Expected 'as' but found ';' @1:11"),
        ("import \"a\" as x 1", "Expected ';' but found '1' @1:17"),
        ("def f: 1; import \"a\" as foo; 1", "Unexpected 'import' @1:11"),
        ("1 | import \"a\" as foo; 1", "Unexpected 'import' @1:5"),
        ("module {\"a\": 1}; module {}; 1", "Unexpected 'module' @1:18"),
        ("import \"a\" as x; module {}; 1", "Unexpected 'module' @1:18"),
    ]);
}

#[test]
fn module_errors() {
    let cases = [
        ("def f: 1; f", "A module contains only definitions @1:11"),
        ("1", "A module contains only definitions @1:1"),
        ("def f: 1", "Expected ';' but found end of program @1:9"),
    ];
    for (source, expected) in cases {
        let error = Parser::try_parse_module(source).expect_err(source);
        assert_eq!(format!("{} @{}:{}", error.message, error.line, error.column), expected);
    }
}

#[test]
fn an_empty_module_has_no_definitions() {
    let program = Parser::try_parse_module("# nothing").expect("an empty module parses");
    assert!(program.definitions.is_empty());
    assert!(program.body.is_none());
}

#[test]
fn nodes_carry_the_position_of_their_token() {
    let program = Parser::try_parse("1 +\n  .a[0] |\n\tf(2)").expect("the program parses");
    let pipe = program.body.as_ref().expect("a program has a body");
    assert_eq!(pipe.position, Position::new(2, 9));
    let NodeKind::Pipe { left: add, right: call } = &pipe.kind else {
        panic!("a pipe is expected");
    };
    assert_eq!(add.position, Position::new(1, 3));
    let NodeKind::Binary { left: one, right: index, .. } = &add.kind else {
        panic!("an operator is expected");
    };
    assert_eq!(one.position, Position::new(1, 1));
    assert_eq!(index.position, Position::new(2, 5));
    let NodeKind::Index { target, .. } = &index.kind else {
        panic!("an index is expected");
    };
    assert_eq!(target.position, Position::new(2, 3));
    assert_eq!(call.position, Position::new(3, 2));
    let NodeKind::Call { arguments, .. } = &call.kind else {
        panic!("a call is expected");
    };
    assert_eq!(arguments[0].position, Position::new(3, 4));
}

#[test]
fn syntax_errors() {
    check_error(&[
        ("1 )", "Unexpected ')' @1:3"),
        ("[1, 2", "Expected ']' but found end of program @1:6"),
        ("(1", "Expected ')' but found end of program @1:3"),
        ("f(1;", "Unexpected end of program @1:5"),
        ("|", "Unexpected '|' @1:1"),
        ("1 |", "Unexpected end of program @1:4"),
        ("1,\n\n  ,", "Unexpected ',' @3:3"),
        ("\"abc", "Unterminated string @1:1"),
        ("1 + \"a\\qb\"", "Invalid escape @1:7"),
        ("[invalid!!!", "Invalid character '!' @1:9"),
        ("then", "Unexpected 'then' @1:1"),
        ("1 as", "Expected a pattern ($name, [...] or {...}) but found end of program @1:5"),
        ("reduce . as $x (0)", "Expected ';' but found ')' @1:18"),
    ]);
}

// ---- ParserLimitTests ----
//
// The nesting limit of spec §11, and programs that must not exhaust the host stack. Each parse
// runs on a thread with a 1 MB stack (the default of a Windows main thread), so the result does
// not depend on the stack size of the test runner's threads.

const LIMIT: usize = MAX_NESTING as usize;

// The outcome of a parse on a small stack: "ok", or "message @line:column". The tree is released
// on that stack too.
fn parse_on_small_stack(source: String) -> String {
    on_thread(MEGABYTE, move || match Parser::try_parse(&source) {
        Ok(_) => "ok".to_owned(),
        Err(error) => format!("{} @{}:{}", error.message, error.line, error.column),
    })
}

fn parses(source: String) {
    assert_eq!(parse_on_small_stack(source), "ok");
}

// Opening repeated, closing repeated (the innermost term is "1"), offset in the opening of the
// token that opens the level.
const NESTINGS: [(&str, &str, usize); 12] = [
    ("(", ")", 0),
    ("[", "]", 0),
    ("{a:", "}", 0),
    (".[", "]", 1),
    ("f(", ")", 1),
    ("\"\\(", ")\"", 0),
    ("if . then ", " end", 0),
    ("try ", "", 0),
    ("reduce . as $x (", ";.)", 0),
    ("foreach . as $x (", ";.)", 0),
    ("def f: ", "; f", 0),
    ("{(", "):1}", 0),
];

// "{(" opens two levels (the object and the parentheses).
fn levels(open: &str) -> usize {
    if open == "{(" { LIMIT / 2 } else { LIMIT }
}

// A parenthesized key must not be a number.
fn innermost(open: &str) -> &'static str {
    if open == "{(" { "\"a\"" } else { "1" }
}

#[test]
fn nesting_up_to_the_limit_parses() {
    for (open, close, _) in NESTINGS {
        let count = levels(open);
        parses(open.repeat(count) + innermost(open) + &close.repeat(count));
    }
}

#[test]
fn nesting_beyond_the_limit_fails_at_the_opening_token() {
    for (open, close, offset) in NESTINGS {
        let count = levels(open) + 1;
        let source = open.repeat(count) + innermost(open) + &close.repeat(count);
        // The level beyond the limit is opened by the token at offset in the last opening.
        let column = (count - 1) * open.chars().count() + offset + 1;
        assert_eq!(parse_on_small_stack(source), format!("Program too deeply nested @1:{column}"), "{open}");
    }
}

#[test]
fn nested_patterns_count_too() {
    parses(". as ".to_owned() + &"[".repeat(LIMIT) + "$x" + &"]".repeat(LIMIT) + " | $x");
    assert_eq!(
        parse_on_small_stack(". as ".to_owned() + &"[".repeat(LIMIT + 1) + "$x" + &"]".repeat(LIMIT + 1) + " | $x"),
        format!("Program too deeply nested @1:{}", LIMIT + 6)
    );
}

#[test]
fn nested_metadata_counts_too() {
    assert_eq!(
        parse_on_small_stack("module {a: ".to_owned() + &"[".repeat(LIMIT) + &"]".repeat(LIMIT) + "}; 1"),
        format!("Program too deeply nested @1:{}", LIMIT + 11)
    );
}

#[test]
fn nested_metadata_within_the_limit_parses() {
    parses("module {a: ".to_owned() + &"[".repeat(LIMIT - 1) + &"]".repeat(LIMIT - 1) + "}; 1");
}

#[test]
fn mixed_nesting_counts_every_kind() {
    assert_eq!(
        parse_on_small_stack("([".repeat(LIMIT / 2 + 1) + "1" + &"])".repeat(LIMIT / 2 + 1)),
        format!("Program too deeply nested @1:{}", LIMIT / 2 * 2 + 1)
    );
}

// Sequences of any length parse without recursing per element: prefix, 10,000 elements, suffix.
#[test]
fn long_sequences_do_not_recurse() {
    let cases = [
        ("", "1 + ", "1"),
        ("", "1 * ", "1"),
        ("", "1 // ", "1"),
        ("", "1 and ", "1"),
        ("", "1 == 1 or ", "1"),
        ("", "1 | ", "1"),
        ("", "1, ", "1"),
        ("", "1 as $x | ", "$x"),
        ("", "label $x | ", "1"),
        ("", "def f: 1; ", "f"),
        ("(", "def f: 1; ", "f)"),
        ("[1, ", "def f: 1; ", "f]"),
        ("", "-", "1"),
        ("", "1 + -", "1"),
        ("", ".a", ""),
        ("", ".[0]", ""),
        ("", ".a?", ""),
        (". as $a", " ?// $a", " | 1"),
        ("{a: ", "1 | ", "1}"),
        ("{", "a: 1, ", "}"),
        ("\"", "\\(1)", "\""),
        ("f(", "1; ", "1)"),
    ];
    for (prefix, element, suffix) in cases {
        parses(prefix.to_owned() + &element.repeat(10_000) + suffix);
    }
}

#[test]
fn long_elif_chains_do_not_recurse() {
    parses("if . then 1 ".to_owned() + &"elif . then 1 ".repeat(10_000) + "else 2 end");
}

#[test]
fn a_long_chain_keeps_its_associativity() {
    let depths = on_thread(MEGABYTE, || {
        // Left-associative: the left spine is 10,000 subtractions deep.
        let program = Parser::try_parse(&("1 - ".repeat(10_000) + "1")).expect("the chain parses");
        let mut left = 0;
        let mut node = program.body.as_ref().expect("a program has a body");
        while let NodeKind::Binary { left: inner, right, .. } = &node.kind {
            assert!(matches!(right.kind, NodeKind::Literal(_)));
            node = inner;
            left += 1;
        }

        let program = Parser::try_parse(&("1 // ".repeat(10_000) + "1")).expect("the chain parses");
        let mut right = 0;
        let mut node = program.body.as_ref().expect("a program has a body");
        while let NodeKind::Alternative { left, right: inner } = &node.kind {
            assert!(matches!(left.kind, NodeKind::Literal(_)));
            node = inner;
            right += 1;
        }
        (left, right)
    });
    assert_eq!(depths, (10_000, 10_000));
}

#[test]
fn a_long_pipe_of_bindings_nests_to_the_right() {
    let depth = on_thread(MEGABYTE, || {
        let program = Parser::try_parse(&("1 as $x | ".repeat(10_000) + "$x")).expect("the pipe parses");
        let mut depth = 0;
        let mut node = program.body.as_ref().expect("a program has a body");
        while let NodeKind::Bind { body, .. } = &node.kind {
            node = body;
            depth += 1;
        }
        assert!(matches!(node.kind, NodeKind::Variable(_)));
        depth
    });
    assert_eq!(depth, 10_000);
}

#[test]
fn many_top_level_definitions_are_a_list() {
    on_thread(MEGABYTE, || {
        let program = Parser::try_parse(&("def f: 1; ".repeat(10_000) + "f")).expect("the definitions parse");
        assert_eq!(program.definitions.len(), 10_000);
        assert!(matches!(program.body.as_ref().map(|body| &body.kind), Some(NodeKind::Call { .. })));
    });
}

// ---- CorpusParseTests ----
//
// The conformance corpus and the prelude as parser tests: every program of spec/tests/*.test
// parses, except the %%FAIL cases that fail for a syntax reason; spec/prelude.jq parses as a module.

// %%FAIL cases whose program is syntactically valid: they fail when names are bound.
const BINDING_FAILURES: [&str; 3] = [". as $foo | break $foo", "input", "[inputs]"];

#[test]
fn every_program_of_the_corpus_parses() {
    let mut failures = Vec::new();
    for (file, cases) in support::corpus() {
        for case in &cases {
            let parsed = Parser::try_parse(&case.program);
            // The failures of modules.test are all binding errors: missing modules, cycles, names.
            if case.should_fail && !BINDING_FAILURES.contains(&case.program.as_str()) && file != "modules.test" {
                if parsed.is_ok() {
                    failures.push(format!("{file}:{}: {} parses but is a %%FAIL case", case.line, case.program));
                }
            } else if let Err(error) = parsed {
                failures.push(format!("{file}:{}: {}: {} at {}:{}", case.line, case.program, error.message, error.line, error.column));
            }
        }
    }
    assert!(failures.is_empty(), "{}", failures.join("\n"));
}

#[test]
fn the_binding_failures_are_in_the_corpus() {
    let programs: Vec<String> = support::corpus().into_iter().flat_map(|(_, cases)| cases).filter(|case| case.should_fail).map(|case| case.program).collect();
    for failure in BINDING_FAILURES {
        assert!(programs.iter().any(|program| program == failure), "{failure}");
    }
}

#[test]
fn the_prelude_parses_as_a_module() {
    let source = std::fs::read_to_string(support::spec_directory().join("prelude.jq")).expect("spec/prelude.jq is read");
    let program = Parser::try_parse_module(&source).expect("the prelude parses");
    assert!(program.body.is_none());
    assert_eq!(program.definitions.len(), 102);
    assert!(program.definitions.iter().any(|definition| definition.name == "_modify" && definition.params.len() == 2));
    // And as a program of definitions only, whose body is '.'.
    let program = Parser::try_parse(&source).expect("the prelude parses as a program");
    assert!(matches!(program.body.as_ref().map(|body| &body.kind), Some(NodeKind::Identity)));
}
