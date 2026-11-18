using Devlooped.Core.Syntax;
using static Devlooped.Tests.Core.Syntax.AstPrinter;

namespace Devlooped.Tests.Core.Syntax;

public class ParserTests
{
    // Every row of the examples table of spec §3.1.
    [Theory]
    [InlineData("1, 2 as $x | $x * 10", "(, 1 (as 2 $x (* $x 10)))")]
    [InlineData("1 + 2 as $x | -$x", "(as (+ 1 2) $x (neg $x))")]
    [InlineData("[-1 as $x | 1, $x]", "(array (as (neg 1) $x (, 1 $x)))")]
    [InlineData("1 // 2 == 2", "(// 1 (== 2 2))")]
    [InlineData("{} | .a = .b // 1", "(| (object) (// (_assign (index . \"a\") (index . \"b\")) 1))")]
    [InlineData("[1 // 2 as $x | 10]", "(array (as (// 1 2) $x 10))")]
    [InlineData("try (2 * -3 / \"a\") catch .", "(try (/ (* 2 (neg 3)) \"a\") .)")]
    [InlineData("1 + try 2 catch 3 + 4", "(+ (+ 1 (try 2 3)) 4)")]
    [InlineData("try -.? catch .", "(try (neg (try .)) .)")]
    [InlineData("def f: 1; f | f, 2", "(def f 1) (| f (, f 2))")]
    public void Precedence_examples_of_the_spec(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Theory]
    [InlineData("reduce 1, 2 as $x (0; . + $x)", "Expected 'as' but found ',' @1:9")]
    [InlineData("try 1 * 2 catch 3", "Unexpected 'catch' @1:11")]
    public void Precedence_examples_that_fail(string source, string expected) => Assert.Equal(expected, Error(source));

    // Each level of the table of spec §3.1, with its associativity and its neighbours.
    [Theory]
    [InlineData("1 | 2 | 3", "(| 1 (| 2 3))")]
    [InlineData("1, 2, 3", "(, (, 1 2) 3)")]
    [InlineData("1, 2 | 3, 4", "(| (, 1 2) (, 3 4))")]
    [InlineData("1 // 2 // 3", "(// 1 (// 2 3))")]
    [InlineData("1 // 2, 3", "(, (// 1 2) 3)")]
    [InlineData(".a = 1 // 2", "(// (_assign (index . \"a\") 1) 2)")]
    [InlineData(".a |= . + 1", "(_modify (index . \"a\") (+ . 1))")]
    [InlineData(".a = 1 or 2", "(_assign (index . \"a\") (or 1 2))")]
    [InlineData("1 or 2 or 3", "(or (or 1 2) 3)")]
    [InlineData("1 or 2 and 3", "(or 1 (and 2 3))")]
    [InlineData("1 and 2 or 3", "(or (and 1 2) 3)")]
    [InlineData("1 and 2 and 3", "(and (and 1 2) 3)")]
    [InlineData("1 == 2 and 3 != 4", "(and (== 1 2) (!= 3 4))")]
    [InlineData("1 + 2 < 3 - 4", "(< (+ 1 2) (- 3 4))")]
    [InlineData("1 <= 2", "(<= 1 2)")]
    [InlineData("1 > 2", "(> 1 2)")]
    [InlineData("1 >= 2", "(>= 1 2)")]
    [InlineData("1 - 2 - 3", "(- (- 1 2) 3)")]
    [InlineData("1 - 2 + 3", "(+ (- 1 2) 3)")]
    [InlineData("1 + 2 * 3", "(+ 1 (* 2 3))")]
    [InlineData("1 * 2 + 3", "(+ (* 1 2) 3)")]
    [InlineData("1 * 2 / 3 % 4", "(% (/ (* 1 2) 3) 4)")]
    [InlineData("-1 * 2", "(* (neg 1) 2)")]
    [InlineData("2 * -3 * 4", "(* (* 2 (neg 3)) 4)")]
    [InlineData("1 - -1", "(- 1 (neg 1))")]
    [InlineData("- - 1", "(neg (neg 1))")]
    [InlineData("-.a[0]?", "(neg (try (index (index . \"a\") 0)))")]
    [InlineData("-1 as $x | $x", "(as (neg 1) $x $x)")]
    [InlineData("1 = 2 as $x | $x", "(as (_assign 1 2) $x $x)")]
    [InlineData("1 or 2 as $x | $x", "(as (or 1 2) $x $x)")]
    public void Precedence_and_associativity(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Theory]
    [InlineData(".a += 1", "(as 1 $*update (_modify (index . \"a\") (+ . $*update)))")]
    [InlineData(".a -= 1", "(as 1 $*update (_modify (index . \"a\") (- . $*update)))")]
    [InlineData(".a *= 1", "(as 1 $*update (_modify (index . \"a\") (* . $*update)))")]
    [InlineData(".a /= 1", "(as 1 $*update (_modify (index . \"a\") (/ . $*update)))")]
    [InlineData(".a %= 1", "(as 1 $*update (_modify (index . \"a\") (% . $*update)))")]
    [InlineData(".a //= 1", "(as 1 $*update (_modify (index . \"a\") (// . $*update)))")]
    [InlineData(".a = (1, 2)", "(_assign (index . \"a\") (, 1 2))")]
    public void Assignments_are_rewritten(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Theory]
    [InlineData("1 == 2 == 3", "Operator '==' cannot be chained: add parentheses @1:8")]
    [InlineData("1 == 2 < 3", "Operator '<' cannot be chained: add parentheses @1:8")]
    [InlineData("1 < 2 < 3", "Operator '<' cannot be chained: add parentheses @1:7")]
    [InlineData(".a = .b = 1", "Operator '=' cannot be chained: add parentheses @1:9")]
    [InlineData(".a = 1 |= 2", "Operator '|=' cannot be chained: add parentheses @1:8")]
    public void Non_associative_operators_do_not_chain(string source, string expected) => Assert.Equal(expected, Error(source));

