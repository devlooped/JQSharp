//! `modulemeta/0` (§9.11), the native of the modules group: a port without the group (see
//! binding/modules.rs) leaves this file out and does not list it.

use crate::binding::resolver::binding_messages;
use crate::runtime::interpreter::{Input, Interpreter};
use crate::values::value_ops::Fallible;
use crate::values::{JqValue, errors};

use super::native_table::Registry;

pub fn register(table: &mut Registry) {
    table.value("modulemeta", 0, modulemeta);
}

// The description of the module the input names, resolved as from the main program with the
// source the program was bound with: what the loader recorded when the program loaded it.
fn modulemeta(interpreter: &Interpreter<'_>, input: Input<'_>, _: &mut [JqValue]) -> Fallible {
    let JqValue::String(path) = &*input else {
        return Err(errors::modulemeta_not_string());
    };
    match &interpreter.program().modules {
        Some(modules) => modules.try_describe(path).map_err(JqValue::from_string),
        None => Err(JqValue::from_string(binding_messages::module_not_found(path))),
    }
}
