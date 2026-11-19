package com.devlooped.jqlite.core.syntax;

import static com.devlooped.jqlite.core.syntax.AstPrinter.error;
import static com.devlooped.jqlite.core.syntax.AstPrinter.parse;
import static com.devlooped.jqlite.core.syntax.AstPrinter.print;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.devlooped.jqlite.syntax.Ast;
import com.devlooped.jqlite.syntax.Ast.Position;
import com.devlooped.jqlite.syntax.Parser;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

class ParserTests {
    private static String moduleError(String source) {
        Parser.Parsed parsed = Parser.tryParseModule(source);
        assertNull(parsed.program());
        return parsed.error().message() + " @" + parsed.error().line() + ":" + parsed.error().column();
    }

    static String[][] precedenceExamplesOfTheSpecCases() {
        return new String[][] {
            {"1, 2 as $x | $x * 10", "(, 1 (as 2 $x (* $x 10)))"},
            {"1 + 2 as $x | -$x", "(as (+ 1 2) $x (neg $x))"},
            {"[-1 as $x | 1, $x]", "(array (as (neg 1) $x (, 1 $x)))"},
            {"1 // 2 == 2", "(// 1 (== 2 2))"},
            {"{} | .a = .b // 1", "(| (object) (// (_assign (index . \"a\") (index . \"b\")) 1))"},
            {"[1 // 2 as $x | 10]", "(array (as (// 1 2) $x 10))"},
            {"try (2 * -3 / \"a\") catch .", "(try (/ (* 2 (neg 3)) \"a\") .)"},
            {"1 + try 2 catch 3 + 4", "(+ (+ 1 (try 2 3)) 4)"},
            {"try -.? catch .", "(try (neg (try .)) .)"},
            {"def f: 1; f | f, 2", "(def f 1) (| f (, f 2))"},
        };
    }

    @ParameterizedTest
    @MethodSource("precedenceExamplesOfTheSpecCases")
    void precedenceExamplesOfTheSpec(String source, String expected) {
        assertEquals(expected, parse(source));
    }

    static String[][] precedenceExamplesThatFailCases() {
        return new String[][] {
            {"reduce 1, 2 as $x (0; . + $x)", "Expected 'as' but found ',' @1:9"},
            {"try 1 * 2 catch 3", "Unexpected 'catch' @1:11"},
        };
    }

    @ParameterizedTest
    @MethodSource("precedenceExamplesThatFailCases")
    void precedenceExamplesThatFail(String source, String expected) {
        assertEquals(expected, error(source));
    }

    static String[][] precedenceAndAssociativityCases() {
        return new String[][] {
            {"1 | 2 | 3", "(| 1 (| 2 3))"},
            {"1, 2, 3", "(, (, 1 2) 3)"},
            {"1, 2 | 3, 4", "(| (, 1 2) (, 3 4))"},
            {"1 // 2 // 3", "(// 1 (// 2 3))"},
            {"1 // 2, 3", "(, (// 1 2) 3)"},
            {".a = 1 // 2", "(// (_assign (index . \"a\") 1) 2)"},
            {".a |= . + 1", "(_modify (index . \"a\") (+ . 1))"},
            {".a = 1 or 2", "(_assign (index . \"a\") (or 1 2))"},
            {"1 or 2 or 3", "(or (or 1 2) 3)"},
            {"1 or 2 and 3", "(or 1 (and 2 3))"},
            {"1 and 2 or 3", "(or (and 1 2) 3)"},
            {"1 and 2 and 3", "(and (and 1 2) 3)"},
            {"1 == 2 and 3 != 4", "(and (== 1 2) (!= 3 4))"},
            {"1 + 2 < 3 - 4", "(< (+ 1 2) (- 3 4))"},
            {"1 <= 2", "(<= 1 2)"},
            {"1 > 2", "(> 1 2)"},
            {"1 >= 2", "(>= 1 2)"},
            {"1 - 2 - 3", "(- (- 1 2) 3)"},
            {"1 - 2 + 3", "(+ (- 1 2) 3)"},
            {"1 + 2 * 3", "(+ 1 (* 2 3))"},
            {"1 * 2 + 3", "(+ (* 1 2) 3)"},
            {"1 * 2 / 3 % 4", "(% (/ (* 1 2) 3) 4)"},
            {"-1 * 2", "(* (neg 1) 2)"},
            {"2 * -3 * 4", "(* (* 2 (neg 3)) 4)"},
            {"1 - -1", "(- 1 (neg 1))"},
            {"- - 1", "(neg (neg 1))"},
            {"-.a[0]?", "(neg (try (index (index . \"a\") 0)))"},
            {"-1 as $x | $x", "(as (neg 1) $x $x)"},
            {"1 = 2 as $x | $x", "(as (_assign 1 2) $x $x)"},
            {"1 or 2 as $x | $x", "(as (or 1 2) $x $x)"},
        };
    }

