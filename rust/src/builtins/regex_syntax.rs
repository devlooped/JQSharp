//! The regex dialect of §9.10: reads a jqlite pattern, rejects everything outside the dialect, and
//! writes an equivalent pattern for the host engine (`regex_engine`) together with the names of
//! its capturing groups in the order of their '('.
//!
//! The output is in the syntax of the `regex` crate, with the meaning the spec demands spelled out
//! so that no engine default leaks in: every code point is written as `\x{…}`, `.` as a class
//! without U+000A (or `(?s:.)`), classes and `\d \w \s \D \W \S` as explicit ranges of code points
//! (the engine's own are Unicode), `$` as `\z`, `\b \B` as the ASCII boundaries `(?-u:\b)` and
//! `(?-u:\B)`, named groups as plain groups (the names are kept here). The `i` flag is done here,
//! never by the engine: it folds ASCII letters only (`RangeSet::add_ascii_cases`, the one place of
//! the rule).
//!
//! Rust: the engine works on code points, so a class is written directly; the reference, whose
//! engine works on UTF-16 units, splits each class into a BMP part and surrogate pairs, and needs
//! lookaround for the ASCII word boundaries, which this engine has natively.

use std::fmt::Write;

use crate::values::code_points;

pub const MAX_REPEAT: u32 = 1000;

/// The groups open at once in a pattern: more is [`TOO_LARGE`]. The dialect sets no limit, and
/// the reference's engine none either; the host engine here compiles groups by recursion.
pub const MAX_NESTING: usize = 1000;

/// The reason of the `Regex failure` of a pattern beyond what this port runs: more than
/// [`MAX_NESTING`] groups open at once, or a compiled size beyond the engine's limit
/// (`regex_engine::SIZE_LIMIT`).
pub const TOO_LARGE: &str = "the pattern is too large";

/// The flags of §9.10 that change the pattern itself.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Options {
    pub extended: bool,
    pub dot_all: bool,
    pub ignore_case: bool,
}

/// A translated pattern: the host pattern, one name (or `None`) per capturing group, and the
/// most groups open at once.
#[derive(Clone, Debug)]
pub struct Translation {
    pub pattern: String,
    pub groups: Vec<Option<String>>,
    pub nesting: usize,
}

// What precedes a quantifier: only an atom may be repeated.
#[derive(Clone, Copy, PartialEq, Eq)]
enum Previous {
    None,
    Atom,
    Quantifier,
    Anchor,
}

pub fn try_translate(pattern: &str, options: Options) -> Result<Translation, String> {
    let points: Vec<char> = pattern.chars().collect();
    let mut output = String::with_capacity(pattern.len() * 6);
    let mut groups: Vec<Option<String>> = Vec::new();
    let mut open = 0;
    let mut nesting = 0;
    let mut previous = Previous::None;
    let mut i = 0;
    while i < points.len() {
        let c = points[i];
        if options.extended && (is_extended_space(c) || c == '#') {
            i = skip_extended(&points, i);
            continue;
        }

        match c {
            '\\' => try_escape(&points, &mut i, &mut output, options, &mut previous)?,
            '[' => {
                try_class(&points, &mut i, &mut output, options)?;
                previous = Previous::Atom;
            }
            '.' => {
                output.push_str(if options.dot_all { "(?s:.)" } else { "[^\\x{A}]" });
                previous = Previous::Atom;
                i += 1;
            }
            '^' => {
                output.push('^');
                previous = Previous::Anchor;
                i += 1;
            }
            '$' => {
                output.push_str("\\z");
                previous = Previous::Anchor;
                i += 1;
            }
            '|' => {
                output.push('|');
                previous = Previous::None;
                i += 1;
            }
            '(' => {
                if open == MAX_NESTING {
                    return Err(TOO_LARGE.to_owned());
                }
                try_group(&points, &mut i, &mut output, &mut groups)?;
                open += 1;
                nesting = nesting.max(open);
                previous = Previous::None;
            }
            ')' => {
                if open == 0 {
                    return Err("unmatched close parenthesis".to_owned());
                }
                open -= 1;
                output.push(')');
                previous = Previous::Atom;
                i += 1;
            }
            '*' | '+' | '?' | '{' => {
                if previous != Previous::Atom {
                    return Err((if previous == Previous::Quantifier { "nested quantifier" } else { "quantifier without an operand" }).to_owned());
                }
                try_quantifier(&points, &mut i, &mut output, options)?;
                previous = Previous::Quantifier;
            }
            ']' | '}' => return Err(format!("unescaped '{c}'")),
            _ => {
                literal(&mut output, c, options);
                previous = Previous::Atom;
                i += 1;
            }
        }
    }

    if open > 0 {
        return Err("missing close parenthesis".to_owned());
    }
    Ok(Translation { pattern: output, groups, nesting })
}

