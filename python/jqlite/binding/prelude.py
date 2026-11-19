"""``spec/prelude.jq``, parsed and bound once against the natives, then shared by every program (Prelude.cs).

Immutable, so safe to use from any thread. A program's function table starts with ``functions``,
so prelude calls keep their indices in every program.

Host-specific (**H**): the source is the package data file ``jqlite/prelude.jq``, a copy of
``spec/prelude.jq`` (a test checks that they are equal), read with ``importlib.resources``.
"""

from __future__ import annotations

import threading
from importlib import resources

from ..syntax.parser import Parser
from ..syntax.syntax_error import SyntaxError
from .bound import Function

_lock = threading.Lock()
_instances: dict[bool, Prelude] = {}


class Prelude:
    __slots__ = ("functions", "definitions", "size", "_visible")

    def __init__(self, functions: list[Function], definitions: list[Function], size: int = 0) -> None:
        # The nodes and patterns bound (BoundProgram.size).
        self.size = size
        # Every function of the prelude, nested ones included, in the order of definition.
        self.functions = tuple(functions)
        # The top-level definitions: the builtins the prelude defines.
        self.definitions = tuple(definitions)
        index_of = {id(f): i for i, f in enumerate(self.functions)}
        self._visible: dict[tuple[str, int], int] = {}
        for definition in self.definitions:
            self._visible[(definition.name, definition.arity)] = index_of[id(definition)]

    @staticmethod
    def instance() -> Prelude:
        """The prelude, with the native overrides of its builtins (Native.overrides_prelude)."""
        prelude = _instances.get(True)
        return prelude if prelude is not None else _get(True)

    @staticmethod
    def without_overrides() -> Prelude:
        """The prelude exactly as written, every builtin bound to its definition: to check that the overrides change nothing."""
        prelude = _instances.get(False)
        return prelude if prelude is not None else _get(False)

    def find(self, name: str, arity: int) -> int | None:
        """The index in ``functions`` of the builtin ``name``/``arity``, or None."""
        return self._visible.get((name, arity))

    @staticmethod
    def load(source: str, native_overrides: bool = True) -> Prelude:
        """Parses and binds a prelude source. The embedded prelude must bind: a failure is a defect of the build, not of a program."""
        from .resolver import Resolver
        module = Parser.try_parse_module(source)
        result = module if type(module) is SyntaxError else Resolver.try_bind_prelude(module, native_overrides)  # type: ignore[arg-type]
        if type(result) is SyntaxError:
            raise RuntimeError(f"The prelude does not bind: {result.message} at {result.line}:{result.column}")
        functions, definitions, size = result  # type: ignore[misc]
        return Prelude(functions, definitions, size)


def read_source() -> str:
    """The one host-specific step of the binding layer: reading the prelude shipped with the package."""
    return resources.files("jqlite").joinpath("prelude.jq").read_text(encoding="utf-8")


def _get(native_overrides: bool) -> Prelude:
    with _lock:
        prelude = _instances.get(native_overrides)
        if prelude is None:
            prelude = Prelude.load(read_source(), native_overrides)
            _instances[native_overrides] = prelude
        return prelude
