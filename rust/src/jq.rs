//! Parses and evaluates jq expressions (the jqlite profile of jq 1.8).

use std::sync::Arc;

use crate::binding::{ModuleSource, Resolver};
use crate::host::resolver_source::ResolverSource;
use crate::jq_error::JqError;
use crate::jq_expression::JqExpression;
use crate::jq_options::JqOptions;
use crate::values::JqValue;

/// Parses and binds `expression` with the default options (no modules) into an expression to
/// evaluate any number of times, concurrently if need be. An empty expression is `.`.
///
/// The error is [`JqError::Parse`]: a syntax or binding error (an unknown function or label, an
/// `import` or `include`), with its line and column.
pub fn parse(expression: &str) -> Result<JqExpression, JqError> {
    parse_with(expression, &JqOptions::default())
}

/// Parses and binds `expression`, loading its modules through the options' resolver, into an
/// expression to evaluate any number of times, concurrently if need be. The options (modules,
/// `$ENV`, clock, depth limit, handlers) apply to every evaluation of it.
pub fn parse_with(expression: &str, options: &JqOptions) -> Result<JqExpression, JqError> {
    let modules = options.resolver.clone().map(|resolver| Arc::new(ResolverSource::new(resolver)) as Arc<dyn ModuleSource>);
    match Resolver::try_compile(expression, true, modules) {
        Ok(program) => Ok(JqExpression::new(program, options)),
        Err(error) => {
            let place = error.module.as_ref().map_or(String::new(), |module| format!("{module}, "));
            Err(JqError::Parse {
                message: format!("{} at {place}line {}, column {}", error.message, error.line, error.column),
                line: error.line,
                column: error.column,
            })
        }
    }
}

/// Parses `expression` with the default options and evaluates it on `input`, returning all its
/// results.
pub fn evaluate(expression: &str, input: JqValue) -> Result<Vec<JqValue>, JqError> {
    parse(expression)?.evaluate(input)
}
