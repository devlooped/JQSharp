//! What the test files share: the corpus reader (spec/tests/README.md), the module sources of
//! the tests, a fixed clock, and helpers to run programs on the core.
#![allow(dead_code)]

pub mod ast_printer;
pub mod backtracking_matcher;
pub mod bound_printer;
pub mod pattern;
pub mod regex_fuzz;

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};

use jqlite::binding::{ModuleKind, ModuleSource, Resolver};
use jqlite::runtime::{Clock, EvaluationOptions, Interpreter, Outcome};
use jqlite::values::{JqValue, value_text};

/// The repository's `spec` folder, shared by every port.
pub fn spec_directory() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("..").join("spec")
}

/// One case of a corpus file.
pub struct Case {
    pub program: String,
    pub input: String,
    pub expected: Vec<String>,
    pub should_fail: bool,
    /// The message lines of a `%%FAIL` case; `None` with `IGNORE MSG`.
    pub expected_error: Option<Vec<String>>,
    pub line: usize,
}

fn is_fail_marker(line: &str) -> bool {
    line.trim_start().starts_with("%%FAIL")
}

fn is_separator(line: &str) -> bool {
    let trimmed = line.trim();
    trimmed.is_empty() || trimmed.starts_with('#')
}

/// Reads a corpus file: lines end at LF (a CR before it is part of the line ending); a lone CR is
/// part of the line.
pub fn parse_corpus(text: &str) -> Vec<Case> {
    let lines: Vec<&str> = text.split('\n').map(|line| line.strip_suffix('\r').unwrap_or(line)).collect();
    let mut cases = Vec::new();
    let mut index = 0;
    while index < lines.len() {
        if is_separator(lines[index]) {
            index += 1;
            continue;
        }

        if is_fail_marker(lines[index]) {
            // "%%FAIL IGNORE MSG": the program must not parse, whatever the message.
            let ignore_message = lines[index].contains("IGNORE MSG");
            index += 1;
            while index < lines.len() && is_separator(lines[index]) {
                index += 1;
            }
            if index >= lines.len() {
                break;
            }
            let program = lines[index].to_owned();
            let line = index + 1;
            index += 1;
            let mut error_lines = Vec::new();
            while index < lines.len() && !is_separator(lines[index]) && !is_fail_marker(lines[index]) {
                error_lines.push(lines[index].to_owned());
                index += 1;
            }
            cases.push(Case {
                program,
                input: String::new(),
                expected: Vec::new(),
                should_fail: true,
                expected_error: if ignore_message { None } else { Some(error_lines) },
                line,
            });
            continue;
        }

        let program = lines[index].to_owned();
        let line = index + 1;
        index += 1;
        assert!(index < lines.len(), "Missing input line for test program at line {line}.");
        let input = lines[index].to_owned();
        index += 1;
        let mut expected = Vec::new();
        while index < lines.len() && !is_separator(lines[index]) && !is_fail_marker(lines[index]) {
            expected.push(lines[index].to_owned());
            index += 1;
        }
        cases.push(Case { program, input, expected, should_fail: false, expected_error: None, line });
    }

    cases
}

/// The corpus files, by name, each with its cases.
pub fn corpus() -> Vec<(String, Vec<Case>)> {
    let directory = spec_directory().join("tests");
    let mut files: Vec<PathBuf> = std::fs::read_dir(&directory)
        .unwrap_or_else(|error| panic!("{}: {error}", directory.display()))
        .filter_map(|entry| entry.ok().map(|entry| entry.path()))
        .filter(|path| path.extension().is_some_and(|extension| extension == "test"))
        .collect();
    files.sort();
    files
        .into_iter()
        .map(|path| {
            let name = path.file_name().map(|name| name.to_string_lossy().into_owned()).unwrap_or_default();
            let text = std::fs::read_to_string(&path).unwrap_or_else(|error| panic!("{}: {error}", path.display()));
            (name, parse_corpus(&text))
        })
        .collect()
}

/// Numbers as doubles (-0 equals 0), strings by code points, arrays in order, objects with the
/// same keys in the same order (spec §14.2).
pub fn same(left: &JqValue, right: &JqValue) -> bool {
    match (left, right) {
        (JqValue::Number(a), JqValue::Number(b)) => a == b,
        (JqValue::String(a), JqValue::String(b)) => a == b,
        (JqValue::Array(a), JqValue::Array(b)) => a.count() == b.count() && a.iter().zip(b.iter()).all(|(x, y)| same(x, y)),
        (JqValue::Object(a), JqValue::Object(b)) => {
            a.count() == b.count() && a.entries().iter().zip(b.entries()).all(|((key, x), (other, y))| key == other && same(x, y))
        }
        _ => left.kind() == right.kind(),
    }
}

/// The outputs, as text, against the expected lines: each read back and compared structurally.
pub fn compare(expected: &[String], actual: &[String]) -> Option<String> {
    let equal = actual.len() == expected.len()
        && expected.iter().zip(actual).all(|(expected, actual)| match (value_text::try_parse(expected), value_text::try_parse(actual)) {
            (Ok(left), Ok(right)) => same(&left, &right),
            _ => false,
        });
    if equal { None } else { Some(format!("expected [{}] but got [{}]", expected.join(", "), actual.join(", "))) }
}

