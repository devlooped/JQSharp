//! The binding layer: what `dotnet/Tests/Core/Binding` covers. Lexical binding printed as hops
//! and slots, the single functions and calls of spec §7.7, binding errors and positions, limits
//! on a 1 MB stack, modules, the corpus as binding tests, and the builtin catalogue against §9.

mod support;

use std::collections::{BTreeSet, HashMap};
use std::sync::Arc;

use jqlite::binding::bound::NodeKind;
use jqlite::binding::prelude::{self, Prelude};
use jqlite::binding::{ModuleSource, Resolver};
use jqlite::builtins::natives::{self, NativeGroup};
use jqlite::runtime::{EvaluationOptions, Interpreter, Outcome};
use jqlite::syntax::parser::{MAX_NESTING, Parser};
use jqlite::values::{JqValue, value_text};

use support::bound_printer::{bind, bind_error as error, print_program};
use support::{DirectoryModules, MemoryModules, on_thread};

const MEGABYTE: usize = 1024 * 1024;

fn check_bind(cases: &[(&str, &str)]) {
    for (program, bound) in cases {
        assert_eq!(bind(program), *bound, "{program}");
    }
}

// ---- ResolverTests ----

#[test]
fn variables_bind_lexically() {
    check_bind(&[
        // Variables: the innermost binding, counted in frames outward.
        ("1 as $x | $x", "(as 1 $0 $0.0)"),
        ("1 as $x | 2 as $x | $x", "(as 1 $0 (as 2 $0 $0.0))"),
        ("1 as $x | 2 as $y | $x, $y", "(as 1 $0 (as 2 $0 (, $1.0 $0.0)))"),
        ("1 as $x | (2 as $x | $x), $x", "(as 1 $0 (, (as 2 $0 $0.0) $0.0))"),
        // A binding is not visible in its own source.
        ("1 as $x | ($x + 1) as $x | $x", "(as 1 $0 (as (+ $0.0 1) $0 $0.0))"),
        // A function body sees the variables of its definition, not of its caller.
        ("1 as $x | def f: $x; 2 as $x | f", "(def f/0#0 $0.0) (as 1 $0 (as 2 $0 f/0#0^1))"),
        ("def f: 1; 2 as $x | f", "(def f/0#0 1) (as 2 $0 f/0#0^1)"),
    ]);
}

#[test]
fn functions_bind_to_the_innermost_visible_definition() {
    check_bind(&[
        // Recursion: a definition is visible in its own body.
        ("def f: f; f", "(def f/0#0 f/0#0^0) f/0#0^0"),
        // Later definitions shadow earlier ones; an earlier body keeps what it saw.
        ("def f: 1; def g: f; def f: 2; g", "(def f/0#0 1) (def g/0#1 f/0#0^0) (def f/0#2 2) g/0#1^0"),
        // Name and arity together identify a function.
        ("def f: 1; def f(a): 2; f, f(3)", "(def f/0#0 1) (def f/1#1 2) (, f/0#0^0 (f/1#1^0 3))"),
        // Local definitions are visible in their scope only.
        ("def f: 1; [f, (def f: 2; f), f]", "(def f/0#0 1) (def f/0#1 2) (array (, f/0#0^0 f/0#1^0 f/0#0^0))"),
        // A call of an enclosing function from a nested one, across the parameters' frame.
        ("def f(g): def h: f(g); h; f(.)", "(def f/1#0 h/0#1^0) (def h/0#1 (f/1#0^1 param^0.0)) (f/1#0^0 .)"),
    ]);
}

#[test]
fn functions_and_calls_are_single_by_the_rules() {
    let cases = [
        // A function is single when its body is, its own calls taken as single (spec §7.7).
        ("def fib: if . < 2 then . else (. - 1 | fib) + (. - 2 | fib) end; fib", "fib/0+ body+"),
        ("def r: ., (.[]? | r); r", "r/0- body-"),
        // A call is single when the function is and the argument of each $ parameter is.
        ("def f($a): $a + 1; f(1)", "f/1+ body+"),
        ("def f($a): $a + 1; f(1, 2)", "f/1+ body-"),
        ("def f($a; b; $c): [b] | $a + $c; f(1; 2, 3; 4)", "f/3+ body+"),
        ("def f($a; b; $c): [b] | $a + $c; f(1; 2; 3, 4)", "f/3+ body-"),
        ("def f($n): if $n < 2 then $n else f($n - 1) + f($n - 2) end; f(9)", "f/1+ body+"),
        // A call of a closure parameter is never single; a function is, when none of them comes out.
        ("def f(g): g + 1; f(1)", "f/1- body-"),
        ("def f(g): [g]; f(1, 2)", "f/1+ body+"),
        // Seen from a nested definition, the function being defined is not single.
        ("def f: def g: f; g; f", "f/0- g/0- body-"),
        // A function found not single is bound again; the functions nested in it keep their answer and their index.
        ("def f: def g: if . < 3 then . + 1 | g else . end; g, (. + 1 | f); f", "f/0- g/0+ body-"),
        ("def f: def g: def h: ., (. + 1 | h); [h], (. + 1 | g); def k: 1 + k; [g] | k, f; [f]", "f/0- g/0- h/0- k/0+ body+"),
        // Natives: with value arguments when these are single; with closures by the list of §7.7.
        ("def f: if . then empty else last(.[]) end; f", "f/0+ body+"),
        ("def f: getpath([\"a\"]) | until(. > 3; . + 1) | length; f", "f/0+ body+"),
        ("def f: getpath([\"a\"], [\"b\"]); f", "f/0- body-"),
        ("def f: limit(1; .); f", "f/0- body-"),
        // Through the prelude: error/1, any, all and isempty are single; select is not.
        ("def f: if any(.[]; . > 1) then error(\"x\") elif isempty(.[]) then 0 else map(f) | add end; f", "f/0+ body+"),
        ("def f: select(. > 1); f", "f/0- body-"),
        // An update is single like its prelude definition, with or without the native that overrides it.
        ("def f: .a |= . + 1 | .b = 2; f", "f/0+ body+"),
        ("def f: .a = (1, 2); f", "f/0- body-"),
    ];
    for (program, expected) in cases {
        for native_overrides in [true, false] {
            let bound = Resolver::try_compile(program, native_overrides, None).expect(program);
            let mut own: Vec<String> = bound.functions[Prelude::instance().functions.len()..]
                .iter()
                .map(|function| format!("{}{}", function.signature(), if function.single { '+' } else { '-' }))
                .collect();
            own.push(format!("body{}", if bound.body.at_most_one { '+' } else { '-' }));
            assert_eq!(own.join(" "), expected, "{program} (native overrides {native_overrides})");
        }
    }
}

