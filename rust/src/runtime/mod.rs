//! Runtime: the push-based interpreter (`eval`/`eval_one`, `Emit`, `Signal`), environments,
//! paths and the host interface of an evaluation. The stack guard it uses is `crate::stack_guard`.

pub mod env;
pub mod evaluation;
pub mod interpreter;
pub mod path;
pub mod signal;

pub use evaluation::{Clock, EvaluationOptions, Outcome};
pub use interpreter::Interpreter;
pub use path::Path;
pub use signal::{Flow, Signal, SignalKind};
