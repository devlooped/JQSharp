//! The host interface of an evaluation (§12).

use std::collections::HashMap;

use crate::values::JqValue;

/// The host's clock and time zone (§12): `now`, and the local zone of `localtime`,
/// `strflocaltime`, `%z`, `%Z`.
pub trait Clock {
    /// The current time, in seconds since the Unix epoch.
    fn now(&self) -> f64;

    /// The offset of the local zone from UTC at the instant `seconds`, in seconds east of UTC.
    fn offset(&self, seconds: f64) -> i32;

    /// The abbreviation of the local zone at the instant `seconds`, if it has one.
    fn abbreviation(&self, seconds: f64) -> Option<String>;
}

/// What an evaluation takes from its host besides the program and the input (§12).
#[derive(Clone, Copy)]
pub struct EvaluationOptions<'o> {
    /// The host variables, by name without '$'; names are validated by the host boundary.
    pub variables: Option<&'o HashMap<String, JqValue>>,
    /// The object `$ENV` is (string to string, in the order the host gives), asked for once per
    /// evaluation, when the program first reads it: a host can read its process environment then.
    /// `None`: the empty object.
    pub environment: Option<&'o dyn Fn() -> JqValue>,
    /// The call depth limit of §11.
    pub max_depth: usize,
    /// The clock and zone, for the date builtins.
    pub clock: Option<&'o dyn Clock>,
    /// Receives the values of `debug`; they are dropped when `None`.
    pub debug: Option<&'o dyn Fn(&JqValue)>,
    /// Receives the values of `stderr`; they are dropped when `None`.
    pub stderr: Option<&'o dyn Fn(&JqValue)>,
}

/// The default call depth limit of §11.
pub const DEFAULT_MAX_DEPTH: usize = 1000;

impl Default for EvaluationOptions<'_> {
    fn default() -> Self {
        EvaluationOptions { variables: None, environment: None, max_depth: DEFAULT_MAX_DEPTH, clock: None, debug: None, stderr: None }
    }
}

/// How an evaluation ended (§12).
#[derive(Clone, Debug)]
pub enum Outcome {
    /// Every output was produced.
    Completed,
    /// The host stopped the stream.
    Stopped,
    /// An error reached the top: its value.
    Error(JqValue),
    /// `halt` (no value, code 0) or `halt_error` (the value and the code).
    Halt { value: Option<JqValue>, code: i32 },
}
