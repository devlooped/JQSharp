//! Runs every case of the jqlite conformance corpus (spec/tests/*.test), by the rules of
//! spec/tests/README.md: through the public API (`jqlite::parse_with`, `JqExpression`), and
//! through the core with every prelude builtin bound to its definition, to check that the natives
//! that override some change nothing. Every case must pass both ways.

mod support;

use std::sync::Arc;

use jqlite::binding::Resolver;
use jqlite::runtime::{EvaluationOptions, Interpreter, Outcome};
use jqlite::values::value_text;
use jqlite::{FixedOffsetClock, JqError, JqFileResolver, JqOptions, JqValue};

use support::{Case, DirectoryModules, FixedZoneClock};

// The host of spec §14.2: no environment variables, a zone at UTC+02:00 without DST or
// abbreviation, the default depth, and the module files of modules/.
fn run_core(case: &Case, native_overrides: bool, modules: &Arc<DirectoryModules>) -> Option<String> {
    let bound = match Resolver::try_compile(&case.program, native_overrides, Some(modules.clone())) {
        Ok(bound) => bound,
        Err(error) if case.should_fail => return support::missing_fixed_text(case.expected_error.as_ref(), &error.message),
        Err(error) => return Some(format!("{} at {}:{}", error.message, error.line, error.column)),
    };
    if case.should_fail {
        return Some("the program parses and binds but must not".to_owned());
    }
    let value = match value_text::try_parse(&case.input) {
        Ok(value) => value,
        Err(error) => return Some(format!("bad input: {error}")),
    };

    let mut outputs = Vec::new();
    let options = EvaluationOptions { max_depth: 1000, clock: Some(&FixedZoneClock), ..EvaluationOptions::default() };
    let outcome = Interpreter::run(&bound, value, &options, &mut |output| {
        outputs.push(value_text::to_json(&output));
        true
    });
    match outcome {
        Outcome::Error(error) => return Some(format!("error {} after [{}]", value_text::to_json(&error), outputs.join(", "))),
        Outcome::Halt { value, code } if value.is_some() || code != 0 => return Some(format!("halt_error {code} after [{}]", outputs.join(", "))),
        _ => {}
    }
    support::compare(&case.expected, &outputs)
}

// The same host, for the public API.
fn options() -> JqOptions {
    JqOptions {
        environment: Some(Vec::new()),
        clock: Arc::new(FixedOffsetClock { offset_seconds: 7200 }),
        max_depth: 1000,
        resolver: Some(Arc::new(JqFileResolver::new(support::spec_directory().join("tests").join("modules")))),
        ..JqOptions::default()
    }
}

fn run_api(case: &Case, options: &JqOptions) -> Option<String> {
    let expression = match jqlite::parse_with(&case.program, options) {
        Ok(expression) => expression,
        Err(error) if case.should_fail => return support::missing_fixed_text(case.expected_error.as_ref(), error.message()),
        Err(error) => return Some(error.to_string()),
    };
    if case.should_fail {
        return Some("the program parses and binds but must not".to_owned());
    }
    let input = match JqValue::parse(&case.input) {
        Ok(input) => input,
        Err(error) => return Some(format!("bad input: {error}")),
    };

    let mut outputs = Vec::new();
    let result = expression.for_each(input, None, |result| {
        outputs.push(result.to_json());
        true
    });
    if let Err(error) = result {
        let kind = if matches!(error, JqError::Halt { .. }) { "halt_error" } else { "error" };
        return Some(format!("{kind} {error} after [{}]", outputs.join(", ")));
    }
    support::compare(&case.expected, &outputs)
}

fn check(name: &str, mut run: impl FnMut(&Case) -> Option<String>) {
    let mut failures = Vec::new();
    let mut count = 0;
    for (file, cases) in support::corpus() {
        for case in &cases {
            count += 1;
            if let Some(failure) = run(case) {
                failures.push(format!("{file}:{}: {}: {failure}", case.line, case.program));
            }
        }
    }

    println!("corpus ({name}): {} of {count} cases pass", count - failures.len());
    for failure in &failures {
        println!("{failure}");
    }
    assert!(failures.is_empty(), "{} of {count} cases fail", failures.len());
    assert!(count > 1000, "the corpus was not found");
}

#[test]
fn corpus_case() {
    let options = options();
    check("public API", |case| run_api(case, &options));
}

#[test]
fn corpus_case_without_native_overrides() {
    let modules = Arc::new(DirectoryModules::corpus());
    check("core without native overrides", |case| run_core(case, false, &modules));
}
