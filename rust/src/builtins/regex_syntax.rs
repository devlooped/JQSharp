//! The regex flavor of §9.10: reads a jqlite pattern, rejects everything outside the flavor, and
//! writes an equivalent program for the engine (`regex_engine`) together with the names of its
//! capturing groups in the order of their '('.
//!
//! The program is made of the instructions of [`Op`], with the meaning the spec demands spelled
//! out: `.`, classes and `\d \w \s` as sets of code points, `$` as the end of the input, `\b \B` as
//! ASCII word boundaries. The `i` flag is done here: it folds ASCII letters only
//! (`RangeSet::add_ascii_cases`, the one place of the rule). No host regex engine reads the flavor
//! with one meaning, so none is used (docs/porting.md §3.3).
//!
//! The limits of §11 are checked here, while the pattern is read ([`Measure`]), so that the engine
//! never meets a pattern beyond them and takes every one within.
//!
//! The pattern is read in one pass, without recursion, and each instruction that jumps says how far
//! (not where to), so a piece of the program can be copied or moved as it is: a quantifier wraps or
//! repeats the instructions of the atom before it.

use crate::values::code_points;

/// The largest count of a quantifier, and of the counts of nested quantifiers multiplied.
pub const MAX_REPEAT: usize = 1000;

/// The largest size of a pattern (§9.10): its items, counted repetitions written out.
pub const MAX_SIZE: usize = 10000;

/// The most groups open at once.
pub const MAX_NESTING: usize = 100;

/// The most capturing groups.
pub const MAX_GROUPS: usize = 1000;

/// The reason of a pattern beyond [`MAX_SIZE`], [`MAX_NESTING`] or [`MAX_GROUPS`]: a fixed text
/// (§10.3).
pub const TOO_LARGE: &str = "the pattern is too large";

/// The flags of §9.10 that change the pattern itself.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Options {
    pub extended: bool,
    pub dot_all: bool,
    pub ignore_case: bool,
}

/// The instructions of a program; `a` and `b` are their operands. The four that read a code point
/// come first (`op <= Op::AnyButLineFeed`).
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum Op {
    /// The code point `a`.
    Char,
    /// A code point of the set `a` of the program.
    Set,
    /// Any code point.
    Any,
    /// Any code point but U+000A.
    AnyButLineFeed,
    /// Go on `a` instructions further; if that fails, `b` instructions further.
    Split,
    /// Go on `a` instructions further.
    Jump,
    /// Store the position in slot `a` (2g: the start of group g; 2g + 1: its end; group 0 is the match).
    Save,
    /// The start of the input.
    Begin,
    /// The end of the input.
    End,
    /// An ASCII word boundary.
    WordBoundary,
    /// Not an ASCII word boundary.
    NotWordBoundary,
    /// Store the position in mark `a`: where an iteration of a repetition starts.
    Mark,
    /// Go on `b` instructions further when the position is the one of mark `a` (an iteration that
    /// matched nothing ends its repetition); else to the next instruction.
    EmptyExit,
    /// The pattern matched.
    Match,
}

/// A translated pattern: the program (an instruction and its operands `a`, `b` per index), its sets
/// of code points (sorted pairs low, high), the number of its marks, and one name (or `None`) per
/// capturing group.
#[derive(Clone, Debug)]
pub struct Translation {
    pub ops: Vec<Op>,
    pub a: Vec<i32>,
    pub b: Vec<i32>,
    pub sets: Vec<Vec<u32>>,
    pub marks: usize,
    pub groups: Vec<Option<String>>,
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
    let mut translator = Translator::new(&points, options);
    translator.translate()?;
    Ok(Translation { ops: translator.ops, a: translator.a, b: translator.b, sets: translator.sets, marks: translator.marks, groups: translator.groups })
}

// An open group, or the whole pattern: where it starts, where its current alternative starts, the
// jumps of its earlier alternatives to its end, and whether it can match nothing (for the loops
// around it): `before` is whether every item of the current alternative but the last can, `last`
// whether the last can (`None`: no item), `any` whether an earlier alternative can.
struct Frame {
    start: usize,
    group: usize,
    alternative: usize,
    jumps: Vec<usize>,
    before: bool,
    last: Option<bool>,
    any: bool,
}

impl Frame {
    fn new(start: usize, group: usize) -> Frame {
        Frame { start, group, alternative: start, jumps: Vec::new(), before: true, last: None, any: false }
    }

    fn add(&mut self, nullable: bool) {
        self.before = self.before && self.last != Some(false);
        self.last = Some(nullable);
    }

