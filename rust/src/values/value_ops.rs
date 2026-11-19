//! The one implementation of equality, ordering, arithmetic, slicing and sorting of values, with
//! jq 1.8 semantics.
//!
//! An operation that can fail in jq returns `Err` with the jqlite error value (the message
//! string); nothing here panics as part of jqlite semantics. Operations that give a new container
//! take their operand by value, so that an operand owned once is changed in place (see `JqArray`).

use std::cmp::Ordering;
use std::sync::Arc;

use super::code_points;
use super::errors;
use super::jq_array::JqArray;
use super::jq_object::JqObject;
use super::jq_value::{JqString, JqValue};

/// The result of an operation that jq can fail: the value, or the error value.
pub type Fallible = Result<JqValue, JqValue>;

// The longest string a repeat may build: 2^31 - 1 UTF-8 bytes (§4.4).
const MAX_STRING_BYTES: u64 = i32::MAX as u64;

// How deep `walk` recurses before it goes on with an explicit stack.
const MAX_RECURSIVE_WALK: u32 = 32;

/// jq's `==`: same kind and same content. Numbers compare as doubles, so `nan == nan` is false
/// and so is `[nan] == [nan]`; objects are equal whatever their key order.
pub fn are_equal(a: &JqValue, b: &JqValue) -> bool {
    walk(a, b, true, 0) == 0
}

/// jq's total order. Kinds first: null < false < true < numbers < strings < arrays < objects.
/// Strings by code point; arrays lexicographically; objects by their sorted key lists, then by
/// their values in that key order. NaN is below every other number and equal to NaN, so the order
/// is total (jq 1.8 has `nan < nan`; spec §13).
pub fn compare(a: &JqValue, b: &JqValue) -> Ordering {
    walk(a, b, false, 0).cmp(&0)
}

// A pair of containers being walked: children 0 to count - 1 are compared in turn, then tail
// (the length comparison of two arrays) decides.
struct Frame<'a> {
    a: &'a JqValue,
    b: &'a JqValue,
    count: usize,
    index: usize,
    tail: i32,
    // For the ordering of objects: the positions of the entries of a and of b in key order.
    keys: Option<(&'a [u32], &'a [u32])>,
}

enum Shallow<'a> {
    // The result, without looking at children.
    Decided(i32),
    // The children must be compared; the frame's tail is the result once they are all equal.
    Children(Frame<'a>),
}

/// Compares (equality false) or tests equality (equality true, any non-zero result means
/// "different"). The first levels recurse; deeper containers are walked with an explicit stack,
/// so nesting depth is not limited by the host stack. (The reference always uses the explicit
/// stack, kept per thread; recursing first keeps the common case free of allocation here.)
fn walk(a: &JqValue, b: &JqValue, equality: bool, depth: u32) -> i32 {
    let frame = match shallow(a, b, equality) {
        Shallow::Decided(c) => return c,
        Shallow::Children(frame) => frame,
    };
    if depth >= MAX_RECURSIVE_WALK {
        return walk_deep(frame, equality);
    }

    for index in 0..frame.count {
        let Some((x, y)) = child(&frame, index, equality) else {
            return 1;
        };
        let c = walk(x, y, equality, depth + 1);
        if c != 0 {
            return c;
        }
    }

    frame.tail
}

fn walk_deep(first: Frame<'_>, equality: bool) -> i32 {
    let mut stack = vec![first];
    loop {
        let top = &stack[stack.len() - 1];
        let Some((a, b)) = child(top, top.index, equality) else {
            return 1;
        };
        match shallow(a, b, equality) {
            Shallow::Decided(0) => {}
            Shallow::Decided(c) => return c,
            Shallow::Children(frame) => {
                stack.push(frame);
                continue;
            }
        }

        // The pair is equal: go on with the next pair, leaving the containers that end.
        loop {
            let Some(top) = stack.last_mut() else {
                return 0;
            };
            top.index += 1;
            if top.index < top.count {
                break;
            }

            let tail = top.tail;
            stack.pop();
            if tail != 0 {
                return tail;
            }
        }
    }
}

