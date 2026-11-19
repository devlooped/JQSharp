//! The host boundary: options, errors, streaming, variables, modules, value conversion, and one
//! expression evaluated from many threads (what the `Jq*Tests` of `dotnet/Tests` cover for .NET).

mod support;

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use jqlite::{FixedOffsetClock, JqClock, JqError, JqExpression, JqFileResolver, JqModuleKind, JqOptions, JqResolver, JqValue};

fn json(text: &str) -> JqValue {
    JqValue::parse(text).unwrap_or_else(|error| panic!("{text}: {error}"))
}

fn texts(results: &[JqValue]) -> Vec<String> {
    results.iter().map(JqValue::to_json).collect()
}

fn evaluate(expression: &str, input: &str) -> Vec<String> {
    texts(&jqlite::evaluate(expression, json(input)).unwrap_or_else(|error| panic!("{expression}: {error}")))
}

fn variables(pairs: &[(&str, &str)]) -> HashMap<String, JqValue> {
    pairs.iter().map(|(name, value)| ((*name).to_owned(), json(value))).collect()
}

// An expression is shared between threads.
#[test]
fn an_expression_is_send_and_sync() {
    fn check<T: Send + Sync>() {}
    check::<JqExpression>();
    check::<JqValue>();
    check::<JqOptions>();
    check::<JqError>();
}

#[test]
fn options_environment_is_env_and_env() {
    let options = JqOptions { environment: Some(vec![("B".to_owned(), "2".to_owned()), ("A".to_owned(), "1".to_owned())]), ..JqOptions::default() };
    let expression = jqlite::parse_with("$ENV, env.A", &options).expect("the expression parses");
    assert_eq!(texts(&expression.evaluate(JqValue::Null).expect("it evaluates")), ["{\"B\":\"2\",\"A\":\"1\"}", "\"1\""]);
}

#[test]
fn without_options_env_is_the_process_environment() {
    let (name, value) = std::env::vars()
        .find(|(name, _)| !name.is_empty() && name.chars().all(|c| c.is_ascii_alphanumeric() || c == '_') && !name.starts_with(|c: char| c.is_ascii_digit()))
        .expect("the process has a variable");
    assert_eq!(evaluate(&format!("$ENV.{name}"), "null"), [JqValue::from(value).to_json()]);
}

// A clock at a fixed instant, in a zone that is one hour ahead of UTC from April to October.
struct Clock;

impl JqClock for Clock {
    fn now(&self) -> f64 {
        1425599507.0
    }

    fn offset(&self, seconds: f64) -> i32 {
        // 2015-04-01 and 2015-10-01, in seconds since the epoch.
        if (1427846400.0..1443657600.0).contains(&seconds) { 7200 } else { 3600 }
    }

    fn abbreviation(&self, seconds: f64) -> Option<String> {
        Some(if self.offset(seconds) == 7200 { "CEST" } else { "CET" }.to_owned())
    }
}

#[test]
fn options_clock_gives_now_and_the_local_zone() {
    let options = JqOptions { clock: Arc::new(Clock), ..JqOptions::default() };
    let expression =
        jqlite::parse_with("now, (now | strflocaltime(\"%Y-%m-%dT%H:%M:%S %z %Z\")), (now | localtime | mktime)", &options).expect("the expression parses");
    assert_eq!(texts(&expression.evaluate(JqValue::Null).expect("it evaluates")), ["1425599507", "\"2015-03-06T00:51:47 +0100 CET\"", "1425603107"]);
    // The offset follows the instant: January and July.
    let expression = jqlite::parse_with("(1420070400, 1435708800) | strflocaltime(\"%z %Z\")", &options).expect("the expression parses");
    assert_eq!(texts(&expression.evaluate(JqValue::Null).expect("it evaluates")), ["\"+0100 CET\"", "\"+0200 CEST\""]);
}

#[test]
fn a_fixed_offset_clock_has_no_abbreviation() {
    let options = JqOptions { clock: Arc::new(FixedOffsetClock { offset_seconds: -3 * 3600 }), ..JqOptions::default() };
    let expression = jqlite::parse_with("0 | strflocaltime(\"%H:%M %z %Z\"), (now > 1700000000)", &options).expect("the expression parses");
    assert_eq!(texts(&expression.evaluate(JqValue::Null).expect("it evaluates")), ["\"21:00 -0300 -03\"", "true"]);
}

#[test]
fn the_default_clock_is_the_system_clock_in_utc() {
    assert_eq!(evaluate("(now > 1700000000), (0 | localtime | mktime), (0 | strflocaltime(\"%z %Z\"))", "null"), ["true", "0", "\"+0000 +00\""]);
}

