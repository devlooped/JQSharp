//! §9.4: keys, membership, sorting and its relatives.

use std::cmp::Ordering;
use std::sync::Arc;

use crate::runtime::interpreter::{Input, Interpreter};
use crate::values::value_ops::Fallible;
use crate::values::{JqArray, JqValue, errors, value_ops};

use super::native_table::Registry;

pub fn register(table: &mut Registry) {
    table.value("keys", 0, |_, input, _| keys(&input, true));
    table.value("keys_unsorted", 0, |_, input, _| keys(&input, false));
    table.value("has", 1, has);
    table.value("sort", 0, |_, input, _| match input.into_owned() {
        JqValue::Array(array) => Ok(JqValue::Array(value_ops::sort(array))),
        other => Err(errors::cannot_be_sorted(&other)),
    });
    table.value("unique", 0, unique);
    table.value("_sort_by_impl", 1, sort_by);
    table.value("_group_by_impl", 1, group_by);
    table.value("_min_by_impl", 1, |_, input, arguments| extreme(&input, &arguments[0], false));
    table.value("_max_by_impl", 1, |_, input, arguments| extreme(&input, &arguments[0], true));
    table.value("min", 0, |_, input, _| extreme(&input, &input, false));
    table.value("max", 0, |_, input, _| extreme(&input, &input, true));
    table.value("bsearch", 1, binary_search);
}

fn keys(input: &JqValue, sorted: bool) -> Fallible {
    match input {
        JqValue::Object(object) => {
            let keys = if sorted {
                object.sorted_order().iter().map(|index| JqValue::String(Arc::clone(object.key_at(*index as usize)))).collect()
            } else {
                object.entries().iter().map(|(key, _)| JqValue::String(Arc::clone(key))).collect()
            };
            Ok(JqValue::Array(JqArray::from_owned(keys)))
        }
        JqValue::Array(array) => Ok(JqValue::Array(JqArray::from_owned((0..array.count()).map(|index| JqValue::Number(index as f64)).collect()))),
        other => Err(errors::has_no_keys(other)),
    }
}

fn has(_: &Interpreter<'_>, input: Input<'_>, arguments: &mut [JqValue]) -> Fallible {
    match (&*input, &arguments[0]) {
        (JqValue::Object(object), JqValue::String(key)) => Ok(JqValue::from_bool(object.contains_key(key))),
        (JqValue::Array(array), JqValue::Number(key)) => {
            let index = key.trunc();
            Ok(JqValue::from_bool(index >= 0.0 && index < array.count() as f64))
        }
        (JqValue::Null, _) => Ok(JqValue::False),
        (input, key) => Err(errors::cannot_check_has(input, key)),
    }
}

fn unique(_: &Interpreter<'_>, input: Input<'_>, _: &mut [JqValue]) -> Fallible {
    match input.into_owned() {
        JqValue::Array(array) => {
            let mut kept = value_ops::sort(array).into_vec();
            kept.dedup_by(|next, previous| value_ops::compare(previous, next) == Ordering::Equal);
            Ok(JqValue::Array(JqArray::from_owned(kept)))
        }
        other => Err(errors::cannot_be_sorted(&other)),
    }
}

// _sort_by_impl(keys): the input stably sorted by the corresponding keys.
fn sort_by(_: &Interpreter<'_>, input: Input<'_>, arguments: &mut [JqValue]) -> Fallible {
    let Some((items, keys)) = same_length_arrays(&input, &arguments[0]) else {
        return Err(errors::cannot_be_sorted_by(&input, &arguments[0]));
    };
    let order = value_ops::sort_order(keys);
    Ok(JqValue::Array(JqArray::from_owned(order.iter().map(|index| items.get(*index).clone()).collect())))
}

// _group_by_impl(keys): sorted as _sort_by_impl, then consecutive elements with equal keys grouped.
fn group_by(_: &Interpreter<'_>, input: Input<'_>, arguments: &mut [JqValue]) -> Fallible {
    let Some((items, keys)) = same_length_arrays(&input, &arguments[0]) else {
        return Err(errors::cannot_be_sorted_by(&input, &arguments[0]));
    };
    let order = value_ops::sort_order(keys);
    let mut groups = Vec::new();
    let mut group = Vec::new();
    for (i, index) in order.iter().enumerate() {
        if i > 0 && value_ops::compare(keys.get(order[i - 1]), keys.get(*index)) != Ordering::Equal {
            groups.push(JqValue::Array(JqArray::from_owned(std::mem::take(&mut group))));
        }
        group.push(items.get(*index).clone());
    }
    if !group.is_empty() {
        groups.push(JqValue::Array(JqArray::from_owned(group)));
    }

    Ok(JqValue::Array(JqArray::from_owned(groups)))
}

// _min_by_impl, _max_by_impl, min, max: null for an empty input; else the first element with the
// smallest key, or the last with the largest.
fn extreme(input: &JqValue, keys: &JqValue, max: bool) -> Fallible {
    let (JqValue::Array(items), JqValue::Array(by)) = (input, keys) else {
        return Err(errors::cannot_be_iterated(input, keys));
    };
    if items.count() != by.count() {
        return Err(errors::wrong_length(input, keys));
    }

    if items.is_empty() {
        return Ok(JqValue::Null);
    }

    let mut best = 0;
    for i in 1..items.count() {
        let c = value_ops::compare(by.get(i), by.get(best));
        if if max { c != Ordering::Less } else { c == Ordering::Less } {
            best = i;
        }
    }

    Ok(items.get(best).clone())
}

fn binary_search(_: &Interpreter<'_>, input: Input<'_>, arguments: &mut [JqValue]) -> Fallible {
    let JqValue::Array(items) = &*input else {
        return Err(errors::cannot_be_searched(&input));
    };
    let mut low: isize = 0;
    let mut high: isize = items.count() as isize - 1;
    while low <= high {
        let middle = (low + high) / 2;
        match value_ops::compare(items.get(middle as usize), &arguments[0]) {
            Ordering::Equal => return Ok(JqValue::Number(middle as f64)),
            Ordering::Less => low = middle + 1,
            Ordering::Greater => high = middle - 1,
        }
    }

    Ok(JqValue::Number((-1 - low) as f64))
}

// The input and its keys, when both are arrays of the same length.
fn same_length_arrays<'a>(input: &'a JqValue, keys: &'a JqValue) -> Option<(&'a JqArray, &'a JqArray)> {
    match (input, keys) {
        (JqValue::Array(items), JqValue::Array(by)) if items.count() == by.count() => Some((items, by)),
        _ => None,
    }
}