    [Theory]
    [InlineData("try 1", "(try 1)")]
    [InlineData("try error(\"x\") catch . | ascii_upcase", "(| (try (error \"x\") .) ascii_upcase)")]
    [InlineData("[try error(\"x\") + 1]", "(array (+ (try (error \"x\")) 1))")]
    [InlineData("try 1 catch .[0]", "(try 1 (index . 0))")]
    [InlineData("try -1", "(try (neg 1))")]
    [InlineData("try try 1 catch 2 catch 3", "(try (try 1 2) 3)")]
    [InlineData("-try 1 catch 2", "(neg (try 1 2))")]
    public void Try_operands_are_unary(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Theory]
    [InlineData("[try -\"a\" * 0 catch \"c\"]", "Expected ']' but found 'catch' @1:15")]
    [InlineData("try def f: 1; f", "Unexpected 'def' @1:5")]
    public void Try_operand_errors(string source, string expected) => Assert.Equal(expected, Error(source));

    [Theory]
    [InlineData("1 as $x | 2 as $y | [$x, $y]", "(as 1 $x (as 2 $y (array (, $x $y))))")]
    [InlineData("1 as $x | 2 | 3", "(as 1 $x (| 2 3))")]
    [InlineData("(1 as $x | $x), 2", "(, (as 1 $x $x) 2)")]
    [InlineData("1, 2, 3 as $x | 4, 5 | 6", "(, (, 1 2) (as 3 $x (| (, 4 5) 6)))")]
    [InlineData("1 | 2 as $x | 3", "(| 1 (as 2 $x 3))")]
    [InlineData("reduce .[] as $x (0; . + $x)", "(reduce (iter .) $x 0 (+ . $x))")]
    [InlineData("reduce .[] / .[] as $x (0; . + $x)", "(reduce (/ (iter .) (iter .)) $x 0 (+ . $x))")]
    [InlineData("reduce 1 // 2 as $x (0; $x)", "(reduce (// 1 2) $x 0 $x)")]
    [InlineData("reduce -1 as $x (0; $x)", "(reduce (neg 1) $x 0 $x)")]
    [InlineData("reduce (1,2) as $x (0; .+$x) | . * 2", "(| (reduce (, 1 2) $x 0 (+ . $x)) (* . 2))")]
    [InlineData("foreach (1,2) as $x (0; . + $x)", "(foreach (, 1 2) $x 0 (+ . $x) .)")]
    [InlineData("foreach 1 + 2 as $x (0; $x; [$x, .])", "(foreach (+ 1 2) $x 0 $x (array (, $x .)))")]
    [InlineData("[foreach .[] as [$a] ?// $a (0; 1)]", "(array (foreach (iter .) (?// [$a] $a) 0 1 .))")]
    public void Binding_sources(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Theory]
    [InlineData("def f: 1; def g: 2; f", "(def f 1) (def g 2) f")]
    [InlineData("def f: 1;", "(def f 1) .")]
    [InlineData("", ".")]
    [InlineData("1, def f: 2; f, 3", "(, 1 (def f 2 (, f 3)))")]
    [InlineData("[1, 2 | def f: 3; f, 4]", "(array (| (, 1 2) (def f 3 (, f 4))))")]
    [InlineData("(def f: 1; f | f), 2", "(, (def f 1 (| f f)) 2)")]
    [InlineData("if def f: 1; f then 2 else 3 end", "(if (def f 1 f) 2 3)")]
    [InlineData("def f: def g: 3; g; f", "(def f (def g 3 g)) f")]
    [InlineData("label $out | 1, 2 | 3", "(label $out (| (, 1 2) 3))")]
    [InlineData("[1, label $f | 2, 3]", "(array (, 1 (label $f (, 2 3))))")]
    [InlineData("label $f | break $f", "(label $f (break $f))")]
    [InlineData("label $if | 1", "(label $if 1)")]
    [InlineData("label $a::b | 1", "(label $a::b 1)")]
    public void Definitions_and_labels_consume_the_rest_of_the_pipe(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Theory]
    [InlineData("1 + def f: 1; f", "Unexpected 'def' @1:5")]
    [InlineData("1 // def f: 1; f", "Unexpected 'def' @1:6")]
    [InlineData("-def f: 1; f", "Unexpected 'def' @1:2")]
    [InlineData("1 + label $f | 1", "Unexpected 'label' @1:5")]
    [InlineData("label f | 1", "Expected a label name ($name) but found 'f' @1:7")]
    [InlineData("break f", "Expected a label name ($name) but found 'f' @1:7")]
    public void Definitions_and_labels_are_not_operands(string source, string expected) => Assert.Equal(expected, Error(source));

