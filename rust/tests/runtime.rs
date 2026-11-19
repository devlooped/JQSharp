//! The runtime layer: what `dotnet/Tests/Core/Runtime` covers. Signals through every construct,
//! laziness, path mode, the depth limit and the host stack, the host interface; deep evaluations
//! on 1 MB and 256 KB stacks; the native `_modify`/`_assign` against the prelude's; every native
//! implemented.

mod support;

use std::collections::HashMap;
use std::sync::Arc;
use std::time::{Duration, Instant};

use jqlite::binding::{BoundProgram, Resolver};
use jqlite::builtins::{native_table, natives};
use jqlite::runtime::{EvaluationOptions, Interpreter, Outcome};
use jqlite::values::{JqObject, JqValue, value_text};

use support::{json, on_thread};

const MEGABYTE: usize = 1024 * 1024;
const SMALL: usize = 256 * 1024;

fn compile(program: &str, native_overrides: bool) -> BoundProgram {
    match Resolver::try_compile(program, native_overrides, None) {
        Ok(bound) => bound,
        Err(error) => panic!("{program}: {} at {}:{}", error.message, error.line, error.column),
    }
}

// The outputs, each as canonical text, then "error: <value>" or "halt …" when the evaluation did
// not complete.
fn run(program: &BoundProgram, input: JqValue, options: &EvaluationOptions<'_>, take: usize) -> Vec<String> {
    let mut outputs = Vec::new();
    let outcome = Interpreter::run(program, input, options, &mut |output| {
        outputs.push(value_text::to_json(&output));
        outputs.len() < take
    });
    match outcome {
        Outcome::Error(error) => outputs.push(format!("error: {}", value_text::to_json(&error))),
        Outcome::Halt { value, code } => {
            outputs.push(format!("halt {code}{}", value.map_or(String::new(), |value| format!(" {}", value_text::to_json(&value)))))
        }
        Outcome::Stopped if outputs.len() < take => outputs.push("stopped".to_owned()),
        _ => {}
    }
    outputs
}

// One line: the outputs separated by ", ".
fn line_with(program: &str, input: &str, options: &EvaluationOptions<'_>) -> String {
    run(&compile(program, true), json(input), options, usize::MAX).join(", ")
}

fn line(program: &str, input: &str) -> String {
    line_with(program, input, &EvaluationOptions::default())
}

// As `line`, on a thread with a stack of that size.
fn on_stack(stack: usize, program: &str, max_depth: usize) -> String {
    let program = program.to_owned();
    on_thread(stack, move || line_with(&program, "null", &EvaluationOptions { max_depth, ..EvaluationOptions::default() }))
}

fn depth(max_depth: usize) -> EvaluationOptions<'static> {
    EvaluationOptions { max_depth, ..EvaluationOptions::default() }
}

// ---- InterpreterTests ----

