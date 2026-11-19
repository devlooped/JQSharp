//! Why evaluation stops besides reaching its end.

use crate::values::JqValue;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SignalKind {
    /// A jqlite error (§6.2); the signal's value is the error value. Caught by `try`, `?` and `?//`.
    Error,
    /// `break` to the label instance of the signal; handled by that label only.
    Break,
    /// `halt` or `halt_error`: stops the whole evaluation; handled by the host only.
    Halt,
    /// The host stopped the stream of outputs (not an error); handled by the host only.
    Stop,
}

/// Why evaluation stops besides reaching its end: evaluation returns `Ok(())` to go on, or
/// `Err(signal)`, which every construct passes up unchanged except the one that handles it (§6.2).
///
/// Rust: a signal moves by value, so it has no identity of its own. An error-handling construct
/// recognizes the signals that came back from its consumer by a serial number, which the
/// interpreter gives a signal the first time one of them sees it (`Interpreter::mark`, the
/// `Consumer` of interpreter.rs); the reference compares object identities. The data is boxed, so
/// that the `Result` of every evaluation step is one machine word.
#[derive(Debug)]
pub struct Signal(Box<Data>);

#[derive(Debug)]
struct Data {
    kind: SignalKind,
    value: JqValue,
    has_value: bool,
    label: u64,
    code: i32,
    serial: u64,
}

/// What an evaluation step returns: `Ok(())` to go on, or the signal that stops it.
pub type Flow = Result<(), Signal>;

impl Signal {
    fn new(kind: SignalKind, value: JqValue, has_value: bool, label: u64, code: i32) -> Signal {
        Signal(Box::new(Data { kind, value, has_value, label, code, serial: 0 }))
    }

    pub fn error(value: JqValue) -> Signal {
        Signal::new(SignalKind::Error, value, true, 0, 0)
    }

    pub fn break_to(label: u64) -> Signal {
        Signal::new(SignalKind::Break, JqValue::Null, false, label, 0)
    }

    pub fn halt(value: Option<JqValue>, code: i32) -> Signal {
        let has_value = value.is_some();
        Signal::new(SignalKind::Halt, value.unwrap_or(JqValue::Null), has_value, 0, code)
    }

    /// The host stopped the stream.
    pub fn stop() -> Signal {
        Signal::new(SignalKind::Stop, JqValue::Null, false, 0, 0)
    }

    pub fn kind(&self) -> SignalKind {
        self.0.kind
    }

    /// Whether this is an error (the one kind `try` handles).
    pub fn is_error(&self) -> bool {
        self.0.kind == SignalKind::Error
    }

    /// Whether this is the break to the label instance `label`.
    pub fn is_break_to(&self, label: u64) -> bool {
        self.0.kind == SignalKind::Break && self.0.label == label
    }

    /// The error value, or the halt value when `has_value`.
    pub fn value(&self) -> &JqValue {
        &self.0.value
    }

    pub fn into_value(self) -> JqValue {
        self.0.value
    }

    /// For `Halt`: whether a value goes with the exit code (`halt_error`).
    pub fn has_value(&self) -> bool {
        self.0.has_value
    }

    /// For `Break`: the label instance.
    pub fn label(&self) -> u64 {
        self.0.label
    }

    /// For `Halt`: the exit code.
    pub fn code(&self) -> i32 {
        self.0.code
    }

    /// The serial number of a signal that a consumer returned; 0 until one marks it.
    pub fn serial(&self) -> u64 {
        self.0.serial
    }

    pub(crate) fn set_serial(&mut self, serial: u64) {
        self.0.serial = serial;
    }
}

/// An error value raised: `value_ops::try_add(a, b)?` in an evaluation step.
impl From<JqValue> for Signal {
    fn from(error: JqValue) -> Signal {
        Signal::error(error)
    }
}
