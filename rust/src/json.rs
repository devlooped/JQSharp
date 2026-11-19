//! The conversions of the host boundary: a value from and to JSON text, and from the host's own
//! numbers, strings and collections. Rust has no standard ordered JSON value, so the boundary
//! exposes the engine's value type itself (`JqValue`, `JqArray`, `JqObject`).

use std::str::FromStr;
use std::sync::Arc;

use crate::jq_error::JqError;
use crate::values::{JqArray, JqObject, JqValue, value_text};

impl JqValue {
    /// Reads one JSON value (spec §5.3: RFC 8259, and `nan`, `infinity` for the numbers JSON lacks).
    pub fn parse(text: &str) -> Result<JqValue, JqError> {
        value_text::try_parse(text).map_err(|error| JqError::Json { message: JqError::message_of(&error) })
    }

    /// The canonical JSON text of the value (spec §5.1): compact, NaN as `null`, infinities as
    /// ±1.7976931348623157e+308.
    pub fn to_json(&self) -> String {
        value_text::to_json(self)
    }
}

impl FromStr for JqValue {
    type Err = JqError;

    fn from_str(text: &str) -> Result<JqValue, JqError> {
        JqValue::parse(text)
    }
}

impl From<bool> for JqValue {
    fn from(value: bool) -> JqValue {
        JqValue::from_bool(value)
    }
}

impl From<f64> for JqValue {
    fn from(value: f64) -> JqValue {
        JqValue::Number(value)
    }
}

impl From<i32> for JqValue {
    fn from(value: i32) -> JqValue {
        JqValue::Number(value as f64)
    }
}

impl From<&str> for JqValue {
    fn from(value: &str) -> JqValue {
        JqValue::from_text(value)
    }
}

impl From<String> for JqValue {
    fn from(value: String) -> JqValue {
        JqValue::from_string(value)
    }
}

impl From<Vec<JqValue>> for JqValue {
    fn from(items: Vec<JqValue>) -> JqValue {
        JqValue::Array(JqArray::from_owned(items))
    }
}

impl From<JqArray> for JqValue {
    fn from(array: JqArray) -> JqValue {
        JqValue::Array(array)
    }
}

impl From<JqObject> for JqValue {
    fn from(object: JqObject) -> JqValue {
        JqValue::Object(object)
    }
}

impl<T: Into<JqValue>> From<Option<T>> for JqValue {
    fn from(value: Option<T>) -> JqValue {
        value.map_or(JqValue::Null, Into::into)
    }
}

/// An object from its entries, in order; a key given twice keeps its first position and its last
/// value.
impl<K: Into<String>, V: Into<JqValue>> FromIterator<(K, V)> for JqValue {
    fn from_iter<I: IntoIterator<Item = (K, V)>>(entries: I) -> JqValue {
        let mut object = JqObject::empty();
        for (key, value) in entries {
            object.insert(Arc::new(key.into()), value.into());
        }
        JqValue::Object(object)
    }
}