    [Theory]
    [InlineData("def f(a; $b): [a, $b, b]; f(1; 2)", "(def f(a;$b) (array (, (, a $b) b))) (f 1 2)")]
    [InlineData("def f($a; $b): $a + $b; 1", "(def f($a;$b) (+ $a $b)) 1")]
    [InlineData("def f(x): x; f(.)", "(def f(x) x) (f .)")]
    [InlineData("def true: 1; true", "(def true 1) true")]
    [InlineData("def f::g: 1; f::g", "(def f::g 1) f::g")]
    [InlineData("def f(a::b): a::b; f(1)", "(def f(a::b) a::b) (f 1)")]
    [InlineData("f(1; 2, 3; 4 | 5)", "(f 1 (, 2 3) (| 4 5))")]
    [InlineData("a::b", "a::b")]
    public void Definitions_and_calls(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Theory]
    [InlineData("def if: 1; 1", "Expected a function name but found 'if' @1:5")]
    [InlineData("def f(): 1; f", "Expected a parameter name but found ')' @1:7")]
    [InlineData("def f(a;): 1; 1", "Expected a parameter name but found ')' @1:9")]
    [InlineData("def f(if): 1; 1", "Expected a parameter name but found 'if' @1:7")]
    [InlineData("def f: 1", "Expected ';' but found end of program @1:9")]
    [InlineData("def f 1; 1", "Expected ':' but found '1' @1:7")]
    public void Definition_errors(string source, string expected) => Assert.Equal(expected, Error(source));

