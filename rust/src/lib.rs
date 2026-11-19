//! jqlite: the profile of the [jq](https://jqlang.org) language defined by `spec/jqlite.md`,
//! ported from the JQLite reference (`dotnet/JQLite/Core`).
//!
//! ```
//! use jqlite::JqValue;
//!
//! let expression = jqlite::parse(".items | map(.price) | add").unwrap();
//! let input = JqValue::parse(r#"{"items":[{"price":1.5},{"price":2}]}"#).unwrap();
//! let results = expression.evaluate(input).unwrap();
//! assert_eq!(results[0].to_json(), "3.5");
//! ```
//!
//! The engine is the five layers `values`, `syntax`, `binding`, `runtime` and `builtins`, one file
//! per file of the reference, and `stack_guard`, the host-specific guard of the host stack that
//! three of them use. They are public so that the tests and the benchmark runner can
//! drive the engine, and are not a stable interface. The host boundary is what this file
//! re-exports: [`parse`], [`JqExpression`], [`JqOptions`], [`JqError`], [`JqResolver`],
//! [`JqClock`] and the value types.

#[doc(hidden)]
pub mod binding;
#[doc(hidden)]
pub mod builtins;
#[doc(hidden)]
pub mod runtime;
#[doc(hidden)]
pub mod stack_guard;
#[doc(hidden)]
pub mod syntax;
#[doc(hidden)]
pub mod values;

mod host;
mod jq;
mod jq_error;
mod jq_expression;
mod jq_file_resolver;
mod jq_options;
mod jq_resolver;
mod json;

pub use host::clock::{FixedOffsetClock, JqClock, SystemClock};
pub use jq::{evaluate, parse, parse_with};
pub use jq_error::JqError;
pub use jq_expression::JqExpression;
pub use jq_file_resolver::JqFileResolver;
pub use jq_options::JqOptions;
pub use jq_resolver::{JqModuleKind, JqResolver};
pub use values::{JqArray, JqKind, JqObject, JqString, JqValue};
