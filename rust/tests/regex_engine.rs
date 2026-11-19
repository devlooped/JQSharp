//! The regex engine (`dotnet/Tests/Core/Builtins/RegexEngineTests.cs`): what it finds is what the
//! simplest matcher of the same programs finds (`support/backtracking_matcher.rs`), in time linear
//! in the input. The corpus shows what the programs mean (spec §9.10); the memory of a search is
//! measured in `tests/linear.rs`. Beyond the reference: the deepest patterns of spec §11 on a small
//! stack.

mod support;

use std::time::Instant;

use std::sync::Arc;

use jqlite::builtins::regex_engine::{self, RegexMatcher, RegexProgram};
use jqlite::builtins::regex_syntax::{self, MAX_GROUPS, MAX_NESTING, MAX_SIZE, Options};
use jqlite::values::{JqValue, value_text};

use support::backtracking_matcher;
use support::regex_fuzz::RegexFuzz;

const MEGABYTE: usize = 1024 * 1024;
const SMALL: usize = 256 * 1024;

const PLAIN: Options = Options { extended: false, dot_all: false, ignore_case: false };

// The one output of a program, or its outputs and error joined.
fn line(program: &str, input: &str) -> String {
    support::outputs(program, input).join("\n")
}

fn random_patterns_match_as_the_backtracking_matcher_does(seed: u32) {
    let mut fuzz = RegexFuzz::new(seed);
    let mut compared = 0;
    for _ in 0..20000 {
        let source = fuzz.pattern(3);
        let options = Options { dot_all: fuzz.next(2) == 0, ..PLAIN };
        let translation = regex_syntax::try_translate(&source, options).unwrap_or_else(|reason| panic!("{source}: {reason}"));
        let mut matcher = RegexMatcher::new(Arc::new(RegexProgram::new(translation.clone())));
        let width = 2 * (translation.groups.len() + 1);
        for _ in 0..4 {
            let text = fuzz.input(7);
            // A search starts between code points.
            for start in (0..=text.len()).filter(|&start| text.is_char_boundary(start)) {
                let mut expected = vec![0; width];
                let Some(found) = backtracking_matcher::try_match(&translation, &text, start, &mut expected, 200000) else {
                    continue;
                };
                let place = format!("{source} on {text:?} from {start}");
                let mut spans = vec![0; width];
                assert_eq!(matcher.try_match(&text, start, &mut spans), found, "{place}");
                if found {
                    assert_eq!(spans, expected, "{place}");
                }
                // Only the match; only whether there is one.
                let mut span = [0; 2];
                assert_eq!(matcher.try_match(&text, start, &mut span), found, "{place}");
                if found {
                    assert_eq!(span, expected[..2], "{place}");
                }
                assert_eq!(matcher.try_match(&text, start, &mut []), found, "{place}");
                compared += 1;
            }
        }
    }

    assert!(compared > 300000, "{compared}");
}

#[test]
fn random_patterns_match_as_the_backtracking_matcher_does_1() {
    random_patterns_match_as_the_backtracking_matcher_does(1);
}

#[test]
fn random_patterns_match_as_the_backtracking_matcher_does_7() {
    random_patterns_match_as_the_backtracking_matcher_does(7);
}

#[test]
fn random_patterns_match_as_the_backtracking_matcher_does_12345() {
    random_patterns_match_as_the_backtracking_matcher_does(12345);
}

// The time a case may take: super-linear time on these inputs takes minutes. A build without
// optimizations is about ten times slower than a release build.
const BOUND_MS: u128 = if cfg!(debug_assertions) { 10_000 } else { 2_000 };

#[test]
fn linear_time() {
    // Patterns a backtracking matcher takes exponential or quadratic time for, and long inputs.
    let cases = [
        ("\"a\" * 28 + \"c\" | test(\"^(a*)*b$\")", "false"),
        ("\"a\" * 5000 + \"c\" | test(\"^(a*)*b$\")", "false"),
        ("\"ab\" * 2000000 | test(\"^(a|b)*$\")", "true"),
        ("\"a\" * 200000 | test(\"a*b\")", "false"),
        ("\"a\" * 200000 | [match(\"a*b\")] | length", "0"),
        ("\"x\" * 100000 | test(\"(x+x+)+y\")", "false"),
        ("\"a\" * 40 | test(\"(a?){40}a{40}\")", "true"),
        ("\"ab\" * 2000000 | match(\"^(a|b)*$\") | .captures[0].offset", "3999999"),
        ("\"a\" * 100000 | [match(\"(a|aa)*?$\"; \"g\") | .length]", "[100000,0]"),
    ];
    for (program, expected) in cases {
        let clock = Instant::now();
        assert_eq!(line(program, "null"), expected, "{program}");
        let elapsed = clock.elapsed().as_millis();
        println!("{elapsed:>6} ms  {program}");
        assert!(elapsed < BOUND_MS, "{program}: {elapsed} ms");
    }
}