#[test]
fn parameters_are_closures() {
    check_bind(&[
        // A parameter is a closure: a call of slot s of the call frame.
        ("def f(g): g; f(.)", "(def f/1#0 param^0.0) (f/1#0^0 .)"),
        ("def f(a; b): b, a; f(1; 2)", "(def f/2#0 (, param^0.1 param^0.0)) (f/2#0^0 1 2)"),
        // A repeated parameter name: the last one wins (jq 1.8.2: 2).
        ("def f(a; a): a; f(1; 2)", "(def f/2#0 param^0.1) (f/2#0^0 1 2)"),
        // Seen from a nested function, the parameter is one frame further when the nested one has parameters.
        ("def f(g): def h(x): g, x; h(1); f(2)", "(def f/1#0 (h/1#1^0 1)) (def h/1#1 (, param^1.0 param^0.0)) (f/1#0^0 2)"),
        // A parameter shadows a function of its name and arity 0 only (jq 1.8.2: 7).
        ("def x: 7; def f(x): x; f(1)", "(def x/0#0 7) (def f/1#1 param^0.0) (f/1#1^0 1)"),
        ("def x(a): 7; def f(x): x(0); f(1)", "(def x/1#0 7) (def f/1#1 (x/1#0^1 0)) (f/1#1^0 1)"),
        // A definition inside the body shadows the parameter.
        ("def f(g): def g: 1; g; f(2)", "(def f/1#0 g/0#1^0) (def g/0#1 1) (f/1#0^0 2)"),
        // def f($a) is def f(a): a as $a | …: both the filter and the variable are visible.
        ("def f($a): $a, a; f(1)", "(def f/1#0 (as param^0.0 $0 (, $0.0 param^1.0))) (f/1#0^0 1)"),
        ("def f($a; $b): $a, $b; f(1; 2)", "(def f/2#0 (as param^0.0 $0 (as param^1.1 $0 (, $1.0 $0.0)))) (f/2#0^0 1 2)"),
        // The binding of $a names its parameter by position; the filter a is the last parameter of that name (spec §7.7).
        ("def f(a; $a): [a, $a]; f(1; 2)", "(def f/2#0 (as param^0.1 $0 (array (, param^1.1 $0.0)))) (f/2#0^0 1 2)"),
        ("def f($a; a): [a, $a]; f(1; 2)", "(def f/2#0 (as param^0.0 $0 (array (, param^1.1 $0.0)))) (f/2#0^0 1 2)"),
        ("def f(x): def x: 5; x; f(1)", "(def f/1#0 x/0#1^0) (def x/0#1 5) (f/1#0^0 1)"),
        // Qualified names are ordinary names when defined (jq 1.8.2).
        ("def f(a::b): a::b; f(3)", "(def f/1#0 param^0.0) (f/1#0^0 3)"),
        ("def f::g: 1; f::g", "(def f::g/0#0 1) f::g/0#0^0"),
    ]);
}

#[test]
fn labels_bind_lexically() {
    check_bind(&[
        // A break refers to the label visible where it is written, also inside a function: the spec's example.
        ("[label $f | def g: break $f; (label $f | 1, g, 2), 3]", "(def g/0#0 (break ^0)) (array (label (, (label (, 1 g/0#0^1 2)) 3)))"),
        ("label $a | label $b | break $a, break $b", "(label (label (, (break ^1) (break ^0))))"),
        ("label $a | 1 as $x | break $a", "(label (as 1 $0 (break ^1)))"),
        // Labels and variables are separate name spaces.
        ("label $x | 1 as $x | $x, break $x", "(label (as 1 $0 (, $0.0 (break ^1))))"),
        ("label $a::b | break $a::b", "(label (break ^0))"),
    ]);
}