#[test]
fn options_debug_and_stderr_receive_the_values() {
    let debugged = Arc::new(Mutex::new(Vec::new()));
    let written = Arc::new(Mutex::new(Vec::new()));
    let (debug, stderr) = (debugged.clone(), written.clone());
    let options = JqOptions {
        debug: Some(Arc::new(move |value: &JqValue| debug.lock().expect("the lock is taken").push(value.to_json()))),
        stderr: Some(Arc::new(move |value: &JqValue| stderr.lock().expect("the lock is taken").push(value.to_json()))),
        ..JqOptions::default()
    };
    let expression = jqlite::parse_with("debug, debug(\"m\", {a: .}), stderr", &options).expect("the expression parses");
    assert_eq!(texts(&expression.evaluate(json("1")).expect("it evaluates")), ["1", "1", "1"]);
    assert_eq!(*debugged.lock().expect("the lock is taken"), ["1", "\"m\"", "{\"a\":1}"]);
    assert_eq!(*written.lock().expect("the lock is taken"), ["1"]);
}

#[test]
fn without_handlers_debug_and_stderr_only_output_their_input() {
    assert_eq!(evaluate("debug, stderr", "1"), ["1", "1"]);
}

#[test]
fn halt_error_without_a_code_exits_with_5() {
    match jqlite::evaluate("halt_error", json("\"bye\"")) {
        Err(JqError::Halt { message, exit_code, .. }) => assert_eq!((message.as_str(), exit_code), ("bye", 5)),
        other => panic!("a halt is expected, not {other:?}"),
    }
}

#[test]
fn options_max_depth_limits_recursion() {
    let options = JqOptions { max_depth: 10, ..JqOptions::default() };
    let expression = jqlite::parse_with("def f: if . == 0 then 0 else (. - 1 | f) + 1 end; f", &options).expect("the expression parses");
    assert_eq!(texts(&expression.evaluate(json("9")).expect("it evaluates")), ["9"]);
    let error = expression.evaluate(json("10")).expect_err("the limit is met");
    assert_eq!(error.message(), "Maximum call depth exceeded");
    // The error is an ordinary one: the expression catches it.
    let expression = jqlite::parse_with("def f: 1 + f; try f catch .", &options).expect("the expression parses");
    assert_eq!(texts(&expression.evaluate(JqValue::Null).expect("it evaluates")), ["\"Maximum call depth exceeded\""]);
}

#[test]
fn parse_errors_have_a_position() {
    let cases = [
        ("1 +", "Unexpected end of program at line 1, column 4", 1, 4),
        (".a |\n  foo", "foo/0 is not defined at line 2, column 3", 2, 3),
        (". as $x | break $x", "$*label-x is not defined at line 1, column 11", 1, 11),
        ("import \"a\" as a; 1", "module not found: a at line 1, column 1", 1, 1),
    ];
    for (expression, expected, expected_line, expected_column) in cases {
        match jqlite::parse(expression) {
            Err(error @ JqError::Parse { .. }) => {
                assert!(error.value().is_none());
                let JqError::Parse { message, line, column } = error else {
                    unreachable!("the error is a parse error");
                };
                assert_eq!((message.as_str(), line, column), (expected, expected_line, expected_column), "{expression}");
            }
            other => panic!("{expression}: a parse error is expected, not {:?}", other.map(|_| "an expression")),
        }
    }
}

#[test]
fn an_empty_expression_is_identity() {
    assert_eq!(evaluate("", "[1]"), ["[1]"]);
    assert_eq!(evaluate("  # nothing\n", "2"), ["2"]);
}

#[test]
fn a_string_error_is_its_message_and_value() {
    let error = jqlite::evaluate("error(\"boom\")", JqValue::Null).expect_err("the error reaches the host");
    assert!(matches!(error, JqError::Runtime { .. }));
    assert_eq!(error.message(), "boom");
    assert_eq!(error.to_string(), "boom");
    assert_eq!(error.value().map(JqValue::to_json).as_deref(), Some("\"boom\""));
}

#[test]
fn another_error_value_is_its_text_not_a_string() {
    let error = jqlite::evaluate("error({a: [1, nan]})", JqValue::Null).expect_err("the error reaches the host");
    assert_eq!(error.message(), "{\"a\":[1,null]} (not a string)");
    assert_eq!(error.value().map(JqValue::to_json).as_deref(), Some("{\"a\":[1,null]}"));
}

