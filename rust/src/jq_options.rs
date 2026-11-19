//! What an expression takes from its host besides the input and the variables.

use std::sync::Arc;

use crate::host::clock::{JqClock, SystemClock};
use crate::jq_resolver::JqResolver;
use crate::runtime::evaluation::DEFAULT_MAX_DEPTH;
use crate::values::JqValue;

/// A handler of `debug` or `stderr`: it may be called from any thread that evaluates the expression.
pub type JqHandler = Arc<dyn Fn(&JqValue) + Send + Sync>;

/// What an expression takes from its host besides the input and the variables: modules, `$ENV`,
/// the clock, the depth limit and the handlers of `debug` and `stderr`. Options are read when the
/// expression is parsed (`parse_with`) and apply to every evaluation of it.
#[derive(Clone)]
pub struct JqOptions {
    /// Resolves `import` and `include` directives and `modulemeta`; without one, no module exists.
    pub resolver: Option<Arc<dyn JqResolver>>,
    /// The variables of `$ENV` and `env`, in this order. When `None` (the default), the process
    /// environment, read when an evaluation first uses `$ENV`.
    pub environment: Option<Vec<(String, String)>>,
    /// The source of `now` and of the local time zone (`localtime`, `strflocaltime`, `%z`, `%Z`).
    /// The default is the system clock in UTC: the standard library cannot tell the local zone.
    pub clock: Arc<dyn JqClock>,
    /// The maximum number of calls of jq-defined functions active at once (spec §11); beyond it,
    /// the catchable error `Maximum call depth exceeded`. Deeper recursion never exhausts the host
    /// stack.
    pub max_depth: usize,
    /// Receives the value of each `debug` (its input) and each message of `debug(msgs)`, as is.
    /// Without one, the values are dropped.
    pub debug: Option<JqHandler>,
    /// Receives the input of each `stderr`, as is. Without one, the values are dropped.
    pub stderr: Option<JqHandler>,
}

impl Default for JqOptions {
    fn default() -> JqOptions {
        JqOptions { resolver: None, environment: None, clock: Arc::new(SystemClock), max_depth: DEFAULT_MAX_DEPTH, debug: None, stderr: None }
    }
}