#[test]
fn patterns_bind_every_variable() {
    check_bind(&[
        (". as [$a, $b] | $a, $b", "(as . [$1 $0] (, $0.1 $0.0))"),
        (". as {$a, b: [$c]} | $a, $c", "(as . {(\"a\" $0 _) (\"b\" _ [$1])} (, $0.0 $0.1))"),
        (". as {$a: [$b]} | $a, $b", "(as . {(\"a\" $0 [$1])} (, $0.0 $0.1))"),
        // Every variable of every alternative has a slot; a name shared by alternatives shares it.
        (". as [$a] ?// {a: $b} ?// $a | [$a, $b]", "(as . (?// [$0] {(\"a\" _ $1)} $0) (array (, $0.0 $0.1)))"),
        // A repeated name: the first occurrence in destructuring order owns the slot (array from the last element, object from the first entry).
        (". as [$a, $a] | $a", "(as . [$_ $0] $0.0)"),
        (". as {$a, b: $a} | $a", "(as . {(\"a\" $0 _) (\"b\" _ $_)} $0.0)"),
        (". as [$a, [$a]] | $a", "(as . [$_ [$0]] $0.0)"),
        // With ?// every occurrence stores and the last in matching order stays (spec §7.5).
        (". as {$a, b: $a} ?// $c | $a", "(as . (?// {(\"a\" $0 _) (\"b\" _ $0)} $1) $0.0)"),
        (". as {x: $c} ?// [$a, $a] | $a", "(as . (?// {(\"x\" _ $0)} [$1 $1]) $0.1)"),
        // Key expressions see the scope outside the binding, not the pattern's variables (jq 1.8.2).
        ("\"b\" as $x | . as {a: $x, ($x): $y} | $y", "(as \"b\" $0 (as . {(\"a\" _ $0) ($0.0 _ $1)} $0.1))"),
        (". as {a: $x, ($x): $y} | $y", "(as . {(\"a\" _ $0) ($$x _ $1)} $0.1)"),
        // reduce/foreach: the source and the initial value outside the binder's frame, update and extract inside.
        ("1 as $x | reduce . as $y ($x; $x, $y)", "(as 1 $0 (reduce . $0 $0.0 (, $1.0 $0.0)))"),
        ("foreach .[] as [$a] (0; $a; [$a, .])", "(foreach (iter .) [$0] 0 $0.0 (array (, $0.0 .)))"),
        ("reduce .[] as $x (.; .) | $x", "(| (reduce (iter .) $0 . .) $$x)"),
    ]);
}

#[test]
fn unbound_variables_are_host_variables() {
    check_bind(&[
        // An unbound simple $name is a host variable, numbered by first appearance.
        ("$foo, $bar, $foo", "(, $$foo $$bar $$foo)"),
        ("$__prog__", "$$__prog__"),
        ("$ENV", "$ENV"),
        ("1 as $ENV | $ENV", "(as 1 $0 $0.0)"),
        ("def env: $ENV; env", "(def env/0#0 $ENV) env/0#0^0"),
    ]);
}

#[test]
fn the_location_is_a_literal() {
    check_bind(&[
        // $__loc__ is not a variable: the resolver writes its location object (§7.5).
        ("$__loc__", "{\"file\":\"<top-level>\",\"line\":1}"),
        ("1 as $x |\n\n$__loc__", "(as 1 $0 {\"file\":\"<top-level>\",\"line\":3})"),
    ]);
}

#[test]
fn host_variables_are_listed_once() {
    let program = Resolver::try_compile("$foo, $bar, ($foo as $bar | $bar), $baz", true, None).expect("the program binds");
    assert_eq!(program.host_variables, ["foo", "bar", "baz"]);
}

#[test]
fn calls_resolve_to_user_then_prelude_then_native() {
    check_bind(&[
        // Builtins after user definitions: prelude, then natives (§7.7). A prelude function is defined
        // in the outermost environment, so the call leaves every frame of the caller.
        ("length", "native:length/0"),
        ("map(.)", "(prelude:map/1^0 .)"),
        ("1 as $x | 2 as $y | map($x)", "(as 1 $0 (as 2 $0 (prelude:map/1^2 $1.0)))"),
        ("def map(f): 1; map(.)", "(def map/1#0 1) (map/1#0^0 .)"),
        ("def length: 1; length", "(def length/0#0 1) length/0#0^0"),
        // The parser's own calls resolve the same way: .. is recurse/0, |= is _modify/2, op= binds $*update.
        ("..", "prelude:recurse/0^0"),
        ("def recurse: 1; ..", "(def recurse/0#0 1) recurse/0#0^0"),
        // _modify and _assign are prelude builtins with a native override (Native::overrides_prelude).
        (".a |= 1", "(native:_modify/2 (index . \"a\") 1)"),
        (".a = 1", "(native:_assign/2 (index . \"a\") 1)"),
        (".a += 1", "(as 1 $0 (native:_modify/2 (index . \"a\") (+ . $0.0)))"),
        ("def _modify(a; b): 42; .a |= 1", "(def _modify/2#0 42) (_modify/2#0^0 (index . \"a\") 1)"),
        // A definition named like a literal does not change the literal (spec §2).
        ("def true: 1; true", "(def true/0#0 1) true"),
    ]);
}

