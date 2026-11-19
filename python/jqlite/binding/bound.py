"""The bound program: what the interpreter evaluates (Bound.cs).

A tree of its own node types rather than the AST plus a side table: plain immutable data, built
bottom-up; nothing is cyclic.

Names are gone. The environment at run time is a chain of frames, innermost first, and every
reference counts frames outward from where it is written:

- a frame is pushed by ``as`` and by the binder of ``reduce``/``foreach`` (one value slot per
  variable of all its patterns), by ``label`` (the label instance), and by a call of a function
  with parameters (one closure slot per parameter);
- a variable is (hops, slot), a closure parameter (hops, slot), a ``break`` hops;
- a function call names its definition by index in BoundProgram.functions (recursion is an index,
  not a cycle), and hops is the number of frames between the call and the definition.

Host variables are the one late-bound name (spec §7.5): each has an index in
BoundProgram.host_variables. Every node also says whether it yields at most one output
(``at_most_one``: the single expressions of spec §7.7), computed from its children when it is
built: the interpreter evaluates such nodes directly to a value, without callbacks
(Interpreter.eval_one). A call is single when its function is; the resolver knows that of a
function still being defined by assuming it (Resolver._call).
"""

from __future__ import annotations

from collections.abc import Sequence
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from ..builtins.natives import Native
    from ..values.jq_value import JqValue
    from .modules import ModuleTable


class NodeKind:
    IDENTITY = 0
    LITERAL = 1
    TEMPLATE = 2
    FORMAT = 3
    INDEX = 4
    SLICE = 5
    ITERATE = 6
    VARIABLE = 7
    HOST_VARIABLE = 8
    ENV_VARIABLE = 9
    CALL_FUNCTION = 10
    CALL_PARAMETER = 11
    CALL_NATIVE = 12
    PIPE = 13
    COMMA = 14
    BINARY = 15
    AND = 16
    OR = 17
    ALTERNATIVE = 18
    NEGATE = 19
    ARRAY_EXPR = 20
    OBJECT_EXPR = 21
    IF = 22
    TRY = 23
    REDUCE = 24
    FOREACH = 25
    BIND = 26
    LABEL = 27
    BREAK = 28

    COUNT = 29


def _all(nodes: Sequence[Node]) -> bool:
    for node in nodes:
        if not node.at_most_one:
            return False
    return True


def _one(node: Node | None) -> bool:
    return node is None or node.at_most_one


def all_slots(slots: Sequence[int], arguments: Sequence[Node]) -> bool:
    """Whether the arguments in ``slots`` are all single: what makes a call single (spec §7.7)."""
    for slot in slots:
        if not arguments[slot].at_most_one:
            return False
    return True


class Node:
    """A bound node; ``at_most_one``: it yields zero or one output whatever its input and environment."""

    __slots__ = ("kind", "at_most_one")

    def __init__(self, kind: int, at_most_one: bool) -> None:
        self.kind = kind
        self.at_most_one = at_most_one


class Identity(Node):
    __slots__ = ()

    def __init__(self) -> None:
        super().__init__(NodeKind.IDENTITY, True)


class Literal(Node):
    __slots__ = ("value",)

    def __init__(self, value: JqValue) -> None:
        super().__init__(NodeKind.LITERAL, True)
        self.value = value


class Template(Node):
    """An interpolated string; see syntax Template."""

    __slots__ = ("format", "texts", "parts")

    def __init__(self, format: str | None, texts: Sequence[str], parts: Sequence[Node]) -> None:
        super().__init__(NodeKind.TEMPLATE, _all(parts))
        self.format = format
        self.texts = tuple(texts)
        self.parts = tuple(parts)


class Format(Node):
    __slots__ = ("name",)

    def __init__(self, name: str) -> None:
        super().__init__(NodeKind.FORMAT, True)
        self.name = name


class Index(Node):
    __slots__ = ("target", "key")

    def __init__(self, target: Node, key: Node) -> None:
        super().__init__(NodeKind.INDEX, target.at_most_one and key.at_most_one)
        self.target = target
        self.key = key


class Slice(Node):
    __slots__ = ("target", "frm", "to")

    def __init__(self, target: Node, frm: Node | None, to: Node | None) -> None:
        super().__init__(NodeKind.SLICE, target.at_most_one and _one(frm) and _one(to))
        self.target = target
        self.frm = frm
        self.to = to


class Iterate(Node):
    __slots__ = ("target",)

    def __init__(self, target: Node) -> None:
        super().__init__(NodeKind.ITERATE, False)
        self.target = target


class Variable(Node):
    """A lexical variable: slot ``slot`` of the frame ``hops`` frames out."""

    __slots__ = ("hops", "slot")

    def __init__(self, hops: int, slot: int) -> None:
        super().__init__(NodeKind.VARIABLE, True)
        self.hops = hops
        self.slot = slot


class HostVariable(Node):
    """A ``$name`` without lexical binding: entry ``index`` of BoundProgram.host_variables."""

    __slots__ = ("name", "index")

    def __init__(self, name: str, index: int) -> None:
        super().__init__(NodeKind.HOST_VARIABLE, True)
        self.name = name
        self.index = index


