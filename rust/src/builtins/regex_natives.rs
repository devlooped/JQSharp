//! §9.10 `test/2` and `match/2`: the flags, the search loop and the match objects, with offsets
//! and lengths in code points. The pattern goes through `regex_syntax` to the host engine
//! (`regex_engine`), which works on UTF-8 bytes.

use std::borrow::Cow;
use std::cell::RefCell;
use std::rc::Rc;
use std::sync::Arc;

use crate::binding::bound::Node;
use crate::runtime::env::EnvRef;
use crate::runtime::interpreter::{Emit, Input, Interpreter, fail, output};
use crate::runtime::path::Path;
use crate::runtime::signal::Flow;
use crate::values::value_ops::Fallible;
use crate::values::{JqArray, JqObject, JqString, JqValue, errors};

use super::native_table::Registry;
use super::regex_engine::{self, HostPattern, Spans};
use super::regex_syntax::{self, Options};

pub fn register(table: &mut Registry) {
    table.value("test", 2, test);
    table.closure("match", 2, match_all);
}

// A search ready to run: the compiled pattern, the names of its groups and the flags of the loop.
struct Search {
    pattern: Arc<HostPattern>,
    groups: Vec<JqValue>,
    global: bool,
    skip_empty: bool,
}

// The searches this thread prepared last, by the regex and flags they were asked with: a program
// asks for the same few again and again, and this spares it the translation and the shared cache.
struct Prepared {
    re: JqString,
    flags: Option<JqString>,
    search: Rc<Search>,
}

const PREPARED_LIMIT: usize = 16;

// The keys of the match objects.
struct Keys {
    offset: JqString,
    length: JqString,
    string: JqString,
    captures: JqString,
    name: JqString,
}

thread_local! {
    static PREPARED: RefCell<Vec<Prepared>> = const { RefCell::new(Vec::new()) };
    static KEYS: Keys = Keys {
        offset: Arc::new("offset".to_owned()),
        length: Arc::new("length".to_owned()),
        string: Arc::new("string".to_owned()),
        captures: Arc::new("captures".to_owned()),
        name: Arc::new("name".to_owned()),
    };
}

fn test(_: &Interpreter<'_>, input: Input<'_>, arguments: &mut [JqValue]) -> Fallible {
    let (text, search) = try_prepare(&input, &arguments[0], &arguments[1])?;
    let mut found = false;
    let mut p = 0;
    while p <= text.len() {
        let Some((start, end)) = search.pattern.try_find(text, p) else {
            break;
        };
        if !(search.skip_empty && start == end) {
            found = true;
            break;
        }
        p = after(text, start, end);
    }

    Ok(JqValue::from_bool(found))
}

// match(re; flags): flags outermost, re innermost (re fastest); one output per reported match.
fn match_all<'p>(interpreter: &Interpreter<'p>, env: &EnvRef<'p>, arguments: &'p [Node], input: Input<'_>, path: Path, emit: Emit<'_>) -> Flow {
    let at = Path::of(&path);
    interpreter.for_each(&arguments[1], env, Cow::Borrowed(&input), Path::Off, &mut |flags, _| {
        interpreter.for_each(&arguments[0], env, Cow::Borrowed(&input), Path::Off, &mut |re, _| {
            let (text, search) = match try_prepare(&input, &re, &flags) {
                Ok(prepared) => prepared,
                Err(error) => return Err(fail(error)),
            };
            // The search loop of §9.10, on bytes; the match objects are in code points.
            let map = CodePointMap::new(text);
            let mut spans = search.pattern.spans();
            let mut p = 0;
            while p <= text.len() {
                let Some((start, end)) = search.pattern.try_match(text, p, &mut spans) else {
                    break;
                };
                if !(search.skip_empty && start == end) {
                    output(emit, match_object(&search, text, &map, (start, end), &spans), at.clone())?;
                    if !search.global {
                        break;
                    }
                }
                p = after(text, start, end);
            }
            Ok(())
        })
    })
}

// Where the next search starts after a match: at its end, or one code point further when it is empty.
fn after(text: &str, start: usize, end: usize) -> usize {
    if end > start {
        return end;
    }
    match text[end..].chars().next() {
        Some(c) => end + c.len_utf8(),
        None => end + 1,
    }
}