#[test]
fn signals_pass_through_every_construct() {
    let cases = [
        // try catches its body's errors, keeps the outputs before, and stops the body.
        ("[try (1, error(\"x\"), 3) catch .]", "[1,\"x\"]"),
        ("[.[] | try error catch .]", "error: \"Cannot iterate over null (null)\""),
        ("[.[]?]", "[]"),
        ("try error({}) catch .", "{}"),
        ("[(1, 2) | try error catch . + 10]", "[11,12]"),
        // Errors raised by the handler propagate.
        ("try (try error(\"a\") catch error(\"b\")) catch .", "\"b\""),
        // Errors of the consumer are not errors of the producer (§6.2), for try, ? and ?// alike.
        ("\"x\" | try ((try . catch \"too much\") | error) catch \"right\"", "\"right\""),
        ("[try ((try (1,2) catch \"inner\") | if . == 2 then error(\"consumer\") else . end) catch (\"outer: \" + .)]", "[1,\"outer: consumer\"]"),
        ("try ((1, 2)? | error(tostring)) catch .", "\"1\""),
        ("try (([[1]] | .[] as [$a] ?// $a | $a) | error(tostring)) catch .", "\"1\""),
        // ?// tries the next pattern when matching fails or the body raises an error; errors with the last propagate.
        ("[[[1],2][] as [$a] ?// $a | $a]", "[1,2]"),
        ("[[[1]][] as [$a] ?// $a | if ($a | type) == \"number\" then error(\"n\") else $a end]", "[[1]]"),
        ("try (1 as [$a] ?// {$a} | $a) catch .", "\"Cannot index number with string (\\\"a\\\")\""),
        // ?// in reduce and foreach: a retried alternative starts from the state before the source value (§7.8, §7.9).
        ("[reduce ([1],[5]) as [$a] ?// $a (10; if $a == 1 then error(\"e\") else [., $a] end)]", "[[[10,[1]],5]]"),
        ("[foreach ([1]) as [$a] ?// $a (10; . + 1; if $a == 1 then ., error(\"x\") else [., $a] end)]", "[11,[11,[1]]]"),
        ("try (reduce ([1],[2]) as [$a] ?// $a (0; if $a == 1 then error(\"e\") else . + $a end)) catch .", "\"number (0) and array ([1]) cannot be added\""),
        ("[reduce ({\"a\":1}, [2]) as {a: $a} ?// [$a] (0; . + $a)]", "[3]"),
        ("try (foreach ([1]) as [$a] ?// $a (0; .; $a) | error(tostring)) catch .", "\"1\""),
        (
            "[foreach ([1], [2]) as [$a] ?// $a (0; . + (if $a == 2 then error(\"two\") else $a end); .)]",
            "error: \"number (1) and array ([2]) cannot be added\"",
        ),
        // A computed pattern key indexes with the rules of §7.6, whatever its kind (§7.5).
        ("[5,6] | . as {(.[0] * 0 + 1): $v} | $v", "6"),
        ("{\"a\":1} | . as {(.a): $v} | $v", "error: \"Cannot index object with number (1)\""),
        ("null | . as {(0 + 1): $v, (\"b\"): $w} | [$v, $w]", "[null,null]"),
        // // propagates errors of its left side; its right side only when the left has no true output.
        ("try [(1, error(\"x\")) // 3] catch .", "\"x\""),
        ("[empty // 3, (false, null) // 4, (1, null, 2) // 5]", "[3,4,1,2]"),
        // break stops its own label instance only, lexically, and is not an error.
        ("[label $f | def g: break $f; (label $f | 1, g, 2), 3]", "[1]"),
        ("[label $a | label $b | 1, break $a, 2]", "[1]"),
        ("label $out | try (0, break $out, 1) catch 99", "0"),
        ("def f: label $x | (1, break $x); [f, f]", "[1,1]"),
        // reduce and foreach with no and several outputs of the update.
        ("reduce (1,2,3) as $x (0; empty)", "null"),
        ("[reduce empty as $x (1, 2; .)]", "[1,2]"),
        ("reduce (1,2) as $x (0; ., 10)", "10"),
        ("[foreach (1,2) as $x (0; (. + 1, . + 10); .)]", "[1,10,11,20]"),
        ("[foreach (1,2) as $x (0; empty; .)]", "[]"),
        ("[foreach (1,2) as $x (0, 1; . + $x)]", "[1,3,2,4]"),
        // halt and halt_error stop everything; try does not catch them.
        ("1, halt, 2", "1, halt 0"),
        ("try (\"bye\" | halt_error(5)) catch 0", "halt 5 \"bye\""),
        ("try halt_error(\"x\") catch .", "\"null (null) halt_error/1: number required\""),
    ];
    for (program, expected) in cases {
        assert_eq!(line(program, "null"), expected, "{program}");
    }
}

#[test]
fn consumers_that_stop_stop_their_producers() {
    let cases = [
        ("first(range(1000000000))", "0"),
        ("[limit(3; repeat(1))]", "[1,1,1]"),
        ("isempty(repeat(1))", "false"),
        ("[label $f | 1, break $f, error(\"unreached\")]", "[1]"),
        ("first(range(10; infinite))", "10"),
        ("[limit(1; 1, error(\"unreached\"))]", "[1]"),
        ("[.[] | select(. > 1)] | first", "2"),
        ("any(range(1000000000); . > 2)", "true"),
    ];
    for (program, expected) in cases {
        let watch = Instant::now();
        assert_eq!(line(program, "[1,2,3]"), expected, "{program}");
        assert!(watch.elapsed() < Duration::from_secs(5), "{program} took {:?}", watch.elapsed());
    }
}

#[test]
fn the_host_stops_the_stream() {
    assert_eq!(run(&compile("range(1000000000)", true), JqValue::Null, &EvaluationOptions::default(), 3), ["0", "1", "2"]);
}

#[test]
fn paths_are_part_of_evaluation() {
    let cases = [
        // §8.1, construct by construct.
        ("path(.)", "[]"),
        ("[path(.a.b, .c[1:2], .c[])]", "[[\"a\",\"b\"],[\"c\",{\"start\":1,\"end\":2}],[\"c\",0],[\"c\",1]]"),
        ("[path(.a[.k])]", "[[\"a\",\"x\"]]"),
        ("[path(.z?, (.c | try .[] catch .), (.a | .b))]", "[[\"z\"],[\"c\",0],[\"c\",1],[\"a\",\"b\"]]"),
        ("[path(if .k == \"x\" then .a else .c end)]", "[[\"a\"]]"),
        ("[path(.z // .a)]", "[[\"a\"]]"),
        ("[path(.k as $k | .a[$k])]", "[[\"a\",\"x\"]]"),
        ("[path(reduce (\"a\", \"b\") as $k (.; .[$k]))]", "[[\"a\",\"b\"]]"),
        ("[path(foreach (\"a\", \"b\") as $k (.; .[$k]))]", "[[\"a\"],[\"a\",\"b\"]]"),
        ("[path(label $x | .a, break $x)]", "[[\"a\"]]"),
        ("def f(p): p | .b; [path(f(.a))]", "[[\"a\",\"b\"]]"),
        ("[path(empty), path(getpath([\"a\", \"q\"]))]", "[[\"a\",\"q\"]]"),
        ("[path(first(.c[]), last(.c[]), limit(1; .c[]), skip(1; .c[]))]", "[[\"c\",0],[\"c\",1],[\"c\",0],[\"c\",1]]"),
        ("[path(.. | select(type == \"number\"))]", "[[\"a\",\"b\"],[\"c\",0],[\"c\",1]]"),
        ("[path(.c | recurse(if . == null then empty else .[0]? end))]", "[[\"c\"],[\"c\",0]]"),
        // The three path errors.
        ("try path(1) catch .", "\"Invalid path expression with result 1\""),
        ("try path($v | .a) catch .", "\"Invalid path expression near attempt to access element \\\"a\\\" of null\""),
        ("try path(.k | ascii_upcase | .[]) catch .", "\"Invalid path expression near attempt to iterate through \\\"X\\\"\""),
        ("try path(.k + \"y\") catch .", "\"Invalid path expression with result \\\"xy\\\"\""),
        // The output of a native that takes closures and does not pass paths has no path (§8.1).
        (
            "try path(.k | match(\"x\") | .captures) catch .",
            "\"Invalid path expression near attempt to access element \\\"captures\\\" of {\\\"offset\\\":0,\\\"length\\\":1,\\\"s...}\"",
        ),
    ];
    let variables = HashMap::from([("v".to_owned(), JqValue::Null)]);
    let options = EvaluationOptions { variables: Some(&variables), ..EvaluationOptions::default() };
    for (program, expected) in cases {
        assert_eq!(line_with(program, "{\"a\":{\"b\":0},\"k\":\"x\",\"c\":[1,2]}", &options), expected, "{program}");
    }
}