    @ParameterizedTest
    @MethodSource("precedenceAndAssociativityCases")
    void precedenceAndAssociativity(String source, String expected) {
        assertEquals(expected, parse(source));
    }

    static String[][] assignmentsAreRewrittenCases() {
        return new String[][] {
            {".a += 1", "(as 1 $*update (_modify (index . \"a\") (+ . $*update)))"},
            {".a -= 1", "(as 1 $*update (_modify (index . \"a\") (- . $*update)))"},
            {".a *= 1", "(as 1 $*update (_modify (index . \"a\") (* . $*update)))"},
            {".a /= 1", "(as 1 $*update (_modify (index . \"a\") (/ . $*update)))"},
            {".a %= 1", "(as 1 $*update (_modify (index . \"a\") (% . $*update)))"},
            {".a //= 1", "(as 1 $*update (_modify (index . \"a\") (// . $*update)))"},
            {".a = (1, 2)", "(_assign (index . \"a\") (, 1 2))"},
        };
    }

    @ParameterizedTest
    @MethodSource("assignmentsAreRewrittenCases")
    void assignmentsAreRewritten(String source, String expected) {
        assertEquals(expected, parse(source));
    }

    static String[][] nonAssociativeOperatorsDoNotChainCases() {
        return new String[][] {
            {"1 == 2 == 3", "Operator '==' cannot be chained: add parentheses @1:8"},
            {"1 == 2 < 3", "Operator '<' cannot be chained: add parentheses @1:8"},
            {"1 < 2 < 3", "Operator '<' cannot be chained: add parentheses @1:7"},
            {".a = .b = 1", "Operator '=' cannot be chained: add parentheses @1:9"},
            {".a = 1 |= 2", "Operator '|=' cannot be chained: add parentheses @1:8"},
        };
    }

    @ParameterizedTest
    @MethodSource("nonAssociativeOperatorsDoNotChainCases")
    void nonAssociativeOperatorsDoNotChain(String source, String expected) {
        assertEquals(expected, error(source));
    }

    static String[][] tryOperandsAreUnaryCases() {
        return new String[][] {
            {"try 1", "(try 1)"},
            {"try error(\"x\") catch . | ascii_upcase", "(| (try (error \"x\") .) ascii_upcase)"},
            {"[try error(\"x\") + 1]", "(array (+ (try (error \"x\")) 1))"},
            {"try 1 catch .[0]", "(try 1 (index . 0))"},
            {"try -1", "(try (neg 1))"},
            {"try try 1 catch 2 catch 3", "(try (try 1 2) 3)"},
            {"-try 1 catch 2", "(neg (try 1 2))"},
        };
    }

    @ParameterizedTest
    @MethodSource("tryOperandsAreUnaryCases")
    void tryOperandsAreUnary(String source, String expected) {
        assertEquals(expected, parse(source));
    }

    static String[][] tryOperandErrorsCases() {
        return new String[][] {
            {"[try -\"a\" * 0 catch \"c\"]", "Expected ']' but found 'catch' @1:15"},
            {"try def f: 1; f", "Unexpected 'def' @1:5"},
        };
    }

    @ParameterizedTest
    @MethodSource("tryOperandErrorsCases")
    void tryOperandErrors(String source, String expected) {
        assertEquals(expected, error(source));
    }