fn try_prepare<'a>(input: &'a JqValue, re: &JqValue, flags: &JqValue) -> Result<(&'a str, Rc<Search>), JqValue> {
    let JqValue::String(text) = input else {
        return Err(errors::cannot_be_matched(input));
    };
    let JqValue::String(pattern) = re else {
        return Err(errors::not_a_string(re));
    };
    let letters = match flags {
        JqValue::String(letters) => Some(letters),
        JqValue::Null => None,
        other => return Err(errors::not_a_string(other)),
    };

    let known = PREPARED.with_borrow(|prepared| {
        prepared.iter().find(|entry| same(&entry.re, pattern) && same_flags(&entry.flags, letters)).map(|entry| Rc::clone(&entry.search))
    });
    if let Some(search) = known {
        return Ok((text, search));
    }

    let (mut global, mut skip_empty) = (false, false);
    let mut options = Options::default();
    for letter in letters.map_or("", |letters| letters.as_str()).chars() {
        match letter {
            'g' => global = true,
            'i' => options.ignore_case = true,
            'x' => options.extended = true,
            'n' => skip_empty = true,
            'm' | 'p' => options.dot_all = true,
            's' | 'l' => {}
            _ => return Err(errors::invalid_modifiers(letters.map_or("", |letters| letters.as_str()))),
        }
    }

    let translation = regex_syntax::try_translate(pattern, options).map_err(|reason| errors::regex_failure(&reason))?;
    let compiled = regex_engine::try_compile(&translation.pattern, translation.nesting).map_err(|reason| errors::regex_failure(&reason))?;
    let groups = translation.groups.into_iter().map(|name| name.map_or(JqValue::Null, JqValue::from_string)).collect();
    let search = Rc::new(Search { pattern: compiled, groups, global, skip_empty });
    PREPARED.with_borrow_mut(|prepared| {
        if prepared.len() >= PREPARED_LIMIT {
            prepared.remove(0);
        }
        prepared.push(Prepared { re: Arc::clone(pattern), flags: letters.cloned(), search: Rc::clone(&search) });
    });
    Ok((text, search))
}

fn same(a: &JqString, b: &JqString) -> bool {
    Arc::ptr_eq(a, b) || a.as_str() == b.as_str()
}

fn same_flags(a: &Option<JqString>, b: Option<&JqString>) -> bool {
    match (a, b) {
        (None, None) => true,
        (Some(a), Some(b)) => same(a, b),
        _ => false,
    }
}

fn match_object(search: &Search, input: &str, map: &CodePointMap, found: (usize, usize), spans: &Spans) -> JqValue {
    KEYS.with(|keys| {
        let mut captures = Vec::with_capacity(search.groups.len());
        for (g, name) in search.groups.iter().enumerate() {
            let mut capture = JqObject::with_capacity(4);
            match spans.group(g + 1) {
                Some((start, end)) if start < end => add_span(&mut capture, keys, input, map, start, end),
                Some((start, _)) => {
                    // An empty group has the key order of an absent one, as jq.
                    capture.insert(Arc::clone(&keys.offset), JqValue::Number(map.code_point(start) as f64));
                    capture.insert(Arc::clone(&keys.string), JqValue::from_text(""));
                    capture.insert(Arc::clone(&keys.length), JqValue::Number(0.0));
                }
                None => {
                    capture.insert(Arc::clone(&keys.offset), JqValue::Number(-1.0));
                    capture.insert(Arc::clone(&keys.string), JqValue::Null);
                    capture.insert(Arc::clone(&keys.length), JqValue::Number(0.0));
                }
            }

            capture.insert(Arc::clone(&keys.name), name.clone());
            captures.push(JqValue::Object(capture));
        }

        let mut matched = JqObject::with_capacity(4);
        add_span(&mut matched, keys, input, map, found.0, found.1);
        matched.insert(Arc::clone(&keys.captures), JqValue::Array(JqArray::from_owned(captures)));
        JqValue::Object(matched)
    })
}

// "offset", "length", "string" of a span given in bytes.
fn add_span(target: &mut JqObject, keys: &Keys, input: &str, map: &CodePointMap, start: usize, end: usize) {
    let offset = map.code_point(start);
    target.insert(Arc::clone(&keys.offset), JqValue::Number(offset as f64));
    target.insert(Arc::clone(&keys.length), JqValue::Number((map.code_point(end) - offset) as f64));
    target.insert(Arc::clone(&keys.string), JqValue::from_text(&input[start..end]));
}

/// Byte indices to code point indices, for a string.
struct CodePointMap {
    // The code point index of each byte index (of the code point the byte is in); None when every
    // code point is one byte.
    to_code_point: Option<Vec<u32>>,
}

impl CodePointMap {
    fn new(text: &str) -> CodePointMap {
        if text.is_ascii() {
            return CodePointMap { to_code_point: None };
        }
        let mut to_code_point = Vec::with_capacity(text.len() + 1);
        let mut code_point = 0;
        for c in text.chars() {
            for _ in 0..c.len_utf8() {
                to_code_point.push(code_point);
            }
            code_point += 1;
        }
        to_code_point.push(code_point);
        CodePointMap { to_code_point: Some(to_code_point) }
    }

    fn code_point(&self, byte: usize) -> usize {
        match &self.to_code_point {
            Some(map) => map[byte] as usize,
            None => byte,
        }
    }
}