/// The message lines of a `%%FAIL` case that are fixed texts of spec §10.2 must be contained in
/// the error; the other lines are jq's own wording and are not compared (spec §14.1).
pub fn missing_fixed_text(expected_error: Option<&Vec<String>>, message: &str) -> Option<String> {
    let fixed = pattern::Pattern::new(
        r"^(\S+/\d+ is not defined|\$\*label-\S+ is not defined|\$\S+::\S+ is not defined|Cannot use .+ as object key|May need parentheses around object key expression|Module metadata must be constant|Module metadata must be an object|Import path must be constant|Invalid escape)$",
    );
    for line in expected_error.into_iter().flatten() {
        if fixed.is_match(line) && !message.contains(line.as_str()) {
            return Some(format!("expected an error containing '{line}', got '{message}'"));
        }
    }
    None
}

/// A module source over a directory, with the rule of spec/tests/README.md: the path `p` imported
/// from a file names `p.jq` (code) or `p.json` (data) in that file's directory, the main
/// program's directory being the root. Identities are paths relative to the root.
pub struct DirectoryModules {
    root: PathBuf,
    reads: AtomicUsize,
}

impl DirectoryModules {
    pub fn new(root: PathBuf) -> DirectoryModules {
        DirectoryModules { root, reads: AtomicUsize::new(0) }
    }

    /// The modules of the corpus.
    pub fn corpus() -> DirectoryModules {
        DirectoryModules::new(spec_directory().join("tests").join("modules"))
    }

    /// How many module files were read.
    pub fn reads(&self) -> usize {
        self.reads.load(Ordering::SeqCst)
    }
}

impl ModuleSource for DirectoryModules {
    fn try_resolve(&self, path: &str, kind: ModuleKind, _: Option<&JqValue>, from: Option<&str>) -> Result<String, String> {
        let directory = from.and_then(|from| from.rfind('/').map(|slash| &from[..=slash])).unwrap_or("");
        let identity = format!("{directory}{path}{}", if kind == ModuleKind::Code { ".jq" } else { ".json" });
        if self.root.join(&identity).is_file() { Ok(identity) } else { Err(format!("module not found: {path}")) }
    }

    fn try_read(&self, identity: &str) -> Result<String, String> {
        self.reads.fetch_add(1, Ordering::SeqCst);
        std::fs::read_to_string(self.root.join(identity)).map_err(|error| error.to_string())
    }
}

/// A module source over a map of identities and texts; a path is its identity without the extension.
pub struct MemoryModules {
    modules: HashMap<String, String>,
}

impl MemoryModules {
    pub fn new(modules: &[(&str, &str)]) -> MemoryModules {
        MemoryModules { modules: modules.iter().map(|(identity, text)| ((*identity).to_owned(), (*text).to_owned())).collect() }
    }
}

impl ModuleSource for MemoryModules {
    fn try_resolve(&self, path: &str, kind: ModuleKind, _: Option<&JqValue>, _: Option<&str>) -> Result<String, String> {
        let identity = format!("{path}{}", if kind == ModuleKind::Code { ".jq" } else { ".json" });
        if self.modules.contains_key(&identity) { Ok(identity) } else { Err(format!("module not found: {path}")) }
    }

    fn try_read(&self, identity: &str) -> Result<String, String> {
        self.modules.get(identity).cloned().ok_or_else(|| format!("module not found: {identity}"))
    }
}

/// UTC+02:00 without DST or abbreviation (spec §14.2).
pub struct FixedZoneClock;

impl Clock for FixedZoneClock {
    fn now(&self) -> f64 {
        std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map_or(0.0, |elapsed| elapsed.as_secs_f64())
    }

    fn offset(&self, _: f64) -> i32 {
        7200
    }

    fn abbreviation(&self, _: f64) -> Option<String> {
        None
    }
}

/// Reads a JSON text with the engine's reader.
pub fn json(text: &str) -> JqValue {
    value_text::try_parse(text).unwrap_or_else(|error| panic!("bad JSON {text}: {error}"))
}

/// How a program ran on the core: its outputs as canonical text, and how it ended.
pub struct Ran {
    pub outputs: Vec<String>,
    pub outcome: Outcome,
}

/// Compiles and runs a program on the core with the default options; panics when it does not compile.
pub fn evaluate(program: &str, input: &str) -> Ran {
    evaluate_with(program, input, &EvaluationOptions::default(), true)
}

pub fn evaluate_with(program: &str, input: &str, options: &EvaluationOptions<'_>, native_overrides: bool) -> Ran {
    let bound = Resolver::try_compile(program, native_overrides, None)
        .unwrap_or_else(|error| panic!("{program}: {} at {}:{}", error.message, error.line, error.column));
    let mut outputs = Vec::new();
    let outcome = Interpreter::run(&bound, json(input), options, &mut |value| {
        outputs.push(value_text::to_json(&value));
        true
    });
    Ran { outputs, outcome }
}

/// The outputs of a program that ends normally, as canonical text.
pub fn outputs(program: &str, input: &str) -> Vec<String> {
    let ran = evaluate(program, input);
    match ran.outcome {
        Outcome::Completed => ran.outputs,
        other => panic!("{program}: ended with {other:?} after {:?}", ran.outputs),
    }
}

/// Runs `test` on a thread with a stack of `stack_size` bytes and returns its result.
pub fn on_thread<T: Send + 'static>(stack_size: usize, test: impl FnOnce() -> T + Send + 'static) -> T {
    std::thread::Builder::new().stack_size(stack_size).spawn(test).expect("the thread starts").join().expect("the thread ends without a panic")
}