// Compares what can be compared without looking at children.
fn shallow<'a>(a: &'a JqValue, b: &'a JqValue, equality: bool) -> Shallow<'a> {
    let kind = a.kind();
    let other = b.kind();
    if kind != other {
        return Shallow::Decided(if kind < other { -1 } else { 1 });
    }

    match (a, b) {
        (JqValue::Number(x), JqValue::Number(y)) => {
            let (x, y) = (*x, *y);
            if equality {
                return Shallow::Decided(if x == y { 0 } else { 1 });
            }
            if x.is_nan() {
                return Shallow::Decided(if y.is_nan() { 0 } else { -1 });
            }
            if y.is_nan() {
                return Shallow::Decided(1);
            }
            Shallow::Decided(if x < y {
                -1
            } else if x == y {
                0
            } else {
                1
            })
        }
        (JqValue::String(x), JqValue::String(y)) => {
            if equality {
                return Shallow::Decided(if x.as_str() == y.as_str() { 0 } else { 1 });
            }
            Shallow::Decided(order(code_points::compare(x, y)))
        }
        (JqValue::Array(x), JqValue::Array(y)) => {
            let p = x.count();
            let q = y.count();
            if equality && p != q {
                return Shallow::Decided(1);
            }
            let tail = if p == q {
                0
            } else if p < q {
                -1
            } else {
                1
            };
            let count = p.min(q);
            if count == 0 {
                return Shallow::Decided(tail);
            }
            Shallow::Children(Frame { a, b, count, index: 0, tail, keys: None })
        }
        (JqValue::Object(m), JqValue::Object(n)) => {
            if equality {
                // Same size, then each key of a found in b with an equal value: no sorting.
                if m.count() != n.count() {
                    return Shallow::Decided(1);
                }
                if m.is_empty() {
                    return Shallow::Decided(0);
                }
                return Shallow::Children(Frame { a, b, count: m.count(), index: 0, tail: 0, keys: None });
            }

            let keys = m.sorted_order();
            let others = n.sorted_order();
            let length = keys.len().min(others.len());
            for i in 0..length {
                let c = code_points::compare(m.key_at(keys[i] as usize), n.key_at(others[i] as usize));
                if c != Ordering::Equal {
                    return Shallow::Decided(order(c));
                }
            }

            if keys.len() != others.len() {
                return Shallow::Decided(if keys.len() < others.len() { -1 } else { 1 });
            }
            if keys.is_empty() {
                return Shallow::Decided(0);
            }
            Shallow::Children(Frame { a, b, count: keys.len(), index: 0, tail: 0, keys: Some((keys, others)) })
        }
        _ => Shallow::Decided(0),
    }
}

fn order(ordering: Ordering) -> i32 {
    match ordering {
        Ordering::Less => -1,
        Ordering::Equal => 0,
        Ordering::Greater => 1,
    }
}

// The children at index; None when b lacks a key of a (equality of objects only).
fn child<'a>(frame: &Frame<'a>, index: usize, equality: bool) -> Option<(&'a JqValue, &'a JqValue)> {
    match (frame.a, frame.b) {
        (JqValue::Array(x), JqValue::Array(y)) => Some((x.get(index), y.get(index))),
        (JqValue::Object(x), JqValue::Object(y)) => {
            if equality {
                return y.get(x.key_at(index)).map(|other| (x.value_at(index), other));
            }
            // The sorted key lists are equal: the entries at the same sorted position correspond.
            let (keys, others) = frame.keys?;
            Some((x.value_at(keys[index] as usize), y.value_at(others[index] as usize)))
        }
        _ => None,
    }
}

/// jq's `+`: null is the identity; numbers add; strings and arrays concatenate; objects merge
/// (right wins, insertion order kept).
pub fn try_add(a: JqValue, b: JqValue) -> Fallible {
    match (a, b) {
        (JqValue::Null, b) => Ok(b),
        (a, JqValue::Null) => Ok(a),
        (JqValue::Number(x), JqValue::Number(y)) => Ok(JqValue::Number(x + y)),
        (JqValue::String(mut x), JqValue::String(y)) => {
            Arc::make_mut(&mut x).push_str(&y);
            Ok(JqValue::String(x))
        }
        (JqValue::Array(x), JqValue::Array(y)) => Ok(JqValue::Array(x.concat(y))),
        (JqValue::Object(x), JqValue::Object(y)) => Ok(JqValue::Object(x.merge(y))),
        (a, b) => Err(errors::cannot_be(&a, &b, errors::ADDED)),
    }
}

/// jq's `-`: numbers subtract; arrays drop every element equal to one of the right-hand array.
pub fn try_subtract(a: JqValue, b: JqValue) -> Fallible {
    match (a, b) {
        (JqValue::Number(x), JqValue::Number(y)) => Ok(JqValue::Number(x - y)),
        (JqValue::Array(mut items), JqValue::Array(remove)) => {
            if !items.is_empty() && !remove.is_empty() {
                items.make_mut().retain(|item| !remove.iter().any(|other| are_equal(item, other)));
            }
            Ok(JqValue::Array(items))
        }
        (a, b) => Err(errors::cannot_be(&a, &b, errors::SUBTRACTED)),
    }
}

