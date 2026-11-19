//! The implementations of the native catalogue, by `Native::index`. Each family of natives
//! registers its entries; every entry of the catalogue has one, which the table checks once, when
//! it is built (a missing one is a defect of the build, not a run-time error).

use std::sync::LazyLock;

use crate::binding::bound::Node;
use crate::runtime::env::EnvRef;
use crate::runtime::interpreter::{Emit, Input, Interpreter};
use crate::runtime::path::Path;
use crate::runtime::signal::Flow;
use crate::values::JqValue;
use crate::values::value_ops::Fallible;

use super::natives::{self, Native};
use super::{
    assign_natives, collection_natives, containment, core_natives, date_natives, loop_natives, math_natives, module_natives, path_natives, regex_natives,
    string_natives,
};

/// A native with value arguments (§9.1): called once per combination of its arguments' outputs,
/// it gives one output or an error. The input is owned when the call is its last user; the
/// arguments are the native's to take.
pub type ValueNative = fn(&Interpreter<'_>, Input<'_>, &mut [JqValue]) -> Fallible;

/// A native that takes its arguments as closures (`Native::takes_closures`): it evaluates them
/// itself with the environment, pushes its outputs to `emit` (with their paths in path mode, when
/// it passes paths) and returns a signal, as `Interpreter::eval` does.
pub type ClosureNative = for<'p> fn(&Interpreter<'p>, &EnvRef<'p>, &'p [Node], Input<'_>, Path, Emit<'_>) -> Flow;

/// The table while the families register their natives.
pub struct Registry {
    values: Vec<Option<ValueNative>>,
    closures: Vec<Option<ClosureNative>>,
}

impl Registry {
    // Registration checks the kind against the catalogue: a mismatch is a defect of the build.
    pub fn value(&mut self, name: &str, arity: usize, function: ValueNative) {
        self.values[find(name, arity, false).index] = Some(function);
    }

    pub fn closure(&mut self, name: &str, arity: usize, function: ClosureNative) {
        self.closures[find(name, arity, true).index] = Some(function);
    }
}

fn find(name: &str, arity: usize, closures: bool) -> &'static Native {
    let Some(native) = natives::find(name, arity) else {
        panic!("{name}/{arity} is not in the native catalogue");
    };
    if native.takes_closures != closures {
        panic!("{name}/{arity} is registered with the wrong kind");
    }
    native
}

static TABLE: LazyLock<Registry> = LazyLock::new(|| {
    let count = natives::all().len();
    let mut table = Registry { values: vec![None; count], closures: vec![None; count] };
    core_natives::register(&mut table);
    loop_natives::register(&mut table);
    path_natives::register(&mut table);
    collection_natives::register(&mut table);
    string_natives::register(&mut table);
    assign_natives::register(&mut table);
    module_natives::register(&mut table);
    containment::register(&mut table);
    math_natives::register(&mut table);
    date_natives::register(&mut table);
    regex_natives::register(&mut table);

    for native in natives::all() {
        if table.values[native.index].is_none() && table.closures[native.index].is_none() {
            panic!("{} has no implementation", native.signature());
        }
    }
    table
});

/// The implementation of a value native (one that does not take closures).
pub fn value(native: &Native) -> ValueNative {
    match TABLE.values[native.index] {
        Some(function) => function,
        None => panic!("{} is not a value native", native.signature()),
    }
}

/// The implementation of a native that takes closures.
pub fn closure(native: &Native) -> ClosureNative {
    match TABLE.closures[native.index] {
        Some(function) => function,
        None => panic!("{} does not take closures", native.signature()),
    }
}

/// Whether `native` has an implementation.
pub fn is_implemented(native: &Native) -> bool {
    TABLE.values[native.index].is_some() || TABLE.closures[native.index].is_some()
}
