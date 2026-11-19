"""The modules group of spec §9.11, which a port may leave out (Modules.cs).

Everything of it is here and in builtins/module_natives.py, behind ``bind_imports``. A port
without the group rejects ``import``, ``include`` and ``module`` in its parser.

Difference from the reference: C# adds ``BindImports`` and ``BindModule`` to the Resolver as a
partial class; Python has no partial classes, so they are the functions ``bind_imports`` and
``bind_module`` here, taking the resolver.
"""

from __future__ import annotations

from collections.abc import Sequence
from typing import TYPE_CHECKING, Protocol

from ..syntax import ast as S
from ..syntax.parser import Parser
from ..syntax.syntax_error import SyntaxError
from ..values import value_text
from ..values.jq_array import JqArray
from ..values.jq_object import JqObject
from ..values.jq_value import Fail

if TYPE_CHECKING:
    from ..values.jq_value import JqValue
    from .resolver import Resolver


class ModuleKind:
    """What an import asks for (§9.11): a code module or a data module."""

    CODE = 0
    DATA = 1


class IModuleSource(Protocol):
    """What the core needs from its host to load modules (§9.11, §12): the host decides where a
    module comes from. Called while binding, and at run time by ``modulemeta``, possibly from
    several threads at once."""

    def try_resolve(self, path: str, kind: int, metadata: JqValue, from_: str | None) -> tuple[bool, str]:
        """Resolves the module ``path`` (as written in the directive) of the given kind, imported
        with ``metadata`` (the directive's metadata object, or None), from the module ``from_`` (its
        identity, or None for the main program): (True, identity) or (False, error message)."""
        ...

    def try_read(self, identity: str) -> tuple[bool, str]:
        """The source text of a module try_resolve found: (True, text) or (False, error message)."""
        ...


class ModuleTable:
    """The modules of a bound program, kept for ``modulemeta``: the host's source and the
    description of every module loaded while binding. Immutable after binding."""

    __slots__ = ("source", "descriptions")

    def __init__(self, source: IModuleSource, descriptions: dict[str, JqValue]) -> None:
        self.source = source
        self.descriptions = descriptions

    def try_describe(self, path: str) -> tuple[bool, JqValue]:
        """``modulemeta`` of a path resolved as from the main program: (True, the loaded module's
        description or, for a module the program does not import, the description of its parse)
        or (False, error message)."""
        ok, identity = self.source.try_resolve(path, ModuleKind.CODE, None, None)
        if not ok:
            return False, identity
        description = self.descriptions.get(identity)
        if description is not None:
            return True, description
        ok, text = self.source.try_read(identity)
        if not ok:
            return False, text
        module = Parser.try_parse_module(text)
        if type(module) is SyntaxError:
            from .resolver import BindingMessages
            return False, BindingMessages.in_module(module.message, identity, module.line, module.column)
        return True, describe(module)  # type: ignore[arg-type]


def describe(module: S.Program) -> JqValue:
    """What ``modulemeta`` outputs for a module (§9.11): its metadata keys, then ``deps`` (per
    directive: its metadata keys, then ``as`` unless it is an include, ``is_data``, ``relpath``),
    then ``defs``, each top-level ``name/arity`` once, by first definition."""
    description: JqObject = module.metadata if module.metadata is not None else JqObject.EMPTY  # type: ignore[assignment]
    deps: list[JqValue] = []
    for imp in module.imports:
        dep: JqObject = imp.metadata if imp.metadata is not None else JqObject.EMPTY  # type: ignore[assignment]
        if imp.alias is not None:
            dep = dep.set("as", imp.alias)
        deps.append(dep.set("is_data", imp.is_data).set("relpath", imp.path))
    defs: list[JqValue] = []
    seen: set[str] = set()
    for definition in module.definitions:
        signature = f"{definition.name}/{len(definition.params)}"
        if signature not in seen:
            seen.add(signature)
            defs.append(signature)
    return description.set("deps", JqArray.from_owned(deps)).set("defs", JqArray.from_owned(defs))


class LoadedModule:
    """A loaded module (ModuleLoader.Module)."""

    __slots__ = ("identity", "kind", "program", "data", "imports", "exports", "loading")

    def __init__(self, identity: str, kind: int, program: S.Program | None = None, data: JqValue = None) -> None:
        self.identity = identity
        self.kind = kind
        # A code module's parse.
        self.program = program
        # A data module's values, as one array.
        self.data = data
        # The identities the module's directives resolved to, in order.
        self.imports: list[str] = []
        # A code module's exports once bound: each top-level name/arity with its function, the last definition winning.
        self.exports: dict[str, tuple[str, int, int]] = {}
        # On the loading stack: importing it again is a cycle.
        self.loading = False