/// jq's unary `-`: IEEE negation of a number (`0 | -.` is -0).
pub fn try_negate(value: JqValue) -> Fallible {
    match value {
        JqValue::Number(number) => Ok(JqValue::Number(-number)),
        other => Err(errors::cannot_negate(&other)),
    }
}

/// jq's `*`: numbers multiply; a string and a number (either order) repeat the string; objects
/// merge recursively.
pub fn try_multiply(a: JqValue, b: JqValue) -> Fallible {
    match (a, b) {
        (JqValue::Number(x), JqValue::Number(y)) => Ok(JqValue::Number(x * y)),
        (JqValue::String(text), JqValue::Number(times)) | (JqValue::Number(times), JqValue::String(text)) => try_repeat(&text, times),
        (JqValue::Object(x), JqValue::Object(y)) => Ok(JqValue::Object(deep_merge(x, y))),
        (a, b) => Err(errors::cannot_be(&a, &b, errors::MULTIPLIED)),
    }
}

// s * n: null for a NaN or negative n; otherwise s repeated trunc(n) times ("" below 1).
fn try_repeat(text: &str, times: f64) -> Fallible {
    if times.is_nan() || times < 0.0 {
        return Ok(JqValue::Null);
    }

    let count = if times >= i32::MAX as f64 { i32::MAX as u64 } else { times as u64 };
    if count == 0 || text.is_empty() {
        return Ok(JqValue::from_text(""));
    }
    if text.len() as u64 * count > MAX_STRING_BYTES {
        return Err(errors::repeat_too_long());
    }

    Ok(JqValue::from_string(text.repeat(count as usize)))
}

// One pair of objects being merged: the result so far, the right side's entries still to take,
// and the key whose values are being merged below.
struct Merge {
    result: JqObject,
    right: std::vec::IntoIter<(JqString, JqValue)>,
    key: Option<JqString>,
}

impl Merge {
    fn new(left: JqObject, right: JqObject) -> Merge {
        Merge { result: left, right: right.into_entries().into_iter(), key: None }
    }
}

// a * b on objects: like a + b, except that a key whose two values are objects gets their merge.
// The objects being merged are on an explicit stack, so nesting depth is not limited by the host
// stack.
fn deep_merge(a: JqObject, b: JqObject) -> JqObject {
    let mut stack = vec![Merge::new(a, b)];
    loop {
        let top = stack.last_mut().expect("a merge is in progress");
        if let Some((key, value)) = top.right.next() {
            if let JqValue::Object(right) = value {
                if matches!(top.result.get(&key), Some(JqValue::Object(_))) {
                    // The existing object leaves its slot while it is merged, so that it is not shared.
                    let JqValue::Object(left) = std::mem::take(top.result.slot(&key)) else {
                        unreachable!("the slot holds an object");
                    };
                    top.key = Some(key);
                    stack.push(Merge::new(left, right));
                } else {
                    top.result.insert(key, JqValue::Object(right));
                }
            } else {
                top.result.insert(key, value);
            }

            continue;
        }

        let merged = stack.pop().expect("a merge is in progress").result;
        let Some(parent) = stack.last_mut() else {
            return merged;
        };
        let key = parent.key.take().expect("the parent waits for a key");
        parent.result.insert(key, JqValue::Object(merged));
    }
}

/// jq's `/`: numbers divide (a zero divisor is an error, `0/0` too); strings split on the
/// right-hand string.
pub fn try_divide(a: JqValue, b: JqValue) -> Fallible {
    match (a, b) {
        (JqValue::Number(x), JqValue::Number(y)) => {
            if y == 0.0 {
                return Err(errors::divisor_is_zero(&JqValue::Number(x), &JqValue::Number(y), errors::DIVIDED));
            }
            Ok(JqValue::Number(x / y))
        }
        (JqValue::String(text), JqValue::String(separator)) => Ok(JqValue::Array(split(&text, &separator))),
        (a, b) => Err(errors::cannot_be(&a, &b, errors::DIVIDED)),
    }
}

/// `split/1` and `/` on strings: the empty string gives []; an empty separator splits into code
/// points.
pub fn split(text: &str, separator: &str) -> JqArray {
    if text.is_empty() {
        return JqArray::empty();
    }

    if separator.is_empty() {
        let mut buffer = [0u8; 4];
        return JqArray::from_owned(text.chars().map(|c| JqValue::from_text(c.encode_utf8(&mut buffer))).collect());
    }

    JqArray::from_owned(text.split(separator).map(JqValue::from_text).collect())
}