    static String[][] bindingSourcesCases() {
        return new String[][] {
            {"1 as $x | 2 as $y | [$x, $y]", "(as 1 $x (as 2 $y (array (, $x $y))))"},
            {"1 as $x | 2 | 3", "(as 1 $x (| 2 3))"},
            {"(1 as $x | $x), 2", "(, (as 1 $x $x) 2)"},
            {"1, 2, 3 as $x | 4, 5 | 6", "(, (, 1 2) (as 3 $x (| (, 4 5) 6)))"},
            {"1 | 2 as $x | 3", "(| 1 (as 2 $x 3))"},
            {"reduce .[] as $x (0; . + $x)", "(reduce (iter .) $x 0 (+ . $x))"},
            {"reduce .[] / .[] as $x (0; . + $x)", "(reduce (/ (iter .) (iter .)) $x 0 (+ . $x))"},
            {"reduce 1 // 2 as $x (0; $x)", "(reduce (// 1 2) $x 0 $x)"},
            {"reduce -1 as $x (0; $x)", "(reduce (neg 1) $x 0 $x)"},
            {"reduce (1,2) as $x (0; .+$x) | . * 2", "(| (reduce (, 1 2) $x 0 (+ . $x)) (* . 2))"},
            {"foreach (1,2) as $x (0; . + $x)", "(foreach (, 1 2) $x 0 (+ . $x) .)"},
            {"foreach 1 + 2 as $x (0; $x; [$x, .])", "(foreach (+ 1 2) $x 0 $x (array (, $x .)))"},
            {"[foreach .[] as [$a] ?// $a (0; 1)]", "(array (foreach (iter .) (?// [$a] $a) 0 1 .))"},
        };
    }

    @ParameterizedTest
    @MethodSource("bindingSourcesCases")
    void bindingSources(String source, String expected) {
        assertEquals(expected, parse(source));
    }

    static String[][] definitionsAndLabelsConsumeTheRestOfThePipeCases() {
        return new String[][] {
            {"def f: 1; def g: 2; f", "(def f 1) (def g 2) f"},
            {"def f: 1;", "(def f 1) ."},
            {"", "."},
            {"1, def f: 2; f, 3", "(, 1 (def f 2 (, f 3)))"},
            {"[1, 2 | def f: 3; f, 4]", "(array (| (, 1 2) (def f 3 (, f 4))))"},
            {"(def f: 1; f | f), 2", "(, (def f 1 (| f f)) 2)"},
            {"if def f: 1; f then 2 else 3 end", "(if (def f 1 f) 2 3)"},
            {"def f: def g: 3; g; f", "(def f (def g 3 g)) f"},
            {"label $out | 1, 2 | 3", "(label $out (| (, 1 2) 3))"},
            {"[1, label $f | 2, 3]", "(array (, 1 (label $f (, 2 3))))"},
            {"label $f | break $f", "(label $f (break $f))"},
            {"label $if | 1", "(label $if 1)"},
            {"label $a::b | 1", "(label $a::b 1)"},
        };
    }

    @ParameterizedTest
    @MethodSource("definitionsAndLabelsConsumeTheRestOfThePipeCases")
    void definitionsAndLabelsConsumeTheRestOfThePipe(String source, String expected) {
        assertEquals(expected, parse(source));
    }

    static String[][] definitionsAndLabelsAreNotOperandsCases() {
        return new String[][] {
            {"1 + def f: 1; f", "Unexpected 'def' @1:5"},
            {"1 // def f: 1; f", "Unexpected 'def' @1:6"},
            {"-def f: 1; f", "Unexpected 'def' @1:2"},
            {"1 + label $f | 1", "Unexpected 'label' @1:5"},
            {"label f | 1", "Expected a label name ($name) but found 'f' @1:7"},
            {"break f", "Expected a label name ($name) but found 'f' @1:7"},
        };
    }

    @ParameterizedTest
    @MethodSource("definitionsAndLabelsAreNotOperandsCases")
    void definitionsAndLabelsAreNotOperands(String source, String expected) {
        assertEquals(expected, error(source));
    }

    static String[][] definitionsAndCallsCases() {
        return new String[][] {
            {"def f(a; $b): [a, $b, b]; f(1; 2)", "(def f(a;$b) (array (, (, a $b) b))) (f 1 2)"},
            {"def f($a; $b): $a + $b; 1", "(def f($a;$b) (+ $a $b)) 1"},
            {"def f(x): x; f(.)", "(def f(x) x) (f .)"},
            {"def true: 1; true", "(def true 1) true"},
            {"def f::g: 1; f::g", "(def f::g 1) f::g"},
            {"def f(a::b): a::b; f(1)", "(def f(a::b) a::b) (f 1)"},
            {"f(1; 2, 3; 4 | 5)", "(f 1 (, 2 3) (| 4 5))"},
            {"a::b", "a::b"},
        };
    }