#[test]
fn the_largest_patterns_of_spec_11_are_programs_the_matcher_runs() {
    // The limits are the translation's, and the corpus has their boundaries. What is the engine's:
    // within them a program has at most five instructions per unit of the pattern's size, and a
    // state is an instruction at a level (nested loops that can match nothing have levels).
    let compiled = |source: &str| regex_engine::try_compile(source, PLAIN).unwrap_or_else(|reason| panic!("{reason}"));
    let nested = |levels: usize, inner: &str| format!("{}{inner}{}", "(?:".repeat(levels), ")*".repeat(levels));

    assert_eq!([MAX_SIZE, MAX_NESTING, MAX_GROUPS], [10000, 100, 1000]);
    // The most instructions for a size: an empty group repeated, each copy optional and with its mark.
    let copies = regex_syntax::try_translate(&format!("{}(){{0,990}}", "(){0,1000}".repeat(9)), PLAIN).expect("the pattern translates");
    assert_eq!(copies.ops.len(), 5 * (MAX_SIZE - 10) + 3);
    assert!(RegexMatcher::new(Arc::new(RegexProgram::new(copies))).try_match("a", 0, &mut [0; 2]));
    // The most states: every instruction inside a hundred loops that can match nothing.
    let deep = compiled(&nested(MAX_NESTING, &"a?".repeat(4900)));
    assert!((1_000_001..=1_999_999).contains(&deep.states), "{}", deep.states);
    assert!(RegexMatcher::new(deep).try_match(&format!("{}b", "a".repeat(100)), 0, &mut [0; 2]));
    assert_eq!(line(&format!("\"a\" * 300 | [match(\"{}\"; \"g\") | .length]", nested(MAX_NESTING, "a*")), "null"), "[300,0]");
    // The most capture slots: a thousand groups, each thread with its own.
    let groups = compiled(&"(a)".repeat(MAX_GROUPS));
    assert_eq!(groups.slots, 2 * (MAX_GROUPS + 1));
    let mut spans = vec![0; groups.slots];
    assert!(RegexMatcher::new(Arc::clone(&groups)).try_match(&"a".repeat(MAX_GROUPS + 5), 0, &mut spans));
    assert_eq!([&spans[..4], &spans[spans.len() - 2..]].concat(), [0, 1000, 0, 1, 999, 1000]);
    // Past a limit the translation fails, with the fixed reason.
    assert_eq!(regex_engine::try_compile(&"(a)".repeat(MAX_GROUPS + 1), PLAIN).err().as_deref(), Some("the pattern is too large"));
}

// test(pattern) on "a", or the error it raises, on a thread of `stack` bytes; the pattern is the
// input of the program.
fn tested(pattern: String, program: &'static str, stack: usize) -> String {
    support::on_thread(stack, move || {
        let input = value_text::to_json(&JqValue::from_string(pattern));
        support::outputs(program, &input).join(" ")
    })
}

const TEST: &str = ". as $re | \"a\" | try test($re) catch .";
const TOO_LARGE: &str = "\"Regex failure: the pattern is too large\"";

#[test]
fn a_pattern_nests_a_hundred_groups_on_a_small_stack() {
    // The translator and the matcher do not recurse: the deepest patterns run on a stack of 256 KB.
    let nested = |levels: usize, open: &str, close: &str| format!("{}a{}", open.repeat(levels), close.repeat(levels));
    for (open, close) in [("(", ")"), ("(?:", ")")] {
        for stack in [MEGABYTE, SMALL] {
            assert_eq!(tested(nested(100, open, close), TEST, stack), "true", "{open} 100");
            assert_eq!(tested(nested(101, open, close), TEST, stack), TOO_LARGE, "{open} 101");
        }
    }
    assert_eq!(tested(nested(100, "(", ")"), ". as $re | \"a\" | match($re) | .captures | length", SMALL), "100");
    // Levels that are an alternative, a sequence, a repetition and a group at once.
    assert_eq!(tested(nested(100, "(b|c", ")*?d|e"), TEST, SMALL), "false");
    assert_eq!(tested(nested(100, "(b|c", ")*?d|e"), ". as $re | \"c\" * 100 + \"a\" + \"d\" * 100 | test($re)", SMALL), "true");
    assert_eq!(tested(nested(101, "(b|c", ")*?d|e"), TEST, SMALL), TOO_LARGE);
    assert_eq!(tested(nested(100_000, "(", ")"), TEST, SMALL), TOO_LARGE);
    // The limit is on the groups open at once, not on their number.
    assert_eq!(tested("(?:a)".repeat(5000), ". as $re | \"a\" * 5000 | test($re)", MEGABYTE), "true");
    assert_eq!(tested("(a)".repeat(300), ". as $re | \"a\" * 300 | test($re)", MEGABYTE), "true");
    // The reason is the same for every builtin that takes a pattern, and is caught like any error.
    assert_eq!(tested(nested(101, "(", ")"), ". as $re | \"a\" | [try match($re) catch ., try sub($re; \"\") catch .] | unique | .[]", MEGABYTE), TOO_LARGE);
}