    // Ends the current alternative.
    fn close(&mut self) {
        self.any = self.any || self.before && self.last != Some(false);
        self.before = true;
        self.last = None;
    }
}

// The instructions of a repeated atom; its mark (`None` when its iterations are not marked) and
// the index where the repetition ends.
struct Body {
    ops: Vec<Op>,
    a: Vec<i32>,
    b: Vec<i32>,
    mark: Option<usize>,
    end: usize,
}

struct Translator<'p> {
    points: &'p [char],
    options: Options,
    ops: Vec<Op>,
    a: Vec<i32>,
    b: Vec<i32>,
    sets: Vec<Vec<u32>>,
    groups: Vec<Option<String>>,
    // The innermost open group (or the whole pattern), and the ones around it.
    frame: Frame,
    outer: Vec<Frame>,
    measure: Measure,
    marks: usize,
    i: usize,
    previous: Previous,
    // Where the instructions of the last atom start.
    atom: usize,
}

impl<'p> Translator<'p> {
    fn new(points: &'p [char], options: Options) -> Translator<'p> {
        Translator {
            points,
            options,
            ops: Vec::new(),
            a: Vec::new(),
            b: Vec::new(),
            sets: Vec::new(),
            groups: Vec::new(),
            frame: Frame::new(0, 0),
            outer: Vec::new(),
            measure: Measure::new(),
            marks: 0,
            i: 0,
            previous: Previous::None,
            atom: 0,
        }
    }

    fn translate(&mut self) -> Result<(), String> {
        let points = self.points;
        self.emit(Op::Save, 0, 0);
        self.frame = Frame::new(self.ops.len(), 0);
        while self.i < points.len() {
            let c = points[self.i];
            if self.options.extended && (is_extended_space(c) || c == '#') {
                self.i = skip_extended(points, self.i);
                continue;
            }

            match c {
                '\\' => {
                    self.try_escape()?;
                    self.add(1)?;
                }
                '[' => {
                    let items = self.try_class()?;
                    self.add(items)?;
                }
                '.' => {
                    self.start_atom();
                    self.emit(if self.options.dot_all { Op::Any } else { Op::AnyButLineFeed }, 0, 0);
                    self.add(1)?;
                    self.i += 1;
                }
                '^' => {
                    self.anchor(Op::Begin);
                    self.add(1)?;
                    self.i += 1;
                }
                '$' => {
                    self.anchor(Op::End);
                    self.add(1)?;
                    self.i += 1;
                }
                '|' => {
                    self.add(1)?;
                    // The alternative so far, or the rest: it ends with a jump to the end of the group.
                    let alternative = self.frame.alternative;
                    let rest = distance(alternative, self.ops.len() + 2);
                    self.insert(alternative, Op::Split, 1, rest);
                    self.frame.jumps.push(self.ops.len());
                    self.emit(Op::Jump, 0, 0);
                    self.frame.alternative = self.ops.len();
                    self.frame.close();
                    self.previous = Previous::None;
                    self.i += 1;
                }
                '(' => {
                    if self.measure.depth() == MAX_NESTING {
                        return Err(TOO_LARGE.to_owned());
                    }
                    let start = self.ops.len();
                    self.try_group()?;
                    if !self.measure.open() {
                        return Err(TOO_LARGE.to_owned());
                    }
                    let mut frame = Frame::new(start, if self.ops.len() > start { self.groups.len() } else { 0 });
                    frame.alternative = self.ops.len();
                    let outer = std::mem::replace(&mut self.frame, frame);
                    self.outer.push(outer);
                    self.previous = Previous::None;
                }
                ')' => {
                    let Some(outer) = self.outer.pop() else {
                        return Err("unmatched close parenthesis".to_owned());
                    };
                    self.measure.close();
                    let mut group = std::mem::replace(&mut self.frame, outer);
                    self.end(&mut group);
                    if group.group > 0 {
                        self.emit(Op::Save, slot(2 * group.group + 1), 0);
                    }
                    self.frame.add(group.any);
                    self.atom = group.start;
                    self.previous = Previous::Atom;
                    self.i += 1;
                }
                '*' | '+' | '?' | '{' => {
                    if self.previous != Previous::Atom {
                        return Err((if self.previous == Previous::Quantifier { "nested quantifier" } else { "quantifier without an operand" }).to_owned());
                    }
                    self.try_quantifier()?;
                    self.previous = Previous::Quantifier;
                }
                ']' | '}' => return Err(format!("unescaped '{c}'")),
                _ => {
                    self.literal(c);
                    self.add(1)?;
                    self.i += 1;
                }
            }
        }

        if !self.outer.is_empty() {
            return Err("missing close parenthesis".to_owned());
        }
        let mut frame = std::mem::replace(&mut self.frame, Frame::new(0, 0));
        self.end(&mut frame);
        self.emit(Op::Save, 1, 0);
        self.emit(Op::Match, 0, 0);
        Ok(())
    }

    // The end of a group or of the pattern: the jumps of its alternatives arrive here.
    fn end(&mut self, frame: &mut Frame) {
        for &jump in &frame.jumps {
            self.a[jump] = distance(jump, self.ops.len());
        }
        frame.close();
    }

    // An item of this size read: the pattern so far must be within MAX_SIZE.
    fn add(&mut self, item_size: usize) -> Result<(), String> {
        if self.measure.add(item_size) { Ok(()) } else { Err(TOO_LARGE.to_owned()) }
    }

    fn emit(&mut self, op: Op, first: i32, second: i32) {
        self.ops.push(op);
        self.a.push(first);
        self.b.push(second);
    }

    fn insert(&mut self, at: usize, op: Op, first: i32, second: i32) {
        self.ops.insert(at, op);
        self.a.insert(at, first);
        self.b.insert(at, second);
    }

    // An atom starts here: a quantifier after it repeats the instructions from here on.
    fn start_atom(&mut self) {
        self.atom = self.ops.len();
        self.previous = Previous::Atom;
        self.frame.add(false);
    }

    fn anchor(&mut self, op: Op) {
        self.emit(op, 0, 0);
        self.previous = Previous::Anchor;
        self.frame.add(true);
    }

    // \x outside a class: a literal, a shorthand class or an anchor.
    fn try_escape(&mut self) -> Result<(), String> {
        let points = self.points;
        if self.i + 1 == points.len() {
            return Err("trailing backslash".to_owned());
        }
        let c = points[self.i + 1];
        match c {
            'b' | 'B' => {
                self.anchor(if c == 'b' { Op::WordBoundary } else { Op::NotWordBoundary });
                self.i += 2;
                return Ok(());
            }
            'd' | 'w' | 's' | 'D' | 'W' | 'S' => {
                let mut set = RangeSet::default();
                set.add_shorthand(c);
                self.start_atom();
                self.class(&mut set, false);
                self.i += 2;
                return Ok(());
            }
            _ => {}
        }

        let literal = try_escaped_literal(points, &mut self.i)?;
        self.literal(literal);
        Ok(())
    }

    // ( (?: or (?<name>: a capturing group starts with the instruction that stores its start.
    fn try_group(&mut self) -> Result<(), String> {
        let points = self.points;
        let i = self.i;
        if points.get(i + 1) == Some(&'?') {
            if points.get(i + 2) == Some(&':') {
                self.i += 3;
                return Ok(());
            }

            if points.get(i + 2) == Some(&'<') {
                let start = i + 3;
                let mut end = start;
                while end < points.len() && points[end] != '>' {
                    end += 1;
                }
                if end == points.len() || !is_group_name(&points[start..end]) {
                    return Err("invalid group name".to_owned());
                }
                let name: String = points[start..end].iter().collect();
                if self.groups.iter().any(|group| group.as_deref() == Some(name.as_str())) {
                    return Err(format!("duplicate group name {name}"));
                }
                if self.groups.len() == MAX_GROUPS {
                    return Err(TOO_LARGE.to_owned());
                }
                self.groups.push(Some(name));
                self.emit(Op::Save, slot(2 * self.groups.len()), 0);
                self.i = end + 1;
                return Ok(());
            }

            return Err("unsupported group syntax (?".to_owned());
        }

        if self.groups.len() == MAX_GROUPS {
            return Err(TOO_LARGE.to_owned());
        }
        self.groups.push(None);
        self.emit(Op::Save, slot(2 * self.groups.len()), 0);
        self.i += 1;
        Ok(())
    }

    // * + ? {n} {n,} {n,m}, each optionally followed by ? (lazy).
    fn try_quantifier(&mut self) -> Result<(), String> {
        let points = self.points;
        let c = points[self.i];
        let min;
        // None: no maximum.
        let max;
        // The count of §9.10: how many times the operand is written out.
        let mut count = 1;
        if c == '{' {
            let mut at = self.i + 1;
            let Some(low) = try_number(points, &mut at) else {
                return Err("'{' is not a quantifier".to_owned());
            };
            let mut high = Some(low);
            if points.get(at) == Some(&',') {
                at += 1;
                if points.get(at) == Some(&'}') {
                    high = None;
                } else {
                    match try_number(points, &mut at) {
                        Some(number) => high = Some(number),
                        None => return Err("'{' is not a quantifier".to_owned()),
                    }
                }
            }

            if points.get(at) != Some(&'}') {
                return Err("'{' is not a quantifier".to_owned());
            }
            if low > MAX_REPEAT || high.is_some_and(|high| high > MAX_REPEAT || high < low) {
                return Err("invalid repetition count".to_owned());
            }
            count = high.unwrap_or(low).max(1);
            if !self.measure.multiply(count) {
                return Err("invalid repetition count".to_owned());
            }
            min = low;
            max = high;
            self.i = at + 1;
        } else {
            min = if c == '+' { 1 } else { 0 };
            max = if c == '?' { Some(1) } else { None };
            self.i += 1;
        }

        if !self.measure.repeat(count) {
            return Err(TOO_LARGE.to_owned());
        }

        if self.options.extended {
            while self.i < points.len() && (is_extended_space(points[self.i]) || points[self.i] == '#') {
                self.i = skip_extended(points, self.i);
            }
        }

        let mut lazy = false;
        if points.get(self.i) == Some(&'?') {
            lazy = true;
            self.i += 1;
        }

        self.repeat(min, max, lazy)
    }

    // Repeats the last atom: its instructions min times, then a loop (no maximum) or max - min
    // optional copies. Each copy is an iteration. When the atom can match nothing and there can be
    // several iterations, each marks where it starts, and one that matched nothing ends the
    // repetition, whatever its counts (§9.10): `"ca" | match("(c??){2}a")` captures "" at 1.
    fn repeat(&mut self, min: usize, max: Option<usize>, lazy: bool) -> Result<(), String> {
        let start = self.atom;
        let length = self.ops.len() - start;
        let nullable = self.frame.last != Some(false);
        let marked = nullable && max.is_none_or(|max| max > 1);
        let iteration = length + if marked { 2 } else { 0 };
        let size = min * iteration
            + match max {
                None => iteration + 2,
                Some(max) => (max - min) * (iteration + 1),
            };
        let mark = if marked {
            self.marks += 1;
            Some(self.marks - 1)
        } else {
            None
        };
        let body = Body { ops: self.ops.split_off(start), a: self.a.split_off(start), b: self.b.split_off(start), mark, end: start + size };
        for _ in 0..min {
            self.copy(&body);
        }
        match max {
            None => {
                // again: split (iteration, end); iteration; jump again; end:
                let again = self.ops.len();
                let exit = distance(again, body.end);
                self.emit(Op::Split, if lazy { exit } else { 1 }, if lazy { 1 } else { exit });
                self.copy(&body);
                self.emit(Op::Jump, distance(self.ops.len(), again), 0);
            }
            Some(max) => {
                // split (iteration, end); iteration; split (iteration, end); iteration; …; end:
                for _ in min..max {
                    let exit = distance(self.ops.len(), body.end);
                    self.emit(Op::Split, if lazy { exit } else { 1 }, if lazy { 1 } else { exit });
                    self.copy(&body);
                }
            }
        }

        self.frame.last = Some(min == 0 || nullable);
        Ok(())
    }

    // One iteration of a repetition: the atom, between its mark and the exit of an empty iteration.
    fn copy(&mut self, body: &Body) {
        if let Some(mark) = body.mark {
            self.emit(Op::Mark, slot(mark), 0);
        }
        self.ops.extend_from_slice(&body.ops);
        self.a.extend_from_slice(&body.a);
        self.b.extend_from_slice(&body.b);
        if let Some(mark) = body.mark {
            self.emit(Op::EmptyExit, slot(mark), distance(self.ops.len(), body.end));
        }
    }

    // [...] or [^...]: literals, escapes, ranges of literals and shorthands; the result is the
    // number of its items.
    fn try_class(&mut self) -> Result<usize, String> {
        let mut items = 0;
        let points = self.points;
        let mut at = self.i + 1;
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
                items += 1;
                at += 2;
                continue;
            }

            let literal = try_class_literal(points, &mut at)?;
            set.add(literal as u32, literal as u32);
            previous_literal = Some(literal);
            items += 1;
        }