#[test]
fn without_overrides_builtins_bind_to_the_prelude() {
    let program = Resolver::try_compile(".a |= 1, (.b = 2), map_values(.)", false, None).expect("the program binds");
    assert_eq!(print_program(&program), "(, (prelude:_modify/2^0 (index . \"a\") 1) (prelude:_assign/2^0 (index . \"b\") 2) (prelude:map_values/1^0 .))");
    // Inside the prelude too: map_values calls _modify.
    let with = Prelude::instance();
    let map_values = &with.functions[with.find("map_values", 1).expect("map_values/1 is a builtin")].body;
    assert!(matches!(map_values.kind, NodeKind::CallNative { .. }));
    let without = Prelude::without_overrides();
    let map_values = &without.functions[without.find("map_values", 1).expect("map_values/1 is a builtin")].body;
    assert!(matches!(map_values.kind, NodeKind::CallFunction { .. }));
}

#[test]
fn every_construct_binds_its_children() {
    assert_eq!(
        bind(". as $x | [{a: $x, b, ($x): 1}, @base64 \"x\\($x)\", .[$x][$x:], if $x then try $x catch $x else $x and $x or -$x // @csv end, $x[], .a?]"),
        "(as . $0 (array (, (object (\"a\" $0.0) (\"b\" _) ($0.0 1)) (str @base64 \"x\" $0.0 \"\") (slice (index . $0.0) $0.0 _) (if $0.0 (try $0.0 $0.0) (// (or (and $0.0 $0.0) (neg $0.0)) @csv)) (iter $0.0) (try (index . \"a\")))))"
    );
}

#[test]
fn prelude_functions_come_first_in_every_program() {
    let first = Resolver::try_compile("def f: 1; f", true, None).expect("the program binds");
    let second = Resolver::try_compile("map(.)", true, None).expect("the program binds");
    let prelude = &Prelude::instance().functions;
    assert_eq!(first.functions.len(), prelude.len() + 1);
    assert_eq!(second.functions.len(), prelude.len());
    for (i, function) in prelude.iter().enumerate() {
        assert!(Arc::ptr_eq(function, &first.functions[i]));
        assert!(Arc::ptr_eq(function, &second.functions[i]));
    }
}

#[test]
fn unknown_names_are_binding_errors() {
    let cases = [
        ("foo", "foo/0 is not defined @1:1"),
        ("1 | foo(1; 2)", "foo/2 is not defined @1:5"),
        ("map(1; 2)", "map/2 is not defined @1:1"),
        ("1 |\n  foo", "foo/0 is not defined @2:3"),
        ("foo::bar", "foo::bar/0 is not defined @1:1"),
        // The first unknown name in source order.
        ("1 + foo, bar", "foo/0 is not defined @1:5"),
        ("foo(bar)", "foo/1 is not defined @1:1"),
        ("map(bar)", "bar/0 is not defined @1:5"),
        // A definition is visible after its own def only, and local ones in their scope only (jq 1.8.2 positions).
        ("def f: g; def g: 1; f", "g/0 is not defined @1:8"),
        ("def f: def g: 3; g; g", "g/0 is not defined @1:21"),
        ("(def f: 1; f), f", "f/0 is not defined @1:16"),
        // A parameter is f/0 only.
        ("def f(g): g(1); f(1)", "g/1 is not defined @1:11"),
        ("def f($a): a(1); f(1)", "a/1 is not defined @1:12"),
        (". as $foo | break $foo", "$*label-foo is not defined @1:13"),
        ("label $f | def g: 1; break $g", "$*label-g is not defined @1:22"),
        // A label is not visible in a function defined outside it.
        ("def g: break $f; label $f | g", "$*label-f is not defined @1:8"),
        ("$foo::bar", "$foo::bar is not defined @1:1"),
        ("1 as $x | $x::y", "$x::y is not defined @1:11"),
        // Errors inside patterns, reduce and foreach.
        (". as {(foo): $x} | $x", "foo/0 is not defined @1:8"),
        ("reduce . as $x (0; foo)", "foo/0 is not defined @1:20"),
        ("foreach . as $x (0; .; foo)", "foo/0 is not defined @1:24"),
        // Without a module source no module exists.
        ("import \"a\" as a; 1", "module not found: a @1:1"),
        ("1 as $x | include \"a\"; 1", "Unexpected 'include' @1:11"),
    ];
    for (program, expected) in cases {
        assert_eq!(error(program), expected, "{program}");
    }
}

#[test]
fn syntax_errors_come_from_the_parser() {
    assert_eq!(error("1 +"), "Unexpected end of program @1:4");
}

#[test]
fn module_metadata_alone_is_accepted() {
    assert_eq!(bind("module {a: 1}; 1"), "1");
}

// ---- ResolverLimitTests ----
//
// Binding does not exhaust the host stack on what the parser accepts: nesting up to the limit of
// spec §11, and sequences of any length, which the parser builds as deep spines. Each program is
// parsed, bound and released on a thread with a 1 MB stack.

const LIMIT: usize = MAX_NESTING as usize;

fn binds(source: String) {
    let outcome = on_thread(MEGABYTE, move || match Resolver::try_compile(&source, true, None) {
        Ok(_) => "ok".to_owned(),
        Err(error) => format!("{} at {}:{}", error.message, error.line, error.column),
    });
    assert_eq!(outcome, "ok");
}

