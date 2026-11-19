"""Binds a parsed program (Resolver.cs; spec §7).

Walks the AST once and resolves every variable, closure parameter, function call and label to its
definition, producing the BoundProgram described in bound.py. Unknown names are binding errors with
a position; nothing raises.

The scope is a stack of entries searched from the top, so the innermost definition wins. A call
resolves to the innermost user definition or closure parameter of that name and arity, then to the
prelude, then to the natives (§7.7). Entries record the frame depth at which they live, so the
distance to a definition is the current depth minus the entry's.

The walk does not recurse: it is a loop over a stack of tasks; ``_visit`` resolves what it can at
once and pushes the node's children, the scope changes between them and the final build; bound
children wait on a stack of results. Only patterns are walked recursively, one small frame per
level of pattern nesting, which the parser limits.
"""

from __future__ import annotations

from collections.abc import Sequence
from typing import TYPE_CHECKING, Any

from ..builtins import natives as natives_catalogue
from ..syntax import ast as S
from ..syntax import recursion_limit
from ..syntax.parser import LOCATION_VARIABLE, PARSE_FRAMES, Parser
from ..syntax.syntax_error import SyntaxError
from ..values.jq_object_builder import JqObjectBuilder
from . import bound as B
from . import modules

if TYPE_CHECKING:
    from ..builtins.natives import Native
    from ..values.jq_value import JqValue
    from .modules import IModuleSource, LoadedModule, ModuleLoader
    from .prelude import Prelude


class BindingMessages:
    """The messages of the binding layer; the first three are fixed by spec §10.2."""

    @staticmethod
    def not_defined(name: str, arity: int) -> str:
        return f"{name}/{arity} is not defined"

    @staticmethod
    def label_not_defined(name: str) -> str:
        return f"$*label-{name} is not defined"

    @staticmethod
    def variable_not_defined(name: str) -> str:
        return f"${name} is not defined"

    # The modules group (modules.py); the texts are jq's.
    @staticmethod
    def module_not_found(path: str) -> str:
        return f"module not found: {path}"

    @staticmethod
    def circular_import(identity: str) -> str:
        return f"circular import of {identity}"

    @staticmethod
    def invalid_data(identity: str, reason: str) -> str:
        return f"error loading data file {identity}: {reason}"

    @staticmethod
    def in_module(message: str, identity: str, line: int, column: int) -> str:
        """An error of a module, with where it is: ``... at <identity>, line L, column C``."""
        return f"{message} at {identity}, line {line}, column {column}"


# Entry kinds. Variable, Parameter, Label: depth is the frame holding it, slot its slot there.
# Constant: a data module's value (modules.py), constants[slot]. Function: depth is the number of
# frames where it is defined, slot its index in functions, top_level whether it is a top-level
# definition of the program or module.
E_VARIABLE = 0
E_PARAMETER = 1
E_LABEL = 2
E_FUNCTION = 3
E_CONSTANT = 4

# Steps of the task stack.
_VISIT = 0          # Resolve node, or schedule its children and its build.
_BUILD = 1          # Pop the bound children of node and push the bound node.
_BUILD_CALL = 2     # Pop the arguments of the call node and push the call of functions[a], defined b frames out.
_BUILD_NATIVE = 3   # Pop the arguments of the call node and push the call of natives[a].
_MAKE_BINDER = 4    # Pop the bound pattern keys of the binding node and push its binder.
_ENTER_BINDER = 5   # Push the frame of the binder on top of binders.
_DEFINE = 6         # Define the definition at this point of the scope (a = 1 at the top level).
_END_FUNCTION = 7   # Pop the body of the definition, which is functions[a].
_LEAVE = 8          # Restore the scope to a entries and the depth to b.

_IDENTITY = B.Identity()
_PARAMETER_BINDER = B.Binder([B.VariablePattern(0)], 1)


class Entry:
    __slots__ = ("kind", "name", "arity", "depth", "slot", "top_level")

    def __init__(self, kind: int, name: str, arity: int, depth: int, slot: int, top_level: bool = False) -> None:
        self.kind = kind
        self.name = name
        self.arity = arity
        self.depth = depth
        self.slot = slot
        self.top_level = top_level