#[test]
fn assignment_works_through_the_prelude() {
    let cases = [
        (".a = 1", "{\"a\":1,\"b\":[1,2]}"),
        (".b[] |= . * 10", "{\"a\":0,\"b\":[10,20]}"),
        (".b[1] += 5", "{\"a\":0,\"b\":[1,7]}"),
        ("[.a += (1, 2)] | map(.a)", "[1,2]"),
        (".c //= \"d\"", "{\"a\":0,\"b\":[1,2],\"c\":\"d\"}"),
        ("(.a, .z) = 9", "{\"a\":9,\"b\":[1,2],\"z\":9}"),
        (".b |= map(select(. > 1))", "{\"a\":0,\"b\":[2]}"),
        ("(.b[] | select(. == 1)) |= empty", "{\"a\":0,\"b\":[2]}"),
        ("del(.a, .b[0])", "{\"b\":[2]}"),
        ("to_entries", "[{\"key\":\"a\",\"value\":0},{\"key\":\"b\",\"value\":[1,2]}]"),
        ("with_entries(.value |= tostring)", "{\"a\":\"0\",\"b\":\"[1,2]\"}"),
        ("[paths]", "[[\"a\"],[\"b\"],[\"b\",0],[\"b\",1]]"),
        ("def _modify(p; f): \"mine\"; .a |= 1", "\"mine\""),
    ];
    for (program, expected) in cases {
        assert_eq!(line(program, "{\"a\":0,\"b\":[1,2]}"), expected, "{program}");
    }
}

#[test]
fn host_variables_and_env() {
    let cases = [
        ("$x", "1"),
        ("[$x, $y]", "[1,\"two\"]"),
        ("$x as $x | $x + 1", "2"),
        ("$missing", "error: \"$missing is not defined\""),
        ("try $missing catch .", "\"$missing is not defined\""),
        ("$ENV.HOME, env.HOME", "\"/home\", \"/home\""),
        ("1 as $ENV | $ENV", "1"),
    ];
    let variables = HashMap::from([("x".to_owned(), JqValue::Number(1.0)), ("y".to_owned(), JqValue::from_text("two"))]);
    let environment = || JqValue::Object(JqObject::empty().set(Arc::new("HOME".to_owned()), JqValue::from_text("/home")));
    let options = EvaluationOptions { variables: Some(&variables), environment: Some(&environment), ..EvaluationOptions::default() };
    for (program, expected) in cases {
        assert_eq!(line_with(program, "null", &options), expected, "{program}");
    }
}

#[test]
fn one_program_runs_concurrently_with_different_variables() {
    let program = compile("def f: if . == 0 then $n else (. - 1 | f) end; [range(200) | {a: f}] | map(.a) | add", true);
    let results: Vec<String> = std::thread::scope(|scope| {
        let handles: Vec<_> = (0..64)
            .map(|i| {
                let program = &program;
                scope.spawn(move || {
                    let variables = HashMap::from([("n".to_owned(), JqValue::Number(i as f64))]);
                    let options = EvaluationOptions { variables: Some(&variables), ..EvaluationOptions::default() };
                    run(program, JqValue::Number(50.0), &options, usize::MAX).join(", ")
                })
            })
            .collect();
        handles.into_iter().map(|handle| handle.join().expect("the thread ends")).collect()
    });
    for (i, result) in results.iter().enumerate() {
        assert_eq!(*result, (200 * i).to_string());
    }
}