    [Theory]
    [InlineData(".", ".")]
    [InlineData("..", "recurse")]
    [InlineData("..?", "(try recurse)")]
    [InlineData("..|.a?", "(| recurse (try (index . \"a\")))")]
    [InlineData(".. .a?", "(try (index recurse \"a\"))")]
    [InlineData("..[0]?", "(try (index recurse 0))")]
    [InlineData(".a", "(index . \"a\")")]
    [InlineData(".a.b.\"c\"[0]", "(index (index (index (index . \"a\") \"b\") \"c\") 0)")]
    [InlineData(".a .b", "(index (index . \"a\") \"b\")")]
    [InlineData(".\"a\"", "(index . \"a\")")]
    [InlineData(". \"a\"", "(index . \"a\")")]
    [InlineData(".\"a\".\"b\"", "(index (index . \"a\") \"b\")")]
    [InlineData(".\"a\\(1)\"", "(index . (str \"a\" 1 \"\"))")]
    [InlineData(".a.[0]", "(index (index . \"a\") 0)")]
    [InlineData(".[0]", "(index . 0)")]
    [InlineData(". [0]", "(index . 0)")]
    [InlineData(".[1 + 1]", "(index . (+ 1 1))")]
    [InlineData(".[\"a\" + \"b\"]", "(index . (+ \"a\" \"b\"))")]
    [InlineData(".a[.k]", "(index (index . \"a\") (index . \"k\"))")]
    [InlineData(".[0, 1]", "(index . (, 0 1))")]
    [InlineData(".[]", "(iter .)")]
    [InlineData(".[]?", "(try (iter .))")]
    [InlineData(".a[]", "(iter (index . \"a\"))")]
    [InlineData(".[1:2]", "(slice . 1 2)")]
    [InlineData(".[1:]", "(slice . 1 _)")]
    [InlineData(".[:2]", "(slice . _ 2)")]
    [InlineData(".[1:$n]", "(slice . 1 $n)")]
    [InlineData(".[1:][0]", "(index (slice . 1 _) 0)")]
    [InlineData(".a?", "(try (index . \"a\"))")]
    [InlineData(".a??", "(try (try (index . \"a\")))")]
    [InlineData(".a?.b?", "(try (index (try (index . \"a\")) \"b\"))")]
    [InlineData(".a[]?.b", "(index (try (iter (index . \"a\"))) \"b\")")]
    [InlineData(".a? // 1", "(// (try (index . \"a\")) 1)")]
    [InlineData("$x[0]", "(index $x 0)")]
    [InlineData("$x.a", "(index $x \"a\")")]
    [InlineData("\"abc\"[1:]", "(slice \"abc\" 1 _)")]
    [InlineData("1[0]?", "(try (index 1 0))")]
    [InlineData("f.a", "(index f \"a\")")]
    [InlineData("if true then [1] else [2] end[0]", "(index (if true (array 1) (array 2)) 0)")]
    [InlineData("{\"end\":4} | .end", "(| (object (\"end\" 4)) (index . \"end\"))")]
    public void Paths_and_postfix(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Theory]
    [InlineData(".[:]", "Unexpected ']' @1:4")]
    [InlineData(".[1:2:3]", "Expected ']' but found ':' @1:6")]
    [InlineData(".a?//1", "Unexpected '?//': write '? //' for an optional followed by an alternative @1:3")]
    [InlineData("[.[]?//1]", "Unexpected '?//': write '? //' for an optional followed by an alternative @1:5")]
    [InlineData("..a", "Unexpected 'a' @1:3")]
    [InlineData("1 . 2", "Unexpected '.' @1:3")]
    public void Path_errors(string source, string expected) => Assert.Equal(expected, Error(source));

