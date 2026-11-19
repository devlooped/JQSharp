// The rows of the theories of InterpreterTests.cs of the reference, copied by scripts/port-cases.mjs. Do not edit.

/* eslint-disable */
export const Signals_pass_through_every_construct = [
  ["[try (1, error(\"x\"), 3) catch .]", "[1,\"x\"]"],
  ["[.[] | try error catch .]", "error: \"Cannot iterate over null (null)\""],
  ["[.[]?]", "[]"],
  ["try error({}) catch .", "{}"],
  ["[(1, 2) | try error catch . + 10]", "[11,12]"],
  ["try (try error(\"a\") catch error(\"b\")) catch .", "\"b\""],
  ["\"x\" | try ((try . catch \"too much\") | error) catch \"right\"", "\"right\""],
  ["[try ((try (1,2) catch \"inner\") | if . == 2 then error(\"consumer\") else . end) catch (\"outer: \" + .)]", "[1,\"outer: consumer\"]"],
  ["try ((1, 2)? | error(tostring)) catch .", "\"1\""],
  ["try (([[1]] | .[] as [$a] ?// $a | $a) | error(tostring)) catch .", "\"1\""],
  ["[[[1],2][] as [$a] ?// $a | $a]", "[1,2]"],
  ["[[[1]][] as [$a] ?// $a | if ($a | type) == \"number\" then error(\"n\") else $a end]", "[[1]]"],
  ["try (1 as [$a] ?// {$a} | $a) catch .", "\"Cannot index number with string (\\\"a\\\")\""],
  ["[reduce ([1],[5]) as [$a] ?// $a (10; if $a == 1 then error(\"e\") else [., $a] end)]", "[[[10,[1]],5]]"],
  ["[foreach ([1]) as [$a] ?// $a (10; . + 1; if $a == 1 then ., error(\"x\") else [., $a] end)]", "[11,[11,[1]]]"],
  ["try (reduce ([1],[2]) as [$a] ?// $a (0; if $a == 1 then error(\"e\") else . + $a end)) catch .", "\"number (0) and array ([1]) cannot be added\""],
  ["[reduce ({\"a\":1}, [2]) as {a: $a} ?// [$a] (0; . + $a)]", "[3]"],
  ["try (foreach ([1]) as [$a] ?// $a (0; .; $a) | error(tostring)) catch .", "\"1\""],
  ["[foreach ([1], [2]) as [$a] ?// $a (0; . + (if $a == 2 then error(\"two\") else $a end); .)]", "error: \"number (1) and array ([2]) cannot be added\""],
  ["[5,6] | . as {(.[0] * 0 + 1): $v} | $v", "6"],
  ["{\"a\":1} | . as {(.a): $v} | $v", "error: \"Cannot index object with number (1)\""],
  ["null | . as {(0 + 1): $v, (\"b\"): $w} | [$v, $w]", "[null,null]"],
  ["try [(1, error(\"x\")) // 3] catch .", "\"x\""],
  ["[empty // 3, (false, null) // 4, (1, null, 2) // 5]", "[3,4,1,2]"],
  ["[label $f | def g: break $f; (label $f | 1, g, 2), 3]", "[1]"],
  ["[label $a | label $b | 1, break $a, 2]", "[1]"],
  ["label $out | try (0, break $out, 1) catch 99", "0"],
  ["def f: label $x | (1, break $x); [f, f]", "[1,1]"],
  ["reduce (1,2,3) as $x (0; empty)", "null"],
  ["[reduce empty as $x (1, 2; .)]", "[1,2]"],
  ["reduce (1,2) as $x (0; ., 10)", "10"],
  ["[foreach (1,2) as $x (0; (. + 1, . + 10); .)]", "[1,10,11,20]"],
  ["[foreach (1,2) as $x (0; empty; .)]", "[]"],
  ["[foreach (1,2) as $x (0, 1; . + $x)]", "[1,3,2,4]"],
  ["1, halt, 2", "1, halt 0"],
  ["try (\"bye\" | halt_error(5)) catch 0", "halt 5 \"bye\""],
  ["try halt_error(\"x\") catch .", "\"null (null) halt_error/1: number required\""],
] as const;

export const Consumers_that_stop_stop_their_producers = [
  ["first(range(1000000000))", "0"],
  ["[limit(3; repeat(1))]", "[1,1,1]"],
  ["isempty(repeat(1))", "false"],
  ["[label $f | 1, break $f, error(\"unreached\")]", "[1]"],
  ["first(range(10; infinite))", "10"],
  ["[limit(1; 1, error(\"unreached\"))]", "[1]"],
  ["[.[] | select(. > 1)] | first", "2"],
  ["any(range(1000000000); . > 2)", "true"],
] as const;