// \x outside a class: a literal, a shorthand class or an anchor.
fn try_escape(points: &[char], i: &mut usize, output: &mut String, options: Options, previous: &mut Previous) -> Result<(), String> {
    if *i + 1 == points.len() {
        return Err("trailing backslash".to_owned());
    }
    let c = points[*i + 1];
    match c {
        'b' | 'B' => {
            // The ASCII word boundaries of §9.10, by the ASCII \w; the engine's own \b is Unicode.
            output.push_str(if c == 'b' { "(?-u:\\b)" } else { "(?-u:\\B)" });
            *previous = Previous::Anchor;
            *i += 2;
            return Ok(());
        }
        'd' | 'w' | 's' | 'D' | 'W' | 'S' => {
            let mut set = RangeSet::default();
            set.add_shorthand(c);
            class(output, &mut set, false);
            *previous = Previous::Atom;
            *i += 2;
            return Ok(());
        }
        _ => {}
    }

    let escaped = try_escaped_literal(points, i)?;
    literal(output, escaped, options);
    *previous = Previous::Atom;
    Ok(())
}

// The escapes that stand for one code point: the metacharacters, \t \n \r \f \v and \xHH.
fn try_escaped_literal(points: &[char], i: &mut usize) -> Result<char, String> {
    if *i + 1 == points.len() {
        return Err("trailing backslash".to_owned());
    }
    let c = points[*i + 1];
    let literal = match c {
        '\\' | '.' | '*' | '+' | '?' | '(' | ')' | '[' | ']' | '{' | '}' | '|' | '^' | '$' | '/' | '-' => c,
        't' => '\t',
        'n' => '\n',
        'r' => '\r',
        'f' => '\u{c}',
        'v' => '\u{b}',
        'x' => {
            let high = points.get(*i + 2).and_then(|c| c.to_digit(16));
            let low = points.get(*i + 3).and_then(|c| c.to_digit(16));
            let (Some(high), Some(low)) = (high, low) else {
                return Err("\\x must be followed by two hex digits".to_owned());
            };
            *i += 4;
            return Ok(char::from_u32(high * 16 + low).unwrap_or(code_points::REPLACEMENT));
        }
        _ => return Err(format!("invalid escape \\{c}")),
    };

    *i += 2;
    Ok(literal)
}

// ( (?: or (?<name>
fn try_group(points: &[char], i: &mut usize, output: &mut String, groups: &mut Vec<Option<String>>) -> Result<(), String> {
    if points.get(*i + 1) == Some(&'?') {
        if points.get(*i + 2) == Some(&':') {
            output.push_str("(?:");
            *i += 3;
            return Ok(());
        }

        if points.get(*i + 2) == Some(&'<') {
            let start = *i + 3;
            let mut end = start;
            while end < points.len() && points[end] != '>' {
                end += 1;
            }
            if end == points.len() || !is_group_name(&points[start..end]) {
                return Err("invalid group name".to_owned());
            }
            let name: String = points[start..end].iter().collect();
            if groups.iter().any(|group| group.as_deref() == Some(name.as_str())) {
                return Err(format!("duplicate group name {name}"));
            }
            groups.push(Some(name));
            output.push('(');
            *i = end + 1;
            return Ok(());
        }

        return Err("unsupported group syntax (?".to_owned());
    }

    groups.push(None);
    output.push('(');
    *i += 1;
    Ok(())
}