class Defining:
    """A function whose body is being bound. ``single``: whether its calls of itself in that body are
    bound as single (spec §7.7): assumed at first, and checked when the body is bound, unless the
    answer is ``known`` already. ``self_calls`` counts the calls bound as single on that assumption."""

    __slots__ = ("index", "value_parameters", "single", "known", "self_calls")

    def __init__(self, index: int, value_parameters: tuple[int, ...], single: bool, known: bool) -> None:
        self.index = index
        self.value_parameters = value_parameters
        self.single = single
        self.known = known
        self.self_calls = 0


class Resolver:
    __slots__ = (
        "prelude", "native_overrides", "scope", "functions", "exports", "host_variables", "constants",
        "tasks", "results", "binders", "depth", "error", "loader", "module", "defining", "replay", "replay_start", "size",
    )

    def __init__(self, prelude: Prelude | None, native_overrides: bool, loader: ModuleLoader | None,
                 functions: list | None = None, host_variables: list[str] | None = None,
                 constants: list | None = None, module: LoadedModule | None = None, size: list[int] | None = None) -> None:
        self.prelude = prelude
        # Whether builtins with a native override bind to it (Native.overrides_prelude).
        self.native_overrides = native_overrides
        self.scope: list[Entry] = []
        # Shared by the main program and its modules (modules.py): one function table, one list of host variables.
        self.functions: list[B.Function | None] = functions if functions is not None else []
        if functions is None and prelude is not None:
            self.functions.extend(prelude.functions)
        self.exports: list[int] = []
        self.host_variables: list[str] = host_variables if host_variables is not None else []
        self.constants: list[JqValue] = constants if constants is not None else []
        self.tasks: list[tuple] = []
        self.results: list[B.Node] = []
        # The binders made and not yet built into their node, with the names of their slots.
        self.binders: list[tuple[B.Binder, list[str]]] = []
        # The functions being defined, innermost last.
        self.defining: list[Defining] = []
        # While the body of a function is bound a second time (_end_function): whether each function
        # nested in it is single, as the first time found, by index from replay_start.
        self.replay: list[bool] | None = None
        self.replay_start = 0
        self.depth = 0
        self.error: SyntaxError | None = None
        # The modules of this binding (shared by the main program and every module), and the module
        # this resolver binds (None for the main program and the prelude).
        self.loader = loader
        self.module = module
        # The nodes and patterns bound, shared with the module resolvers: what the recursion limit of
        # an evaluation is sized from (runtime/stack_guard.py). A body bound twice counts twice.
        self.size: list[int] = size if size is not None else [0]

    def for_module(self, module: LoadedModule) -> Resolver:
        """A resolver for a module: a scope of its own, the functions, host variables and constants of the whole program."""
        return Resolver(self.prelude, self.native_overrides, self.loader, self.functions, self.host_variables,
                        self.constants, module, self.size)

    @staticmethod
    def try_bind(program: S.Program, native_overrides: bool = True, source: IModuleSource | None = None) -> B.BoundProgram | SyntaxError:
        """Binds a program against the prelude and the natives, loading its modules from ``source``
        (§9.11). Without ``native_overrides`` every prelude builtin binds to its definition."""
        from .prelude import Prelude
        with recursion_limit.raised(PARSE_FRAMES):
            prelude = Prelude.instance() if native_overrides else Prelude.without_overrides()
            resolver = Resolver(prelude, native_overrides, modules.ModuleLoader(source))
            body = resolver._bind_program(program)
        if body is None:
            assert resolver.error is not None
            return resolver.error
        return B.BoundProgram(resolver._functions(), resolver.host_variables, body, resolver.loader.table(),  # type: ignore[union-attr]
                              prelude.size + resolver.size[0])

    @staticmethod
    def try_compile(source: str, native_overrides: bool = True, modules_source: IModuleSource | None = None) -> B.BoundProgram | SyntaxError:
        """Parses and binds a program."""
        program = Parser.try_parse(source)
        if type(program) is SyntaxError:
            return program
        return Resolver.try_bind(program, native_overrides, modules_source)  # type: ignore[arg-type]

    @staticmethod
    def try_bind_prelude(module: S.Program, native_overrides: bool) -> tuple[list[B.Function], list[B.Function], int] | SyntaxError:
        """Binds the prelude module against the natives only: all its functions, its top-level
        definitions in order, and its size (nodes and patterns)."""
        resolver = Resolver(None, native_overrides, None)
        with recursion_limit.raised(PARSE_FRAMES):
            body = resolver._bind_program(module)
        if body is None:
            assert resolver.error is not None
            return resolver.error
        functions = resolver._functions()
        return functions, [functions[i] for i in resolver.exports], resolver.size[0]

    def _functions(self) -> list[B.Function]:
        return list(self.functions)  # type: ignore[arg-type]

    def _bind_program(self, program: S.Program) -> B.Node | None:
        # The body of a program (Identity for a module, whose result is its definitions), or None.
        if not modules.bind_imports(self, program.imports):
            return None
        tasks = self.tasks
        # Pushed last first: the definitions in order, then the body.
        if program.body is None:
            self.results.append(_IDENTITY)
        else:
            tasks.append((_VISIT, program.body, None, 0, 0))
        for i in range(len(program.definitions) - 1, -1, -1):
            tasks.append((_DEFINE, None, program.definitions[i], 1, 0))
        run = self._run
        while tasks and self.error is None:
            run(tasks.pop())
        return self.results[0] if self.error is None else None

    def _run(self, task: tuple) -> None:
        step, node, definition, a, b = task
        if step == _VISIT:
            self.size[0] += 1
            self._visit(node)
        elif step == _BUILD:
            self.results.append(self._build(node, a))
        elif step == _BUILD_CALL:
            self.results.append(self._call(a, b, self._pop_all(len(node.arguments))))
        elif step == _BUILD_NATIVE:
            self.results.append(B.CallNative(natives_catalogue.ALL[a], self._pop_all(len(node.arguments))))
        elif step == _MAKE_BINDER:
            self._make_binder(node)
        elif step == _ENTER_BINDER:
            self._enter_binder()
        elif step == _DEFINE:
            self._define(definition, a == 1)
        elif step == _END_FUNCTION:
            self._end_function(definition, a)
        else:
            del self.scope[a:]
            self.depth = b

    # Tasks run last pushed first, so each case pushes its steps in reverse order.
    def _visit(self, node: S.Node) -> None:
        k = node.kind
        K = S.NodeKind
        results = self.results
        tasks = self.tasks
        if k == K.IDENTITY:
            results.append(_IDENTITY)
        elif k == K.LITERAL:
            results.append(B.Literal(node.value))  # type: ignore[attr-defined]
        elif k == K.FORMAT:
            results.append(B.Format(node.name))  # type: ignore[attr-defined]
        elif k == K.VARIABLE:
            self._add(self._bind_variable(node))  # type: ignore[arg-type]
        elif k == K.BREAK:
            self._add(self._bind_break(node))  # type: ignore[arg-type]
        elif k == K.CALL:
            self._visit_call(node)  # type: ignore[arg-type]
        elif k == K.TEMPLATE:
            tasks.append((_BUILD, node, None, 0, 0))
            self._push_all(node.parts)  # type: ignore[attr-defined]
        elif k in (K.PIPE, K.BINARY, K.AND, K.OR, K.ALTERNATIVE):
            self._push(_BUILD, node, node.right, node.left)  # type: ignore[attr-defined]
        elif k == K.COMMA:
            self._visit_comma(node)  # type: ignore[arg-type]
        elif k == K.NEGATE:
            self._push(_BUILD, node, node.operand)  # type: ignore[attr-defined]
        elif k == K.INDEX:
            self._push(_BUILD, node, node.key, node.target)  # type: ignore[attr-defined]
        elif k == K.SLICE:
            self._push(_BUILD, node, node.to, node.frm, node.target)  # type: ignore[attr-defined]
        elif k == K.ITERATE:
            self._push(_BUILD, node, node.target)  # type: ignore[attr-defined]
        elif k == K.TRY:
            self._push(_BUILD, node, node.handler, node.body)  # type: ignore[attr-defined]
        elif k == K.IF:
            self._push(_BUILD, node, node.else_, node.then, node.condition)  # type: ignore[attr-defined]
        elif k == K.ARRAY_EXPR:
            self._push(_BUILD, node, node.body)  # type: ignore[attr-defined]
        elif k == K.OBJECT_EXPR:
            tasks.append((_BUILD, node, None, 0, 0))
            entries = node.entries  # type: ignore[attr-defined]
            for i in range(len(entries) - 1, -1, -1):
                self._push_nodes(entries[i].value, entries[i].key)
        elif k == K.BIND:
            # The source and the pattern keys in the current scope, the body with the variables' frame.
            self._leave()
            self._push(_BUILD, node, node.body)  # type: ignore[attr-defined]
            tasks.append((_ENTER_BINDER, None, None, 0, 0))
            self._push_binder(node, node.patterns)  # type: ignore[attr-defined]
            self._push_nodes(node.source)  # type: ignore[attr-defined]
        elif k == K.REDUCE:
            self._leave()
            self._push(_BUILD, node, node.update)  # type: ignore[attr-defined]
            tasks.append((_ENTER_BINDER, None, None, 0, 0))
            self._push_nodes(node.init)  # type: ignore[attr-defined]
            self._push_binder(node, node.patterns)  # type: ignore[attr-defined]
            self._push_nodes(node.source)  # type: ignore[attr-defined]
        elif k == K.FOREACH:
            self._leave()
            self._push(_BUILD, node, node.extract, node.update)  # type: ignore[attr-defined]
            tasks.append((_ENTER_BINDER, None, None, 0, 0))
            self._push_nodes(node.init)  # type: ignore[attr-defined]
            self._push_binder(node, node.patterns)  # type: ignore[attr-defined]
            self._push_nodes(node.source)  # type: ignore[attr-defined]
        elif k == K.LABEL:
            self._leave()
            self._push(_BUILD, node, node.body)  # type: ignore[attr-defined]
            self.depth += 1
            self.scope.append(Entry(E_LABEL, node.name, 0, self.depth, 0))  # type: ignore[attr-defined]
        elif k == K.FUNC_DEF:
            # The definition is in functions; the node's result is the rest's.
            self._leave()
            self._push_nodes(node.rest)  # type: ignore[attr-defined]
            tasks.append((_DEFINE, None, node.definition, 0, 0))  # type: ignore[attr-defined]
        else:
            raise AssertionError(f"Unknown node {k}")

    def _visit_comma(self, comma: S.Comma) -> None:
        # A left-nested chain of commas becomes one node with the items in order.
        items: list[S.Node] = []
        while True:
            items.append(comma.right)
            left = comma.left
            if left.kind != S.NodeKind.COMMA:
                break
            comma = left  # type: ignore[assignment]
        items.append(comma.left)
        items.reverse()
        self.tasks.append((_BUILD, comma, None, len(items), 0))
        self._push_all(items)

    def _build(self, node: S.Node, count: int) -> B.Node:
        # Pops the bound children of node, last first, and builds its bound node; count is the number of items of a comma.
        k = node.kind
        K = S.NodeKind
        pop = self._pop
        if k == K.TEMPLATE:
            return B.Template(node.format, node.texts, self._pop_all(len(node.parts)))  # type: ignore[attr-defined]
        if k == K.PIPE:
            right = pop()
            return B.Pipe(pop(), right)
        if k == K.COMMA:
            return B.Comma(self._pop_all(count))
        if k == K.BINARY:
            right = pop()
            return B.Binary(node.op, pop(), right)  # type: ignore[attr-defined]
        if k == K.AND:
            right = pop()
            return B.And(pop(), right)
        if k == K.OR:
            right = pop()
            return B.Or(pop(), right)
        if k == K.ALTERNATIVE:
            right = pop()
            return B.Alternative(pop(), right)
        if k == K.NEGATE:
            return B.Negate(pop())
        if k == K.INDEX:
            key = pop()
            return B.Index(pop(), key)
        if k == K.SLICE:
            to = None if node.to is None else pop()  # type: ignore[attr-defined]
            frm = None if node.frm is None else pop()  # type: ignore[attr-defined]
            return B.Slice(pop(), frm, to)
        if k == K.ITERATE:
            return B.Iterate(pop())
        if k == K.TRY:
            handler = None if node.handler is None else pop()  # type: ignore[attr-defined]
            return B.Try(pop(), handler)
        if k == K.IF:
            otherwise = pop()
            then = pop()
            return B.If(pop(), then, otherwise)
        if k == K.ARRAY_EXPR:
            return B.ArrayExpr(None if node.body is None else pop())  # type: ignore[attr-defined]
        if k == K.OBJECT_EXPR:
            source = node.entries  # type: ignore[attr-defined]
            entries: list[B.ObjectEntry] = [None] * len(source)  # type: ignore[list-item]
            for i in range(len(source) - 1, -1, -1):
                value = None if source[i].value is None else pop()
                entries[i] = B.ObjectEntry(pop(), value)
            return B.ObjectExpr(entries)
        if k == K.BIND:
            body = pop()
            return B.Bind(pop(), self._pop_binder(), body)
        if k == K.REDUCE:
            update = pop()
            init = pop()
            return B.Reduce(pop(), self._pop_binder(), init, update)
        if k == K.FOREACH:
            extract = pop()
            update = pop()
            init = pop()
            return B.Foreach(pop(), self._pop_binder(), init, update, extract)
        return B.Label(pop())

    def _define(self, definition: S.Definition, top_level: bool) -> None:
        # Defines a function at the current point of the scope, visible in its own body and after it.
        index = len(self.functions)
        self.functions.append(None)
        if top_level:
            self.exports.append(index)
        params = definition.params
        self.scope.append(Entry(E_FUNCTION, definition.name, len(params), self.depth, index, top_level))
        # Single until its body says otherwise, or what the first binding of this body found.
        replay = self.replay
        replayed = replay is not None and index - self.replay_start < len(replay)
        self.defining.append(Defining(index, _value_parameters(definition),
                                      not replayed or replay[index - self.replay_start], replayed))  # type: ignore[index]
        self._leave()
        self.tasks.append((_END_FUNCTION, None, definition, index, 0))
        self._push_nodes(definition.body)
        if not params:
            return
        self.depth += 1
        for slot in range(len(params)):
            self.scope.append(Entry(E_PARAMETER, params[slot].name, 0, self.depth, slot))
        # Each $name parameter then has a frame of its own, first parameter outermost (_bind_parameters).
        for parameter in params:
            if parameter.is_variable:
                # Its binding (a pattern and a reduce over the argument) is bound by _bind_parameters.
                self.size[0] += 2
                self.depth += 1
                self.scope.append(Entry(E_VARIABLE, parameter.name, 0, self.depth, 0))

    def _end_function(self, definition: S.Definition, index: int) -> None:
        # The body of functions[index] is bound: the function is single when its body is (spec §7.7).
        body = self._pop()
        own = self.defining[-1]
        if own.single and not body.at_most_one and not own.known:
            # It was assumed single and is not. The calls of itself bound as single are wrong, and so
            # is every node built on them: bind the body again, this time knowing. The functions
            # nested in it do not depend on the assumption (their calls of this one are never
            # single, _call), so they keep what this binding found and are not bound a third time.
            own.single = False
            own.known = True
            if own.self_calls > 0:
                self.replay_start = index + 1
                self.replay = [f.single for f in self.functions[self.replay_start:]]  # type: ignore[union-attr]
                del self.functions[self.replay_start:]
                self.tasks.append((_END_FUNCTION, None, definition, index, 0))
                self._push_nodes(definition.body)
                return
        self.defining.pop()
        if self.replay is not None and self.replay_start == index + 1:
            self.replay = None
        self.functions[index] = B.Function(definition.name, len(definition.params), _bind_parameters(definition, body),
                                           body.at_most_one, own.value_parameters)

    def _call(self, function: int, hops: int, arguments: list[B.Node]) -> B.CallFunction:
        # A call of functions[function]: single when the function is and the argument of each of its
        # $ parameters is. A function still being defined is single in its own body as long as its
        # Defining says so; seen from a function nested in it, it is not.
        single = False
        defined = self.functions[function]
        if defined is not None:
            single = defined.single and B.all_slots(defined.value_parameters, arguments)
        elif self.defining:
            own = self.defining[-1]
            if own.single and own.index == function and B.all_slots(own.value_parameters, arguments):
                single = True
                if not own.known:
                    own.self_calls += 1
        return B.CallFunction(function, hops, arguments, single)

    def _bind_variable(self, variable: S.Variable) -> B.Node | None:
        # The parser lets nothing bind $__loc__ (§7.5).
        name = variable.name
        if name == LOCATION_VARIABLE:
            return B.Literal(self._location(variable.position))
        scope = self.scope
        for i in range(len(scope) - 1, -1, -1):
            entry = scope[i]
            if entry.name == name:
                if entry.kind == E_VARIABLE:
                    return B.Variable(self.depth - entry.depth, entry.slot)
                if entry.kind == E_CONSTANT:
                    return B.Literal(self.constants[entry.slot])
        if name == "ENV":
            return B.EnvVariable()
        # A qualified name can only come from a data import (bind_imports); the prelude has no host variables.
        if "::" in name or self.prelude is None:
            return self._fail(BindingMessages.variable_not_defined(name), variable.position)
        host_variables = self.host_variables
        if name in host_variables:
            index = host_variables.index(name)
        else:
            index = len(host_variables)
            host_variables.append(name)
        return B.HostVariable(name, index)

    def _location(self, position: S.Position) -> JqValue:
        # $__loc__: {"file": the module's identity or "<top-level>", "line": the line of its '$'}.
        builder = JqObjectBuilder()
        builder.set("file", self.module.identity if self.module is not None else "<top-level>")
        builder.set("line", float(position.line))
        return builder.to_object()

    def _visit_call(self, call: S.Call) -> None:
        arity = len(call.arguments)
        name = call.name
        scope = self.scope
        tasks = self.tasks
        for i in range(len(scope) - 1, -1, -1):
            entry = scope[i]
            if entry.name != name or entry.arity != arity:
                continue
            if entry.kind == E_PARAMETER:
                self.results.append(B.CallParameter(self.depth - entry.depth, entry.slot))
                return
            if entry.kind == E_FUNCTION:
                # Inside the prelude, its own builtins with a native override bind to the native too.
                native = self._override(name, arity) if self.prelude is None and entry.top_level else None
                if native is not None:
                    tasks.append((_BUILD_NATIVE, call, None, native.index, 0))
                else:
                    tasks.append((_BUILD_CALL, call, None, entry.slot, self.depth - entry.depth))
                self._push_all(call.arguments)
                return

        # The prelude is defined in the outermost, empty environment: every frame of the caller is left.
        function = self.prelude.find(name, arity) if self.prelude is not None else None
        if function is not None:
            native = self._override(name, arity)
            if native is not None:
                tasks.append((_BUILD_NATIVE, call, None, native.index, 0))
            else:
                tasks.append((_BUILD_CALL, call, None, function, self.depth))
        else:
            native = natives_catalogue.find(name, arity)
            if native is not None:
                tasks.append((_BUILD_NATIVE, call, None, native.index, 0))
            else:
                self._fail(BindingMessages.not_defined(name, arity), call.position)
        self._push_all(call.arguments)

    def _override(self, name: str, arity: int) -> Native | None:
        # The native override of a prelude builtin, when overrides are on.
        return natives_catalogue.override(name, arity) if self.native_overrides else None

    def _bind_break(self, node: S.Break) -> B.Node | None:
        scope = self.scope
        for i in range(len(scope) - 1, -1, -1):
            entry = scope[i]
            if entry.kind == E_LABEL and entry.name == node.name:
                return B.Break(self.depth - entry.depth)
        return self._fail(BindingMessages.label_not_defined(node.name), node.position)

    def _push_binder(self, node: S.Node, patterns: Sequence[S.Pattern]) -> None:
        # Schedules the binder of node: its pattern keys, in destructuring order, then _make_binder.
        self.tasks.append((_MAKE_BINDER, node, None, 0, 0))
        keys: list[S.Node] = []
        for pattern in patterns:
            _collect_keys(pattern, keys)
        self._push_all(keys)

    def _make_binder(self, node: S.Node) -> None:
        # Builds the ?// alternatives of a binding from its bound keys (the last results, in the order
        # of _collect_keys). Every distinct variable name of all the alternatives gets a slot. A name
        # repeated in one pattern (§7.5): without ?// its first occurrence in matching order gives the
        # value, so later ones discard theirs; with ?// every occurrence stores and the last one stays.
        patterns = node.patterns  # type: ignore[attr-defined]
        keys = 0
        for pattern in patterns:
            found: list[S.Node] = []
            _collect_keys(pattern, found)
            keys += len(found)
        results = self.results
        nxt = [len(results) - keys]
        names: list[str] = []
        seen: set[str] | None = set() if len(patterns) == 1 else None
        bound_patterns = [self._bind_pattern(pattern, names, seen, nxt) for pattern in patterns]
        if keys:
            del results[len(results) - keys:]
        self.binders.append((B.Binder(bound_patterns, len(names)), names))

    def _bind_pattern(self, pattern: S.Pattern, names: list[str], seen: set[str] | None, nxt: list[int]) -> B.Pattern:
        # Visits elements and entries in the order of _collect_keys, which is matching order (spec §7.5).
        self.size[0] += 1
        if pattern.kind == S.PatternKind.VARIABLE:
            return B.VariablePattern(_slot(pattern.name, names, seen))  # type: ignore[attr-defined]
        if pattern.kind == S.PatternKind.ARRAY:
            source = pattern.elements  # type: ignore[attr-defined]
            elements: list[B.Pattern] = [None] * len(source)  # type: ignore[list-item]
            for i in range(len(source) - 1, -1, -1):
                elements[i] = self._bind_pattern(source[i], names, seen, nxt)
            return B.ArrayPattern(elements)
        entries: list[B.PatternEntry] = []
        for entry in pattern.entries:  # type: ignore[attr-defined]
            key = self.results[nxt[0]]
            nxt[0] += 1
            slot = -1 if entry.variable is None else _slot(entry.variable, names, seen)
            value = None if entry.value is None else self._bind_pattern(entry.value, names, seen, nxt)
            entries.append(B.PatternEntry(key, slot, value))
        return B.ObjectPattern(entries)

    def _enter_binder(self) -> None:
        # Pushes the frame of the last binder made: one variable per name, in slot order.
        names = self.binders[-1][1]
        self.depth += 1
        for slot in range(len(names)):
            self.scope.append(Entry(E_VARIABLE, names[slot], 0, self.depth, slot))

    def _pop_binder(self) -> B.Binder:
        return self.binders.pop()[0]

    def _leave(self) -> None:
        # Schedules the restoring of the scope as it is now, after the tasks pushed later.
        self.tasks.append((_LEAVE, None, None, len(self.scope), self.depth))

    def _push(self, step: int, node: S.Node, last: S.Node | None = None, middle: S.Node | None = None, first: S.Node | None = None) -> None:
        self.tasks.append((step, node, None, 0, 0))
        self._push_nodes(last, middle, first)

    def _push_nodes(self, last: S.Node | None, middle: S.Node | None = None, first: S.Node | None = None) -> None:
        # Schedules the visit of the given nodes, missing ones skipped, the last argument first.
        tasks = self.tasks
        if last is not None:
            tasks.append((_VISIT, last, None, 0, 0))
        if middle is not None:
            tasks.append((_VISIT, middle, None, 0, 0))
        if first is not None:
            tasks.append((_VISIT, first, None, 0, 0))

    def _push_all(self, nodes: Sequence[S.Node]) -> None:
        tasks = self.tasks
        for i in range(len(nodes) - 1, -1, -1):
            tasks.append((_VISIT, nodes[i], None, 0, 0))

    def _add(self, node: B.Node | None) -> None:
        if node is not None:
            self.results.append(node)

    def _pop(self) -> B.Node:
        return self.results.pop()

    def _pop_all(self, count: int) -> list[B.Node]:
        if count == 0:
            return []
        results = self.results
        nodes = results[len(results) - count:]
        del results[len(results) - count:]
        return nodes

    def _fail(self, message: str, position: S.Position) -> Any:
        if self.error is None:
            self.error = SyntaxError(message, position.line, position.column,
                                     self.module.identity if self.module is not None else None)
        return None


