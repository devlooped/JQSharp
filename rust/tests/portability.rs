//! The portability rules of AGENTS.md as tests, as `dotnet/Tests/PortabilityTests.cs` has them
//! for the reference: the core (the five layers under `src/`) must read the same in every
//! language, so a host feature in its code fails here, naming the file and the line. Comments and
//! string literals are not code and are not matched. Where a rule has exceptions, they are listed
//! by file with the reason: a new use anywhere else must be justified by adding it here. The
//! patterns of the rules run on the crate's own regex engine (`support/pattern.rs`).

mod support;

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

use support::pattern::Pattern;

/// The layers of the core, in order: each uses only the ones before it.
const LAYERS: [&str; 5] = ["values", "syntax", "binding", "runtime", "builtins"];

/// A rule: its name, the pattern on code, and the files (relative to `src/`) allowed to match,
/// each with the reason.
struct Rule {
    name: &'static str,
    pattern: &'static str,
    allowed: &'static [(&'static str, &'static str)],
}

const CORE_RULES: &[Rule] = &[
    Rule { name: "unsafe code", pattern: r"\bunsafe\b", allowed: &[] },
    Rule { name: "a host JSON library", pattern: r"\bserde\w*\b|\bsimd_json\b|\bjson::|\bcrate::json\b", allowed: &[] },
    Rule { name: "the host regex engine", pattern: r"\bregex::|\buse\s+regex\b|\bRegex(Builder|Set)?\b|\bregex_automata\b|\bCaptureLocations\b", allowed: &[] },
    Rule { name: "the stacker crate", pattern: r"\bstacker\b", allowed: &[] },
    Rule {
        name: "the stack guard",
        pattern: r"\bstack_guard\b",
        allowed: &[
            ("syntax/parser.rs", "a level of nesting takes several KB of stack: 1,000 levels do not fit a 1 MB thread"),
            ("runtime/interpreter.rs", "every evaluation step and every output: only MaxDepth stops a recursion"),
        ],
    },
    Rule { name: "the host clock", pattern: r"\bstd::time\b|\bSystemTime\b|\bInstant\b|\bDuration\b|\bchrono\b", allowed: &[] },
    Rule { name: "host files, processes, environment or network", pattern: r"\bstd::(fs|io|net|process|env|path)\b|\benv!\s*\(|\benv::var", allowed: &[] },
    Rule {
        name: "iterators, generators, channels or threads to produce outputs",
        pattern: r"\bimpl\s+(\w+::)*Iterator\b|\bdyn\s+(\w+::)*Iterator\b|\bfor\s+Iterator\b|\bmpsc\b|\bstd::thread\b|\bthread::(spawn|scope|Builder)\b|\basync\b|\bawait\b|\byield\b",
        allowed: &[],
    },
    Rule { name: "reflection", pattern: r"\bstd::any\b|\bdyn\s+Any\b|\btype_name(_of_val)?::|\bTypeId\b|\bdowncast\w*\b", allowed: &[] },
    Rule { name: "panics as control flow", pattern: r"\bcatch_unwind\b|\bresume_unwind\b|\bpanic::|\bset_hook\b", allowed: &[] },
    Rule {
        name: "an embedded file",
        pattern: r"\binclude_(str|bytes)!",
        allowed: &[("binding/prelude.rs", "reads the embedded prelude: spec/prelude.jq, unchanged")],
    },
    Rule { name: "the host boundary", pattern: r"\bcrate::(host|jq|jq_error|jq_expression|jq_file_resolver|jq_options|jq_resolver)\b", allowed: &[] },
    Rule { name: "grouped imports of the crate (they would hide a layer from the rules below)", pattern: r"\bcrate::\{", allowed: &[] },
    Rule {
        // `.expect("…")` with its message; the parser's own `expect(kind, what)` is not one.
        name: "panic (the reference's throw)",
        pattern: r#"\bpanic!|\bunreachable!|\btodo!|\bunimplemented!|\b(debug_)?assert(_eq|_ne)?!|\.unwrap\(\)|\.expect\(""#,
        allowed: &[
            ("binding/prelude.rs", "the embedded prelude does not bind: a build defect"),
            ("builtins/native_table.rs", "start-up checks of the registrations against the catalogue"),
            ("runtime/env.rs", "a frame of another kind than the resolver numbered: a defect of the binding"),
            ("values/jq_array.rs", "range guard of the host caller"),
            ("values/jq_value.rs", "the as_* accessors on a value of another kind: a programming error"),
            ("values/value_ops.rs", "the explicit stack of deep_merge holds what its loop pushed"),
            ("values/value_text.rs", "the explicit stack of the JSON reader: a container is open when one closes"),
        ],
    },
    Rule {
        // A lazy field of one value or of one evaluation (OnceLock, OnceCell) is not such state.
        name: "state of the process or of the thread",
        pattern: r"\bthread_local!|\bstatic\s+(mut\s+)?[A-Z_0-9]+\s*:|\bLazyLock\b|\bMutex\b|\bRwLock\b|\bAtomic\w+\b",
        allowed: &[
            ("binding/prelude.rs", "the prelude, bound once per process and shared: immutable"),
            ("builtins/core_natives.rs", "the list that `builtins` gives, made once: immutable"),
            ("builtins/native_table.rs", "the table of the native implementations, made once: immutable"),
            ("builtins/natives.rs", "the native catalogue, made once: immutable"),
            ("builtins/regex_engine.rs", "the cache of compiled programs (immutable), behind a mutex"),
            ("builtins/regex_natives.rs", "the searches prepared last, each with its matcher, and the key strings of match objects, per thread"),
            ("values/jq_value.rs", "the depth of nested drops, per thread: values are released without recursion"),
            ("values/value_text.rs", "the key strings the JSON reader shares, per thread"),
        ],
    },
];

