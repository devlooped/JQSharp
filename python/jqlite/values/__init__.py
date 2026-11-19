"""Values (spec §4, §5): the value, arrays, objects, their operations, text, and error messages."""

from __future__ import annotations

from .jq_array import JqArray
from .jq_object import JqObject
from .jq_value import ARRAY, KIND_OF_TYPE, NULL, NUMBER, OBJECT, STRING, TYPE_NAMES, NoneType

KIND_OF_TYPE.update({NoneType: NULL, float: NUMBER, str: STRING, JqArray: ARRAY, JqObject: OBJECT})
TYPE_NAMES.update({
    NoneType: "null", bool: "boolean", float: "number", str: "string", JqArray: "array", JqObject: "object",
})
