"""`modulemeta/0` (§9.11), the native of the modules group (ModuleNatives.cs)."""

from __future__ import annotations

from typing import TYPE_CHECKING, Any

from ..binding.resolver import BindingMessages
from ..runtime.native_table import add_value
from ..values import errors
from ..values.jq_value import Fail

if TYPE_CHECKING:
    from ..values.jq_value import JqValue


def register() -> None:
    add_value("modulemeta", 0, _modulemeta)


def _modulemeta(interpreter: Any, input: JqValue, arguments: Any) -> JqValue | Fail:
    # The description of the module the input names, resolved as from the main program with the
    # source the program was bound with: what the loader recorded when the program loaded it.
    if type(input) is not str:
        return Fail(errors.MODULEMETA_NOT_STRING)
    modules = interpreter.program.modules
    if modules is None:
        return Fail(BindingMessages.module_not_found(input))  # type: ignore[arg-type]
    ok, description = modules.try_describe(input)
    return description if ok else Fail(description)
