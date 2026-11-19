//! The regular expressions of the tests: the crate's own engine, so that the crate has no
//! dependency for them. A pattern is of the flavor of spec §9.10: `^` and `$` are the start and the
//! end of the text, so a test that means a line matches one line at a time.

use std::cell::RefCell;

use jqlite::builtins::regex_engine::{self, RegexMatcher};
use jqlite::builtins::regex_syntax::Options;

pub struct Pattern {
    matcher: RefCell<RegexMatcher>,
}

impl Pattern {
    pub fn new(source: &str) -> Pattern {
        let program = regex_engine::try_compile(source, Options::default()).unwrap_or_else(|reason| panic!("{source}: {reason}"));
        Pattern { matcher: RefCell::new(RegexMatcher::new(program)) }
    }

    pub fn is_match(&self, text: &str) -> bool {
        self.matcher.borrow_mut().try_match(text, 0, &mut [])
    }

    /// The text of the first match.
    pub fn find<'t>(&self, text: &'t str) -> Option<&'t str> {
        self.captures(text).map(|found| found[0])
    }

    /// The first match: its text, then the text of each group ("" for a group that did not
    /// participate).
    pub fn captures<'t>(&self, text: &'t str) -> Option<Vec<&'t str>> {
        self.captures_from(text, 0).map(|(found, _)| found)
    }

    /// Every match, as the search loop of §9.10 finds them with the `g` flag.
    pub fn captures_iter<'t>(&self, text: &'t str) -> Vec<Vec<&'t str>> {
        let mut all = Vec::new();
        let mut p = 0;
        while p <= text.len() {
            let Some((found, end)) = self.captures_from(text, p) else {
                break;
            };
            let empty = found[0].is_empty();
            all.push(found);
            p = if !empty { end } else { end + text[end..].chars().next().map_or(1, char::len_utf8) };
        }
        all
    }

    // The match at or after the byte offset `start`, and where it ends.
    fn captures_from<'t>(&self, text: &'t str, start: usize) -> Option<(Vec<&'t str>, usize)> {
        let mut matcher = self.matcher.borrow_mut();
        let mut spans = vec![-1; matcher.program().slots];
        if !matcher.try_match(text, start, &mut spans) {
            return None;
        }
        let found = spans.chunks_exact(2).map(|span| if span[0] < 0 { "" } else { &text[span[0] as usize..span[1] as usize] }).collect();
        Some((found, spans[1] as usize))
    }
}