/// jq's `%`: both numbers truncate to integers (saturating at the 64-bit range), the sign follows
/// the dividend; NaN gives NaN; a divisor that truncates to 0 is an error.
pub fn try_modulo(a: JqValue, b: JqValue) -> Fallible {
    let (JqValue::Number(x), JqValue::Number(y)) = (&a, &b) else {
        return Err(errors::cannot_be(&a, &b, errors::REMAINDER));
    };
    if x.is_nan() || y.is_nan() {
        return Ok(JqValue::Number(f64::NAN));
    }

    let divisor = to_integer(*y);
    if divisor == 0 {
        return Err(errors::divisor_is_zero(&a, &b, errors::REMAINDER));
    }

    // -1 divides everything, and i64::MIN % -1 overflows.
    Ok(JqValue::Number(if divisor == -1 { 0.0 } else { (to_integer(*x) % divisor) as f64 }))
}

// Truncation toward zero, clamped to [-2^63, 2^63 - 1] (±Infinity clamp to the ends): what the
// cast does in Rust.
fn to_integer(value: f64) -> i64 {
    value as i64
}

/// `.[key]` (§7.6): an object by a string (absent: null), an array by a number (NaN and out of
/// range: null; negative counts from the end) or by an array (the indices where it occurs), an
/// array or a string by a slice object, null by a string, a number or an object (null); anything
/// else is `Cannot index T with T (D)`.
pub fn try_index(target: &JqValue, key: &JqValue) -> Fallible {
    match (key, target) {
        (JqValue::String(name), JqValue::Object(object)) => Ok(object.get(name).cloned().unwrap_or(JqValue::Null)),
        (JqValue::Number(index), JqValue::Array(array)) => Ok(element(array, *index)),
        (JqValue::Array(needle), JqValue::Array(array)) => Ok(JqValue::Array(indices(array, needle))),
        (JqValue::Object(_), JqValue::Array(_) | JqValue::String(_) | JqValue::Null) => try_index_slice(target, key),
        (JqValue::String(_) | JqValue::Number(_), JqValue::Null) => Ok(JqValue::Null),
        _ => Err(errors::cannot_index(target, key)),
    }
}

/// `.[key]` where it is, when `target` holds it (a field that is there, an index in range): what
/// `try_index` gives then, without a count of its own. `None` in every other case.
pub fn index_ref<'a>(target: &'a JqValue, key: &JqValue) -> Option<&'a JqValue> {
    match (key, target) {
        (JqValue::String(name), JqValue::Object(object)) => object.get(name),
        (JqValue::Number(index), JqValue::Array(array)) => element_ref(array, *index),
        _ => None,
    }
}

// .[n] on an array: NaN and out of range give null, a negative index counts from the end.
fn element(array: &JqArray, index: f64) -> JqValue {
    element_ref(array, index).cloned().unwrap_or(JqValue::Null)
}

fn element_ref(array: &JqArray, index: f64) -> Option<&JqValue> {
    if index.is_nan() {
        return None;
    }
    let mut i = index.trunc();
    if i < 0.0 {
        i += array.count() as f64;
    }
    if i >= 0.0 && i < array.count() as f64 { Some(array.get(i as usize)) } else { None }
}

// The positions at which needle occurs in items as a contiguous sub-array; none for an empty needle.
fn indices(items: &JqArray, needle: &JqArray) -> JqArray {
    let mut found = Vec::new();
    let mut start = 0;
    while !needle.is_empty() && start + needle.count() <= items.count() {
        let mut i = 0;
        while i < needle.count() && are_equal(items.get(start + i), needle.get(i)) {
            i += 1;
        }
        if i == needle.count() {
            found.push(JqValue::Number(start as f64));
        }
        start += 1;
    }

    JqArray::from_owned(found)
}

/// The slice object `{"start": start, "end": end}` that `.[s:e]` stands for (§8.1).
pub fn slice_key(start: JqValue, end: JqValue) -> JqValue {
    JqValue::Object(JqObject::from_owned(vec![(Arc::new("start".to_owned()), start), (Arc::new("end".to_owned()), end)]))
}

/// `.[start:end]` (§7.6) on an array, a string (by code point) or null (gives null). A null or
/// NaN start is 0, a null or NaN end the length; a negative bound counts from the end; both are
/// clamped, then the start rounds down and the end up.
pub fn try_slice(input: JqValue, start: &JqValue, end: &JqValue) -> Fallible {
    if !matches!(input, JqValue::Null | JqValue::Array(_) | JqValue::String(_)) {
        // The key in the message is the slice object that `.[s:e]` stands for.
        return Err(errors::cannot_index(&input, &slice_key(start.clone(), end.clone())));
    }

    try_slice_bounds(input, start, end)
}

