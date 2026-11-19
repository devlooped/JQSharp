"""The abstract syntax tree (Ast.cs): plain data, no behaviour, no back-pointers.

Every node and pattern carries the position of the token that introduces it (its keyword,
operator, bracket or name). Names stay as written (without '$', '.' or '@'; a qualified name keeps
its "::"): the resolver binds them. Forms the spec defines by rewriting (§3.3) are already
rewritten by the parser, so the node set below is all the resolver sees.

The parser completes its partial nodes (a pipe, a definition, a binding... whose rest is not
parsed yet) by setting their last field; after parsing, nodes are never changed.
"""

from __future__ import annotations

from typing import TYPE_CHECKING, NamedTuple

if TYPE_CHECKING:
    from ..values.jq_value import JqValue


class Position(NamedTuple):
    """A 1-based source position; columns count code points."""

    line: int
    column: int


class NodeKind:
    IDENTITY = 0
    LITERAL = 1
    TEMPLATE = 2
    FORMAT = 3
    INDEX = 4
    SLICE = 5
    ITERATE = 6
    VARIABLE = 7
    CALL = 8
    PIPE = 9
    COMMA = 10
    BINARY = 11
    AND = 12
    OR = 13
    ALTERNATIVE = 14
    NEGATE = 15
    ARRAY_EXPR = 16
    OBJECT_EXPR = 17
    IF = 18
    TRY = 19
    REDUCE = 20
    FOREACH = 21
    BIND = 22
    FUNC_DEF = 23
    LABEL = 24
    BREAK = 25


class Node:
    __slots__ = ("kind", "position")

    def __init__(self, kind: int, position: Position) -> None:
        self.kind = kind
        self.position = position


class Identity(Node):
    """``.``"""

    __slots__ = ()

    def __init__(self, position: Position) -> None:
        super().__init__(NodeKind.IDENTITY, position)


class Literal(Node):
    """A constant: a number, a string without interpolation, ``null``, ``true``, ``false``."""

    __slots__ = ("value",)

    def __init__(self, value: JqValue, position: Position) -> None:
        super().__init__(NodeKind.LITERAL, position)
        self.value = value


class Template(Node):
    """A string with interpolations: ``texts[0] parts[0] texts[1] ... parts[n-1] texts[n]``.

    Each part's outputs are converted with ``format`` (None: ``tostring``)."""

    __slots__ = ("format", "texts", "parts")

    def __init__(self, format: str | None, texts: list[str], parts: list[Node], position: Position) -> None:
        super().__init__(NodeKind.TEMPLATE, position)
        self.format = format
        self.texts = texts
        self.parts = parts


class Format(Node):
    """``@name`` alone: the format applied to the input."""

    __slots__ = ("name",)

    def __init__(self, name: str, position: Position) -> None:
        super().__init__(NodeKind.FORMAT, position)
        self.name = name


class Index(Node):
    """``target[key]``; also ``.name`` and ``."s"``. ``key`` runs on the input of ``target``."""

    __slots__ = ("target", "key")

    def __init__(self, target: Node, key: Node, position: Position) -> None:
        super().__init__(NodeKind.INDEX, position)
        self.target = target
        self.key = key


class Slice(Node):
    """``target[frm:to]``; a missing bound is None."""

    __slots__ = ("target", "frm", "to")

    def __init__(self, target: Node, frm: Node | None, to: Node | None, position: Position) -> None:
        super().__init__(NodeKind.SLICE, position)
        self.target = target
        self.frm = frm
        self.to = to


class Iterate(Node):
    """``target[]``"""

    __slots__ = ("target",)

    def __init__(self, target: Node, position: Position) -> None:
        super().__init__(NodeKind.ITERATE, position)
        self.target = target


class Variable(Node):
    """``$name`` or ``$alias::name``."""

    __slots__ = ("name",)

    def __init__(self, name: str, position: Position) -> None:
        super().__init__(NodeKind.VARIABLE, position)
        self.name = name


class Call(Node):
    """``name`` or ``name(a; b)``: a function, closure parameter or builtin of arity len(arguments)."""

    __slots__ = ("name", "arguments")

    def __init__(self, name: str, arguments: list[Node], position: Position) -> None:
        super().__init__(NodeKind.CALL, position)
        self.name = name
        self.arguments = arguments