#[test]
fn a_builtin_error_is_its_message() {
    let error = jqlite::evaluate(". + 1", json("\"a\"")).expect_err("the error reaches the host");
    assert_eq!(error.message(), "string (\"a\") and number (1) cannot be added");
}

#[test]
fn streaming_gives_results_as_they_come_and_stops_when_asked() {
    let expression = jqlite::parse("range(1000000000)").expect("the expression parses");
    let mut seen = Vec::new();
    expression
        .for_each(JqValue::Null, None, |result| {
            seen.push(result.as_number());
            seen.len() < 3
        })
        .expect("stopping is not an error");
    assert_eq!(seen, [0.0, 1.0, 2.0]);
}

#[test]
fn streaming_delivers_results_before_an_error() {
    let mut seen = Vec::new();
    let error = jqlite::parse("1, 2, error(\"late\"), 3")
        .expect("the expression parses")
        .for_each(JqValue::Null, None, |result| {
            seen.push(result.to_json());
            true
        })
        .expect_err("the error reaches the host");
    assert_eq!(seen, ["1", "2"]);
    assert_eq!(error.message(), "late");
}

#[test]
fn halt_ends_normally() {
    assert_eq!(evaluate("1, halt, 2", "null"), ["1"]);
}

#[test]
fn halt_error_carries_the_value_and_the_code() {
    let mut seen = Vec::new();
    let error = jqlite::parse("1, ({a: 1} | halt_error(5)), 2")
        .expect("the expression parses")
        .for_each(JqValue::Null, None, |result| {
            seen.push(result.to_json());
            true
        })
        .expect_err("the halt reaches the host");
    assert_eq!(seen, ["1"]);
    match &error {
        JqError::Halt { message, value, exit_code } => {
            assert_eq!(*exit_code, 5);
            assert_eq!(value.to_json(), "{\"a\":1}");
            assert_eq!(message, "{\"a\":1} (not a string)");
        }
        other => panic!("a halt is expected, not {other:?}"),
    }
    assert_eq!(jqlite::evaluate("\"bye\" | halt_error(0)", JqValue::Null).expect_err("the halt reaches the host").message(), "bye");
}

#[test]
fn invalid_variable_names_are_rejected_before_evaluation() {
    for name in ["$root", "1x", "a-b", "", "ENV", "é"] {
        let expression = jqlite::parse("error(\"evaluated\")").expect("the expression parses");
        let error = expression.evaluate_with(JqValue::Null, &variables(&[(name, "1")])).expect_err(name);
        assert!(matches!(error, JqError::Variable { .. }), "{name}: {error:?}");
    }
}

#[test]
fn variables_are_given_per_evaluation() {
    let expression = jqlite::parse("$x + 1").expect("the expression parses");
    assert_eq!(texts(&expression.evaluate_with(JqValue::Null, &variables(&[("x", "1")])).expect("it evaluates")), ["2"]);
    assert_eq!(texts(&expression.evaluate_with(JqValue::Null, &variables(&[("x", "10"), ("unused", "0")])).expect("it evaluates")), ["11"]);
    assert_eq!(expression.evaluate(JqValue::Null).expect_err("the variable is missing").message(), "$x is not defined");
}

#[test]
fn host_variables_bind_where_nothing_else_does() {
    let cases = [
        // "root" binds $root; a missing variable is a run-time error that try catches.
        ("$root.a", "1"),
        ("try $missing catch .", "\"$missing is not defined\""),
        ("[$a, $_b1]", "[\"A\",true]"),
        // Bindings and parameters of the expression shadow a host variable of the same name.
        ("2 as $a | $a", "2"),
        ("def f($a): $a; f(3)", "3"),
        (". as [$a] | $a", "null"),
        ("reduce 4 as $a (0; $a), $a", "4, \"A\""),
        // $ENV is the environment, never a host variable; $__loc__ is the location.
        ("$__loc__.line", "1"),
    ];
    let given = variables(&[("root", "{\"a\":1}"), ("a", "\"A\""), ("_b1", "true")]);
    for (program, expected) in cases {
        let expression = jqlite::parse(program).expect(program);
        let results = texts(&expression.evaluate_with(JqValue::Null, &given).expect(program));
        assert_eq!(results.join(", "), expected, "{program}");
    }
}

