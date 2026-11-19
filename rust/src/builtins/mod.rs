//! Builtins: the native catalogue (`natives`, plain data, used by the binding layer) and the
//! native implementations, registered in `native_table` and called by the interpreter.

pub mod assign_natives;
pub mod calendar;
pub mod collection_natives;
pub mod containment;
pub mod core_natives;
pub mod date_natives;
pub mod formats;
pub mod loop_natives;
pub mod math_extra;
pub mod math_natives;
pub mod module_natives;
pub mod native_table;
pub mod natives;
pub mod path_natives;
pub mod regex_engine;
pub mod regex_natives;
pub mod regex_syntax;
pub mod strftime;
pub mod string_natives;