#[test]
fn the_call_depth_is_limited() {
    for max_depth in [1000, 50] {
        let count = "def f: if . == 0 then 0 else (. - 1 | f) end; ";
        // n | f makes n + 1 calls.
        assert_eq!(on_stack(MEGABYTE, &format!("{count}{} | f", max_depth - 1), max_depth), "0");
        assert_eq!(on_stack(MEGABYTE, &format!("{count}{max_depth} | f"), max_depth), "error: \"Maximum call depth exceeded\"");
        assert_eq!(on_stack(MEGABYTE, &format!("try ({count}100000 | f) catch ."), max_depth), "\"Maximum call depth exceeded\"");
    }
}

#[test]
fn a_single_call_has_ended_when_its_output_is_used() {
    // 20 | fib makes 21,891 calls, 20 deep.
    let fib = "def fib: if . < 2 then . else (. - 1 | fib) + (. - 2 | fib) end; 20 | fib";
    assert_eq!(line_with(fib, "null", &depth(20)), "6765");
    assert_eq!(line_with(fib, "null", &depth(19)), "error: \"Maximum call depth exceeded\"");
    assert_eq!(line_with("def fib($n): if $n < 2 then $n else fib($n - 1) + fib($n - 2) end; fib(20)", "null", &depth(20)), "6765");
    // A call that is not single stays active while its outputs are consumed: the two calls nest.
    let many = "def g: if . < 2 then ., empty else (. - 1 | g) + (. - 2 | g) end; ";
    assert_eq!(line(&format!("{many}10 | g"), "null"), "55");
    assert_eq!(line(&format!("{many}15 | g"), "null"), "error: \"Maximum call depth exceeded\"");
}

// A recursion of single calls 1,000 deep runs through eval_one: the limit, never the host stack.
#[test]
fn a_deep_recursion_of_single_calls_meets_the_limit() {
    for stack in [MEGABYTE, SMALL] {
        for native_overrides in [true, false] {
            let result = on_thread(stack, move || {
                run(&compile("def f: 1 + f; try f catch .", native_overrides), JqValue::Null, &EvaluationOptions::default(), usize::MAX).join(", ")
            });
            assert_eq!(result, "\"Maximum call depth exceeded\"");
        }
    }
}

#[test]
fn tail_recursion_reaches_the_limit_without_the_host_stack() {
    assert_eq!(on_stack(MEGABYTE, "def f: if . == 0 then 0 else (. - 1 | f) end; 100000 | f", 1000), "error: \"Maximum call depth exceeded\"");
}

#[test]
fn loop_natives_do_not_consume_the_depth() {
    let cases = [
        // Natives marked *loop* do not consume the call depth.
        ("[limit(5000; repeat(1))] | length", "5000"),
        ("[range(100000)] | length", "100000"),
        ("last(range(100000))", "99999"),
        ("0 | until(. >= 100000; . + 1)", "100000"),
        ("[0 | while(. < 100000; . + 1)] | length", "100000"),
        ("[skip(99998; range(100000))]", "[99998,99999]"),
    ];
    for (program, expected) in cases {
        assert_eq!(on_stack(MEGABYTE, program, 10), expected, "{program}");
    }
}

#[test]
fn outputs_come_in_the_order_of_the_spec() {
    let cases = [
        ("[1, 2] | [.[] as $x | $x * 10]", "[10,20]"),
        ("[(1,2) + (10,20)]", "[11,12,21,22]"),
        ("[{a: (1,2), b: (3,4)}]", "[{\"a\":1,\"b\":3},{\"a\":1,\"b\":4},{\"a\":2,\"b\":3},{\"a\":2,\"b\":4}]"),
        ("[\"\\(1,2)-\\(3,4)\"]", "[\"1-3\",\"2-3\",\"1-4\",\"2-4\"]"),
        ("[range(0,1; 3,4)]", "[0,1,2,0,1,2,3,1,2,1,2,3]"),
        ("[.[(0,1)]]", "[1,2]"),
        ("[range(10)] | [.[(1,2):(3,4)] | length]", "[2,3,1,2]"),
        ("[(true, false) and (true, false)]", "[true,false,false]"),
        ("[(true, false) or (true, false)]", "[true,true,false]"),
        ("[-(1, 2)]", "[-1,-2]"),
        ("[{(\"a\", \"b\"): 1}]", "[{\"a\":1},{\"b\":1}]"),
        ("{\"x1\":\"a\",\"x2\":\"b\"} | [{\"x\\(1,2)\"}]", "[{\"x1\":\"a\"},{\"x2\":\"b\"}]"),
        ("[. as [$a, $b] | {$a, $b}]", "[{\"a\":1,\"b\":2}]"),
        (". as {(\"a\", \"b\"): $v} ?// $v | $v", "[1,2]"),
        ("{\"a\":1,\"b\":2} | [. as {(\"a\", \"b\"): $v} | $v]", "[1,2]"),
    ];
    for (program, expected) in cases {
        assert_eq!(line(program, "[1,2]"), expected, "{program}");
    }
}