fn source_directory() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("src")
}

// The Rust files under a directory, as paths relative to it with forward slashes, in order.
fn files_of(directory: &Path) -> Vec<String> {
    let mut found = Vec::new();
    let mut pending = vec![directory.to_path_buf()];
    while let Some(current) = pending.pop() {
        for entry in std::fs::read_dir(&current).expect("the directory is read") {
            let path = entry.expect("the entry is read").path();
            if path.is_dir() {
                pending.push(path);
            } else if path.extension().is_some_and(|extension| extension == "rs") {
                found.push(path.strip_prefix(directory).expect("the file is under the directory").to_string_lossy().replace('\\', "/"));
            }
        }
    }
    found.sort();
    found
}

// The files of the five layers, relative to src/.
fn core_files() -> Vec<String> {
    files_of(&source_directory()).into_iter().filter(|file| LAYERS.iter().any(|layer| file.starts_with(&format!("{layer}/")))).collect()
}

fn read(file: &str) -> String {
    std::fs::read_to_string(source_directory().join(file)).unwrap_or_else(|error| panic!("{file}: {error}"))
}

/// The source without its comments and without the contents of its string and character
/// literals; the lines stay where they are.
fn code(source: &str) -> String {
    let chars: Vec<char> = source.chars().collect();
    let mut out = String::with_capacity(source.len());
    let is_word = |c: char| c.is_alphanumeric() || c == '_';
    let mut i = 0;
    while i < chars.len() {
        let c = chars[i];
        let next = chars.get(i + 1).copied();
        if c == '/' && next == Some('/') {
            while i < chars.len() && chars[i] != '\n' {
                i += 1;
            }
        } else if c == '/' && next == Some('*') {
            // Block comments nest.
            let mut depth = 0;
            loop {
                if i + 1 < chars.len() && chars[i] == '/' && chars[i + 1] == '*' {
                    depth += 1;
                    i += 2;
                } else if i + 1 < chars.len() && chars[i] == '*' && chars[i + 1] == '/' {
                    depth -= 1;
                    i += 2;
                    if depth == 0 {
                        break;
                    }
                } else if i < chars.len() {
                    if chars[i] == '\n' {
                        out.push('\n');
                    }
                    i += 1;
                } else {
                    break;
                }
            }
        } else if c == 'r' && (i == 0 || !is_word(chars[i - 1]) || chars[i - 1] == 'b') && matches!(next, Some('"' | '#')) {
            // A raw string: r"…", r#"…"#, with any number of #. (r#name, a raw identifier, has no quote.)
            let mut hashes = 0;
            while chars.get(i + 1 + hashes) == Some(&'#') {
                hashes += 1;
            }
            if chars.get(i + 1 + hashes) != Some(&'"') {
                out.push(c);
                i += 1;
                continue;
            }
            out.push_str("r\"");
            i += 2 + hashes;
            loop {
                if i >= chars.len() {
                    break;
                }
                if chars[i] == '"' && (0..hashes).all(|k| chars.get(i + 1 + k) == Some(&'#')) {
                    i += 1 + hashes;
                    break;
                }
                if chars[i] == '\n' {
                    out.push('\n');
                }
                i += 1;
            }
            out.push('"');
        } else if c == '"' {
            out.push('"');
            i += 1;
            while i < chars.len() && chars[i] != '"' {
                if chars[i] == '\\' {
                    i += 1;
                }
                if i < chars.len() && chars[i] == '\n' {
                    out.push('\n');
                }
                i += 1;
            }
            out.push('"');
            i += 1;
        } else if c == '\'' {
            // A character literal ('a', '\n', '\u{1F600}') or a lifetime ('a).
            if next == Some('\\') {
                i += 2;
                while i < chars.len() && chars[i] != '\'' {
                    i += 1;
                }
                i += 1;
                out.push_str("''");
            } else if chars.get(i + 2) == Some(&'\'') {
                i += 3;
                out.push_str("''");
            } else {
                out.push(c);
                i += 1;
            }
        } else {
            out.push(c);
            i += 1;
        }
    }
    out
}