/// `.[k]` with an object key: on an array or a string the key is a slice when it has both `start`
/// and `end` (other keys are ignored, as in jq), else the error `Array/string slice indices must
/// be integers`; null input gives null whatever the key; any other input is
/// `Cannot index T with object (D)`.
pub fn try_index_slice(input: &JqValue, key: &JqValue) -> Fallible {
    match input {
        JqValue::Null => Ok(JqValue::Null),
        JqValue::Array(_) | JqValue::String(_) => {
            let slice = key.as_object();
            let (Some(start), Some(end)) = (slice.get("start"), slice.get("end")) else {
                return Err(errors::slice_indices_not_integers());
            };
            try_slice_bounds(input.clone(), start, end)
        }
        _ => Err(errors::cannot_index(input, key)),
    }
}

// Slices an array, a string or null (gives null).
fn try_slice_bounds(input: JqValue, start: &JqValue, end: &JqValue) -> Fallible {
    match input {
        JqValue::Array(array) => {
            let (first, last) = try_slice_range(array.count(), start, end)?;
            Ok(JqValue::Array(array.slice(first, last)))
        }
        JqValue::String(text) => {
            let length = code_points::length(&text);
            let (first, last) = try_slice_range(length, start, end)?;
            if first == 0 && last == length {
                return Ok(JqValue::String(text));
            }
            Ok(JqValue::from_text(code_points::substring(&text, first, last)))
        }
        _ => Ok(JqValue::Null),
    }
}

/// The positions [first, last) that the bounds of a slice select in a sequence of `length` items
/// (§7.6): null or NaN start is 0, null or NaN end the length, negative bounds count from the end,
/// both are clamped, the start rounds down and the end up. Bounds that are not numbers or null:
/// `Array/string slice indices must be integers`.
pub fn try_slice_range(length: usize, start: &JqValue, end: &JqValue) -> Result<(usize, usize), JqValue> {
    let size = length as f64;
    let mut from = match start {
        JqValue::Null => 0.0,
        JqValue::Number(number) => *number,
        _ => return Err(errors::slice_indices_not_integers()),
    };
    let mut to = match end {
        JqValue::Null => size,
        JqValue::Number(number) => *number,
        _ => return Err(errors::slice_indices_not_integers()),
    };
    if from.is_nan() {
        from = 0.0;
    }
    if to.is_nan() {
        to = size;
    }
    if from < 0.0 {
        from += size;
    }
    if to < 0.0 {
        to += size;
    }

    let first = from.clamp(0.0, size).floor();
    let last = to.clamp(first, size).ceil();
    Ok((first as usize, last as usize))
}

/// jq's `explode`: the code points of a string.
pub fn try_explode(input: &JqValue) -> Fallible {
    match input {
        JqValue::String(text) => Ok(JqValue::Array(code_points::explode(text))),
        _ => Err(errors::explode_not_string()),
    }
}

/// jq's `implode`: an array of numbers to the string of the code points `trunc(n)`; one outside
/// [0, 0x10FFFF] or a surrogate becomes U+FFFD (`-0.5` truncates to U+0000). A non-number or NaN
/// element is an error that describes the element.
pub fn try_implode(input: &JqValue) -> Fallible {
    let JqValue::Array(codes) = input else {
        return Err(errors::implode_not_array());
    };

    let mut builder = String::with_capacity(codes.count());
    for code in codes {
        let number = match code {
            JqValue::Number(number) if !number.is_nan() => number.trunc(),
            _ => return Err(errors::codepoint_not_numeric(code)),
        };
        let scalar = number >= 0.0 && number <= code_points::MAX as f64 && code_points::is_scalar(number as i64);
        builder.push(if scalar { char::from_u32(number as u32).unwrap_or(code_points::REPLACEMENT) } else { code_points::REPLACEMENT });
    }

    Ok(JqValue::from_string(builder))
}

/// The array stably sorted (jq's `sort`).
pub fn sort(items: JqArray) -> JqArray {
    let mut sorted = items.into_vec();
    // The standard sort is stable (a merge sort), as §9.4 requires.
    sorted.sort_by(compare);
    JqArray::from_owned(sorted)
}

/// The positions of `keys` in sorted order, equal keys in their original order (a stable sort,
/// for `sort_by`, `group_by` and `unique_by`).
pub fn sort_order(keys: &JqArray) -> Vec<usize> {
    let mut order: Vec<usize> = (0..keys.count()).collect();
    order.sort_by(|a, b| compare(keys.get(*a), keys.get(*b)));
    order
}