    @ParameterizedTest
    @MethodSource("definitionsAndCallsCases")
    void definitionsAndCalls(String source, String expected) {
        assertEquals(expected, parse(source));
    }

    static String[][] definitionErrorsCases() {
        return new String[][] {
            {"def if: 1; 1", "Expected a function name but found 'if' @1:5"},
            {"def f(): 1; f", "Expected a parameter name but found ')' @1:7"},
            {"def f(a;): 1; 1", "Expected a parameter name but found ')' @1:9"},
            {"def f(if): 1; 1", "Expected a parameter name but found 'if' @1:7"},
            {"def f: 1", "Expected ';' but found end of program @1:9"},
            {"def f 1; 1", "Expected ':' but found '1' @1:7"},
        };
    }

    @ParameterizedTest
    @MethodSource("definitionErrorsCases")
    void definitionErrors(String source, String expected) {
        assertEquals(expected, error(source));
    }

    static String[][] pathsAndPostfixCases() {
        return new String[][] {
            {".", "."},
            {"..", "recurse"},
            {"..?", "(try recurse)"},
            {"..|.a?", "(| recurse (try (index . \"a\")))"},
            {".. .a?", "(try (index recurse \"a\"))"},
            {"..[0]?", "(try (index recurse 0))"},
            {".a", "(index . \"a\")"},
            {".a.b.\"c\"[0]", "(index (index (index (index . \"a\") \"b\") \"c\") 0)"},
            {".a .b", "(index (index . \"a\") \"b\")"},
            {".\"a\"", "(index . \"a\")"},
            {". \"a\"", "(index . \"a\")"},
            {".\"a\".\"b\"", "(index (index . \"a\") \"b\")"},
            {".\"a\\(1)\"", "(index . (str \"a\" 1 \"\"))"},
            {".a.[0]", "(index (index . \"a\") 0)"},
            {".[0]", "(index . 0)"},
            {". [0]", "(index . 0)"},
            {".[1 + 1]", "(index . (+ 1 1))"},
            {".[\"a\" + \"b\"]", "(index . (+ \"a\" \"b\"))"},
            {".a[.k]", "(index (index . \"a\") (index . \"k\"))"},
            {".[0, 1]", "(index . (, 0 1))"},
            {".[]", "(iter .)"},
            {".[]?", "(try (iter .))"},
            {".a[]", "(iter (index . \"a\"))"},
            {".[1:2]", "(slice . 1 2)"},
            {".[1:]", "(slice . 1 _)"},
            {".[:2]", "(slice . _ 2)"},
            {".[1:$n]", "(slice . 1 $n)"},
            {".[1:][0]", "(index (slice . 1 _) 0)"},
            {".a?", "(try (index . \"a\"))"},
            {".a??", "(try (try (index . \"a\")))"},
            {".a?.b?", "(try (index (try (index . \"a\")) \"b\"))"},
            {".a[]?.b", "(index (try (iter (index . \"a\"))) \"b\")"},
            {".a? // 1", "(// (try (index . \"a\")) 1)"},
            {"$x[0]", "(index $x 0)"},
            {"$x.a", "(index $x \"a\")"},
            {"\"abc\"[1:]", "(slice \"abc\" 1 _)"},
            {"1[0]?", "(try (index 1 0))"},
            {"f.a", "(index f \"a\")"},
            {"if true then [1] else [2] end[0]", "(index (if true (array 1) (array 2)) 0)"},
            {"{\"end\":4} | .end", "(| (object (\"end\" 4)) (index . \"end\"))"},
        };
    }

    @ParameterizedTest
    @MethodSource("pathsAndPostfixCases")
    void pathsAndPostfix(String source, String expected) {
        assertEquals(expected, parse(source));
    }

    static String[][] pathErrorsCases() {
        return new String[][] {
            {".[:]", "Unexpected ']' @1:4"},
            {".[1:2:3]", "Expected ']' but found ':' @1:6"},
            {".a?//1", "Unexpected '?//': write '? //' for an optional followed by an alternative @1:3"},
            {"[.[]?//1]", "Unexpected '?//': write '? //' for an optional followed by an alternative @1:5"},
            {"..a", "Unexpected 'a' @1:3"},
            {"1 . 2", "Unexpected '.' @1:3"},
        };
    }

    @ParameterizedTest
    @MethodSource("pathErrorsCases")
    void pathErrors(String source, String expected) {
        assertEquals(expected, error(source));
    }