// A value owned once is changed in place and a shared one is copied: the results are the same.
#[test]
fn values_a_program_still_holds_never_change() {
    let cases = [
        (". as $x | (. + [9]), $x", "[1,2,9], [1,2]"),
        (". as $x | (.[0] = 9), $x", "[9,2], [1,2]"),
        (". as $x | (.[0] |= . + 9), $x", "[10,2], [1,2]"),
        (". as $x | (.[] |= . + 9), $x", "[10,11], [1,2]"),
        (". as $x | (. - [1]), $x", "[2], [1,2]"),
        (". as $x | sort_by(-.), $x", "[2,1], [1,2]"),
        (". as $x | del(.[0]), $x", "[2], [1,2]"),
        (". as $x | .[1:], $x", "[2], [1,2]"),
        ("[., .] | (.[0][0] = 9), .", "[[9,2],[1,2]], [[1,2],[1,2]]"),
        ("{a: .} | . as $o | (.a[0] = 5 | .b = 1), $o", "{\"a\":[5,2],\"b\":1}, {\"a\":[1,2]}"),
        ("{a: .} | . as $o | (.a += [3]), $o", "{\"a\":[1,2,3]}, {\"a\":[1,2]}"),
        ("{a: .} | . as $o | (. * {a: 1, c: {d: 2}}), $o", "{\"a\":1,\"c\":{\"d\":2}}, {\"a\":[1,2]}"),
        ("\"ab\" as $s | $s | (. + \"c\" | ascii_upcase), $s", "\"ABC\", \"ab\""),
        ("[., .] | map(. + [3]) | ., (.[0] | setpath([0]; 0))", "[[1,2,3],[1,2,3]], [0,2,3]"),
        ("reduce (1, 2) as $i ([.]; . + [.[0] + [$i]])", "[[1,2],[1,2,1],[1,2,2]]"),
        ("[foreach (1, 2) as $i (.; . + [$i]; .)]", "[[1,2,1],[1,2,1,2]]"),
        ("[limit(2; repeat(. + [0]))]", "[[1,2,0],[1,2,0]]"),
        ("[.[] as $x | [$x] | . + [$x]] | ., .[0]", "[[1,1],[2,2]], [1,1]"),
    ];
    for (program, expected) in cases {
        for native_overrides in [true, false] {
            assert_eq!(run(&compile(program, native_overrides), json("[1,2]"), &EvaluationOptions::default(), usize::MAX).join(", "), expected, "{program}");
        }
    }
}

// ---- StackGuardTests ----
//
// MaxDepth is the only recursion limit a program meets (spec §11): on a small host stack, deep
// evaluations continue on a fresh stack (stack_guard). Each case runs on a thread with a 1 MB or
// a 256 KB stack, in whatever build the tests run.

#[test]
fn nesting_to_the_parser_limit_works() {
    let nested = "[".repeat(1000) + "1" + &"]".repeat(1000);
    let objects = "{\"a\":".repeat(1000) + "1" + &"}".repeat(1000);
    // opening, closing, and the output for 1000 levels around "1"
    let cases: [(&str, &str, &str); 10] = [
        ("[", "]", &nested),
        ("{a: ", "}", &objects),
        (".[", "]", "error: \"Cannot index null with null (null)\""),
        ("\"\\(", ")\"", "\"1\""),
        ("reduce . as $x (", ";.)", "1"),
        ("[1 + ", "]", "error"),
        ("1 + (", ")", "1001"),
        ("[label $x | ", "]", &nested),
        ("if . then 1 else ", " end", "1"),
        ("try ", "", "1"),
    ];
    for (open, close, expected) in cases {
        for stack in [MEGABYTE, SMALL] {
            let result = on_stack(stack, &(open.repeat(1000) + "1" + &close.repeat(1000)), 1000);
            if expected == "error" {
                assert!(result.starts_with("error: \"number (1) and array ([2]) cannot be added"), "{open}: {result}");
            } else {
                assert_eq!(result, expected, "{open}");
            }
        }
    }
}

#[test]
fn recursion_reaches_exactly_max_depth() {
    // program with N, stack
    let cases = [
        ("def f: if . == 0 then 0 else (. - 1 | f) + 1 end; N | f", MEGABYTE),
        ("def f: if . == 0 then 0 else (. - 1 | f) + 1 end; N | f", SMALL),
        ("def f: if . == 0 then 0 else [. - 1 | f] | length end; N | f", SMALL),
        ("def f: if . == 0 then 0 else ((. - 1, . - 1) | f) end; label $out | (N | f) | ., break $out", MEGABYTE),
        ("def f: if . == 0 then 0 else ((. - 1, . - 1) | f) end; label $out | (N | f) | ., break $out", SMALL),
        ("def f(g): if . == 0 then g else (. - 1 | f(g)) + 0 end; N | f(0)", SMALL),
    ];
    for (program, stack) in cases {
        for max_depth in [1000, 5000] {
            // N | f makes N + 1 calls.
            let deepest = on_stack(stack, &program.replace('N', &(max_depth - 1).to_string()), max_depth);
            assert!(!deepest.contains("error"), "{program}: {deepest}");
            assert_eq!(on_stack(stack, &program.replace('N', &max_depth.to_string()), max_depth), "error: \"Maximum call depth exceeded\"", "{program}");
        }
    }
}

#[test]
fn hops_nest() {
    assert_eq!(on_stack(SMALL, "def f: if . == 0 then 0 else (. - 1 | f) + 1 end; 19999 | f", 20000), "19999");
}