    [Theory]
    [InlineData("null", "null")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("1.5e3", "1500")]
    [InlineData("\"a\"", "\"a\"")]
    [InlineData("$__loc__", "$__loc__")]
    [InlineData("$ENV.PATH", "(index $ENV \"PATH\")")]
    [InlineData("[]", "(array)")]
    [InlineData("[1, 2]", "(array (, 1 2))")]
    [InlineData("[.[] | . * 2]", "(array (| (iter .) (* . 2)))")]
    [InlineData("(1)", "1")]
    [InlineData("((1, 2))", "(, 1 2)")]
    [InlineData("not", "not")]
    [InlineData("empty", "empty")]
    public void Terms(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Theory]
    [InlineData("\"a\\(1)b\\(2)c\"", "(str \"a\" 1 \"b\" 2 \"c\")")]
    [InlineData("\"\\(1, 2)\"", "(str \"\" (, 1 2) \"\")")]
    [InlineData("\"a\\(\"b\\(\"c\")\")\"", "(str \"a\" (str \"b\" \"c\" \"\") \"\")")]
    [InlineData("\"\\(1 + \"\\(2)\")\"", "(str \"\" (+ 1 (str \"\" 2 \"\")) \"\")")]
    [InlineData("\"\\((1))\"", "(str \"\" 1 \"\")")]
    [InlineData("@base64 \"x\\(.)y\"", "(str @base64 \"x\" . \"y\")")]
    [InlineData("@json \"x\\(\"y\")\"", "(str @json \"x\" \"y\" \"\")")]
    [InlineData("@base64 \"abc\"", "\"abc\"")]
    [InlineData("@base64", "@base64")]
    [InlineData("@foo", "@foo")]
    [InlineData("@foo \"x\"", "\"x\"")]
    [InlineData("@text | @json", "(| @text @json)")]
    public void Strings_and_formats(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Theory]
    [InlineData("\"\\()\"", "Unexpected ')' @1:4")]
    [InlineData("\"\\(1;2)\"", "Expected ')' but found ';' @1:5")]
    [InlineData("\"a\\(1", "Unterminated string @1:1")]
    public void String_errors(string source, string expected) => Assert.Equal(expected, Error(source));

    [Theory]
    [InlineData("if 1 then 2 else 3 end", "(if 1 2 3)")]
    [InlineData("if 1 then 2 end", "(if 1 2 .)")]
    [InlineData("if 1 then 2 elif 3 then 4 end", "(if 1 2 (if 3 4 .))")]
    [InlineData("if 1 then 2 elif 3 then 4 else 5 end", "(if 1 2 (if 3 4 5))")]
    [InlineData("if 1, 2 then 3 | 4 else 5, 6 end", "(if (, 1 2) (| 3 4) (, 5 6))")]
    [InlineData("[-if true then 1 else 2 end]", "(array (neg (if true 1 2)))")]
    public void Conditionals(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Theory]
    [InlineData("if 1 then 2 else 3", "Expected 'end' but found end of program @1:19")]
    [InlineData("if 1 2 end", "Expected 'then' but found '2' @1:6")]
    public void Conditional_errors(string source, string expected) => Assert.Equal(expected, Error(source));