    static String[][] termsCases() {
        return new String[][] {
            {"null", "null"},
            {"true", "true"},
            {"false", "false"},
            {"1.5e3", "1500"},
            {"\"a\"", "\"a\""},
            {"$__loc__", "$__loc__"},
            {"$ENV.PATH", "(index $ENV \"PATH\")"},
            {"[]", "(array)"},
            {"[1, 2]", "(array (, 1 2))"},
            {"[.[] | . * 2]", "(array (| (iter .) (* . 2)))"},
            {"(1)", "1"},
            {"((1, 2))", "(, 1 2)"},
            {"not", "not"},
            {"empty", "empty"},
        };
    }

    @ParameterizedTest
    @MethodSource("termsCases")
    void terms(String source, String expected) {
        assertEquals(expected, parse(source));
    }

    static String[][] stringsAndFormatsCases() {
        return new String[][] {
            {"\"a\\(1)b\\(2)c\"", "(str \"a\" 1 \"b\" 2 \"c\")"},
            {"\"\\(1, 2)\"", "(str \"\" (, 1 2) \"\")"},
            {"\"a\\(\"b\\(\"c\")\")\"", "(str \"a\" (str \"b\" \"c\" \"\") \"\")"},
            {"\"\\(1 + \"\\(2)\")\"", "(str \"\" (+ 1 (str \"\" 2 \"\")) \"\")"},
            {"\"\\((1))\"", "(str \"\" 1 \"\")"},
            {"@base64 \"x\\(.)y\"", "(str @base64 \"x\" . \"y\")"},
            {"@json \"x\\(\"y\")\"", "(str @json \"x\" \"y\" \"\")"},
            {"@base64 \"abc\"", "\"abc\""},
            {"@base64", "@base64"},
            {"@foo", "@foo"},
            {"@foo \"x\"", "\"x\""},
            {"@text | @json", "(| @text @json)"},
        };
    }

    @ParameterizedTest
    @MethodSource("stringsAndFormatsCases")
    void stringsAndFormats(String source, String expected) {
        assertEquals(expected, parse(source));
    }

    static String[][] stringErrorsCases() {
        return new String[][] {
            {"\"\\()\"", "Unexpected ')' @1:4"},
            {"\"\\(1;2)\"", "Expected ')' but found ';' @1:5"},
            {"\"a\\(1", "Unterminated string @1:1"},
        };
    }

    @ParameterizedTest
    @MethodSource("stringErrorsCases")
    void stringErrors(String source, String expected) {
        assertEquals(expected, error(source));
    }

    static String[][] conditionalsCases() {
        return new String[][] {
            {"if 1 then 2 else 3 end", "(if 1 2 3)"},
            {"if 1 then 2 end", "(if 1 2 .)"},
            {"if 1 then 2 elif 3 then 4 end", "(if 1 2 (if 3 4 .))"},
            {"if 1 then 2 elif 3 then 4 else 5 end", "(if 1 2 (if 3 4 5))"},
            {"if 1, 2 then 3 | 4 else 5, 6 end", "(if (, 1 2) (| 3 4) (, 5 6))"},
            {"[-if true then 1 else 2 end]", "(array (neg (if true 1 2)))"},
        };
    }

    @ParameterizedTest
    @MethodSource("conditionalsCases")
    void conditionals(String source, String expected) {
        assertEquals(expected, parse(source));
    }

    static String[][] conditionalErrorsCases() {
        return new String[][] {
            {"if 1 then 2 else 3", "Expected 'end' but found end of program @1:19"},
            {"if 1 2 end", "Expected 'then' but found '2' @1:6"},
        };
    }

    @ParameterizedTest
    @MethodSource("conditionalErrorsCases")
    void conditionalErrors(String source, String expected) {
        assertEquals(expected, error(source));
    }

