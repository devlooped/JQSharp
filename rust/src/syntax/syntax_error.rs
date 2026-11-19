//! The one error of lexing, parsing and binding, and the message texts of the syntax layer.

use crate::values::JqValue;
use crate::values::errors;

use super::token::Token;

/// The one error a lexing, parsing or binding run returns: a message and the 1-based position of
/// the token where it was detected, in the main program or, when `module` is set, in the module of
/// that identity (§9.11).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SyntaxError {
    pub message: String,
    pub line: u32,
    pub column: u32,
    pub module: Option<String>,
}

impl SyntaxError {
    pub fn new(message: impl Into<String>, line: u32, column: u32) -> SyntaxError {
        SyntaxError { message: message.into(), line, column, module: None }
    }
}

/// Every message text of the syntax layer, in one place. The texts of spec §10.2 are fixed; the
/// others are this implementation's.
pub mod syntax_messages {
    use super::*;

    // Fixed by spec §10.2.
    pub const INVALID_ESCAPE: &str = "Invalid escape";
    pub const MAY_NEED_PARENTHESES: &str = "May need parentheses around object key expression";
    pub const METADATA_NOT_CONSTANT: &str = "Module metadata must be constant";
    pub const METADATA_NOT_OBJECT: &str = "Module metadata must be an object";
    pub const IMPORT_PATH_NOT_CONSTANT: &str = "Import path must be constant";

    /// `Cannot use number (1) as object key`.
    pub fn invalid_object_key(key: &JqValue) -> String {
        errors::cannot_use_as_key(key).as_string().to_owned()
    }

    // Fixed by spec §11.
    pub const TOO_DEEPLY_NESTED: &str = "Program too deeply nested";

    // This implementation's.
    pub const UNTERMINATED_STRING: &str = "Unterminated string";
    pub const VARIABLE_NAME_EXPECTED: &str = "Expected a variable name after '$'";
    pub const FORMAT_NAME_EXPECTED: &str = "Expected a format name after '@'";
    pub const MODULE_HAS_BODY: &str = "A module contains only definitions";

    pub fn invalid_character(code_point: char) -> String {
        let c = code_point as u32;
        if c < 0x20 || c == 0x7F { format!("Invalid character U+{c:04X}") } else { format!("Invalid character '{code_point}'") }
    }

    pub fn unexpected(token: &Token) -> String {
        format!("Unexpected {}", token.describe())
    }

    pub fn expected(what: &str, token: &Token) -> String {
        format!("Expected {what} but found {}", token.describe())
    }

    pub fn not_associative(token: &Token) -> String {
        format!("Operator {} cannot be chained: add parentheses", token.describe())
    }

    pub fn question_alt(token: &Token) -> String {
        format!("Unexpected {}: write '? //' for an optional followed by an alternative", token.describe())
    }
}