    // Every key form and shorthand of spec §7.11.
    [Theory]
    [InlineData("{}", "(object)")]
    [InlineData("{a: 1}", "(object (\"a\" 1))")]
    [InlineData("{if: 1, then: 2}", "(object (\"if\" 1) (\"then\" 2))")]
    [InlineData("{\"a b\": 1}", "(object (\"a b\" 1))")]
    [InlineData("{\"a\\(1)\": 2}", "(object ((str \"a\" 1 \"\") 2))")]
    [InlineData("{@base64 \"a\\(1)\": 2}", "(object ((str @base64 \"a\" 1 \"\") 2))")]
    [InlineData("{$k: 1}", "(object ($k 1))")]
    [InlineData("{(.a, .b): 1}", "(object ((, (index . \"a\") (index . \"b\")) 1))")]
    [InlineData("{(\"a\"): 1}", "(object (\"a\" 1))")]
    [InlineData("{a}", "(object (\"a\" _))")]
    [InlineData("{if}", "(object (\"if\" _))")]
    [InlineData("{null}", "(object (\"null\" _))")]
    [InlineData("{\"a\"}", "(object (\"a\" _))")]
    [InlineData("{\"x\\(1, 2)\"}", "(object ((str \"x\" (, 1 2) \"\") _))")]
    [InlineData("{@base64 \"a\"}", "(object (\"a\" _))")]
    [InlineData("{$x}", "(object (\"x\" $x))")]
    [InlineData("{$__loc__}", "(object (\"__loc__\" $__loc__))")]
    [InlineData("{a, $b, \"c\": 3}", "(object (\"a\" _) (\"b\" $b) (\"c\" 3))")]
    [InlineData("{a: 1,}", "(object (\"a\" 1))")]
    [InlineData("{a: 1, b: 2}", "(object (\"a\" 1) (\"b\" 2))")]
    [InlineData("{a: (1, 2)}", "(object (\"a\" (, 1 2)))")]
    [InlineData("{a: 1 | 2 | 3}", "(object (\"a\" (| 1 (| 2 3))))")]
    [InlineData("{a: 1, b: 2 | . + 1}", "(object (\"a\" 1) (\"b\" (| 2 (+ . 1))))")]
    [InlineData("{a: 1 + 2}", "(object (\"a\" (+ 1 2)))")]
    [InlineData("{a: -1}", "(object (\"a\" (neg 1)))")]
    [InlineData("{a: .x = 1}", "(object (\"a\" (_assign (index . \"x\") 1)))")]
    [InlineData("{a: 1 // 2}", "(object (\"a\" (// 1 2)))")]
    [InlineData("{a: reduce .[] as $x (0; 1)}", "(object (\"a\" (reduce (iter .) $x 0 1)))")]
    [InlineData("{a: if . then 1 end}", "(object (\"a\" (if . 1 .)))")]
    [InlineData("{a: try 1 catch 2}", "(object (\"a\" (try 1 2)))")]
    [InlineData("{a: .b?}", "(object (\"a\" (try (index . \"b\"))))")]
    [InlineData("{a: {b: {c: 1}}}", "(object (\"a\" (object (\"b\" (object (\"c\" 1))))))")]
    [InlineData("{a::b: 1}", "(object (\"a::b\" 1))")]
    public void Object_construction(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Theory]
    [InlineData("{1: 2}", "May need parentheses around object key expression @1:2")]
    [InlineData("{1+2: 3}", "May need parentheses around object key expression @1:2")]
    [InlineData("{-1: 2}", "May need parentheses around object key expression @1:2")]
    [InlineData("{.a: 2}", "May need parentheses around object key expression @1:2")]
    [InlineData("{(1): 2}", "Cannot use number (1) as object key @1:3")]
    [InlineData("{(true): 2}", "Cannot use boolean (true) as object key @1:3")]
    [InlineData("{(null): 2}", "Cannot use null (null) as object key @1:3")]
    [InlineData("{(\"a\")}", "Expected ':' but found '}' @1:7")]
    [InlineData("{,}", "Unexpected ',' @1:2")]
    [InlineData("{a:1,,}", "Unexpected ',' @1:6")]
    [InlineData("{a 1}", "Expected ',' or '}' but found '1' @1:4")]
    [InlineData("{a: 1, b}c", "Unexpected 'c' @1:10")]
    [InlineData("{a: 1 as $x | $x}", "Expected ',' or '}' but found 'as' @1:7")]
    [InlineData("{a: 1 | 2 as $x | $x}", "Expected ',' or '}' but found 'as' @1:11")]
    [InlineData("{a: def f: 1; f}", "Unexpected 'def' @1:5")]
    [InlineData("{a: label $f | 1}", "Unexpected 'label' @1:5")]
    [InlineData("{a: 1, 2}", "May need parentheses around object key expression @1:8")]
    [InlineData("[1, 2,]", "Unexpected ']' @1:7")]
    public void Object_errors(string source, string expected) => Assert.Equal(expected, Error(source));