// A depth limit far above the default: the host stack still never fails.
#[test]
fn a_recursion_a_hundred_thousand_calls_deep() {
    assert_eq!(on_stack(SMALL, "def f: if . == 0 then 0 else (. - 1 | f) + 1 end; 99999 | f", 100_000), "99999");
    assert_eq!(on_stack(SMALL, "def f: if . == 0 then [] else [. - 1 | f] end; 99999 | f | getpath([range(99999) | 0])", 100_000), "[]");
}

#[test]
fn a_stop_of_the_host_comes_back_across_hops() {
    for stack in [MEGABYTE, SMALL] {
        let outputs = on_thread(stack, || {
            let program = compile("def f: if . == 0 then (1, 2, 3) else (. - 1 | f) + 0 end; 900 | f", true);
            run(&program, JqValue::Null, &EvaluationOptions::default(), 1)
        });
        assert_eq!(outputs, ["1"]);
    }
}

#[test]
fn an_error_comes_back_across_hops() {
    for stack in [MEGABYTE, SMALL] {
        assert_eq!(on_stack(stack, "try (def f: if . == 0 then error(\"deep\") else (. - 1 | f) + 1 end; 900 | f) catch .", 1000), "\"deep\"");
    }
}

// ---- AssignTests ----
//
// The native `_modify` and `_assign` against their prelude definitions (§8.3): each program is
// compiled twice, with the native overrides and without them, and both must give the same
// outputs, errors included.

#[test]
fn native_and_prelude_agree() {
    let cases = [
        // parent then child, child then parent, the same path twice
        ("(.a, .a.b) |= {b: 1}", "{\"a\":{\"b\":0}}"),
        ("(.a.b, .a) |= {x: .}", "{\"a\":{\"b\":0}}"),
        ("(.a, .a) |= . + 1", "{\"a\":1}"),
        ("(.a[0], .a, .a[0]) |= [.]", "{\"a\":[1,2]}"),
        // every path of a value, parents first
        (".. |= (if type == \"number\" then . + 1 else . end)", "{\"a\":[1,{\"b\":2}],\"c\":3}"),
        ("[..] as $all | .. |= length", "[[1,[2]],{\"x\":3}]"),
        // updates that return the input itself, or structures holding it twice
        (".a |= ., .b |= [., .]", "{\"a\":[1],\"b\":{\"c\":2}}"),
        ("(.a, .a[0]) |= [., .]", "{\"a\":[1]}"),
        (". as $in | .a |= $in", "{\"a\":1}"),
        // empty updates (deletions at the end) mixed with updates
        ("(.[] | select(. >= 2)) |= empty", "[1,5,3,0,7]"),
        (".[] |= (if . % 2 == 0 then empty else . * 10 end)", "[1,2,3,4,5]"),
        ("(.a, .b, .c) |= (if . == null then empty else . + 1 end)", "{\"a\":1,\"c\":3}"),
        ("(.a.x, .a) |= empty", "{\"a\":{\"x\":1}}"),
        ("to_entries |= map(select(.key != \"b\")) | from_entries", "{\"a\":1,\"b\":2}"),
        // errors midway, and their order
        ("(.a, .b, .c) |= (if . == 2 then error(\"two\") else . end)", "{\"a\":1,\"b\":2,\"c\":3}"),
        ("(.a, .b.x) |= 1", "{\"a\":0,\"b\":5}"),
        ("(.a, 1) |= 1", "{\"a\":0}"),
        (".[0] |= 1", "{}"),
        ("(.a, .[0]) |= 1", "null"),
        ("(.a | .b) |= 1", "{\"a\":[1]}"),
        // _assign: several right-hand outputs, none, and paths of the right side's input
        ("(.a, .b) = (1, 2)", "{}"),
        (".a = empty", "{}"),
        ("empty = 1", "{\"a\":0}"),
        (".a = .b", "{\"a\":0,\"b\":1}"),
        ("(.a, .a.b) = 1", "{}"),
        (".a[1:3] = [\"x\"]", "{\"a\":[0,1,2,3]}"),
        // slices, paths through null, negative indices, arithmetic updates
        (".[1:3] |= map(. * 10)", "[0,1,2,3,4]"),
        ("(.[1:3], .[0]) |= (if type == \"array\" then [] else -1 end)", "[0,1,2,3,4]"),
        (".[2:4][0] |= 9", "[0,1,2,3,4]"),
        (".[1:2] |= \"x\"", "\"abc\""),
        (".a.b[2].c |= 1", "null"),
        (".[-1] |= . + 1", "[1,2,3]"),
        (".[-5] |= 1", "[1,2,3]"),
        (".[nan] |= 1", "[1]"),
        (".[3] |= 1", "[1]"),
        (".[536870912] |= 1", "[]"),
        (".[] += 1, .a -= 1, .b *= 2, .c /= 2, .d %= 2, .e //= 7", "{\"a\":1,\"b\":2,\"c\":4,\"d\":5,\"e\":null}"),
        ("map_values(. + 1)", "{\"a\":1,\"b\":2}"),
        ("walk(if type == \"number\" then . + 1 else . end)", "[1,[2,{\"a\":3}]]"),
        ("INDEX(.id)", "[{\"id\":1},{\"id\":2}]"),
        ("sub(\"b\"; \"x\")", "\"abc\""),
        ("path(.a |= 1)", "{\"a\":0}"),
        ("path(empty = 1)", "{\"a\":0}"),
        ("[path(.a = 1)]", "{\"a\":0}"),
        (".a |= (1, 2)", "{\"a\":0}"),
        ("(.a, .b) |= (.x |= 1)", "{\"a\":{},\"b\":null}"),
        // new keys and elements added to the value itself, then replaced, nested or failing below
        ("(.c, .a, .d, .c) |= length", "{\"a\":\"xy\",\"b\":1}"),
        (".c.d[1] = 1, .[\"c\", \"d\"] = 2", "{\"a\":0}"),
        ("(.[2], .[3], .[0], .[3][\"x\"]) |= 7", "[0,1]"),
        ("(.c, .c[-1]) |= 1", "{\"a\":0}"),
        (".c[-1] |= 1", "{\"a\":0}"),
        (".[2].x = 1, .[-0.5] = 1, .[2.5] = 1", "[0,1]"),
        ("reduce range(20) as $i ({}; .[\"k\\($i)\"] = $i) | ., .k3 = 0", "null"),
        ("reduce range(20) as $i ([]; .[$i] |= $i) | ., .[1] = 0", "null"),
        // the paths that are one expression (§7.7): the path is taken before anything is written
        (".a |= empty", "{\"a\":1,\"b\":2}"),
        (".a.b |= empty", "null"),
        (".a.b |= 1", "{\"a\":5}"),
        (".a.b = 1", "{\"a\":5}"),
        (".[1.5] |= . + 1", "[1,2,3]"),
        (".[-1] = 9", "[1,2,3]"),
        (".[-9] = 9", "[1,2,3]"),
        (". |= empty", "[1]"),
        (". |= . + 1", "1"),
        (". = 2", "1"),
        (".a |= error(\"x\")", "{\"a\":1}"),
        (".a |= (.b |= (.c = 1))", "{\"a\":{\"b\":{}}}"),
        (".a[1:] |= map(. + 1)", "{\"a\":[1,2,3]}"),
        (".a[1:] = 5", "{\"a\":[1,2,3]}"),
        (".a.b.c += 1 | .a.b.d -= 1 | del(.a.b.c)", "{\"a\":{\"b\":{\"c\":1,\"d\":1}}}"),
        ("first(.a, .b) |= 9", "{\"a\":1,\"b\":2}"),
        ("(1 | .a) |= 9", "{}"),
        ("path(. |= 1)", "5"),
        ("[path(. = 1)]", "5"),
        // several sets of one path fail: the innermost one raises
        (".[nan][-9] |= 1", "[[1]]"),
        ("(.[nan][-9], .[0]) |= 1", "[[1]]"),
        (".a[-5].x |= 1", "{\"a\":[1]}"),
        ("setpath([nan, -9]; 1)", "[[1]]"),
    ];
    for (program, input) in cases {
        let native = run(&compile(program, true), json(input), &EvaluationOptions::default(), usize::MAX).join(", ");
        let prelude = run(&compile(program, false), json(input), &EvaluationOptions::default(), usize::MAX).join(", ");
        assert_eq!(native, prelude, "{program} on {input}");
    }
}