    static String[][] objectConstructionCases() {
        return new String[][] {
            {"{}", "(object)"},
            {"{a: 1}", "(object (\"a\" 1))"},
            {"{if: 1, then: 2}", "(object (\"if\" 1) (\"then\" 2))"},
            {"{\"a b\": 1}", "(object (\"a b\" 1))"},
            {"{\"a\\(1)\": 2}", "(object ((str \"a\" 1 \"\") 2))"},
            {"{@base64 \"a\\(1)\": 2}", "(object ((str @base64 \"a\" 1 \"\") 2))"},
            {"{$k: 1}", "(object ($k 1))"},
            {"{(.a, .b): 1}", "(object ((, (index . \"a\") (index . \"b\")) 1))"},
            {"{(\"a\"): 1}", "(object (\"a\" 1))"},
            {"{a}", "(object (\"a\" _))"},
            {"{if}", "(object (\"if\" _))"},
            {"{null}", "(object (\"null\" _))"},
            {"{\"a\"}", "(object (\"a\" _))"},
            {"{\"x\\(1, 2)\"}", "(object ((str \"x\" (, 1 2) \"\") _))"},
            {"{@base64 \"a\"}", "(object (\"a\" _))"},
            {"{$x}", "(object (\"x\" $x))"},
            {"{$__loc__}", "(object (\"__loc__\" $__loc__))"},
            {"{a, $b, \"c\": 3}", "(object (\"a\" _) (\"b\" $b) (\"c\" 3))"},
            {"{a: 1,}", "(object (\"a\" 1))"},
            {"{a: 1, b: 2}", "(object (\"a\" 1) (\"b\" 2))"},
            {"{a: (1, 2)}", "(object (\"a\" (, 1 2)))"},
            {"{a: 1 | 2 | 3}", "(object (\"a\" (| 1 (| 2 3))))"},
            {"{a: 1, b: 2 | . + 1}", "(object (\"a\" 1) (\"b\" (| 2 (+ . 1))))"},
            {"{a: 1 + 2}", "(object (\"a\" (+ 1 2)))"},
            {"{a: -1}", "(object (\"a\" (neg 1)))"},
            {"{a: .x = 1}", "(object (\"a\" (_assign (index . \"x\") 1)))"},
            {"{a: 1 // 2}", "(object (\"a\" (// 1 2)))"},
            {"{a: reduce .[] as $x (0; 1)}", "(object (\"a\" (reduce (iter .) $x 0 1)))"},
            {"{a: if . then 1 end}", "(object (\"a\" (if . 1 .)))"},
            {"{a: try 1 catch 2}", "(object (\"a\" (try 1 2)))"},
            {"{a: .b?}", "(object (\"a\" (try (index . \"b\"))))"},
            {"{a: {b: {c: 1}}}", "(object (\"a\" (object (\"b\" (object (\"c\" 1))))))"},
            {"{a::b: 1}", "(object (\"a::b\" 1))"},
        };
    }

    @ParameterizedTest
    @MethodSource("objectConstructionCases")
    void objectConstruction(String source, String expected) {
        assertEquals(expected, parse(source));
    }

    static String[][] objectErrorsCases() {
        return new String[][] {
            {"{1: 2}", "May need parentheses around object key expression @1:2"},
            {"{1+2: 3}", "May need parentheses around object key expression @1:2"},
            {"{-1: 2}", "May need parentheses around object key expression @1:2"},
            {"{.a: 2}", "May need parentheses around object key expression @1:2"},
            {"{(1): 2}", "Cannot use number (1) as object key @1:3"},
            {"{(true): 2}", "Cannot use boolean (true) as object key @1:3"},
            {"{(null): 2}", "Cannot use null (null) as object key @1:3"},
            {"{(\"a\")}", "Expected ':' but found '}' @1:7"},
            {"{,}", "Unexpected ',' @1:2"},
            {"{a:1,,}", "Unexpected ',' @1:6"},
            {"{a 1}", "Expected ',' or '}' but found '1' @1:4"},
            {"{a: 1, b}c", "Unexpected 'c' @1:10"},
            {"{a: 1 as $x | $x}", "Expected ',' or '}' but found 'as' @1:7"},
            {"{a: 1 | 2 as $x | $x}", "Expected ',' or '}' but found 'as' @1:11"},
            {"{a: def f: 1; f}", "Unexpected 'def' @1:5"},
            {"{a: label $f | 1}", "Unexpected 'label' @1:5"},
            {"{a: 1, 2}", "May need parentheses around object key expression @1:8"},
            {"[1, 2,]", "Unexpected ']' @1:7"},
        };
    }

    @ParameterizedTest
    @MethodSource("objectErrorsCases")
    void objectErrors(String source, String expected) {
        assertEquals(expected, error(source));
    }