    [Theory]
    [InlineData(". as $x | $x", "(as . $x $x)")]
    [InlineData(". as [$a, $b] | 1", "(as . [$a $b] 1)")]
    [InlineData(". as [$a, [$b]] | 1", "(as . [$a [$b]] 1)")]
    [InlineData(". as {$a} | 1", "(as . {(\"a\" $a _)} 1)")]
    [InlineData(". as {$a: [$b]} | 1", "(as . {(\"a\" $a [$b])} 1)")]
    [InlineData(". as {a: $x, \"b\": $y, if: $z} | 1", "(as . {(\"a\" _ $x) (\"b\" _ $y) (\"if\" _ $z)} 1)")]
    [InlineData(". as {\"a\\(1)\": $x} | 1", "(as . {((str \"a\" 1 \"\") _ $x)} 1)")]
    [InlineData(". as {@base64 \"a\": $x} | 1", "(as . {(\"a\" _ $x)} 1)")]
    [InlineData(". as {(1 | tostring): $x} | 1", "(as . {((| 1 tostring) _ $x)} 1)")]
    [InlineData(". as {$then} | 1", "(as . {(\"then\" $then _)} 1)")]
    [InlineData(". as [$a, {b: $c}] | 1", "(as . [$a {(\"b\" _ $c)}] 1)")]
    [InlineData(". as [$a] ?// $a | $a", "(as . (?// [$a] $a) $a)")]
    [InlineData(". as [$a]?//[$b] ?// {$c} | 1", "(as . (?// [$a] [$b] {(\"c\" $c _)}) 1)")]
    [InlineData("reduce .[] as [$a] ?// $a (0; . + $a)", "(reduce (iter .) (?// [$a] $a) 0 (+ . $a))")]
    [InlineData("1 as $a::b | 1", "(as 1 $a::b 1)")]
    public void Patterns(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Theory]
    [InlineData(". as [] | 1", "Expected a pattern ($name, [...] or {...}) but found ']' @1:7")]
    [InlineData(". as {} | 1", "Unexpected '}' @1:7")]
    [InlineData(". as [$a,] | 1", "Expected a pattern ($name, [...] or {...}) but found ']' @1:10")]
    [InlineData(". as {(1): $x} | $x", "Cannot use number (1) as object key @1:8")]
    [InlineData(". as {(true): $x} | $x", "Cannot use boolean (true) as object key @1:8")]
    [InlineData(". as {a} | 1", "Expected ':' but found '}' @1:8")]
    [InlineData(". as 1 | 1", "Expected a pattern ($name, [...] or {...}) but found '1' @1:6")]
    [InlineData(". as $x 1", "Expected '|' but found '1' @1:9")]
    [InlineData("1 as $ x | $x", "Expected a variable name after '$' @1:6")]
    public void Pattern_errors(string source, string expected) => Assert.Equal(expected, Error(source));

    [Fact]
    public void Module_directives()
    {
        Assert.True(Parser.TryParseModule(
            "module {a: 1, \"b\": [1, \"x\", null, true, {c: false}], if: {},};\n"
            + "import \"m\" as m {search: \"./\"};\nimport \"d\" as $d;\ninclude \"i\";\ninclude \"j\" {k: []};\n"
            + "def f: 1; def g($x): $x;",
            out var program, out var error), error?.Message);
        Assert.Equal(
            "(module {\"a\":1,\"b\":[1,\"x\",null,true,{\"c\":false}],\"if\":{}}) (import \"m\" m {\"search\":\"./\"}) (import \"d\" $d) "
            + "(include \"i\") (include \"j\" {\"k\":[]}) (def f 1) (def g($x) $x)",
            Print(program));
        Assert.Null(program.Body);
        Assert.Equal(new Position(2, 1), program.Imports[0].Position);
    }

    [Fact]
    public void A_program_with_directives_has_a_body()
        => Assert.Equal("(module {}) (import \"a\" foo::bar) (include \"b\") (def f 1) f",
            Parse("module {}; import \"a\" as foo::bar; include \"b\"; def f: 1; f"));