        if at == first {
            return Err("empty class".to_owned());
        }
        // The i flag folds before a negation applies: [^a] matches neither a nor A.
        if self.options.ignore_case {
            set.add_ascii_cases();
        }
        self.start_atom();
        self.class(&mut set, negated);
        self.i = at + 1;
        Ok(items)
    }

    // A class as a set of the program: a negated class is its complement.
    fn class(&mut self, set: &mut RangeSet, negated: bool) {
        let mut complement;
        let ranges = if negated {
            complement = set.complement();
            complement.ranges()
        } else {
            set.ranges()
        };
        let mut pairs = Vec::with_capacity(2 * ranges.len());
        for &(low, high) in ranges {
            pairs.push(low);
            pairs.push(high);
        }

        self.sets.push(pairs);
        self.emit(Op::Set, slot(self.sets.len() - 1), 0);
    }

    // A code point; under the i flag an ASCII letter becomes the class of its two cases.
    fn literal(&mut self, c: char) {
        self.start_atom();
        if self.options.ignore_case && c.is_ascii_alphabetic() {
            let mut set = RangeSet::default();
            set.add(c as u32, c as u32);
            set.add_ascii_cases();
            self.class(&mut set, false);
            return;
        }

        self.emit(Op::Char, c as i32, 0);
    }
}