export const Paths_are_part_of_evaluation = [
  ["path(.)", "[]"],
  ["[path(.a.b, .c[1:2], .c[])]", "[[\"a\",\"b\"],[\"c\",{\"start\":1,\"end\":2}],[\"c\",0],[\"c\",1]]"],
  ["[path(.a[.k])]", "[[\"a\",\"x\"]]"],
  ["[path(.z?, (.c | try .[] catch .), (.a | .b))]", "[[\"z\"],[\"c\",0],[\"c\",1],[\"a\",\"b\"]]"],
  ["[path(if .k == \"x\" then .a else .c end)]", "[[\"a\"]]"],
  ["[path(.z // .a)]", "[[\"a\"]]"],
  ["[path(.k as $k | .a[$k])]", "[[\"a\",\"x\"]]"],
  ["[path(reduce (\"a\", \"b\") as $k (.; .[$k]))]", "[[\"a\",\"b\"]]"],
  ["[path(foreach (\"a\", \"b\") as $k (.; .[$k]))]", "[[\"a\"],[\"a\",\"b\"]]"],
  ["[path(label $x | .a, break $x)]", "[[\"a\"]]"],
  ["def f(p): p | .b; [path(f(.a))]", "[[\"a\",\"b\"]]"],
  ["[path(empty), path(getpath([\"a\", \"q\"]))]", "[[\"a\",\"q\"]]"],
  ["[path(first(.c[]), last(.c[]), limit(1; .c[]), skip(1; .c[]))]", "[[\"c\",0],[\"c\",1],[\"c\",0],[\"c\",1]]"],
  ["[path(.. | select(type == \"number\"))]", "[[\"a\",\"b\"],[\"c\",0],[\"c\",1]]"],
  ["[path(.c | recurse(if . == null then empty else .[0]? end))]", "[[\"c\"],[\"c\",0]]"],
  ["try path(1) catch .", "\"Invalid path expression with result 1\""],
  ["try path($v | .a) catch .", "\"Invalid path expression near attempt to access element \\\"a\\\" of null\""],
  ["try path(.k | ascii_upcase | .[]) catch .", "\"Invalid path expression near attempt to iterate through \\\"X\\\"\""],
  ["try path(.k + \"y\") catch .", "\"Invalid path expression with result \\\"xy\\\"\""],
] as const;

export const Assignment_works_through_the_prelude = [
  [".a = 1", "{\"a\":1,\"b\":[1,2]}"],
  [".b[] |= . * 10", "{\"a\":0,\"b\":[10,20]}"],
  [".b[1] += 5", "{\"a\":0,\"b\":[1,7]}"],
  ["[.a += (1, 2)] | map(.a)", "[1,2]"],
  [".c //= \"d\"", "{\"a\":0,\"b\":[1,2],\"c\":\"d\"}"],
  ["(.a, .z) = 9", "{\"a\":9,\"b\":[1,2],\"z\":9}"],
  [".b |= map(select(. > 1))", "{\"a\":0,\"b\":[2]}"],
  ["(.b[] | select(. == 1)) |= empty", "{\"a\":0,\"b\":[2]}"],
  ["del(.a, .b[0])", "{\"b\":[2]}"],
  ["to_entries", "[{\"key\":\"a\",\"value\":0},{\"key\":\"b\",\"value\":[1,2]}]"],
  ["with_entries(.value |= tostring)", "{\"a\":\"0\",\"b\":\"[1,2]\"}"],
  ["[paths]", "[[\"a\"],[\"b\"],[\"b\",0],[\"b\",1]]"],
  ["def _modify(p; f): \"mine\"; .a |= 1", "\"mine\""],
] as const;

export const Host_variables_and_ENV = [
  ["$x", "1"],
  ["[$x, $y]", "[1,\"two\"]"],
  ["$x as $x | $x + 1", "2"],
  ["$missing", "error: \"$missing is not defined\""],
  ["try $missing catch .", "\"$missing is not defined\""],
  ["$ENV.HOME, env.HOME", "\"/home\", \"/home\""],
  ["1 as $ENV | $ENV", "1"],
] as const;

export const The_call_depth_is_limited = [
  [1000],
  [50],
] as const;

export const Loop_natives_do_not_consume_the_depth = [
  ["[limit(5000; repeat(1))] | length", "5000"],
  ["[range(100000)] | length", "100000"],
  ["last(range(100000))", "99999"],
  ["0 | until(. >= 100000; . + 1)", "100000"],
  ["[0 | while(. < 100000; . + 1)] | length", "100000"],
  ["[skip(99998; range(100000))]", "[99998,99999]"],
] as const;

export const Outputs_come_in_the_order_of_the_spec = [
  ["[1, 2] | [.[] as $x | $x * 10]", "[10,20]"],
  ["[(1,2) + (10,20)]", "[11,12,21,22]"],
  ["[{a: (1,2), b: (3,4)}]", "[{\"a\":1,\"b\":3},{\"a\":1,\"b\":4},{\"a\":2,\"b\":3},{\"a\":2,\"b\":4}]"],
  ["[\"\\(1,2)-\\(3,4)\"]", "[\"1-3\",\"2-3\",\"1-4\",\"2-4\"]"],
  ["[range(0,1; 3,4)]", "[0,1,2,0,1,2,3,1,2,1,2,3]"],
  ["[.[(0,1)]]", "[1,2]"],
  ["[range(10)] | [.[(1,2):(3,4)] | length]", "[2,3,1,2]"],
  ["[(true, false) and (true, false)]", "[true,false,false]"],
  ["[(true, false) or (true, false)]", "[true,true,false]"],
  ["[-(1, 2)]", "[-1,-2]"],
  ["[{(\"a\", \"b\"): 1}]", "[{\"a\":1},{\"b\":1}]"],
  ["{\"x1\":\"a\",\"x2\":\"b\"} | [{\"x\\(1,2)\"}]", "[{\"x1\":\"a\"},{\"x2\":\"b\"}]"],
  ["[. as [$a, $b] | {$a, $b}]", "[{\"a\":1,\"b\":2}]"],
  [". as {(\"a\", \"b\"): $v} ?// $v | $v", "[1,2]"],
  ["{\"a\":1,\"b\":2} | [. as {(\"a\", \"b\"): $v} | $v]", "[1,2]"],
] as const;