    [Theory]
    [InlineData("module 1; 1", "Module metadata must be an object @1:8")]
    [InlineData("module [1]; 1", "Module metadata must be an object @1:8")]
    [InlineData("module .a; 1", "Module metadata must be constant @1:8")]
    [InlineData("module {a: 1+1}; 1", "Module metadata must be constant @1:8")]
    [InlineData("module {a: \"x\\(1)\"}; 1", "Module metadata must be constant @1:8")]
    [InlineData("module {(1): 1}; 1", "Module metadata must be constant @1:8")]
    [InlineData("import \"a\" as $d {x: [1, -2]}; 1", "Module metadata must be constant @1:18")]
    [InlineData("include \"a\" {a: -1}; 1", "Module metadata must be constant @1:13")]
    [InlineData("include \"a\" {a: 1 2}; 1", "Module metadata must be constant @1:13")]
    [InlineData("import \"a\\(1)\" as foo; 1", "Import path must be constant @1:8")]
    [InlineData("import a as foo; 1", "Expected a module path string but found 'a' @1:8")]
    [InlineData("import \"a\" as if; 1", "Expected a module name or a $name but found 'if' @1:15")]
    [InlineData("import \"a\"; 1", "Expected 'as' but found ';' @1:11")]
    [InlineData("import \"a\" as x 1", "Expected ';' but found '1' @1:17")]
    [InlineData("def f: 1; import \"a\" as foo; 1", "Unexpected 'import' @1:11")]
    [InlineData("1 | import \"a\" as foo; 1", "Unexpected 'import' @1:5")]
    [InlineData("module {\"a\": 1}; module {}; 1", "Unexpected 'module' @1:18")]
    [InlineData("import \"a\" as x; module {}; 1", "Unexpected 'module' @1:18")]
    public void Directive_errors(string source, string expected) => Assert.Equal(expected, Error(source));

    [Theory]
    [InlineData("def f: 1; f", "A module contains only definitions @1:11")]
    [InlineData("1", "A module contains only definitions @1:1")]
    [InlineData("def f: 1", "Expected ';' but found end of program @1:9")]
    public void Module_errors(string source, string expected)
    {
        Assert.False(Parser.TryParseModule(source, out _, out var error));
        Assert.Equal(expected, $"{error.Message} @{error.Line}:{error.Column}");
    }

    [Fact]
    public void An_empty_module_has_no_definitions()
    {
        Assert.True(Parser.TryParseModule("# nothing", out var program, out _));
        Assert.Empty(program.Definitions);
        Assert.Null(program.Body);
    }

    [Fact]
    public void Nodes_carry_the_position_of_their_token()
    {
        Assert.True(Parser.TryParse("1 +\n  .a[0] |\n\tf(2)", out var program, out _));
        var pipe = Assert.IsType<Devlooped.Core.Syntax.Pipe>(program.Body);
        Assert.Equal(new Position(2, 9), pipe.Position);
        var add = Assert.IsType<Binary>(pipe.Left);
        Assert.Equal(new Position(1, 3), add.Position);
        Assert.Equal(new Position(1, 1), add.Left.Position);
        var index = Assert.IsType<Devlooped.Core.Syntax.Index>(add.Right);
        Assert.Equal(new Position(2, 5), index.Position);
        Assert.Equal(new Position(2, 3), index.Target.Position);
        var call = Assert.IsType<Call>(pipe.Right);
        Assert.Equal(new Position(3, 2), call.Position);
        Assert.Equal(new Position(3, 4), call.Arguments[0].Position);
    }

    [Theory]
    [InlineData("1 )", "Unexpected ')' @1:3")]
    [InlineData("[1, 2", "Expected ']' but found end of program @1:6")]
    [InlineData("(1", "Expected ')' but found end of program @1:3")]
    [InlineData("f(1;", "Unexpected end of program @1:5")]
    [InlineData("|", "Unexpected '|' @1:1")]
    [InlineData("1 |", "Unexpected end of program @1:4")]
    [InlineData("1,\n\n  ,", "Unexpected ',' @3:3")]
    [InlineData("\"abc", "Unterminated string @1:1")]
    [InlineData("1 + \"a\\qb\"", "Invalid escape @1:7")]
    [InlineData("[invalid!!!", "Invalid character '!' @1:9")]
    [InlineData("then", "Unexpected 'then' @1:1")]
    [InlineData("1 as", "Expected a pattern ($name, [...] or {...}) but found end of program @1:5")]
    [InlineData("reduce . as $x (0)", "Expected ';' but found ')' @1:18")]
    public void Syntax_errors(string source, string expected) => Assert.Equal(expected, Error(source));
}