"""Module resolution for `import`, `include` and `modulemeta` (§9.11, §12): the public resolver
types (JqResolver.cs, JqFileResolver.cs) and their adapter to the core's module source
(Host/ResolverSource.cs)."""

from __future__ import annotations

import enum
import os
from typing import Any

from . import json_adapter
from .binding.modules import ModuleKind as CoreModuleKind
from .binding.resolver import BindingMessages
from .exceptions import JqError


class ModuleKind(enum.Enum):
    """Whether an `import` asks for a code module (`.jq`) or a data module (JSON values)."""

    CODE = "code"
    DATA = "data"


class Resolver:
    """Finds the modules of `import` and `include` directives and of `modulemeta`: maps a path, as
    written, to a canonical path (the module's identity), then reads it.

    A module is read once per canonical path while an expression is parsed. `modulemeta` calls the
    resolver while expressions are evaluated, possibly from several threads at once.
    """

    def get_canonical_path(self, path: str, kind: ModuleKind, metadata: dict[str, Any] | None, from_path: str | None) -> str | None:
        """The canonical path of the module `path` of the given kind, imported with `metadata` (the
        directive's metadata object, if any) from the module at `from_path` (a canonical path, or
        None for the expression itself); None when there is no such module."""
        raise NotImplementedError

    def read(self, canonical_path: str) -> str:
        """The text of the module at a canonical path that get_canonical_path returned."""
        raise NotImplementedError


class FileResolver(Resolver):
    """A resolver over the file system: a path is relative to the directory of the importing
    module, or to `base_directory` for the expression itself, and gets the extension `.jq` (code)
    or `.json` (data) when it has none. The canonical path is the full file path."""

    def __init__(self, base_directory: str) -> None:
        self.base_directory = os.path.abspath(base_directory)

    def get_canonical_path(self, path: str, kind: ModuleKind, metadata: dict[str, Any] | None, from_path: str | None) -> str | None:
        directory = self.base_directory if from_path is None else (os.path.dirname(from_path) or self.base_directory)
        file = os.path.abspath(os.path.join(directory, path))
        if not os.path.splitext(file)[1]:
            file += ".json" if kind == ModuleKind.DATA else ".jq"
        return file if os.path.isfile(file) else None

    def read(self, canonical_path: str) -> str:
        with open(canonical_path, encoding="utf-8", errors="replace") as f:
            return f.read()


class ResolverSource:
    """A public Resolver as the module source of the core: a resolver's JqError or OSError becomes
    the binding error."""

    __slots__ = ("resolver",)

    def __init__(self, resolver: Resolver) -> None:
        self.resolver = resolver

    def try_resolve(self, path: str, kind: int, metadata: Any, from_: str | None) -> tuple[bool, str]:
        public_kind = ModuleKind.DATA if kind == CoreModuleKind.DATA else ModuleKind.CODE
        try:
            identity = self.resolver.get_canonical_path(path, public_kind, None if metadata is None else json_adapter.to_python(metadata), from_)
        except (JqError, OSError) as e:
            return False, str(e)
        if not identity:
            return False, BindingMessages.module_not_found(path)
        return True, identity

    def try_read(self, identity: str) -> tuple[bool, str]:
        try:
            return True, self.resolver.read(identity)
        except (JqError, OSError) as e:
            return False, str(e)