class EnvVariable(Node):
    """``$ENV`` where no binding shadows it."""

    __slots__ = ()

    def __init__(self) -> None:
        super().__init__(NodeKind.ENV_VARIABLE, True)


class CallFunction(Node):
    """A call of BoundProgram.functions[function], defined ``hops`` frames out. ``single``: the
    function is single and the argument of each of its ``$`` parameters is (spec §7.7)."""

    __slots__ = ("function", "hops", "arguments", "single")

    def __init__(self, function: int, hops: int, arguments: Sequence[Node], single: bool) -> None:
        super().__init__(NodeKind.CALL_FUNCTION, single)
        self.function = function
        self.hops = hops
        self.arguments = tuple(arguments)
        self.single = single


class CallParameter(Node):
    """A call of a closure parameter: slot ``slot`` of the call frame ``hops`` frames out."""

    __slots__ = ("hops", "slot")

    def __init__(self, hops: int, slot: int) -> None:
        super().__init__(NodeKind.CALL_PARAMETER, False)
        self.hops = hops
        self.slot = slot


class CallNative(Node):
    """A call of a native; the arguments are closures over the caller's environment."""

    __slots__ = ("native", "arguments")

    def __init__(self, native: Native, arguments: Sequence[Node]) -> None:
        arguments = tuple(arguments)
        slots = native.single_when
        super().__init__(NodeKind.CALL_NATIVE, slots is not None and all_slots(slots, arguments))
        self.native = native
        self.arguments = arguments


class Pipe(Node):
    __slots__ = ("left", "right")

    def __init__(self, left: Node, right: Node) -> None:
        super().__init__(NodeKind.PIPE, left.at_most_one and right.at_most_one)
        self.left = left
        self.right = right


class Comma(Node):
    """``a, b, c``: a left-nested chain of commas is one node, so that evaluating it does not recurse per term."""

    __slots__ = ("items",)

    def __init__(self, items: Sequence[Node]) -> None:
        super().__init__(NodeKind.COMMA, False)
        self.items = tuple(items)


class Binary(Node):
    __slots__ = ("op", "left", "right")

    def __init__(self, op: int, left: Node, right: Node) -> None:
        super().__init__(NodeKind.BINARY, left.at_most_one and right.at_most_one)
        self.op = op
        self.left = left
        self.right = right


class And(Node):
    __slots__ = ("left", "right")

    def __init__(self, left: Node, right: Node) -> None:
        super().__init__(NodeKind.AND, left.at_most_one and right.at_most_one)
        self.left = left
        self.right = right


class Or(Node):
    __slots__ = ("left", "right")

    def __init__(self, left: Node, right: Node) -> None:
        super().__init__(NodeKind.OR, left.at_most_one and right.at_most_one)
        self.left = left
        self.right = right


class Alternative(Node):
    __slots__ = ("left", "right")

    def __init__(self, left: Node, right: Node) -> None:
        super().__init__(NodeKind.ALTERNATIVE, left.at_most_one and right.at_most_one)
        self.left = left
        self.right = right


class Negate(Node):
    __slots__ = ("operand",)

    def __init__(self, operand: Node) -> None:
        super().__init__(NodeKind.NEGATE, operand.at_most_one)
        self.operand = operand


class ArrayExpr(Node):
    __slots__ = ("body",)

    def __init__(self, body: Node | None) -> None:
        super().__init__(NodeKind.ARRAY_EXPR, True)
        self.body = body


class ObjectEntry:
    """See syntax ObjectEntry: a None value is the input indexed by each output of the key."""

    __slots__ = ("key", "value")

    def __init__(self, key: Node, value: Node | None) -> None:
        self.key = key
        self.value = value


def _all_entries(entries: Sequence[ObjectEntry]) -> bool:
    for entry in entries:
        if not entry.key.at_most_one or not _one(entry.value):
            return False
    return True


class ObjectExpr(Node):
    __slots__ = ("entries",)

    def __init__(self, entries: Sequence[ObjectEntry]) -> None:
        super().__init__(NodeKind.OBJECT_EXPR, _all_entries(entries))
        self.entries = tuple(entries)


class If(Node):
    __slots__ = ("condition", "then", "else_")

    def __init__(self, condition: Node, then: Node, else_: Node) -> None:
        super().__init__(NodeKind.IF, condition.at_most_one and then.at_most_one and else_.at_most_one)
        self.condition = condition
        self.then = then
        self.else_ = else_


class Try(Node):
    __slots__ = ("body", "handler")

    def __init__(self, body: Node, handler: Node | None) -> None:
        super().__init__(NodeKind.TRY, body.at_most_one and _one(handler))
        self.body = body
        self.handler = handler


class Reduce(Node):
    """``update`` runs with the binder's frame pushed; ``init`` without it."""

    __slots__ = ("source", "binder", "init", "update")

    def __init__(self, source: Node, binder: Binder, init: Node, update: Node) -> None:
        super().__init__(NodeKind.REDUCE, init.at_most_one)
        self.source = source
        self.binder = binder
        self.init = init
        self.update = update