// Opening and closing repeated LIMIT times around "1"; each opening is one level of nesting and
// costs the binder the most frames that level can (a construct, then a sequence inside it).
#[test]
fn nesting_up_to_the_limit_binds() {
    let cases = [
        ("(", ")"),
        ("[", "]"),
        ("{a: ", "}"),
        ("{(\"a\" + ", "): 1}"),
        (".[", "]"),
        ("f(", ")"),
        ("\"\\(", ")\""),
        ("if . then ", " end"),
        ("try ", ""),
        ("reduce . as $x (", ";.)"),
        ("foreach . as $x (", ";.)"),
        ("def f: ", "; f"),
        ("def f(g): ", "; f(.)"),
        ("[1 + ", "]"),
        ("[1, 2 as $x | ", "]"),
        ("(1 | ", ")"),
        (".[1, ", "]"),
        ("{a: 1 | 1 + ", "}"),
        ("if 1, 1 as $x | ", " then . end"),
        ("try -", ""),
        ("f(label $x | ", ")"),
        (". as [$x] ?// {(\"a\" + ", "): $y} | 1"),
    ];
    for (open, close) in cases {
        let levels = if open.contains("{(") { LIMIT / 2 } else { LIMIT };
        binds("def f: 1; def f(g): 1; ".to_owned() + &open.repeat(levels) + "1" + &close.repeat(levels));
    }
}

// The sequences of the parser's limit tests: prefix, 10,000 elements, suffix.
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
        ("", "label $x | ", "break $x"),
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
        ("[", "1, 1 as $x | ", "$x]"),
        ("", "1, def f: 1; ", "f"),
        ("", "1, label $x | ", "1"),
        ("", "1 // 1 | ", "1"),
        ("", "1 as $x | label $y | def f: $x; ", "f"),
        ("", "1 | 1, 1 | ", "1"),
        ("", "def f(g): g; ", "f(.)"),
        ("", "reduce . as $x (.; .) | ", "1"),
    ];
    for (prefix, element, suffix) in cases {
        binds(prefix.to_owned() + &element.repeat(10_000) + suffix);
    }
}

#[test]
fn long_elif_chains_do_not_recurse() {
    binds("if . then 1 ".to_owned() + &"elif . then 1 ".repeat(10_000) + "else 2 end");
}

#[test]
fn a_variable_ten_thousand_frames_out() {
    let found = on_thread(MEGABYTE, || {
        let program = Resolver::try_compile(&("0 as $a | ".to_owned() + &"1 as $x | ".repeat(10_000) + "$a"), true, None).expect("the program binds");
        let mut node = &program.body;
        let mut depth = 0;
        while let NodeKind::Bind { body, .. } = &node.kind {
            node = body;
            depth += 1;
        }
        match node.kind {
            NodeKind::Variable { hops, slot } => (depth, hops, slot),
            _ => (depth, 0, 0),
        }
    });
    assert_eq!(found, (10_001, 10_000, 0));
}

#[test]
fn many_definitions_in_a_row() {
    on_thread(MEGABYTE, || {
        let program = Resolver::try_compile(&("def f: 1; ".repeat(10_000) + "f"), true, None).expect("the program binds");
        assert_eq!(program.functions.len(), Prelude::instance().functions.len() + 10_000);
        let NodeKind::CallFunction { function, hops, .. } = &program.body.kind else {
            panic!("a call is expected");
        };
        assert_eq!((*function, *hops), (program.functions.len() - 1, 0));
    });
}

#[test]
fn many_parameters_and_arguments() {
    on_thread(MEGABYTE, || {
        let source = "def f(".to_owned() + &"a; ".repeat(10_000) + "b): b; f(" + &"1; ".repeat(10_000) + "1)";
        let program = Resolver::try_compile(&source, true, None).expect("the program binds");
        let NodeKind::CallFunction { arguments, .. } = &program.body.kind else {
            panic!("a call is expected");
        };
        assert_eq!(arguments.len(), 10_001);
        let last = &program.functions[program.functions.len() - 1];
        assert!(matches!(last.body.kind, NodeKind::CallParameter { hops: 0, slot: 10_000 }));
    });
}

// ---- ModuleTests ----
//
// The modules group (spec §9.11) beyond the corpus: errors and where they are, loading once,
// limits, modulemeta.

fn module_error(program: &str, modules: Option<Arc<dyn ModuleSource>>) -> String {
    let error = Resolver::try_compile(program, true, modules).expect_err(program);
    format!("{} @{}:{}:{}", error.message, error.module.as_deref().unwrap_or("main"), error.line, error.column)
}

fn corpus_modules() -> Arc<dyn ModuleSource> {
    Arc::new(DirectoryModules::corpus())
}

fn run(program: &str, modules: Arc<dyn ModuleSource>, variables: Option<&HashMap<String, JqValue>>) -> Vec<String> {
    let bound = match Resolver::try_compile(program, true, Some(modules)) {
        Ok(bound) => bound,
        Err(error) => panic!("{program}: {}", error.message),
    };
    let mut outputs = Vec::new();
    let options = EvaluationOptions { variables, ..EvaluationOptions::default() };
    let outcome = Interpreter::run(&bound, JqValue::Null, &options, &mut |value| {
        outputs.push(value_text::to_json(&value));
        true
    });
    assert!(matches!(outcome, Outcome::Completed), "{program}: {outcome:?}");
    outputs
}

