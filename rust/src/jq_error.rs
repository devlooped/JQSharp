//! The errors of the host boundary: the only place where a jqlite error becomes a host error.

use std::fmt;

use crate::values::{JqValue, value_text};

/// An error of a jq expression, or of what the host gave it.
#[derive(Clone, Debug)]
pub enum JqError {
    /// A syntax or binding error, found by `parse` before any evaluation: the 1-based line and
    /// column (counting code points) of the error, in the expression or in the module the message
    /// names.
    Parse { message: String, line: u32, column: u32 },
    /// An error that reached the top of the expression: `value` is what `error(v)` raised, or the
    /// message string of a builtin's error. The message is the value itself when it is a string,
    /// else its JSON text followed by ` (not a string)`.
    Runtime { message: String, value: JqValue },
    /// `halt_error` ended the evaluation: the value it was given and its exit code. (`halt` ends
    /// an evaluation normally.)
    Halt { message: String, value: JqValue, exit_code: i32 },
    /// A host variable name that is not a jq identifier, or is `ENV`.
    Variable { message: String },
    /// A text that is not one JSON value (`JqValue::parse`).
    Json { message: String },
}

impl JqError {
    /// The message of the error.
    pub fn message(&self) -> &str {
        match self {
            JqError::Parse { message, .. }
            | JqError::Runtime { message, .. }
            | JqError::Halt { message, .. }
            | JqError::Variable { message }
            | JqError::Json { message } => message,
        }
    }

    /// The error value of an uncaught runtime error or of `halt_error`.
    pub fn value(&self) -> Option<&JqValue> {
        match self {
            JqError::Runtime { value, .. } | JqError::Halt { value, .. } => Some(value),
            _ => None,
        }
    }

    // The message of an error value that reaches the host (spec §10.1).
    pub(crate) fn message_of(value: &JqValue) -> String {
        match value {
            JqValue::String(text) => text.as_str().to_owned(),
            other => format!("{} (not a string)", value_text::to_json(other)),
        }
    }
}

impl fmt::Display for JqError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.message())
    }
}

impl std::error::Error for JqError {}