    static String[][] patternsCases() {
        return new String[][] {
            {". as $x | $x", "(as . $x $x)"},
            {". as [$a, $b] | 1", "(as . [$a $b] 1)"},
            {". as [$a, [$b]] | 1", "(as . [$a [$b]] 1)"},
            {". as {$a} | 1", "(as . {(\"a\" $a _)} 1)"},
            {". as {$a: [$b]} | 1", "(as . {(\"a\" $a [$b])} 1)"},
            {". as {a: $x, \"b\": $y, if: $z} | 1", "(as . {(\"a\" _ $x) (\"b\" _ $y) (\"if\" _ $z)} 1)"},
            {". as {\"a\\(1)\": $x} | 1", "(as . {((str \"a\" 1 \"\") _ $x)} 1)"},
            {". as {@base64 \"a\": $x} | 1", "(as . {(\"a\" _ $x)} 1)"},
            {". as {(1 | tostring): $x} | 1", "(as . {((| 1 tostring) _ $x)} 1)"},
            {". as {$then} | 1", "(as . {(\"then\" $then _)} 1)"},
            {". as [$a, {b: $c}] | 1", "(as . [$a {(\"b\" _ $c)}] 1)"},
            {". as [$a] ?// $a | $a", "(as . (?// [$a] $a) $a)"},
            {". as [$a]?//[$b] ?// {$c} | 1", "(as . (?// [$a] [$b] {(\"c\" $c _)}) 1)"},
            {"reduce .[] as [$a] ?// $a (0; . + $a)", "(reduce (iter .) (?// [$a] $a) 0 (+ . $a))"},
            {"1 as $a::b | 1", "(as 1 $a::b 1)"},
        };
    }

    @ParameterizedTest
    @MethodSource("patternsCases")
    void patterns(String source, String expected) {
        assertEquals(expected, parse(source));
    }

    static String[][] patternErrorsCases() {
        return new String[][] {
            {". as [] | 1", "Expected a pattern ($name, [...] or {...}) but found ']' @1:7"},
            {". as {} | 1", "Unexpected '}' @1:7"},
            {". as [$a,] | 1", "Expected a pattern ($name, [...] or {...}) but found ']' @1:10"},
            {". as {(1): $x} | $x", "Cannot use number (1) as object key @1:8"},
            {". as {(true): $x} | $x", "Cannot use boolean (true) as object key @1:8"},
            {". as {a} | 1", "Expected ':' but found '}' @1:8"},
            {". as 1 | 1", "Expected a pattern ($name, [...] or {...}) but found '1' @1:6"},
            {". as $x 1", "Expected '|' but found '1' @1:9"},
            {"1 as $ x | $x", "Expected a variable name after '$' @1:6"},
        };
    }

    @ParameterizedTest
    @MethodSource("patternErrorsCases")
    void patternErrors(String source, String expected) {
        assertEquals(expected, error(source));
    }

    static String[][] directiveErrorsCases() {
        return new String[][] {
            {"module 1; 1", "Module metadata must be an object @1:8"},
            {"module [1]; 1", "Module metadata must be an object @1:8"},
            {"module .a; 1", "Module metadata must be constant @1:8"},
            {"module {a: 1+1}; 1", "Module metadata must be constant @1:8"},
            {"module {a: \"x\\(1)\"}; 1", "Module metadata must be constant @1:8"},
            {"module {(1): 1}; 1", "Module metadata must be constant @1:8"},
            {"import \"a\" as $d {x: [1, -2]}; 1", "Module metadata must be constant @1:18"},
            {"include \"a\" {a: -1}; 1", "Module metadata must be constant @1:13"},
            {"include \"a\" {a: 1 2}; 1", "Module metadata must be constant @1:13"},
            {"import \"a\\(1)\" as foo; 1", "Import path must be constant @1:8"},
            {"import a as foo; 1", "Expected a module path string but found 'a' @1:8"},
            {"import \"a\" as if; 1", "Expected a module name or a $name but found 'if' @1:15"},
            {"import \"a\"; 1", "Expected 'as' but found ';' @1:11"},
            {"import \"a\" as x 1", "Expected ';' but found '1' @1:17"},
            {"def f: 1; import \"a\" as foo; 1", "Unexpected 'import' @1:11"},
            {"1 | import \"a\" as foo; 1", "Unexpected 'import' @1:5"},
            {"module {\"a\": 1}; module {}; 1", "Unexpected 'module' @1:18"},
            {"import \"a\" as x; module {}; 1", "Unexpected 'module' @1:18"},
        };
    }