#[test]
fn binding_errors_say_where_they_are() {
    let cases = [
        ("import \"double\" as d; import \"nope\" as n; 1", "module not found: nope @main:1:23"),
        ("import \"broken\" as b; 1", "Expected ')' but found ';' @broken.jq:1:15"),
        ("import \"usesmain\" as u; 1", "mainf/0 is not defined @usesmain.jq:1:15"),
        ("import \"cycle1\" as c; 1", "circular import of cycle1.jq @cycle2.jq:1:1"),
        ("import \"self\" as s; 1", "circular import of self.jq @self.jq:1:1"),
        ("include \"config\"; 1", "module not found: config @main:1:1"),
        ("import \"a\" as a; a::zz", "a::zz/0 is not defined @main:1:18"),
        ("import \"values\" as $d; $d::x", "$d::x is not defined @main:1:24"),
    ];
    for (program, expected) in cases {
        assert_eq!(module_error(program, Some(corpus_modules())), expected, "{program}");
    }
}

#[test]
fn bad_data_is_a_binding_error() {
    assert!(module_error("import \"bad\" as $b; $b", Some(corpus_modules())).starts_with("error loading data file bad.json: Unfinished JSON term at EOF"));
}

#[test]
fn without_a_source_no_module_exists() {
    assert_eq!(module_error("import \"a\" as a; 1", None), "module not found: a @main:1:1");
    assert_eq!(support::outputs("try (\"a\" | modulemeta) catch .", "null"), ["\"module not found: a\""]);
}

#[test]
fn each_module_is_read_once() {
    let modules = Arc::new(DirectoryModules::corpus());
    assert_eq!(run("import \"one\" as o; import \"one\" as p; include \"one\"; o::k + p::k", modules.clone(), None), ["2"]);
    assert_eq!(modules.reads(), 1);
}

#[test]
fn a_long_import_chain_is_no_recursion() {
    // m0 imports m1, …, m999 imports nothing; bound on a 1 MB stack.
    let sources: Vec<(String, String)> = (0..1000)
        .map(|i| (format!("m{i}.jq"), if i == 999 { "def f: 999;".to_owned() } else { format!("import \"m{}\" as n; def f: n::f; def g: {i};", i + 1) }))
        .collect();
    let result = on_thread(MEGABYTE, move || {
        let pairs: Vec<(&str, &str)> = sources.iter().map(|(identity, text)| (identity.as_str(), text.as_str())).collect();
        run("import \"m0\" as m; m::f, m::g", Arc::new(MemoryModules::new(&pairs)), None)
    });
    assert_eq!(result, ["999", "0"]);
}

#[test]
fn modulemeta_uses_what_the_program_loaded() {
    // A source whose modules change after the program is bound.
    struct Changing(std::sync::Mutex<HashMap<String, String>>);
    impl ModuleSource for Changing {
        fn try_resolve(&self, path: &str, _: jqlite::binding::ModuleKind, _: Option<&JqValue>, _: Option<&str>) -> Result<String, String> {
            let identity = format!("{path}.jq");
            if self.0.lock().expect("the lock is taken").contains_key(&identity) { Ok(identity) } else { Err(format!("module not found: {path}")) }
        }

        fn try_read(&self, identity: &str) -> Result<String, String> {
            self.0.lock().expect("the lock is taken").get(identity).cloned().ok_or_else(|| "missing".to_owned())
        }
    }

    let modules = Arc::new(Changing(std::sync::Mutex::new(HashMap::from([("x.jq".to_owned(), "def a: 1;".to_owned())]))));
    let bound = Resolver::try_compile("import \"x\" as x; [\"x\", \"y\"][] | modulemeta | .defs", true, Some(modules.clone())).expect("the program binds");
    {
        let mut sources = modules.0.lock().expect("the lock is taken");
        sources.insert("x.jq".to_owned(), "def changed: 1;".to_owned());
        sources.insert("y.jq".to_owned(), "def b: 1; def c: 2;".to_owned());
    }
    // x as the program loaded it; y, which it does not import, from its source now.
    let mut outputs = Vec::new();
    Interpreter::run(&bound, JqValue::Null, &EvaluationOptions::default(), &mut |value| {
        outputs.push(value_text::to_json(&value));
        true
    });
    assert_eq!(outputs, ["[\"a/0\"]", "[\"b/0\",\"c/0\"]"]);
}

#[test]
fn modules_share_the_host_variables() {
    let modules = Arc::new(MemoryModules::new(&[("v.jq", "def v: $x;")]));
    let variables = HashMap::from([("x".to_owned(), JqValue::Number(7.0))]);
    assert_eq!(run("import \"v\" as v; v::v, $x", modules, Some(&variables)), ["7", "7"]);
}

#[test]
fn qualified_aliases_nest() {
    let modules = Arc::new(MemoryModules::new(&[("a.jq", "def f: 1;")]));
    assert_eq!(run("import \"a\" as x::y; x::y::f", modules, None), ["1"]);
}

#[test]
fn a_module_obeys_the_nesting_limit_of_its_file() {
    let deep = format!("def f: {}{};", "[".repeat(LIMIT + 1), "]".repeat(LIMIT + 1));
    let modules: Arc<dyn ModuleSource> = Arc::new(MemoryModules::new(&[("deep.jq", &deep)]));
    // def opens the first level, so the 1000th bracket is one too many.
    assert_eq!(module_error("import \"deep\" as d; 1", Some(modules)), "Program too deeply nested @deep.jq:1:1007");
}

