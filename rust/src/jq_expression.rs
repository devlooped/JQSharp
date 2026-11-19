//! A parsed expression.

use std::collections::HashMap;
use std::sync::Arc;

use crate::binding::BoundProgram;
use crate::host::clock::{ClockAdapter, JqClock};
use crate::jq_error::JqError;
use crate::jq_options::{JqHandler, JqOptions};
use crate::runtime::{EvaluationOptions, Interpreter, Outcome};
use crate::values::{JqObject, JqValue};

/// A jq expression parsed and bound once by [`crate::parse`], to evaluate on any number of inputs.
///
/// Immutable, `Send` and `Sync`: safe to evaluate concurrently from many threads, each evaluation
/// with its own input and variables. Cloning it is cheap (the bound program is shared).
#[derive(Clone)]
pub struct JqExpression {
    program: Arc<BoundProgram>,
    // The object of $ENV when the options gave the variables; None: the process environment.
    environment: Option<JqValue>,
    clock: Arc<dyn JqClock>,
    max_depth: usize,
    debug: Option<JqHandler>,
    stderr: Option<JqHandler>,
}

impl JqExpression {
    pub(crate) fn new(program: BoundProgram, options: &JqOptions) -> JqExpression {
        JqExpression {
            program: Arc::new(program),
            environment: options.environment.as_ref().map(|variables| to_object(variables.iter().map(|(name, value)| (name.clone(), value.clone())))),
            clock: options.clock.clone(),
            max_depth: options.max_depth,
            debug: options.debug.clone(),
            stderr: options.stderr.clone(),
        }
    }

    /// Evaluates the expression on `input` and returns all its results.
    ///
    /// The error is [`JqError::Runtime`] when an error reached the top of the expression
    /// ([`JqError::Halt`] for `halt_error`); no results are returned then.
    pub fn evaluate(&self, input: JqValue) -> Result<Vec<JqValue>, JqError> {
        let mut results = Vec::new();
        self.for_each(input, None, |result| {
            results.push(result);
            true
        })?;
        Ok(results)
    }

    /// As [`JqExpression::evaluate`], with host variables, by name without `$` (`"root"` is
    /// `$root`): each name a jq identifier, `ENV` excluded ([`JqError::Variable`] otherwise). A
    /// variable the expression uses but the host does not give is the runtime error
    /// `$name is not defined`; a binding in the expression shadows a host variable.
    pub fn evaluate_with(&self, input: JqValue, variables: &HashMap<String, JqValue>) -> Result<Vec<JqValue>, JqError> {
        let mut results = Vec::new();
        self.for_each(input, Some(variables), |result| {
            results.push(result);
            true
        })?;
        Ok(results)
    }

    /// Evaluates the expression on `input`, giving each result to `on_result` as soon as it is
    /// produced; the callback returns `false` to stop the evaluation (stopping is not an error).
    /// An error that reaches the top of the expression is returned after the results produced
    /// before it were given to `on_result`.
    pub fn for_each(&self, input: JqValue, variables: Option<&HashMap<String, JqValue>>, mut on_result: impl FnMut(JqValue) -> bool) -> Result<(), JqError> {
        if let Some(variables) = variables {
            validate(variables)?;
        }

        let read_environment = || match &self.environment {
            Some(snapshot) => snapshot.clone(),
            None => process_environment(),
        };
        let clock = ClockAdapter(&*self.clock);
        let options = EvaluationOptions {
            variables,
            environment: Some(&read_environment),
            max_depth: self.max_depth,
            clock: Some(&clock),
            debug: self.debug.as_deref().map(|handler| handler as &dyn Fn(&JqValue)),
            stderr: self.stderr.as_deref().map(|handler| handler as &dyn Fn(&JqValue)),
        };
        match Interpreter::run(&self.program, input, &options, &mut on_result) {
            Outcome::Error(value) => Err(JqError::Runtime { message: JqError::message_of(&value), value }),
            Outcome::Halt { value: Some(value), code } => Err(JqError::Halt { message: JqError::message_of(&value), value, exit_code: code }),
            _ => Ok(()),
        }
    }
}

// Validates the names (spec §12).
fn validate(variables: &HashMap<String, JqValue>) -> Result<(), JqError> {
    for name in variables.keys() {
        if !is_identifier(name) {
            return Err(JqError::Variable { message: format!("'{name}' is not a valid variable name: give a jq identifier without '$'.") });
        }
        if name == "ENV" {
            return Err(JqError::Variable { message: "$ENV cannot be a host variable: use JqOptions::environment.".to_owned() });
        }
    }
    Ok(())
}

fn is_identifier(name: &str) -> bool {
    let mut characters = name.chars();
    characters.next().is_some_and(|first| first.is_ascii_alphabetic() || first == '_') && characters.all(|c| c.is_ascii_alphanumeric() || c == '_')
}

// The process environment, in the order the host gives; text that is not Unicode is replaced (§4.1).
fn process_environment() -> JqValue {
    to_object(std::env::vars_os().map(|(name, value)| (name.to_string_lossy().into_owned(), value.to_string_lossy().into_owned())))
}

fn to_object(variables: impl Iterator<Item = (String, String)>) -> JqValue {
    let mut object = JqObject::empty();
    for (name, value) in variables {
        object.insert(Arc::new(name), JqValue::from_string(value));
    }
    JqValue::Object(object)
}