#[test]
fn a_loop_binds_each_value_anew() {
    // The frame of `$x` is used again from one value to the next, unless something still holds it.
    assert_eq!(support::outputs("[range(3) as $x | reduce range(2) as $y (0; . + $x * 10 + $y)]", "null"), ["[1,21,41]"]);
    assert_eq!(support::outputs("[foreach (1, 2, 3) as $x (0; . + $x; [$x, .])]", "null"), ["[[1,1],[2,3],[3,6]]"]);
    assert_eq!(support::outputs("[.[] as $x | def f: $x * 2; f]", "[1,2,3]"), ["[2,4,6]"]);
    assert_eq!(support::outputs("[range(3) as $x | [limit(2; repeat($x))]]", "null"), ["[[0,0],[1,1],[2,2]]"]);
    assert_eq!(support::outputs("[range(2) as $x | range(2) as $y | [$x, $y]]", "null"), ["[[0,0],[0,1],[1,0],[1,1]]"]);
}

#[test]
fn iterating_what_cannot_be_iterated_under_a_try_yields_nothing() {
    // `.[]?` does not write the error it would drop; the results are the same in both modes.
    assert_eq!(support::outputs("[.[] | .[]?]", "[1,null,\"a\",[2],{\"a\":3}]"), ["[2,3]"]);
    assert_eq!(support::outputs("[paths]", "[1,[2]]"), ["[[0],[1],[1,0]]"]);
    assert_eq!(support::outputs("[.[] | try .[] catch \"no\"]", "[1,[2]]"), ["[\"no\",2]"]);
    assert_eq!(support::outputs("[path(.[] | .[]?)]", "[1,[2]]"), ["[[1,0]]"]);
}