// ---- CorpusBindTests ----

#[test]
fn every_program_of_the_corpus_binds() {
    let modules = corpus_modules();
    let mut failures = Vec::new();
    for (file, cases) in support::corpus() {
        for case in &cases {
            let program = match Parser::try_parse(&case.program) {
                Ok(program) => program,
                Err(error) => {
                    if !case.should_fail {
                        failures.push(format!("{file}:{}: {} does not parse: {}", case.line, case.program, error.message));
                    }
                    continue;
                }
            };

            match Resolver::try_bind(&program, true, Some(modules.clone())) {
                Ok(_) if case.should_fail => failures.push(format!("{file}:{}: {} binds but is a %%FAIL case", case.line, case.program)),
                Ok(_) => {}
                Err(error) if !case.should_fail => {
                    failures.push(format!("{file}:{}: {}: {} at {}:{}", case.line, case.program, error.message, error.line, error.column))
                }
                Err(error) => {
                    if let Some(missing) = support::missing_fixed_text(case.expected_error.as_ref(), &error.message) {
                        failures.push(format!("{file}:{}: {}: {missing}", case.line, case.program));
                    }
                }
            }
        }
    }
    assert!(failures.is_empty(), "{}", failures.join("\n"));
}

#[test]
fn some_failure_of_the_corpus_is_a_binding_error() {
    let binding_failures: Vec<String> = support::corpus()
        .into_iter()
        .flat_map(|(_, cases)| cases)
        .filter(|case| case.should_fail && Parser::try_parse(&case.program).is_ok())
        .map(|case| case.program)
        .collect();
    assert!(binding_failures.iter().any(|program| program == ". as $foo | break $foo"));
}

// ---- BuiltinCatalogueTests ----
//
// The native catalogue and the embedded prelude against spec §9: the prelude binds completely,
// and together they are exactly the builtins the spec names, natives where the spec says N and
// prelude definitions where it says P. The lists are read from the spec, so spec and code cannot
// drift.

#[test]
fn the_embedded_prelude_is_spec_prelude_jq() {
    let shared = std::fs::read_to_string(support::spec_directory().join("prelude.jq")).expect("spec/prelude.jq is read");
    // The file may be checked out with CR LF line ends; the text is the same.
    assert_eq!(prelude::SOURCE.replace("\r\n", "\n"), shared.replace("\r\n", "\n"));
}

#[test]
fn the_prelude_binds_against_the_natives() {
    let prelude = Prelude::instance();
    assert_eq!(prelude.definitions.len(), 102);
    // Nested definitions (recurse's r, walk's w) are functions of the prelude too.
    assert!(prelude.functions.len() > prelude.definitions.len());
    let signatures: BTreeSet<String> = prelude.definitions.iter().map(|function| function.signature()).collect();
    assert_eq!(signatures.len(), prelude.definitions.len());
}

#[test]
fn a_prelude_cannot_use_host_variables() {
    let panic = std::panic::catch_unwind(|| Prelude::load("def f: $x;", true).functions.len()).expect_err("the prelude does not bind");
    let message = panic.downcast_ref::<String>().cloned().unwrap_or_default();
    assert!(message.contains("$x is not defined at 1:8"), "{message}");
}

#[test]
fn the_catalogue_has_unique_signatures_and_indices() {
    let all = natives::all();
    let signatures: BTreeSet<String> = all.iter().map(|native| native.signature()).collect();
    assert_eq!(signatures.len(), all.len());
    for (i, native) in all.iter().enumerate() {
        assert_eq!(native.index, i);
        assert!(natives::find(native.name, native.arity).is_some_and(|found| std::ptr::eq(found, native)));
    }
    assert!(natives::find("map", 1).is_none());
}

struct SpecLists {
    all: BTreeSet<String>,
    native: BTreeSet<String>,
    prelude: BTreeSet<String>,
    modules: BTreeSet<String>,
}

fn section<'a>(text: &'a str, start: &str, end: &str) -> &'a str {
    let from = text.find(&format!("\n{start}")).unwrap_or_else(|| panic!("Section '{start}' not found in the spec"));
    let to = from + 1 + text[from + 1..].find(&format!("\n{end}")).unwrap_or_else(|| panic!("Section '{end}' not found in the spec"));
    &text[from..to]
}

fn signatures(text: &str) -> Vec<String> {
    let pattern = regex::Regex::new(r"`([A-Za-z_][A-Za-z0-9_]*/[0-9]+)`").expect("the pattern is valid");
    pattern.captures_iter(text).map(|found| found[1].to_owned()).collect()
}

// The signatures in the first cells of table rows, in the library paragraph, and at the start of lines.
fn declarations(text: &str) -> BTreeSet<String> {
    let first_cells = regex::Regex::new(r"(?m)^\| ([^|\n]*) \|").expect("the pattern is valid");
    let library = regex::Regex::new(r"(?m)^\*\*Library functions\*\*(.*?`([a-z0-9 ]+)` \(one input\).*)$").expect("the pattern is valid");
    // One or more signatures at the start of a line, separated by ", ".
    let headings = regex::Regex::new(r"(?m)^`[A-Za-z_][A-Za-z0-9_]*/[0-9]+`(, `[A-Za-z_][A-Za-z0-9_]*/[0-9]+`)*").expect("the pattern is valid");
    let mut declared = BTreeSet::new();
    for cell in first_cells.captures_iter(text) {
        declared.extend(signatures(&cell[1]));
    }
    for paragraph in library.captures_iter(text) {
        declared.extend(signatures(&paragraph[1]));
    }
    for heading in headings.find_iter(text) {
        declared.extend(signatures(heading.as_str()));
    }
    declared
}