class Pipe(Node):
    """``left | right``"""

    __slots__ = ("left", "right")

    def __init__(self, left: Node, right: Node, position: Position) -> None:
        super().__init__(NodeKind.PIPE, position)
        self.left = left
        self.right = right


class Comma(Node):
    """``left, right``"""

    __slots__ = ("left", "right")

    def __init__(self, left: Node, right: Node, position: Position) -> None:
        super().__init__(NodeKind.COMMA, position)
        self.left = left
        self.right = right


class BinaryOp:
    ADD = 0
    SUBTRACT = 1
    MULTIPLY = 2
    DIVIDE = 3
    MODULO = 4
    EQUAL = 5
    NOT_EQUAL = 6
    LESS = 7
    LESS_EQUAL = 8
    GREATER = 9
    GREATER_EQUAL = 10


class Binary(Node):
    """An arithmetic operator or a comparison: one output per pair of outputs of the operands."""

    __slots__ = ("op", "left", "right")

    def __init__(self, op: int, left: Node, right: Node, position: Position) -> None:
        super().__init__(NodeKind.BINARY, position)
        self.op = op
        self.left = left
        self.right = right


class And(Node):
    __slots__ = ("left", "right")

    def __init__(self, left: Node, right: Node, position: Position) -> None:
        super().__init__(NodeKind.AND, position)
        self.left = left
        self.right = right


class Or(Node):
    __slots__ = ("left", "right")

    def __init__(self, left: Node, right: Node, position: Position) -> None:
        super().__init__(NodeKind.OR, position)
        self.left = left
        self.right = right


class Alternative(Node):
    """``left // right``"""

    __slots__ = ("left", "right")

    def __init__(self, left: Node, right: Node, position: Position) -> None:
        super().__init__(NodeKind.ALTERNATIVE, position)
        self.left = left
        self.right = right


class Negate(Node):
    """Unary ``-operand``."""

    __slots__ = ("operand",)

    def __init__(self, operand: Node, position: Position) -> None:
        super().__init__(NodeKind.NEGATE, position)
        self.operand = operand


class ArrayExpr(Node):
    """``[body]``; ``[]`` has no body."""

    __slots__ = ("body",)

    def __init__(self, body: Node | None, position: Position) -> None:
        super().__init__(NodeKind.ARRAY_EXPR, position)
        self.body = body


class ObjectEntry:
    """One entry of an object construction (see Ast.cs). A None ``value`` is the shorthand
    ``{"s"}``, ``{name}``, ``{@f "s"}``: the value is the input indexed by each key."""

    __slots__ = ("key", "value", "position")

    def __init__(self, key: Node, value: Node | None, position: Position) -> None:
        self.key = key
        self.value = value
        self.position = position


class ObjectExpr(Node):
    """``{...}``"""

    __slots__ = ("entries",)

    def __init__(self, entries: list[ObjectEntry], position: Position) -> None:
        super().__init__(NodeKind.OBJECT_EXPR, position)
        self.entries = entries


class If(Node):
    """``if condition then then_ else else_ end``; ``elif`` nests, a missing else is Identity."""

    __slots__ = ("condition", "then", "else_")

    def __init__(self, condition: Node, then: Node, else_: Node, position: Position) -> None:
        super().__init__(NodeKind.IF, position)
        self.condition = condition
        self.then = then
        self.else_ = else_


class Try(Node):
    """``try body catch handler``; also ``body?``. A None handler discards the error."""

    __slots__ = ("body", "handler")

    def __init__(self, body: Node, handler: Node | None, position: Position) -> None:
        super().__init__(NodeKind.TRY, position)
        self.body = body
        self.handler = handler


class Reduce(Node):
    """``reduce source as patterns (init; update)``"""

    __slots__ = ("source", "patterns", "init", "update")

    def __init__(self, source: Node, patterns: list[Pattern], init: Node, update: Node, position: Position) -> None:
        super().__init__(NodeKind.REDUCE, position)
        self.source = source
        self.patterns = patterns
        self.init = init
        self.update = update


class Foreach(Node):
    """``foreach source as patterns (init; update; extract)``; a missing extract is Identity."""

    __slots__ = ("source", "patterns", "init", "update", "extract")

    def __init__(self, source: Node, patterns: list[Pattern], init: Node, update: Node, extract: Node, position: Position) -> None:
        super().__init__(NodeKind.FOREACH, position)
        self.source = source
        self.patterns = patterns
        self.init = init
        self.update = update
        self.extract = extract