    @ParameterizedTest
    @MethodSource("directiveErrorsCases")
    void directiveErrors(String source, String expected) {
        assertEquals(expected, error(source));
    }

    static String[][] moduleErrorsCases() {
        return new String[][] {
            {"def f: 1; f", "A module contains only definitions @1:11"},
            {"1", "A module contains only definitions @1:1"},
            {"def f: 1", "Expected ';' but found end of program @1:9"},
        };
    }

    @ParameterizedTest
    @MethodSource("moduleErrorsCases")
    void moduleErrors(String source, String expected) {
        assertEquals(expected, moduleError(source));
    }

    static String[][] syntaxErrorsCases() {
        return new String[][] {
            {"1 )", "Unexpected ')' @1:3"},
            {"[1, 2", "Expected ']' but found end of program @1:6"},
            {"(1", "Expected ')' but found end of program @1:3"},
            {"f(1;", "Unexpected end of program @1:5"},
            {"|", "Unexpected '|' @1:1"},
            {"1 |", "Unexpected end of program @1:4"},
            {"1,\n\n  ,", "Unexpected ',' @3:3"},
            {"\"abc", "Unterminated string @1:1"},
            {"1 + \"a\\qb\"", "Invalid escape @1:7"},
            {"[invalid!!!", "Invalid character '!' @1:9"},
            {"then", "Unexpected 'then' @1:1"},
            {"1 as", "Expected a pattern ($name, [...] or {...}) but found end of program @1:5"},
            {"reduce . as $x (0)", "Expected ';' but found ')' @1:18"},
        };
    }

    @ParameterizedTest
    @MethodSource("syntaxErrorsCases")
    void syntaxErrors(String source, String expected) {
        assertEquals(expected, error(source));
    }

    @Test
    void moduleDirectives() {
        Parser.Parsed parsed = Parser.tryParseModule(
            "module {a: 1, \"b\": [1, \"x\", null, true, {c: false}], if: {},};\n"
            + "import \"m\" as m {search: \"./\"};\nimport \"d\" as $d;\ninclude \"i\";\ninclude \"j\" {k: []};\n"
            + "def f: 1; def g($x): $x;");
        assertNotNull(parsed.program(), String.valueOf(parsed.error()));
        assertEquals(
            "(module {\"a\":1,\"b\":[1,\"x\",null,true,{\"c\":false}],\"if\":{}}) (import \"m\" m {\"search\":\"./\"}) (import \"d\" $d) "
            + "(include \"i\") (include \"j\" {\"k\":[]}) (def f 1) (def g($x) $x)",
            print(parsed.program()));
        assertNull(parsed.program().body());
        assertEquals(new Position(2, 1), parsed.program().imports().get(0).position());
    }

    @Test
    void aProgramWithDirectivesHasABody() {
        assertEquals("(module {}) (import \"a\" foo::bar) (include \"b\") (def f 1) f",
            parse("module {}; import \"a\" as foo::bar; include \"b\"; def f: 1; f"));
    }

    @Test
    void anEmptyModuleHasNoDefinitions() {
        Parser.Parsed parsed = Parser.tryParseModule("# nothing");
        assertNotNull(parsed.program());
        assertTrue(parsed.program().definitions().isEmpty());
        assertNull(parsed.program().body());
    }

    @Test
    void nodesCarryThePositionOfTheirToken() {
        Parser.Parsed parsed = Parser.tryParse("1 +\n  .a[0] |\n\tf(2)");
        Ast.Pipe pipe = assertInstanceOf(Ast.Pipe.class, parsed.program().body());
        assertEquals(new Position(2, 9), pipe.position());
        Ast.Binary add = assertInstanceOf(Ast.Binary.class, pipe.left());
        assertEquals(new Position(1, 3), add.position());
        assertEquals(new Position(1, 1), add.left().position());
        Ast.Index index = assertInstanceOf(Ast.Index.class, add.right());
        assertEquals(new Position(2, 5), index.position());
        assertEquals(new Position(2, 3), index.target().position());
        Ast.Call call = assertInstanceOf(Ast.Call.class, pipe.right());
        assertEquals(new Position(3, 2), call.position());
        assertEquals(new Position(3, 4), call.arguments().get(0).position());
    }
}
