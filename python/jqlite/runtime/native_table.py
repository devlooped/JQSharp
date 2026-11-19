"""The implementations of the native catalogue, by Native.index (NativeTable.cs).

The two native signatures, and the table, live in the runtime package (guide §1.1): the
interpreter calls natives through it, and the builtins package fills it at start-up
(`jqlite.builtins.register_all`), each family registering its entries; every entry of the
catalogue must have one (`check`), a missing one being a defect of the build.

- A value native (§9.1), called once per combination of its arguments' outputs:
  `f(interpreter, input, arguments) -> value | Fail`.
- A closure native, which evaluates its arguments itself, pushes its outputs (with their paths
  in path mode) and returns a signal as Interpreter.eval does:
  `f(interpreter, env, arguments, input, path, emit) -> Signal | None`.
"""

from __future__ import annotations

from collections.abc import Callable
from typing import TYPE_CHECKING, Any

from ..builtins import natives

if TYPE_CHECKING:
    from ..builtins.natives import Native

ValueNative = Callable[..., Any]
ClosureNative = Callable[..., Any]

VALUES: list[ValueNative | None] = [None] * len(natives.ALL)
CLOSURES: list[ClosureNative | None] = [None] * len(natives.ALL)


def _find(name: str, arity: int, closures: bool) -> Native:
    native = natives.find(name, arity)
    if native is None:
        raise RuntimeError(f"{name}/{arity} is not in the native catalogue")
    if native.takes_closures != closures:
        raise RuntimeError(f"{name}/{arity} is registered with the wrong kind")
    return native


def add_value(name: str, arity: int, function: ValueNative) -> None:
    """Registers a value native; the kind is checked against the catalogue."""
    VALUES[_find(name, arity, False).index] = function


def add_closure(name: str, arity: int, function: ClosureNative) -> None:
    """Registers a closure native; the kind is checked against the catalogue."""
    CLOSURES[_find(name, arity, True).index] = function


def is_implemented(native: Native) -> bool:
    return VALUES[native.index] is not None or CLOSURES[native.index] is not None


def check() -> None:
    """Every native of the catalogue has an implementation."""
    for native in natives.ALL:
        if not is_implemented(native):
            raise RuntimeError(f"{native} has no implementation")

# The formats of §9.9 (`@name`, templates, `format/1`): `FORMATS(name, value) -> str | Fail`. The
# interpreter applies them, and the builtins package provides them (builtins/formats.py), with
# the natives.
FORMATS: Callable[[str, Any], Any] = None  # type: ignore[assignment]


def set_formats(function: Callable[[str, Any], Any]) -> None:
    global FORMATS
    FORMATS = function