class ModuleLoader:
    """Loads the modules of one binding: resolves and parses each identity once, depth first from
    the main program's directives, with an explicit stack, detecting cycles. Code modules are then
    bound one after the other, each after the modules it imports, each in a scope of its own."""

    __slots__ = ("source", "modules")

    def __init__(self, source: IModuleSource | None) -> None:
        self.source = source
        self.modules: dict[str, LoadedModule] = {}

    def __getitem__(self, identity: str) -> LoadedModule:
        return self.modules[identity]

    def try_load(self, imports: Sequence[S.Import]) -> tuple[list[str], list[LoadedModule]] | SyntaxError:
        """Loads what ``imports`` (the main program's directives) need: the identities they
        resolved to, and the code modules to bind, each after those it imports."""
        order: list[LoadedModule] = []
        identities: list[str] = []
        # One frame per module being loaded: its directives, the next one to resolve, where identities go.
        frames: list[tuple[LoadedModule | None, Sequence[S.Import], list[str]]] = [(None, imports, identities)]
        nxt = [0]
        while frames:
            module, directives, resolved = frames[-1]
            i = nxt.pop()
            if i == len(directives):
                frames.pop()
                if module is not None:
                    module.loading = False
                    order.append(module)
                continue
            nxt.append(i + 1)
            imp = directives[i]
            kind = ModuleKind.DATA if imp.is_data else ModuleKind.CODE
            from_ = module.identity if module is not None else None
            ok, identity = self._try_resolve(imp, kind, from_)
            if not ok:
                return _error(identity, imp.position, from_)
            resolved.append(identity)
            loaded = self.modules.get(identity)
            if loaded is not None:
                if loaded.loading:
                    from .resolver import BindingMessages
                    return _error(BindingMessages.circular_import(identity), imp.position, from_)
                continue
            ok, text = self.source.try_read(identity)  # type: ignore[union-attr]
            if not ok:
                return _error(text, imp.position, from_)
            if kind == ModuleKind.DATA:
                values = value_text.try_parse_sequence(text)
                if type(values) is Fail:
                    from .resolver import BindingMessages
                    return _error(BindingMessages.invalid_data(identity, values.error), imp.position, from_)  # type: ignore[union-attr,arg-type]
                self.modules[identity] = LoadedModule(identity, kind, data=values)
                continue
            program = Parser.try_parse_module(text)
            if type(program) is SyntaxError:
                return program.with_module(identity)  # type: ignore[union-attr]
            code = LoadedModule(identity, kind, program=program)  # type: ignore[arg-type]
            code.loading = True
            self.modules[identity] = code
            frames.append((code, program.imports, code.imports))  # type: ignore[union-attr]
            nxt.append(0)
        return identities, order

    def table(self) -> ModuleTable | None:
        """The descriptions of the code modules loaded, for ``modulemeta``; None without a source."""
        if self.source is None:
            return None
        descriptions: dict[str, JqValue] = {}
        for module in self.modules.values():
            if module.program is not None:
                descriptions[module.identity] = describe(module.program)
        return ModuleTable(self.source, descriptions)

    def _try_resolve(self, imp: S.Import, kind: int, from_: str | None) -> tuple[bool, str]:
        # Without a source no module exists.
        if self.source is not None:
            return self.source.try_resolve(imp.path, kind, imp.metadata, from_)
        from .resolver import BindingMessages
        return False, BindingMessages.module_not_found(imp.path)


def _error(message: str, position: S.Position, module: str | None) -> SyntaxError:
    return SyntaxError(message, position.line, position.column, module)


def bind_imports(resolver: Resolver, imports: Sequence[S.Import]) -> bool:
    """Where modules enter a scope (§9.11), before the top-level definitions: the main program loads
    and binds every module it needs first. Each directive then adds its module's exports as
    definitions at that point: ``alias::f`` for ``import``, ``f`` for ``include``, so that later
    definitions and directives shadow them as usual; a data import adds the constants ``$alias``
    and ``$alias::alias``."""
    if not imports:
        return True
    from .resolver import E_CONSTANT, E_FUNCTION, Entry
    loader = resolver.loader
    assert loader is not None
    if resolver.module is not None:
        identities = resolver.module.imports
    else:
        result = loader.try_load(imports)
        if type(result) is SyntaxError:
            if resolver.error is None:
                resolver.error = result
            return False
        identities, order = result  # type: ignore[misc]
        for code in order:
            if not bind_module(resolver, code):
                return False

    for i, imp in enumerate(imports):
        loaded = loader[identities[i]]
        if imp.is_data:
            resolver.constants.append(loaded.data)
            slot = len(resolver.constants) - 1
            resolver.scope.append(Entry(E_CONSTANT, imp.alias, 0, 0, slot))  # type: ignore[arg-type]
            resolver.scope.append(Entry(E_CONSTANT, f"{imp.alias}::{imp.alias}", 0, 0, slot))
            continue
        prefix = "" if imp.alias is None else imp.alias + "::"
        for name, arity, function in loaded.exports.values():
            resolver.scope.append(Entry(E_FUNCTION, prefix + name, arity, 0, function))
    return True


def bind_module(resolver: Resolver, code: LoadedModule) -> bool:
    """Binds a code module in a scope of its own and records its exports."""
    module_resolver = resolver.for_module(code)
    if module_resolver._bind_program(code.program) is None:  # type: ignore[arg-type]
        if resolver.error is None:
            resolver.error = module_resolver.error
        return False
    for index in module_resolver.exports:
        function = resolver.functions[index]
        assert function is not None
        code.exports[f"{function.name}/{function.arity}"] = (function.name, function.arity, index)
    return True