def _bind_parameters(definition: S.Definition, body: B.Node) -> B.Node:
    # def f($a; $b): B is def f(a; b): a as $a | b as $b | B, except that each binding names its
    # parameter by position: the k-th $ parameter's frame is k frames inside the parameters' frame.
    params = definition.params
    variables = sum(1 for p in params if p.is_variable)
    for slot in range(len(params) - 1, -1, -1):
        if params[slot].is_variable:
            variables -= 1
            body = B.Bind(B.CallParameter(variables, slot), _PARAMETER_BINDER, body)
    return body


def _value_parameters(definition: S.Definition) -> tuple[int, ...]:
    # The slots of the $ parameters.
    return tuple(slot for slot, parameter in enumerate(definition.params) if parameter.is_variable)


def _collect_keys(pattern: S.Pattern, keys: list[S.Node]) -> None:
    if pattern.kind == S.PatternKind.ARRAY:
        elements = pattern.elements  # type: ignore[attr-defined]
        for i in range(len(elements) - 1, -1, -1):
            _collect_keys(elements[i], keys)
    elif pattern.kind == S.PatternKind.OBJECT:
        for entry in pattern.entries:  # type: ignore[attr-defined]
            keys.append(entry.key)
            if entry.value is not None:
                _collect_keys(entry.value, keys)


def _slot(name: str, names: list[str], seen: set[str] | None) -> int:
    # The slot of a variable in names, or -1 when seen (first occurrence wins) already has the name.
    if seen is not None:
        if name in seen:
            return -1
        seen.add(name)
    if name in names:
        return names.index(name)
    names.append(name)
    return len(names) - 1
