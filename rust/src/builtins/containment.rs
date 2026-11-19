//! §9.4 `contains/1`: whether the input contains the argument.

use crate::runtime::interpreter::{Input, Interpreter};
use crate::values::value_ops::Fallible;
use crate::values::{JqValue, errors, value_ops};

use super::native_table::Registry;

pub fn register(table: &mut Registry) {
    table.value("contains", 1, contains);
}

// At the top level the kinds must be equal; below it a kind mismatch is false.
fn contains(_: &Interpreter<'_>, input: Input<'_>, arguments: &mut [JqValue]) -> Fallible {
    if input.kind() == arguments[0].kind() {
        Ok(JqValue::from_bool(check(&input, &arguments[0])))
    } else {
        Err(errors::cannot_check_containment(&input, &arguments[0]))
    }
}

// A pending check of containers: a contains b. For arrays, every element b[item] must be contained
// by some a[candidate]; for objects, every key of b (b's entry item) must be in a with a value
// that contains b's.
struct Frame<'a> {
    a: &'a JqValue,
    b: &'a JqValue,
    item: usize,
    candidate: usize,
}

// What a frame asks for next: its result, or the result of a child check.
enum Step<'a> {
    Done(bool),
    Child(&'a JqValue, &'a JqValue),
}

// Containers are checked with an explicit stack, so that deep values cannot exhaust the host
// stack (§11): each frame asks for one child result at a time, which comes back in 'answer'.
fn check(a: &JqValue, b: &JqValue) -> bool {
    if let Some(scalar) = try_scalar(a, b) {
        return scalar;
    }

    let mut stack = vec![Frame { a, b, item: 0, candidate: 0 }];
    let mut answer = false;
    let mut answered = false;
    while let Some(frame) = stack.last_mut() {
        let step = if matches!(frame.b, JqValue::Array(_)) { step_array(frame, answered, answer) } else { step_object(frame, answered, answer) };
        answered = false;
        match step {
            Step::Done(finished) => {
                stack.pop();
                (answer, answered) = (finished, true);
            }
            Step::Child(child_a, child_b) => match try_scalar(child_a, child_b) {
                Some(direct) => (answer, answered) = (direct, true),
                None => stack.push(Frame { a: child_a, b: child_b, item: 0, candidate: 0 }),
            },
        }
    }

    answer
}

// The next child check of an array frame, or its result.
fn step_array<'a>(frame: &mut Frame<'a>, answered: bool, answer: bool) -> Step<'a> {
    let (JqValue::Array(a), JqValue::Array(b)) = (frame.a, frame.b) else {
        return Step::Done(false);
    };
    if answered {
        if answer {
            (frame.item, frame.candidate) = (frame.item + 1, 0);
        } else {
            frame.candidate += 1;
        }
    }

    if frame.item == b.count() {
        return Step::Done(true);
    }
    if frame.candidate == a.count() {
        return Step::Done(false);
    }
    Step::Child(a.get(frame.candidate), b.get(frame.item))
}

// The next child check of an object frame, or its result.
fn step_object<'a>(frame: &mut Frame<'a>, answered: bool, answer: bool) -> Step<'a> {
    let (JqValue::Object(a), JqValue::Object(b)) = (frame.a, frame.b) else {
        return Step::Done(false);
    };
    if answered {
        if !answer {
            return Step::Done(false);
        }
        frame.item += 1;
    }

    if frame.item == b.count() {
        return Step::Done(true);
    }
    match a.get(b.key_at(frame.item)) {
        Some(value) => Step::Child(value, b.value_at(frame.item)),
        None => Step::Done(false),
    }
}

// The result of a check that needs no frame: kinds differ, strings, or other scalars.
fn try_scalar(a: &JqValue, b: &JqValue) -> Option<bool> {
    if a.kind() != b.kind() {
        return Some(false);
    }

    match (a, b) {
        (JqValue::Array(_), _) | (JqValue::Object(_), _) => None,
        (JqValue::String(text), JqValue::String(part)) => Some(text.contains(part.as_str())),
        _ => Some(value_ops::are_equal(a, b)),
    }
}