// Section 9 of spec/jqlite.md, read only where it declares builtins, so that examples in its
// prose cannot add any: the first cell of each table row; the §9.8 library paragraph (its
// signatures and its list of names "(one input)"); the lines that start with signatures (the
// §9.8 formula headings and abs/0); the functions §9.8 defines as `name(x):`; and the group
// sentence of §9.11. Rows whose K column says N or P give the kind; §9.8 is native but abs/0.
fn spec_builtins() -> SpecLists {
    let text = std::fs::read_to_string(support::spec_directory().join("jqlite.md")).expect("spec/jqlite.md is read").replace("\r\n", "\n");
    let section9 = section(&text, "## 9. ", "## 10. ");
    let math = section(section9, "### 9.8 ", "### 9.9 ");
    let mut math_declared = declarations(math);
    let library = regex::Regex::new(r"(?m)^\*\*Library functions\*\*(.*?`([a-z0-9 ]+)` \(one input\).*)$").expect("the pattern is valid");
    let names = library.captures(math).expect("The library functions of §9.8 were not found");
    for name in names[2].split(' ') {
        math_declared.insert(format!("{name}/0"));
    }
    let formulas = regex::Regex::new(r"(?m)^\s*([a-z][a-z0-9]*)\(x\):").expect("the pattern is valid");
    for formula in formulas.captures_iter(math) {
        math_declared.insert(format!("{}/0", &formula[1]));
    }

    // The first sentence of §9.11, which names the members of the optional group.
    let group = regex::Regex::new(r"A port MAY leave out the whole group:[^.]*\.").expect("the pattern is valid");
    let sentence = group.find(section(section9, "### 9.11 ", "### 9.12 ")).expect("The group sentence of §9.11 was not found");
    let modules: BTreeSet<String> = signatures(sentence.as_str()).into_iter().collect();

    let mut all = declarations(section9);
    all.extend(math_declared.iter().cloned());
    all.extend(modules.iter().cloned());
    let mut native = modules.clone();
    let mut prelude = BTreeSet::new();
    let kind_rows = regex::Regex::new(r"(?m)^\| ([^|]*) \| ([NP]) \|").expect("the pattern is valid");
    for row in kind_rows.captures_iter(section9) {
        let kind = if &row[2] == "N" { &mut native } else { &mut prelude };
        kind.extend(signatures(&row[1]));
    }
    for signature in math_declared {
        if signature == "abs/0" {
            prelude.insert(signature)
        } else {
            native.insert(signature)
        };
    }
    SpecLists { all, native, prelude, modules }
}

#[test]
fn natives_and_prelude_are_the_builtins_of_the_spec() {
    let spec = spec_builtins();
    let natives: BTreeSet<String> = natives::all().iter().filter(|native| !native.overrides_prelude).map(|native| native.signature()).collect();
    let overrides: BTreeSet<String> = natives::all().iter().filter(|native| native.overrides_prelude).map(|native| native.signature()).collect();
    let prelude: BTreeSet<String> = Prelude::instance().definitions.iter().map(|function| function.signature()).collect();
    // An override implements a prelude builtin natively: it stays a prelude builtin of §9.
    assert!(overrides.is_subset(&prelude));
    assert!(natives.is_disjoint(&prelude));
    let both: BTreeSet<String> = natives.union(&prelude).cloned().collect();
    assert_eq!(both, spec.all);
    assert!(natives.is_subset(&spec.native), "{:?}", natives.difference(&spec.native));
    assert!(prelude.is_subset(&spec.prelude), "{:?}", prelude.difference(&spec.prelude));
    let modules: BTreeSet<String> = natives::all().iter().filter(|native| native.group == NativeGroup::Modules).map(|native| native.signature()).collect();
    assert_eq!(modules, spec.modules);
    // What `builtins` lists is the same set, without the internal names.
    let listed = support::outputs("builtins", "null");
    let expected: Vec<String> = spec.all.iter().filter(|signature| !signature.starts_with('_')).cloned().collect();
    assert_eq!(listed, [value_text::to_json(&JqValue::from(expected.into_iter().map(JqValue::from).collect::<Vec<_>>()))]);
}

#[test]
fn internal_builtins_start_with_an_underscore() {
    let natives: BTreeSet<String> =
        natives::all().iter().filter(|native| native.is_internal() && !native.overrides_prelude).map(|native| native.signature()).collect();
    assert_eq!(natives.into_iter().collect::<Vec<_>>(), ["_group_by_impl/1", "_max_by_impl/1", "_min_by_impl/1", "_sort_by_impl/1", "_strindices/1"]);
    let prelude: BTreeSet<String> =
        Prelude::instance().definitions.iter().filter(|function| function.name.starts_with('_')).map(|function| function.signature()).collect();
    assert_eq!(prelude.into_iter().collect::<Vec<_>>(), ["_assign/2", "_flatten/1", "_modify/2"]);
}