// * + ? {n} {n,} {n,m}, each optionally followed by ? (lazy).
fn try_quantifier(points: &[char], i: &mut usize, output: &mut String, options: Options) -> Result<(), String> {
    let c = points[*i];
    if c == '{' {
        let mut at = *i + 1;
        let Some(min) = try_number(points, &mut at) else {
            return Err("'{' is not a quantifier".to_owned());
        };
        let mut max = min;
        let mut unbounded = false;
        if points.get(at) == Some(&',') {
            at += 1;
            if points.get(at) == Some(&'}') {
                unbounded = true;
            } else {
                match try_number(points, &mut at) {
                    Some(number) => max = number,
                    None => return Err("'{' is not a quantifier".to_owned()),
                }
            }
        }

        if points.get(at) != Some(&'}') {
            return Err("'{' is not a quantifier".to_owned());
        }
        if min > MAX_REPEAT || !unbounded && (max > MAX_REPEAT || max < min) {
            return Err("invalid repetition count".to_owned());
        }
        let _ = write!(output, "{{{min}");
        if unbounded {
            output.push(',');
        } else if max != min {
            let _ = write!(output, ",{max}");
        }
        output.push('}');
        *i = at + 1;
    } else {
        output.push(c);
        *i += 1;
    }

    if options.extended {
        while *i < points.len() && (is_extended_space(points[*i]) || points[*i] == '#') {
            *i = skip_extended(points, *i);
        }
    }

    if points.get(*i) == Some(&'?') {
        output.push('?');
        *i += 1;
    }

    Ok(())
}

fn try_number(points: &[char], at: &mut usize) -> Option<u32> {
    let mut value = 0;
    let start = *at;
    while let Some(digit) = points.get(*at).and_then(|c| c.to_digit(10)) {
        value = (value * 10 + digit).min(MAX_REPEAT + 1);
        *at += 1;
    }

    if *at > start { Some(value) } else { None }
}

// [...] or [^...]: literals, escapes, ranges of literals and shorthands.
fn try_class(points: &[char], i: &mut usize, output: &mut String, options: Options) -> Result<(), String> {
    let mut at = *i + 1;
    let negated = points.get(at) == Some(&'^');
    if negated {
        at += 1;
    }
    let first = at;
    let mut set = RangeSet::default();
    let mut previous_literal: Option<char> = None;
    loop {
        if at == points.len() {
            return Err("missing ']'".to_owned());
        }
        let c = points[at];
        if c == ']' {
            break;
        }
        if c == '[' {
            return Err("unescaped '[' in a class".to_owned());
        }

        if c == '-' && at != first && at + 1 < points.len() && points[at + 1] != ']' {
            // A range: the item before must be a single literal, the one after a literal too.
            let Some(low) = previous_literal else {
                return Err("'-' must be first, last, escaped or between two literals".to_owned());
            };
            at += 1;
            let high = try_class_literal(points, &mut at)?;
            if high < low {
                return Err("range out of order".to_owned());
            }
            set.add(low as u32, high as u32);
            previous_literal = None;
            continue;
        }

        if c == '\\' && at + 1 < points.len() && matches!(points[at + 1], 'd' | 'w' | 's' | 'D' | 'W' | 'S') {
            set.add_shorthand(points[at + 1]);
            previous_literal = None;
            at += 2;
            continue;
        }

        let item = try_class_literal(points, &mut at)?;
        set.add(item as u32, item as u32);
        previous_literal = Some(item);
    }

    if at == first {
        return Err("empty class".to_owned());
    }
    // The i flag folds before a negation applies: [^a] matches neither a nor A.
    if options.ignore_case {
        set.add_ascii_cases();
    }
    class(output, &mut set, negated);
    *i = at + 1;
    Ok(())
}

fn try_class_literal(points: &[char], at: &mut usize) -> Result<char, String> {
    let c = points[*at];
    if c == '\\' {
        return try_escaped_literal(points, at);
    }
    if c == '[' || c == ']' {
        return Err(format!("unescaped '{c}' in a class"));
    }
    *at += 1;
    Ok(c)
}

// A class as ranges of code points; a range never has a surrogate for a bound, which the engine
// would refuse.
fn class(output: &mut String, set: &mut RangeSet, negated: bool) {
    output.push('[');
    if negated {
        output.push('^');
    }
    for (low, high) in set.ranges() {
        if *low < 0xD800 {
            append_range(output, *low, (*high).min(0xD7FF));
        }
        if *high > 0xDFFF {
            append_range(output, (*low).max(0xE000), *high);
        }
    }
    output.push(']');
}

