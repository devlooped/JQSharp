//! Syntax: lexer, AST, parser. Never panics for bad input: returns the program or one
//! `SyntaxError`.

pub mod ast;
pub mod lexer;
pub mod parser;
pub mod syntax_error;
pub mod token;

pub use syntax_error::SyntaxError;
