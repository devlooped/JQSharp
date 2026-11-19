//! §9.2 Core and §9.3 Types: the natives of the evaluation model itself and of its host (§12).

use std::borrow::Cow;
use std::sync::LazyLock;

use crate::binding::bound::Node;
use crate::binding::prelude::Prelude;
use crate::runtime::env::EnvRef;
use crate::runtime::interpreter::{Emit, Input, Interpreter, fail, output};
use crate::runtime::path::Path;
use crate::runtime::signal::{Flow, Signal};
use crate::values::value_ops::Fallible;
use crate::values::{JqArray, JqValue, code_points, errors};

use super::native_table::Registry;
use super::natives;

// builtins/0: every non-internal builtin, natives and prelude, sorted by code point.
static BUILTINS: LazyLock<JqValue> = LazyLock::new(list_builtins);

pub fn register(table: &mut Registry) {
    table.closure("empty", 0, |_, _, _, _, _, _| Ok(()));
    table.value("error", 0, |_, input, _| Err(input.into_owned()));
    table.value("type", 0, |_, input, _| Ok(JqValue::from_text(input.type_name())));
    table.value("length", 0, length);
    table.value("utf8bytelength", 0, utf8_byte_length);
    table.value("builtins", 0, |_, _, _| Ok(BUILTINS.clone()));
    table.closure("halt", 0, |_, _, _, _, _, _| Err(Signal::halt(None, 0)));
    table.closure("halt_error", 1, halt_error);
    table.closure("last", 1, last);
    table.value("debug", 0, |interpreter, input, _| {
        if let Some(debug) = interpreter.debug() {
            debug(&input);
        }
        Ok(input.into_owned())
    });
    table.value("stderr", 0, |interpreter, input, _| {
        if let Some(stderr) = interpreter.stderr() {
            stderr(&input);
        }
        Ok(input.into_owned())
    });
    table.value("infinite", 0, |_, _, _| Ok(JqValue::Number(f64::INFINITY)));
    table.value("nan", 0, |_, _, _| Ok(JqValue::Number(f64::NAN)));
    table.value("isinfinite", 0, |_, input, _| Ok(JqValue::from_bool(matches!(&*input, JqValue::Number(number) if number.is_infinite()))));
    table.value("isnan", 0, |_, input, _| Ok(JqValue::from_bool(matches!(&*input, JqValue::Number(number) if number.is_nan()))));
    table.value("isnormal", 0, |_, input, _| Ok(JqValue::from_bool(matches!(&*input, JqValue::Number(number) if number.is_normal()))));
}

fn length(_: &Interpreter<'_>, input: Input<'_>, _: &mut [JqValue]) -> Fallible {
    match &*input {
        JqValue::Null => Ok(JqValue::Number(0.0)),
        JqValue::Number(number) => Ok(JqValue::Number(number.abs())),
        JqValue::String(text) => Ok(JqValue::Number(code_points::length(text) as f64)),
        JqValue::Array(array) => Ok(JqValue::Number(array.count() as f64)),
        JqValue::Object(object) => Ok(JqValue::Number(object.count() as f64)),
        other => Err(errors::has_no_length(other)),
    }
}

fn utf8_byte_length(_: &Interpreter<'_>, input: Input<'_>, _: &mut [JqValue]) -> Fallible {
    match &*input {
        JqValue::String(text) => Ok(JqValue::Number(text.len() as f64)),
        other => Err(errors::only_strings_have_utf8_length(other)),
    }
}

// halt_error(c): halts with the input and trunc(c) for the first output c.
fn halt_error<'p>(interpreter: &Interpreter<'p>, env: &EnvRef<'p>, arguments: &'p [Node], input: Input<'_>, _: Path, _: Emit<'_>) -> Flow {
    let first = interpreter.new_label();
    let mut code = JqValue::Null;
    let result = interpreter.eval(&arguments[0], env, Cow::Borrowed(&input), Path::Off, &mut |value, _| {
        code = value;
        Err(Signal::break_to(first))
    });
    match result {
        Err(signal) if signal.is_break_to(first) => {}
        other => return other,
    }

    let JqValue::Number(number) = code else {
        return Err(fail(errors::halt_error_number_required(&input)));
    };
    let number = number.trunc();
    // The cast clamps to the range of the exit code; NaN gives 0.
    Err(Signal::halt(Some(input.into_owned()), number as i32))
}

// last(f): the last output of f, with its path. *loop*
fn last<'p>(interpreter: &Interpreter<'p>, env: &EnvRef<'p>, arguments: &'p [Node], input: Input<'_>, path: Path, emit: Emit<'_>) -> Flow {
    let mut last = None;
    interpreter.eval(&arguments[0], env, input, path, &mut |value, at| {
        last = Some((value, at));
        Ok(())
    })?;
    match last {
        Some((value, at)) => output(emit, value, at),
        None => Ok(()),
    }
}

fn list_builtins() -> JqValue {
    let mut names: Vec<String> = Vec::new();
    for native in natives::all() {
        if !native.is_internal() && !native.overrides_prelude {
            names.push(native.signature());
        }
    }
    for function in &Prelude::instance().definitions {
        if !function.name.starts_with('_') {
            names.push(function.signature());
        }
    }

    names.sort_by(|a, b| code_points::compare(a, b));
    JqValue::Array(JqArray::from_owned(names.into_iter().map(JqValue::from_string).collect()))
}