fn append_range(output: &mut String, low: u32, high: u32) {
    if low > high {
        return;
    }
    unit(output, low);
    if high > low {
        output.push('-');
        unit(output, high);
    }
}

// A code point; under the i flag an ASCII letter becomes the class of its two cases.
fn literal(output: &mut String, c: char, options: Options) {
    if options.ignore_case && c.is_ascii_alphabetic() {
        let mut set = RangeSet::default();
        set.add(c as u32, c as u32);
        set.add_ascii_cases();
        class(output, &mut set, false);
        return;
    }

    unit(output, c as u32);
}

fn unit(output: &mut String, code_point: u32) {
    let _ = write!(output, "\\x{{{code_point:X}}}");
}

// With the x flag: whitespace and a # comment up to the next line feed.
fn skip_extended(points: &[char], i: usize) -> usize {
    if points[i] != '#' {
        return i + 1;
    }
    let mut i = i;
    while i < points.len() && points[i] != '\n' {
        i += 1;
    }
    i
}

fn is_extended_space(c: char) -> bool {
    matches!(c, '\u{9}'..='\u{d}' | ' ')
}

fn is_group_name(points: &[char]) -> bool {
    !points.is_empty() && !points[0].is_ascii_digit() && points.iter().all(|c| c.is_ascii_alphanumeric() || *c == '_')
}

/// A set of code points as sorted, disjoint, non-adjacent ranges.
#[derive(Default)]
struct RangeSet {
    ranges: Vec<(u32, u32)>,
    unsorted: bool,
}

impl RangeSet {
    fn ranges(&mut self) -> &[(u32, u32)] {
        self.normalize();
        &self.ranges
    }

    fn add(&mut self, low: u32, high: u32) {
        self.ranges.push((low, high));
        self.unsorted = true;
    }

    // The ASCII classes of §9.10 and their complements.
    fn add_shorthand(&mut self, c: char) {
        let mut set = RangeSet::default();
        match c {
            'd' | 'D' => set.add('0' as u32, '9' as u32),
            'w' | 'W' => {
                set.add('0' as u32, '9' as u32);
                set.add('A' as u32, 'Z' as u32);
                set.add('a' as u32, 'z' as u32);
                set.add('_' as u32, '_' as u32);
            }
            _ => {
                set.add('\t' as u32, '\r' as u32);
                set.add(' ' as u32, ' ' as u32);
            }
        }

        let mut set = if matches!(c, 'D' | 'W' | 'S') { set.complement() } else { set };
        for (low, high) in set.ranges().to_vec() {
            self.add(low, high);
        }
    }

    /// The i flag of §9.10: adds the other case of every ASCII letter in the set (A-Z and a-z, 32
    /// apart); no other code point folds.
    fn add_ascii_cases(&mut self) {
        let mut added = Vec::new();
        for (low, high) in self.ranges() {
            let upper_low = (*low).max('A' as u32);
            let upper_high = (*high).min('Z' as u32);
            if upper_low <= upper_high {
                added.push((upper_low + 32, upper_high + 32));
            }
            let lower_low = (*low).max('a' as u32);
            let lower_high = (*high).min('z' as u32);
            if lower_low <= lower_high {
                added.push((lower_low - 32, lower_high - 32));
            }
        }

        for (low, high) in added {
            self.add(low, high);
        }
    }

    fn complement(&mut self) -> RangeSet {
        let mut complement = RangeSet::default();
        let mut next = 0;
        for (low, high) in self.ranges() {
            if *low > next {
                complement.add(next, low - 1);
            }
            next = high + 1;
        }

        if next <= code_points::MAX {
            complement.add(next, code_points::MAX);
        }
        complement
    }

    fn normalize(&mut self) {
        if !self.unsorted {
            return;
        }
        self.ranges.sort_by_key(|range| range.0);
        let mut merged: Vec<(u32, u32)> = Vec::with_capacity(self.ranges.len());
        for range in &self.ranges {
            match merged.last_mut() {
                Some(last) if range.0 <= last.1 + 1 => last.1 = last.1.max(range.1),
                _ => merged.push(*range),
            }
        }

        self.ranges = merged;
        self.unsorted = false;
    }
}