// The lines of a file whose code matches the pattern: "line: text".
fn matches_of(file: &str, pattern: &Pattern) -> Vec<String> {
    let source = read(file);
    let original: Vec<&str> = source.lines().collect();
    code(&source)
        .lines()
        .enumerate()
        .filter(|(_, line)| pattern.is_match(line))
        .map(|(index, _)| format!("{}: {}", index + 1, original.get(index).map_or("", |line| line.trim())))
        .collect()
}

// The matches of a pattern in the given files, but for the allowed ones: "file:line: text".
fn violations(files: &[String], pattern: &str, allowed: &[(&str, &str)]) -> Vec<String> {
    let pattern = Pattern::new(pattern);
    files
        .iter()
        .filter(|file| !allowed.iter().any(|(name, _)| name == file))
        .flat_map(|file| matches_of(file, &pattern).into_iter().map(move |line| format!("{file}:{line}")))
        .collect()
}

#[test]
fn the_core_follows_the_portability_rules() {
    let files = core_files();
    assert!(files.len() > 40, "the core was not found: {files:?}");
    let mut failures = Vec::new();
    for rule in CORE_RULES {
        let found = violations(&files, rule.pattern, rule.allowed);
        if !found.is_empty() {
            let allowed: Vec<&str> = rule.allowed.iter().map(|(file, _)| *file).collect();
            failures.push(format!(
                "{} is not allowed in the core outside {}:\n  {}",
                rule.name,
                if allowed.is_empty() { "nowhere".to_owned() } else { allowed.join(", ") },
                found.join("\n  ")
            ));
        }
    }
    assert!(failures.is_empty(), "\n{}", failures.join("\n"));
}

#[test]
fn allowances_are_still_used() {
    // An allowance nothing uses any more is removed, so the lists stay the truth.
    for rule in CORE_RULES {
        let pattern = Pattern::new(rule.pattern);
        for (file, _) in rule.allowed {
            assert!(!matches_of(file, &pattern).is_empty(), "{file} no longer uses {}: remove it from the allowed files.", rule.name);
        }
    }
}

/// What a layer may name of the crate: itself, the layers before it, and the links listed here.
const LINKS: &[(&str, &str, &str)] = &[
    ("binding", r"crate::builtins::natives\b", "the native catalogue: plain data the resolver binds calls against"),
    ("runtime", r"crate::builtins::(natives|native_table)\b", "the interpreter calls the natives through their table"),
    ("runtime", r"crate::builtins::formats\b", "a format string (`@f \"…\"`) applies a format of §9.9 to its interpolations"),
];