#[test]
fn values_cross_the_boundary() {
    let cases = [
        // Non-finite numbers come out as jq prints them; large numbers stay doubles.
        ("[nan, infinite, -infinite]", "null", "[null,1.7976931348623157e+308,-1.7976931348623157e+308]"),
        (".", "100000000000000000000000000001", "1e+29"),
        (".", "1E400", "1.7976931348623157e+308"),
        (". + 1", "9007199254740993", "9007199254740992"),
        (".", "-0", "-0"),
        // A lone surrogate becomes U+FFFD on the way in (spec §4.1).
        (". | explode", "\"a\\ud800b\"", "[97,65533,98]"),
        ("keys", "{\"\\udc00\":1}", "[\"\u{FFFD}\"]"),
        ("[(.[0] | keys), .[1]]", "[{\"\\udc00x\":1,\"\\udc00x\":2}, 3]", "[[\"\u{FFFD}x\"],3]"),
        ("tojson", "{\"b\":1,\"a\":2,\"b\":3}", "\"{\\\"b\\\":3,\\\"a\\\":2}\""),
    ];
    for (expression, input, expected) in cases {
        assert_eq!(evaluate(expression, input), [expected], "{expression}");
    }
}

#[test]
fn values_are_built_from_host_values() {
    let object: JqValue =
        [("b", JqValue::from(1)), ("a", JqValue::from("x")), ("b", JqValue::from(vec![JqValue::from(true), JqValue::from(None::<f64>)]))].into_iter().collect();
    assert_eq!(object.to_json(), "{\"b\":[true,null],\"a\":\"x\"}");
    assert_eq!("[1, 2.5]".parse::<JqValue>().expect("the text is JSON").to_json(), "[1,2.5]");
    assert_eq!(JqValue::from(1.5).to_string(), "1.5");
    match JqValue::parse("[1,") {
        Err(JqError::Json { message }) => assert_eq!(message, "Unfinished JSON term at EOF at line 1, column 3 (while parsing '[1,')"),
        other => panic!("a JSON error is expected, not {other:?}"),
    }
}

#[test]
fn results_do_not_depend_on_the_input() {
    let input = json("{\"a\":{\"b\":[1,2]}}");
    let results = jqlite::evaluate(".a, .a.b[], (.a.b |= . + [3])", input.clone()).expect("it evaluates");
    drop(input);
    assert_eq!(texts(&results), ["{\"b\":[1,2]}", "1", "2", "{\"a\":{\"b\":[1,2,3]}}"]);
}

#[test]
fn deep_input_and_output_convert_without_recursion() {
    let text = "[".repeat(5000) + &"]".repeat(5000);
    let result = support::on_thread(256 * 1024, move || {
        let results = jqlite::evaluate(".", json(&text)).expect("it evaluates");
        (results[0].to_json() == text, results.len())
    });
    assert_eq!(result, (true, 1));
}

#[test]
fn one_expression_evaluates_concurrently() {
    let expression = jqlite::parse("def f: if . == 0 then $k else (. - 1 | f) end; [.[] | . * $k] | add + (40 | f) - $k").expect("the expression parses");
    let input = json("[1,2,3]");
    let results: Vec<String> = std::thread::scope(|scope| {
        let handles: Vec<_> = (0..64)
            .map(|i| {
                let (expression, input) = (&expression, &input);
                scope.spawn(move || {
                    let given = HashMap::from([("k".to_owned(), JqValue::from(i))]);
                    let mut last = String::new();
                    for _ in 0..50 {
                        last = expression.evaluate_with(input.clone(), &given).expect("it evaluates")[0].to_json();
                    }
                    last
                })
            })
            .collect();
        handles.into_iter().map(|handle| handle.join().expect("the thread ends")).collect()
    });
    for (i, result) in results.iter().enumerate() {
        assert_eq!(*result, (6 * i).to_string());
    }
}

// An expression moves to other threads and outlives the options it was parsed with.
#[test]
fn an_expression_moves_between_threads() {
    let expression = {
        let options = JqOptions { environment: Some(vec![("NAME".to_owned(), "jq".to_owned())]), ..JqOptions::default() };
        Arc::new(jqlite::parse_with("[.[] | test(\"^a\") and ($ENV.NAME == \"jq\")]", &options).expect("the expression parses"))
    };
    let handles: Vec<_> = (0..8)
        .map(|_| {
            let expression = expression.clone();
            std::thread::spawn(move || expression.evaluate(json("[\"ab\",\"ba\"]")).expect("it evaluates")[0].to_json())
        })
        .collect();
    for handle in handles {
        assert_eq!(handle.join().expect("the thread ends"), "[true,false]");
    }
}

// ---- Modules through the boundary ----

fn modules_directory() -> std::path::PathBuf {
    support::spec_directory().join("tests").join("modules")
}

