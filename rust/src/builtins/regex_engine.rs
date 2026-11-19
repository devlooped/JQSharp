//! The host-specific part of §9.10, the only file of the core that uses a host regex engine: it
//! compiles a pattern that `regex_syntax` translated and finds the next match from a position.
//!
//! Host-specific: Rust uses the `regex` crate, which runs in time linear in the input and works on
//! UTF-8, so offsets are bytes (`regex_natives` turns them into code points). A search from a
//! position sees the whole input (`find_at`, `captures_read_at`): `^` and `\b` look before the
//! position. Case-insensitivity is never the engine's: the translation does it. Compiled patterns
//! are cached per process in a map behind a mutex that is emptied when it reaches `CACHE_LIMIT`
//! entries.
//!
//! Limits: the engine's defaults would refuse patterns of the dialect with its own messages, so
//! both are set here. Its nesting limit is lifted: the translation allows `MAX_NESTING` groups
//! open at once. The engine compiles by recursion, so a pattern is compiled where the stack has
//! [`STACK_PER_GROUP`] bytes for each of its levels, and on a fresh stack of that size otherwise.
//! Its size limit is [`SIZE_LIMIT`]: the engine unrolls a
//! counted repetition, so `(?:a{1000}){1000}` is a million states, and nothing in the dialect
//! bounds a nest of them. Beyond it the reason is `regex_syntax::TOO_LARGE`.

use std::collections::HashMap;
use std::sync::{Arc, LazyLock, Mutex};

use regex::{CaptureLocations, Regex, RegexBuilder};

use crate::stack_guard;

use super::regex_syntax::TOO_LARGE;

const CACHE_LIMIT: usize = 256;

/// The most bytes a compiled pattern may take (the engine's own default, stated): about 200,000
/// repeated literals, or 10,000 repeated classes as wide as `.`; compiling to the limit takes
/// tens of milliseconds.
pub const SIZE_LIMIT: usize = 10 * 1024 * 1024;

// The stack the engine may use to compile one level of groups: a level that is an alternative,
// a sequence, a repetition and a group takes 27 KB in a build without optimizations (measured:
// 300 levels fit 8 MB, 400 do not), a tenth of it in an optimized one.
const STACK_PER_GROUP: usize = 64 * 1024;

static CACHE: LazyLock<Mutex<HashMap<String, Arc<HostPattern>>>> = LazyLock::new(|| Mutex::new(HashMap::new()));

/// A pattern compiled by the host engine.
pub struct HostPattern {
    regex: Regex,
}

/// A compiled pattern, or the reason for refusing it; `nesting` is the most groups open at once
/// in it (`Translation::nesting`).
pub fn try_compile(pattern: &str, nesting: usize) -> Result<Arc<HostPattern>, String> {
    if let Ok(cache) = CACHE.lock() {
        if let Some(compiled) = cache.get(pattern) {
            return Ok(Arc::clone(compiled));
        }
    }

    // The boundary of the host engine: its error becomes the dialect's.
    let build = || RegexBuilder::new(pattern).size_limit(SIZE_LIMIT).nest_limit(u32::MAX).build();
    let stack = (nesting + 1) * STACK_PER_GROUP;
    let regex = if stack_guard::has_room(stack) { build() } else { stack_guard::on_stack_of(stack + STACK_PER_GROUP, build) };
    let regex = regex.map_err(|error| match error {
        regex::Error::CompiledTooBig(_) => TOO_LARGE.to_owned(),
        other => other.to_string(),
    })?;
    let compiled = Arc::new(HostPattern { regex });
    if let Ok(mut cache) = CACHE.lock() {
        if cache.len() >= CACHE_LIMIT {
            cache.clear();
        }
        cache.insert(pattern.to_owned(), Arc::clone(&compiled));
    }
    Ok(compiled)
}

/// What a search fills: the spans of the match and of each group.
pub struct Spans {
    locations: CaptureLocations,
}

impl HostPattern {
    /// Room for the spans of one match of this pattern.
    pub fn spans(&self) -> Spans {
        Spans { locations: self.regex.capture_locations() }
    }

    /// The first match starting at or after the byte index `start` (anchors and word boundaries
    /// see the whole input): its start and end, without the groups.
    pub fn try_find(&self, input: &str, start: usize) -> Option<(usize, usize)> {
        self.regex.find_at(input, start).map(|found| (found.start(), found.end()))
    }

    /// As `try_find`, and `spans` receives the start and end of the match and of each group.
    pub fn try_match(&self, input: &str, start: usize, spans: &mut Spans) -> Option<(usize, usize)> {
        self.regex.captures_read_at(&mut spans.locations, input, start).map(|found| (found.start(), found.end()))
    }
}

impl Spans {
    /// The start and end of group `group` (1 is the first), or `None` for a group that did not
    /// participate.
    pub fn group(&self, group: usize) -> Option<(usize, usize)> {
        self.locations.get(group)
    }
}