class Bind(Node):
    """``source as patterns | body``. Several patterns are the ``?//`` alternatives."""

    __slots__ = ("source", "patterns", "body")

    def __init__(self, source: Node, patterns: list[Pattern], body: Node, position: Position) -> None:
        super().__init__(NodeKind.BIND, position)
        self.source = source
        self.patterns = patterns
        self.body = body


class FuncDef(Node):
    """``def ...; rest``: the definition is visible in its own body and in ``rest``."""

    __slots__ = ("definition", "rest")

    def __init__(self, definition: Definition, rest: Node, position: Position) -> None:
        super().__init__(NodeKind.FUNC_DEF, position)
        self.definition = definition
        self.rest = rest


class Label(Node):
    """``label $name | body``"""

    __slots__ = ("name", "body")

    def __init__(self, name: str, body: Node, position: Position) -> None:
        super().__init__(NodeKind.LABEL, position)
        self.name = name
        self.body = body


class Break(Node):
    """``break $name``"""

    __slots__ = ("name",)

    def __init__(self, name: str, position: Position) -> None:
        super().__init__(NodeKind.BREAK, position)
        self.name = name


class Param:
    __slots__ = ("name", "is_variable", "position")

    def __init__(self, name: str, is_variable: bool, position: Position) -> None:
        self.name = name
        self.is_variable = is_variable
        self.position = position


class Definition:
    """``def name(params): body;``. Every parameter is a filter parameter; a ``$name`` parameter
    (``is_variable``) also binds ``$name`` to each output of its own argument, first parameter outermost (§3.3)."""

    __slots__ = ("name", "params", "body", "position")

    def __init__(self, name: str, params: list[Param], body: Node, position: Position) -> None:
        self.name = name
        self.params = params
        self.body = body
        self.position = position


class PatternKind:
    VARIABLE = 0
    ARRAY = 1
    OBJECT = 2


class Pattern:
    __slots__ = ("kind", "position")

    def __init__(self, kind: int, position: Position) -> None:
        self.kind = kind
        self.position = position


class VariablePattern(Pattern):
    """``$name``"""

    __slots__ = ("name",)

    def __init__(self, name: str, position: Position) -> None:
        super().__init__(PatternKind.VARIABLE, position)
        self.name = name


class ArrayPattern(Pattern):
    """``[P0, P1, ...]``: element i is matched against ``.[i]``."""

    __slots__ = ("elements",)

    def __init__(self, elements: list[Pattern], position: Position) -> None:
        super().__init__(PatternKind.ARRAY, position)
        self.elements = elements


class PatternEntry:
    """One entry of an object pattern: for each output of ``key``, the value ``.[key]`` is bound to
    ``variable`` (when not None) and matched against ``value`` (when not None)."""

    __slots__ = ("key", "variable", "value", "position")

    def __init__(self, key: Node, variable: str | None, value: Pattern | None, position: Position) -> None:
        self.key = key
        self.variable = variable
        self.value = value
        self.position = position


class ObjectPattern(Pattern):
    """``{...}``"""

    __slots__ = ("entries",)

    def __init__(self, entries: list[PatternEntry], position: Position) -> None:
        super().__init__(PatternKind.OBJECT, position)
        self.entries = entries


class Import:
    """``import "path" as alias metadata;``, ``import "path" as $alias ...;`` (data) or ``include "path" ...;`` (no alias)."""

    __slots__ = ("path", "alias", "is_data", "metadata", "position")

    def __init__(self, path: str, alias: str | None, is_data: bool, metadata: JqValue, position: Position) -> None:
        self.path = path
        self.alias = alias
        self.is_data = is_data
        # None when the directive has no metadata (a constant object otherwise).
        self.metadata = metadata
        self.position = position


class Program:
    """A parsed program or module file. ``definitions`` are the top-level definitions in order (what
    a module exports); ``body`` is the main expression, Identity for a program of definitions
    only, and None for a module file. ``metadata`` is None without a ``module`` directive."""

    __slots__ = ("metadata", "imports", "definitions", "body")

    def __init__(self, metadata: JqValue, imports: list[Import], definitions: list[Definition], body: Node | None) -> None:
        self.metadata = metadata
        self.imports = imports
        self.definitions = definitions
        self.body = body