#[test]
fn a_layer_uses_only_the_layers_before_it() {
    let module = Pattern::new(r"\bcrate::(\w+)(::\w+)?");
    let mut failures = Vec::new();
    let mut used: BTreeMap<&str, bool> = LINKS.iter().map(|(_, pattern, _)| (*pattern, false)).collect();
    for file in core_files() {
        let layer = LAYERS.iter().position(|layer| file.starts_with(&format!("{layer}/"))).expect("a file of the core is in a layer");
        let source = read(&file);
        for (index, line) in code(&source).lines().enumerate() {
            for found in module.captures_iter(line) {
                let name = found[1];
                // The stack guard belongs to no layer; its uses are a rule of their own.
                if name == "stack_guard" {
                    continue;
                }
                let Some(other) = LAYERS.iter().position(|layer| *layer == name) else {
                    failures.push(format!("{file}:{}: `crate::{name}` is not a layer of the core", index + 1));
                    continue;
                };
                if other <= layer {
                    continue;
                }
                let link = LINKS.iter().find(|(from, pattern, _)| *from == LAYERS[layer] && Pattern::new(pattern).is_match(found[0]));
                match link {
                    Some((_, pattern, _)) => {
                        used.insert(*pattern, true);
                    }
                    None => failures.push(format!("{file}:{}: the layer `{}` uses `{}`, a later layer", index + 1, LAYERS[layer], &found[0])),
                }
            }
        }
    }
    assert!(failures.is_empty(), "\n{}", failures.join("\n"));
    for (pattern, used) in used {
        assert!(used, "the link {pattern} is no longer used: remove it.");
    }
}

#[test]
fn only_its_file_uses_the_dependency() {
    // Over the whole crate, the host boundary included.
    let files = files_of(&source_directory());
    assert_eq!(violations(&files, r"\bstacker\b", &[("stack_guard.rs", "")]), Vec::<String>::new());
    assert_eq!(violations(&files, r"\bregex::|\buse\s+regex\b", &[]), Vec::<String>::new());
    assert_eq!(violations(&files, r"\bunsafe\b", &[]), Vec::<String>::new());
    // The clock of the host is read at the boundary only.
    let clock = violations(&files, r"\bstd::time\b|\bSystemTime\b", &[("host/clock.rs", "")]);
    assert_eq!(clock, Vec::<String>::new());
}

// The names under a `[section]` of a manifest: the keys of its `name = …` lines.
fn keys(manifest: &str, section: &str) -> Vec<String> {
    let mut found = Vec::new();
    let mut inside = false;
    for line in manifest.lines().map(str::trim) {
        if line.starts_with('[') {
            inside = line == format!("[{section}]");
        } else if inside && !line.is_empty() && !line.starts_with('#') {
            found.push(line.split('=').next().unwrap_or("").trim().to_owned());
        }
    }
    found
}

#[test]
fn the_crate_has_one_dependency() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let manifest = std::fs::read_to_string(root.join("Cargo.toml")).expect("the manifest is read");
    assert_eq!(keys(&manifest, "dependencies"), ["stacker"]);
    for section in ["build-dependencies", "dev-dependencies", "features"] {
        assert!(!manifest.contains(&format!("[{section}]")) && !manifest.contains(&format!("[{section}.")), "the manifest has a [{section}] section");
    }
    assert!(!manifest.contains("[target.") && !manifest.contains("[dependencies."), "the manifest has dependencies in another section");
    assert!(!root.join("build.rs").exists(), "the library has a build script");

    // The benchmark runner uses the library and nothing else.
    let bench = std::fs::read_to_string(root.join("bench").join("Cargo.toml")).expect("the manifest of the runner is read");
    assert_eq!(keys(&bench, "dependencies"), ["jqlite"]);
}

#[test]
fn code_is_what_remains_without_comments_and_strings() {
    let source = r####"
let a = "unsafe"; // unsafe
/* unsafe
   /* yield */ still a comment */
let b = r#"he said "unsafe""#; let c = 'u'; let d = '"'; let e = "\"unsafe\"";
let f = "a
    unsafe";
fn g<'a>(x: &'a str) -> &'a str { x } let h = '\''; let i = b"unsafe"; let j = br##"unsafe"##;
unsafe { call() } /// unsafe
"####;
    let stripped = code(source);
    let lines: Vec<&str> = stripped.lines().collect();
    assert_eq!(lines.len(), source.lines().count());
    assert_eq!(stripped.matches("unsafe").count(), 1);
    assert!(!stripped.contains("yield") && !stripped.contains("comment"));
    assert!(lines[lines.len() - 1].starts_with("unsafe { call() }"));
    assert!(stripped.contains("fn g<'a>(x: &'a str) -> &'a str { x }"));
}