fn with_files() -> JqOptions {
    JqOptions { resolver: Some(Arc::new(JqFileResolver::new(modules_directory()))), ..JqOptions::default() }
}

#[test]
fn the_file_resolver_finds_modules_beside_their_importer() {
    let options = with_files();
    let expression = jqlite::parse_with("import \"a\" as a; import \"values\" as $v; include \"one\"; [a::f, $v, k]", &options).expect("the modules are found");
    let results = texts(&expression.evaluate(JqValue::Null).expect("it evaluates"));
    assert_eq!(results.len(), 1);
    assert!(results[0].starts_with("[\"a.f\","), "{}", results[0]);
    // A path with an extension is taken as it is.
    assert!(jqlite::parse_with("import \"a.jq\" as a; a::f", &options).is_ok());
    assert_eq!(JqFileResolver::new(modules_directory()).base_directory(), std::path::absolute(modules_directory()).expect("the path is absolute"));
}

#[test]
fn module_errors_name_the_module() {
    let options = with_files();
    match jqlite::parse_with("import \"broken\" as b; 1", &options) {
        Err(JqError::Parse { message, line, column }) => {
            assert_eq!((line, column), (1, 15));
            assert!(message.starts_with("Expected ')' but found ';' at "), "{message}");
            assert!(message.ends_with("broken.jq, line 1, column 15"), "{message}");
        }
        other => panic!("a parse error is expected, not {:?}", other.map(|_| "an expression")),
    }
    let error = jqlite::parse_with("import \"nope\" as n; 1", &options).err().expect("the module is missing");
    assert_eq!(error.message(), "module not found: nope at line 1, column 1");
}

#[test]
fn modulemeta_asks_the_resolver() {
    let expression = jqlite::parse_with("modulemeta | .defs", &with_files()).expect("the expression parses");
    assert_eq!(texts(&expression.evaluate(json("\"one\"")).expect("it evaluates")), ["[\"k/0\"]"]);
    let error = expression.evaluate(json("\"nope\"")).expect_err("the module is missing");
    assert_eq!(error.message(), "module not found: nope");
    // Without a resolver no module exists.
    assert_eq!(evaluate("try modulemeta catch .", "\"one\""), ["\"module not found: one\""]);
}

// A resolver of the host: modules in memory, which records what it is asked.
struct Memory {
    modules: HashMap<&'static str, &'static str>,
    asked: Mutex<Vec<String>>,
}

impl JqResolver for Memory {
    fn canonical_path(&self, path: &str, kind: JqModuleKind, metadata: Option<&JqValue>, from_path: Option<&str>) -> Option<String> {
        let metadata = metadata.map_or("-".to_owned(), JqValue::to_json);
        self.asked.lock().expect("the lock is taken").push(format!("{path} {kind:?} {metadata} {}", from_path.unwrap_or("-")));
        let identity = format!("mem:{path}");
        self.modules.contains_key(identity.as_str()).then_some(identity)
    }

    fn read(&self, canonical_path: &str) -> std::io::Result<String> {
        match self.modules.get(canonical_path) {
            Some(&"") => Err(std::io::Error::other("the module cannot be read")),
            Some(text) => Ok((*text).to_owned()),
            None => Err(std::io::Error::new(std::io::ErrorKind::NotFound, "no such module")),
        }
    }
}

#[test]
fn a_resolver_of_the_host_gives_the_modules() {
    let memory = Arc::new(Memory {
        modules: HashMap::from([
            ("mem:m", "import \"n\" as n {\"x\": 1}; def f: n::g + 1;"),
            ("mem:n", "def g: 41;"),
            ("mem:d", "1 2 [3]"),
            ("mem:locked", ""),
        ]),
        asked: Mutex::new(Vec::new()),
    });
    let options = JqOptions { resolver: Some(memory.clone()), ..JqOptions::default() };
    let expression = jqlite::parse_with("import \"m\" as m; import \"d\" as $d; [m::f, $d, $d::d]", &options).expect("the modules are found");
    assert_eq!(texts(&expression.evaluate(JqValue::Null).expect("it evaluates")), ["[42,[1,2,[3]],[1,2,[3]]]"]);
    assert_eq!(*memory.asked.lock().expect("the lock is taken"), ["m Code - -", "n Code {\"x\":1} mem:m", "d Data - -"]);
    // An I/O error of the resolver is the binding error.
    let error = jqlite::parse_with("import \"locked\" as l; 1", &options).err().expect("the module cannot be read");
    assert_eq!(error.message(), "the module cannot be read at line 1, column 1");
}