// The limits of a pattern are the translation's (spec §11, regex_syntax.rs), and the corpus has
// their boundaries. What is this port's: the engine compiles by recursion, so the deepest patterns
// run on small stacks, under a recursion of the program too; and the engine's size limit
// (regex_engine.rs) is above what the largest patterns compile to.

const TOO_LARGE: &str = "\"Regex failure: the pattern is too large\"";

// test(pattern) on "a", or the error it raises; the pattern is the input of the program.
fn tested(pattern: String, program: &'static str, stack: usize) -> String {
    on_thread(stack, move || {
        let input = value_text::to_json(&JqValue::from_string(pattern));
        support::outputs(program, &input).join(" ")
    })
}

#[test]
fn a_pattern_nests_a_hundred_groups_on_a_small_stack() {
    const TEST: &str = ". as $re | \"a\" | try test($re) catch .";
    let nested = |levels: usize, open: &str, close: &str| format!("{}a{}", open.repeat(levels), close.repeat(levels));
    for (open, close) in [("(", ")"), ("(?:", ")")] {
        for stack in [MEGABYTE, SMALL] {
            assert_eq!(tested(nested(100, open, close), TEST, stack), "true", "{open} 100");
            assert_eq!(tested(nested(101, open, close), TEST, stack), TOO_LARGE, "{open} 101");
        }
    }
    // Levels that are an alternative, a sequence, a repetition and a group at once: what takes the
    // most stack in the engine.
    assert_eq!(tested(nested(100, "(b|c", ")*?d|e"), TEST, SMALL), "false");
    assert_eq!(tested(nested(101, "(b|c", ")*?d|e"), TEST, SMALL), TOO_LARGE);
    assert_eq!(tested(format!("{}a{}", "(".repeat(100_000), ")".repeat(100_000)), TEST, SMALL), TOO_LARGE);
    // The limit is on the groups open at once, not on their number.
    assert_eq!(tested("(?:a)".repeat(5000), ". as $re | \"a\" * 5000 | test($re)", MEGABYTE), "true");
    assert_eq!(tested("(a)".repeat(300), ". as $re | \"a\" * 300 | test($re)", MEGABYTE), "true");

    // Under 900 calls of the program: the pattern is compiled where the stack has room for it, and
    // on a fresh stack otherwise.
    const DEEP: &str = ". as $re | def f: if . == 0 then (\"a\" | try test($re) catch .) else (. - 1 | f) | not | not end; 900 | f";
    for levels in [1, 32, 33, 100] {
        let pattern = format!("{}a{}", "(".repeat(levels), ")".repeat(levels));
        for stack in [MEGABYTE, SMALL] {
            assert_eq!(tested(pattern.clone(), DEEP, stack), "true", "{levels} levels");
        }
    }
}

#[test]
fn the_largest_patterns_fit_the_engine() {
    const TEST: &str = ". as $re | \"a\" | try test($re) catch .";
    // 10,000 times the items the engine builds the most for (a UTF-8 automaton each): the size
    // limit of the engine is not met within the size of §11.
    for item in ["[^\u{10005}]", "[\u{81}-\u{10FFFE}]", ".", "\\W", "\\D", "[^a]", "\u{10FFFF}"] {
        let largest = format!("{}{item}{{990}}", format!("{item}{{1000}}").repeat(9));
        assert_eq!(tested(largest.clone(), TEST, MEGABYTE), "false", "{item}");
        assert_eq!(tested(format!("{largest}a"), TEST, MEGABYTE), TOO_LARGE, "{item}");
    }
    // The same under the flags that widen an item.
    const FLAGS: &str = ". as $re | \"a\" | try test($re; \"im\") catch .";
    assert_eq!(tested(format!("{}.{{990}}", ".{1000}".repeat(9)), FLAGS, MEGABYTE), "false");
    // Long without repetition, and a thousand capturing groups.
    assert_eq!(tested("a".repeat(10_000), TEST, MEGABYTE), "false");
    assert_eq!(tested("(a)|".repeat(1000), TEST, MEGABYTE), "true");
}

#[test]
fn paths_of_ten_thousand_keys() {
    // A value nested 10,000 arrays deep and the path of its innermost 0, run on a 1 MB stack.
    let deep = "reduce range(10000) as $i (0; [.]) | [range(10000) | 0] as $p | ";
    let cases = [
        ("getpath($p) |= . + 1 | getpath($p)", "1"),
        ("setpath($p; 2) | getpath($p)", "2"),
        ("delpaths([$p]) | getpath($p[1:])", "[]"),
        ("del(getpath($p)) | getpath($p[1:])", "[]"),
        ("getpath($p) = 3 | getpath($p)", "3"),
    ];
    for (program, expected) in cases {
        assert_eq!(on_stack(MEGABYTE, &format!("{deep}{program}"), 1000), expected, "{program}");
    }
}

// ---- NativeTableTests ----

// A native of the catalogue without an implementation is a defect of the build: the table
// refuses to load, and this test names it.
#[test]
fn every_native_of_the_catalogue_is_implemented() {
    let missing: Vec<String> = natives::all().iter().filter(|native| !native_table::is_implemented(native)).map(|native| native.signature()).collect();
    assert!(missing.is_empty(), "{missing:?}");
}