// How far an instruction that jumps from `from` goes to reach `to`.
fn distance(from: usize, to: usize) -> i32 {
    to as i32 - from as i32
}

// A slot, a mark or a set as an operand.
fn slot(index: usize) -> i32 {
    index as i32
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
            // Two hex digits: a code point below U+0100, always a char.
            return Ok(char::from_u32(high * 16 + low).unwrap_or(code_points::REPLACEMENT));
        }
        _ => return Err(format!("invalid escape \\{c}")),
    };

    *i += 2;
    Ok(literal)
}

fn try_number(points: &[char], at: &mut usize) -> Option<usize> {
    let mut value = 0;
    let start = *at;
    while let Some(digit) = points.get(*at).and_then(|c| c.to_digit(10)) {
        value = (value * 10 + digit as usize).min(MAX_REPEAT + 1);
        *at += 1;
    }

    if *at > start { Some(value) } else { None }
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

/// The limits of §11 on the part of a pattern read so far: its size, the groups open, and the
/// counts of the counted quantifiers nested in one another.
struct Measure {
    // Per open group: the size when it opened.
    starts: Vec<usize>,
    // For the pattern and per open group: the largest product of the counts nested in it so far.
    products: Vec<usize>,
    size: usize,
    // The last item, the operand of a quantifier: its size and the largest product of the counts
    // nested in it.
    atom: usize,
    atom_product: usize,
}

impl Measure {
    fn new() -> Measure {
        Measure { starts: Vec::new(), products: vec![1], size: 0, atom: 0, atom_product: 1 }
    }

    /// The groups open.
    fn depth(&self) -> usize {
        self.starts.len()
    }

    /// An item of this size (a literal, a class, an anchor, a '|'); false when the pattern is too
    /// large.
    fn add(&mut self, item_size: usize) -> bool {
        self.atom = item_size;
        self.atom_product = 1;
        self.size += item_size;
        self.size <= MAX_SIZE
    }

    /// A group opens: it counts 1; false when the pattern is too large.
    fn open(&mut self) -> bool {
        self.starts.push(self.size);
        self.products.push(1);
        self.add(1)
    }

    /// A group closes: it is the last item, with all it holds.
    fn close(&mut self) {
        self.atom = self.size - self.starts.pop().unwrap_or(0);
        self.atom_product = self.products.pop().unwrap_or(1);
        if let Some(outer) = self.products.last_mut() {
            *outer = (*outer).max(self.atom_product);
        }
    }

    /// A counted quantifier on the last item; false when the nested counts multiply above
    /// [`MAX_REPEAT`].
    fn multiply(&mut self, count: usize) -> bool {
        let product = count * self.atom_product;
        if let Some(outer) = self.products.last_mut() {
            *outer = (*outer).max(product);
        }
        product <= MAX_REPEAT
    }

    /// A quantifier on the last item: it counts 1, and the item once more for each count above 1;
    /// false when the pattern is too large.
    fn repeat(&mut self, count: usize) -> bool {
        self.size += self.atom * (count - 1) + 1;
        self.size <= MAX_SIZE
    }
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
        for &(low, high) in set.ranges() {
            self.add(low, high);
        }
    }

    /// The i flag of §9.10: adds the other case of every ASCII letter in the set (A-Z and a-z, 32
    /// apart); no other code point folds.
    fn add_ascii_cases(&mut self) {
        let mut added = Vec::new();
        for &(low, high) in self.ranges() {
            let upper_low = low.max('A' as u32);
            let upper_high = high.min('Z' as u32);
            if upper_low <= upper_high {
                added.push((upper_low + 32, upper_high + 32));
            }
            let lower_low = low.max('a' as u32);
            let lower_high = high.min('z' as u32);
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
        for &(low, high) in self.ranges() {
            if low > next {
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