class Foreach(Node):
    """``update`` and ``extract`` run with the binder's frame pushed."""

    __slots__ = ("source", "binder", "init", "update", "extract")

    def __init__(self, source: Node, binder: Binder, init: Node, update: Node, extract: Node) -> None:
        super().__init__(NodeKind.FOREACH, False)
        self.source = source
        self.binder = binder
        self.init = init
        self.update = update
        self.extract = extract


class Bind(Node):
    """``source as patterns | body``: ``body`` runs with the binder's frame pushed."""

    __slots__ = ("source", "binder", "body")

    def __init__(self, source: Node, binder: Binder, body: Node) -> None:
        super().__init__(NodeKind.BIND, source.at_most_one and binder.keys_at_most_one and body.at_most_one)
        self.source = source
        self.binder = binder
        self.body = body


class Label(Node):
    """``body`` runs with a frame holding a new label instance."""

    __slots__ = ("body",)

    def __init__(self, body: Node) -> None:
        super().__init__(NodeKind.LABEL, body.at_most_one)
        self.body = body


class Break(Node):
    """Breaks to the label instance of the frame ``hops`` frames out."""

    __slots__ = ("hops",)

    def __init__(self, hops: int) -> None:
        super().__init__(NodeKind.BREAK, True)
        self.hops = hops


class PatternKind:
    VARIABLE = 0
    ARRAY = 1
    OBJECT = 2


class Pattern:
    __slots__ = ("kind",)

    def __init__(self, kind: int) -> None:
        self.kind = kind


class VariablePattern(Pattern):
    """Stores the value in ``slot``; -1 discards it (spec §7.5)."""

    __slots__ = ("slot",)

    def __init__(self, slot: int) -> None:
        super().__init__(PatternKind.VARIABLE)
        self.slot = slot


class ArrayPattern(Pattern):
    __slots__ = ("elements",)

    def __init__(self, elements: Sequence[Pattern]) -> None:
        super().__init__(PatternKind.ARRAY)
        self.elements = tuple(elements)


class PatternEntry:
    """See syntax PatternEntry; ``slot`` is -1 without a variable. The key runs in the scope
    outside the binding: the pattern's own variables are not visible in it."""

    __slots__ = ("key", "slot", "value")

    def __init__(self, key: Node, slot: int, value: Pattern | None) -> None:
        self.key = key
        self.slot = slot
        self.value = value


class ObjectPattern(Pattern):
    __slots__ = ("entries",)

    def __init__(self, entries: Sequence[PatternEntry]) -> None:
        super().__init__(PatternKind.OBJECT)
        self.entries = tuple(entries)


def _keys_of(pattern: Pattern) -> bool:
    if pattern.kind == PatternKind.ARRAY:
        return _all_keys(pattern.elements)  # type: ignore[attr-defined]
    if pattern.kind == PatternKind.OBJECT:
        for entry in pattern.entries:  # type: ignore[attr-defined]
            if not entry.key.at_most_one or (entry.value is not None and not _keys_of(entry.value)):
                return False
    return True


def _all_keys(patterns: Sequence[Pattern]) -> bool:
    for pattern in patterns:
        if not _keys_of(pattern):
            return False
    return True


class Binder:
    """The patterns of one binding (several are the ``?//`` alternatives) and the size of the frame
    they fill: one slot per distinct variable name of all of them, null until a pattern binds it."""

    __slots__ = ("patterns", "slots", "keys_at_most_one")

    def __init__(self, patterns: Sequence[Pattern], slots: int) -> None:
        self.patterns = tuple(patterns)
        self.slots = slots
        # Every key expression of the patterns yields at most one output, so a value binds at most once per pattern.
        self.keys_at_most_one = _all_keys(self.patterns)


class Function:
    """A jqlite-defined function (user or prelude). Its body runs with its parameters' frame pushed
    when arity > 0. ``single``: the body yields at most one output for each binding of the ``$``
    parameters, whose slots are ``value_parameters`` (spec §7.7)."""

    __slots__ = ("name", "arity", "body", "single", "value_parameters")

    def __init__(self, name: str, arity: int, body: Node, single: bool, value_parameters: Sequence[int]) -> None:
        self.name = name
        self.arity = arity
        self.body = body
        self.single = single
        self.value_parameters = tuple(value_parameters)

    def __repr__(self) -> str:
        return f"{self.name}/{self.arity}"


class BoundProgram:
    """A bound program: immutable and shareable between threads. ``functions`` starts with the
    prelude's functions (shared by every program), followed by the program's own."""

    __slots__ = ("functions", "host_variables", "body", "modules", "size")

    def __init__(self, functions: Sequence[Function], host_variables: Sequence[str], body: Node, modules: ModuleTable | None = None,
                 size: int = 0) -> None:
        self.functions = tuple(functions)
        self.host_variables = tuple(host_variables)
        self.body = body
        # The modules loaded, for `modulemeta`; None when the host gave no module source (§9.11).
        self.modules = modules
        # The nodes and patterns bound, the prelude's included: what the recursion limit of an
        # evaluation is sized from (Python only, runtime/stack_guard.py).
        self.size = size
