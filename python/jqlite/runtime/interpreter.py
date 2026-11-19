"""The push-based interpreter of a bound program (Interpreter.cs; spec §6 to §8).

One instance is one evaluation: it holds every piece of per-evaluation state (host variables,
``$ENV``, the call depth, label numbers, scratch stacks), while the bound program and the prelude
are immutable and shared, so one program is evaluated concurrently from many threads, each with
its own interpreter.

``eval`` evaluates a node with an input and an environment and pushes each output, with its path
in path mode, to ``emit(value, path)``. It returns None when the node is done, or the Signal that
stops it: an error, a break, a halt, or a signal returned by the consumer, which is passed up
unchanged. Nothing raises. Path mode (§8.1) is ``path`` not None.

``eval_one`` is the same evaluation for nodes that yield at most one output (``at_most_one``): it
returns the output instead of pushing it, so arithmetic, indexing, object construction and the
like run without callbacks. Its result is the output value, NOTHING (a Signal object that is not a
signal: no output) or a Signal; the output's path is in ``self.at`` (the reference's ``out``
parameters; guide §2.3). A caller that needs the path reads ``self.at`` right after the call.

Both are loops where a node's last step is another node (a pipe whose left side yields at most
one value, a branch of ``if``, the body of a binding or of a function, a closure, the last item of
a comma), so tail positions and calls in them do not recurse; left-deep chains of operators and
indexes are walked with scratch stacks. The nodes that are not in tail position are dispatched by
kind through tables of handlers (``_MANY``, ``_OTHER``). What still recurses is bounded by
MaxDepth and the program's nesting: see stack_guard.py.
"""

from __future__ import annotations

from collections.abc import Callable
from typing import TYPE_CHECKING, Any

from ..binding import bound as B
from ..syntax.ast import BinaryOp
from ..values import errors, value_ops, value_text
from ..values.jq_array import JqArray
from ..values.jq_object import JqObject
from ..values.jq_object_builder import JqObjectBuilder
from ..values.jq_value import Fail
from . import native_table, stack_guard
from .env import Closure, Env
from .evaluation import EvaluationOptions, Outcome, OutcomeKind
from .path import Path
from .signal import NOTHING, Signal, SignalKind

if TYPE_CHECKING:
    from ..binding.bound import BoundProgram, Node
    from ..values.jq_value import JqValue

Emit = Callable[[Any, Any], Any]

_UNTRACKED = Path.UNTRACKED
_ERROR = SignalKind.ERROR
_BREAK = SignalKind.BREAK

_K = B.NodeKind
_IDENTITY = _K.IDENTITY
_LITERAL = _K.LITERAL
_VARIABLE = _K.VARIABLE
_INDEX = _K.INDEX
_SLICE = _K.SLICE
_PIPE = _K.PIPE
_COMMA = _K.COMMA
_BINARY = _K.BINARY
_AND = _K.AND
_OR = _K.OR
_ALTERNATIVE = _K.ALTERNATIVE
_NEGATE = _K.NEGATE
_IF = _K.IF
_TRY = _K.TRY
_BIND = _K.BIND
_CALL_FUNCTION = _K.CALL_FUNCTION
_CALL_PARAMETER = _K.CALL_PARAMETER

_P_VARIABLE = B.PatternKind.VARIABLE
_P_ARRAY = B.PatternKind.ARRAY

_try_index = value_ops.try_index
_VALUES = native_table.VALUES
_CLOSURES = native_table.CLOSURES


def _equal(a: JqValue, b: JqValue) -> bool:
    return value_ops.are_equal(a, b)


def _not_equal(a: JqValue, b: JqValue) -> bool:
    return not value_ops.are_equal(a, b)


def _less(a: JqValue, b: JqValue) -> bool:
    return value_ops.compare(a, b) < 0


def _less_equal(a: JqValue, b: JqValue) -> bool:
    return value_ops.compare(a, b) <= 0


def _greater(a: JqValue, b: JqValue) -> bool:
    return value_ops.compare(a, b) > 0


def _greater_equal(a: JqValue, b: JqValue) -> bool:
    return value_ops.compare(a, b) >= 0


# The arithmetic operators and comparisons of §4, by BinaryOp.
_APPLY: list[Callable[[Any, Any], Any]] = [None] * 11  # type: ignore[list-item]
_APPLY[BinaryOp.ADD] = value_ops.try_add
_APPLY[BinaryOp.SUBTRACT] = value_ops.try_subtract
_APPLY[BinaryOp.MULTIPLY] = value_ops.try_multiply
_APPLY[BinaryOp.DIVIDE] = value_ops.try_divide
_APPLY[BinaryOp.MODULO] = value_ops.try_modulo
_APPLY[BinaryOp.EQUAL] = _equal
_APPLY[BinaryOp.NOT_EQUAL] = _not_equal
_APPLY[BinaryOp.LESS] = _less
_APPLY[BinaryOp.LESS_EQUAL] = _less_equal
_APPLY[BinaryOp.GREATER] = _greater
_APPLY[BinaryOp.GREATER_EQUAL] = _greater_equal


def try_apply(op: int, left: JqValue, right: JqValue) -> JqValue | Fail:
    """The arithmetic operators and comparisons of §4."""
    return _APPLY[op](left, right)


class Consumer:
    """Tells an operand's own signals from those of its consumer (§6.2).

    It stands between the operand and the consumer and remembers the last signal the consumer
    returned; every construct passes a signal up unchanged and stops at once, so when the operand
    returns that very object, it came from the consumer.
    """

    __slots__ = ("target", "last")

    def __init__(self, target: Emit) -> None:
        self.target = target
        self.last: Signal | None = None

    def emit(self, value: JqValue, path: Path | None) -> Signal | None:
        last = self.target(value, path)
        self.last = last
        return last

    def raised(self, signal: Signal) -> bool:
        return signal is self.last


class _State:
    """The state of a reduce or foreach, with its path."""

    __slots__ = ("value", "at")

    def __init__(self, value: JqValue, at: Path | None) -> None:
        self.value = value
        self.at = at

    def saved(self) -> Callable[[], None]:
        # Takes the state back to what it is now.
        value = self.value
        at = self.at

        def restore() -> None:
            self.value = value
            self.at = at
        return restore


def _is_one_variable(binder: B.Binder) -> bool:
    # A binder of one pattern that is one variable: `as $x`.
    patterns = binder.patterns
    return len(patterns) == 1 and patterns[0].kind == _P_VARIABLE and patterns[0].slot == 0 and binder.slots == 1  # type: ignore[attr-defined]


def _call_env(hops: int, arguments: tuple, env: Env | None) -> Env | None:
    # The environment of a call: the caller's with the frames between the call and the definition
    # removed, plus the closures of the arguments when there are any.
    defined = env
    while hops:
        defined = defined.parent  # type: ignore[union-attr]
        hops -= 1
    if not arguments:
        return defined
    return Env(defined, None, [Closure(argument, env) for argument in arguments])


def _slice_step(target: JqValue, at: Path | None, frm: JqValue, to: JqValue) -> tuple[Any, Path | None]:
    # One slice of a target with its path; in a path the slice is the key {"start": from, "end": to} (§8.1).
    nxt = at
    if at is not None:
        key = JqObject.EMPTY.set("start", frm).set("end", to)
        if at.length < 0:
            return Signal.error(errors.invalid_path_access(key, target)), at
        nxt = at.append(key)
    result = value_ops.try_slice(target, frm, to)
    if type(result) is Fail:
        return Signal.error(result.error), nxt
    return result, nxt


def _convert(format: str | None, part: JqValue) -> str | Fail:
    # An interpolated value as text: tostring, or the template's format.
    if format is None:
        return part if type(part) is str else value_text.to_json(part)  # type: ignore[return-value]
    formatted = native_table.FORMATS(format, part)
    return formatted  # type: ignore[no-any-return]


def _join(node: B.Template, parts: list[str]) -> str:
    texts = node.texts
    pieces = [texts[0]]
    for i in range(len(parts)):
        pieces.append(parts[i])
        pieces.append(texts[i + 1])
    return "".join(pieces)


class Interpreter:
    __slots__ = (
        "program", "functions", "host_values", "read_environment", "environment", "max_depth", "depth",
        "labels", "clock", "debug", "stderr", "at", "spine_nodes", "spine_values",
    )

    def __init__(self, program: BoundProgram, options: EvaluationOptions) -> None:
        self.program = program
        self.functions = program.functions
        # The host variables, by the index of BoundProgram.host_variables; NOTHING when the host has none.
        variables = options.variables
        self.host_values: list = [
            variables[name] if variables is not None and name in variables else NOTHING
            for name in program.host_variables
        ]
        self.read_environment = options.environment
        self.environment: JqValue = NOTHING  # type: ignore[assignment]
        self.max_depth = options.max_depth
        self.depth = 0
        self.labels = 0
        # The host's clock, for the date builtins; the handlers of `debug` and `stderr`.
        self.clock = options.clock
        self.debug = options.debug
        self.stderr = options.stderr
        # The path of the last output of eval_one.
        self.at: Path | None = None
        # Scratch stacks of the chain walks of eval_one; each walk uses the part above its mark.
        self.spine_nodes: list[Node] = []
        self.spine_values: list[JqValue] = []

    @staticmethod
    def run(program: BoundProgram, input: JqValue, options: EvaluationOptions, output: Callable[[JqValue], bool]) -> Outcome:
        """Evaluates ``program`` on ``input``, giving each output to ``output``, which returns false to stop the stream."""
        stop = Signal.STOP

        def emit(value: JqValue, _: Path | None) -> Signal | None:
            return None if output(value) else stop

        with stack_guard.evaluation(program, options.max_depth):
            signal = Interpreter(program, options).eval(program.body, None, input, None, emit)
        if signal is None:
            return Outcome(OutcomeKind.COMPLETED)
        if signal.kind == _ERROR:
            return Outcome(OutcomeKind.ERROR, signal.value, True)
        if signal.kind == SignalKind.HALT:
            return Outcome(OutcomeKind.HALT, signal.value, signal.has_value, signal.code)
        # A break always meets its label: the resolver binds every break to an enclosing one.
        return Outcome(OutcomeKind.STOPPED)

    def new_label(self) -> int:
        """The next label instance, unique within this evaluation."""
        self.labels += 1
        return self.labels

    @staticmethod
    def fail(error: JqValue) -> Signal:
        """An error signal."""
        return Signal.error(error)

    # --- eval -------------------------------------------------------------------------------

    def eval(self, node: Node, env: Env | None, input: JqValue, path: Path | None, emit: Emit) -> Signal | None:
        """Evaluates ``node`` and pushes each output to ``emit``."""
        # Function calls entered by this frame (tail calls): they stay active, counting in the depth
        # of §11, while their outputs are consumed, that is until this frame returns.
        calls = 0
        signal: Signal | None
        while True:
            if node.at_most_one:
                value = self.eval_one(node, env, input, path)
                if type(value) is Signal:
                    signal = None if value is NOTHING else value
                else:
                    signal = emit(value, self.at)
                break
            k = node.kind
            many = _MANY[k]
            if many is not None:
                signal = many(self, node, env, input, path, emit)
                break
            if k == _PIPE:
                left = node.left  # type: ignore[attr-defined]
                if left.at_most_one:
                    value = self.eval_one(left, env, input, path)
                    if type(value) is Signal:
                        signal = None if value is NOTHING else value
                        break
                    input = value
                    path = self.at
                    node = node.right  # type: ignore[attr-defined]
                    continue
                signal = self._eval_pipe(node, env, input, path, emit)  # type: ignore[arg-type]
                break
            if k == _CALL_FUNCTION:
                calls += 1
                self.depth += 1
                if self.depth > self.max_depth:
                    signal = Signal.error(errors.DEPTH_EXCEEDED)
                    break
                env = _call_env(node.hops, node.arguments, env)  # type: ignore[attr-defined]
                node = self.functions[node.function].body  # type: ignore[attr-defined]
                continue
            if k == _CALL_PARAMETER:
                frame = env
                hops = node.hops  # type: ignore[attr-defined]
                while hops:
                    frame = frame.parent  # type: ignore[union-attr]
                    hops -= 1
                closure = frame.closures[node.slot]  # type: ignore[union-attr,attr-defined,index]
                env = closure.env
                node = closure.node
                continue
            if k == _COMMA:
                items = node.items  # type: ignore[attr-defined]
                last = len(items) - 1
                signal = None
                for i in range(last):
                    signal = self.eval(items[i], env, input, path, emit)
                    if signal is not None:
                        break
                if signal is not None:
                    break
                node = items[last]
                continue
            if k == _IF:
                condition = node.condition  # type: ignore[attr-defined]
                if condition.at_most_one:
                    value = self.eval_one(condition, env, input, None)
                    if type(value) is Signal:
                        signal = None if value is NOTHING else value
                        break
                    node = node.then if value is not None and value is not False else node.else_  # type: ignore[attr-defined]
                    continue
                signal = self._eval_if(node, env, input, path, emit)  # type: ignore[arg-type]
                break
            if k == _ALTERNATIVE:
                signal, found = self._eval_alternative(node, env, input, path, emit)  # type: ignore[arg-type]
                if signal is not None or found:
                    break
                node = node.right  # type: ignore[attr-defined]
                continue
            if k == _BIND:
                binder = node.binder  # type: ignore[attr-defined]
                if node.source.at_most_one and binder.keys_at_most_one and len(binder.patterns) == 1:  # type: ignore[attr-defined]
                    bound = self._bind_one(node, env, input)  # type: ignore[arg-type]
                    if type(bound) is Signal:
                        signal = None if bound is NOTHING else bound
                        break
                    env = bound  # type: ignore[assignment]
                    node = node.body  # type: ignore[attr-defined]
                    continue
                signal = self._eval_bind(node, env, input, path, emit)  # type: ignore[arg-type]
                break
            raise AssertionError(f"Unknown node {k}")
        if calls:
            self.depth -= calls
        return signal

    def for_each(self, node: Node, env: Env | None, input: JqValue, path: Path | None, each: Emit) -> Signal | None:
        """Evaluates ``node`` and calls ``each`` with each output, without a callback for a node that yields at most one."""
        if not node.at_most_one:
            return self.eval(node, env, input, path, each)
        value = self.eval_one(node, env, input, path)
        if type(value) is Signal:
            return None if value is NOTHING else value
        return each(value, self.at)  # type: ignore[no-any-return]

    def _eval_pipe(self, node: B.Pipe, env: Env | None, input: JqValue, path: Path | None, emit: Emit) -> Signal | None:
        right = node.right
        evaluate = self.eval

        def each(value: JqValue, at: Path | None) -> Signal | None:
            return evaluate(right, env, value, at, emit)
        return self.eval(node.left, env, input, path, each)

    def _eval_if(self, node: B.If, env: Env | None, input: JqValue, path: Path | None, emit: Emit) -> Signal | None:
        def each(condition: JqValue, _: Path | None) -> Signal | None:
            branch = node.then if condition is not None and condition is not False else node.else_
            return self.eval(branch, env, input, path, emit)
        return self.eval(node.condition, env, input, None, each)

    def _eval_alternative(self, node: B.Alternative, env: Env | None, input: JqValue, path: Path | None, emit: Emit) -> tuple[Signal | None, bool]:
        # a // b: the true outputs of a; found tells whether there was one (then b is not evaluated).
        found = False

        def each(value: JqValue, at: Path | None) -> Signal | None:
            nonlocal found
            if value is None or value is False:
                return None
            found = True
            return emit(value, at)  # type: ignore[no-any-return]
        signal = self.eval(node.left, env, input, path, each)
        return signal, found

    # --- eval_one ---------------------------------------------------------------------------

    def eval_one(self, node: Node, env: Env | None, input: JqValue, path: Path | None) -> Any:
        """Evaluates a node that yields at most one output: the output (its path in ``self.at``),
        NOTHING, or a Signal. Any signal returned is the node's own: there is no consumer."""
        while True:
            k = node.kind
            if k == _IDENTITY:
                self.at = path
                return input
            if k == _LITERAL:
                self.at = path and _UNTRACKED
                return node.value  # type: ignore[attr-defined]
            if k == _VARIABLE:
                frame = env
                hops = node.hops  # type: ignore[attr-defined]
                while hops:
                    frame = frame.parent  # type: ignore[union-attr]
                    hops -= 1
                self.at = path and _UNTRACKED
                return frame.values[node.slot]  # type: ignore[union-attr,attr-defined,index]
            other = _OTHER[k]
            if other is not None:
                return other(self, node, env, input, path)
            if k == _PIPE:
                value = self.eval_one(node.left, env, input, path)  # type: ignore[attr-defined]
                if type(value) is Signal:
                    return value
                input = value
                path = self.at
                node = node.right  # type: ignore[attr-defined]
                continue
            if k == _IF:
                value = self.eval_one(node.condition, env, input, None)  # type: ignore[attr-defined]
                if type(value) is Signal:
                    return value
                node = node.then if value is not None and value is not False else node.else_  # type: ignore[attr-defined]
                continue
            if k == _ALTERNATIVE:
                value = self.eval_one(node.left, env, input, path)  # type: ignore[attr-defined]
                if type(value) is Signal:
                    if value is not NOTHING:
                        return value
                elif value is not None and value is not False:
                    return value
                node = node.right  # type: ignore[attr-defined]
                continue
            if k == _TRY:
                value = self.eval_one(node.body, env, input, path)  # type: ignore[attr-defined]
                if type(value) is not Signal or value.kind != _ERROR:
                    return value
                handler = node.handler  # type: ignore[attr-defined]
                if handler is None:
                    return NOTHING
                node = handler
                input = value.value
                path = path and _UNTRACKED
                continue
            if k == _CALL_FUNCTION:
                return self._eval_call_one(node, env, input, path)  # type: ignore[arg-type]
            if k == _CALL_PARAMETER:
                # The argument of a $ parameter of a single call (Resolver._call): it is single too.
                frame = env
                hops = node.hops  # type: ignore[attr-defined]
                while hops:
                    frame = frame.parent  # type: ignore[union-attr]
                    hops -= 1
                closure = frame.closures[node.slot]  # type: ignore[union-attr,attr-defined,index]
                env = closure.env
                node = closure.node
                continue
            # k == _BIND
            if len(node.binder.patterns) == 1:  # type: ignore[attr-defined]
                bound = self._bind_one(node, env, input)  # type: ignore[arg-type]
                if type(bound) is Signal:
                    return bound
                env = bound
                node = node.body  # type: ignore[attr-defined]
                continue
            return self._bind_many_one(node, env, input, path)  # type: ignore[arg-type]

    def _eval_call_one(self, call: B.CallFunction, env: Env | None, input: JqValue, path: Path | None) -> Any:
        # A single call (spec §7.7): it has ended when its output is here, so, unlike a call eval
        # enters, it does not count in the depth of §11 while that output is consumed.
        self.depth += 1
        if self.depth > self.max_depth:
            self.depth -= 1
            return Signal.error(errors.DEPTH_EXCEEDED)
        body = self.functions[call.function].body
        result = self.eval_one(body, _call_env(call.hops, call.arguments, env), input, path)
        self.depth -= 1
        return result

    def _bind_one(self, node: B.Bind, env: Env | None, input: JqValue) -> Any:
        # Binds the output of a simple binding's source: the new environment, NOTHING when nothing binds, or a signal.
        source = self.eval_one(node.source, env, input, None)
        if type(source) is Signal:
            return source
        binder = node.binder
        slots: list = [None] * binder.slots
        pattern = binder.patterns[0]
        if pattern.kind == _P_VARIABLE:
            if pattern.slot >= 0:  # type: ignore[attr-defined]
                slots[pattern.slot] = source  # type: ignore[attr-defined]
            return Env(env, slots)
        matched = self._match_one(pattern, source, slots, env)
        if matched is not None:
            return matched
        return Env(env, slots)

    def _leaf_host_variable(self, node: B.HostVariable, env: Env | None, input: JqValue, path: Path | None) -> Any:
        value = self.host_values[node.index]
        if value is NOTHING:
            return Signal.error(errors.variable_not_defined(node.name))
        self.at = path and _UNTRACKED
        return value

    def _leaf_env_variable(self, node: Node, env: Env | None, input: JqValue, path: Path | None) -> Any:
        value = self.environment
        if value is NOTHING:
            value = self.read_environment()
            self.environment = value
        self.at = path and _UNTRACKED
        return value

    def _leaf_format(self, node: B.Format, env: Env | None, input: JqValue, path: Path | None) -> Any:
        value = native_table.FORMATS(node.name, input)
        if type(value) is Fail:
            return Signal.error(value.error)
        self.at = path and _UNTRACKED
        return value

    def _leaf_break(self, node: B.Break, env: Env | None, input: JqValue, path: Path | None) -> Any:
        return Signal.break_(Env.up(env, node.hops).label)  # type: ignore[union-attr]

    def _label_one(self, node: B.Label, env: Env | None, input: JqValue, path: Path | None) -> Any:
        self.labels += 1
        label = self.labels
        result = self.eval_one(node.body, Env(env, None, None, label), input, path)
        if type(result) is Signal and result.kind == _BREAK and result.label == label:
            return NOTHING
        return result

    def _reduce_one(self, node: B.Reduce, env: Env | None, input: JqValue, path: Path | None) -> Any:
        # Reduce with one initial value: through the general evaluation, keeping its output.
        out: list = []

        def capture(value: JqValue, at: Path | None) -> None:
            out.append(value)
            out.append(at)
            return None
        signal = self._eval_reduce(node, env, input, path, capture)
        if signal is not None:
            return signal
        if not out:
            return NOTHING
        self.at = out[1]
        return out[0]

    def _bind_many_one(self, node: B.Bind, env: Env | None, input: JqValue, path: Path | None) -> Any:
        # E as P1 ?// P2 | B where E, the keys and B yield at most one output.
        source = self.eval_one(node.source, env, input, None)
        if type(source) is Signal:
            return source
        binder = node.binder
        patterns = binder.patterns
        last = len(patterns) - 1
        for i in range(last + 1):
            slots: list = [None] * binder.slots
            result = self._match_one(patterns[i], source, slots, env)
            if result is None:
                result = self.eval_one(node.body, Env(env, slots), input, path)
            if type(result) is not Signal or result.kind != _ERROR or i == last:
                return result
        return NOTHING

    # --- chains -------------------------------------------------------------------------------

    def _postfix_one(self, node: Node, env: Env | None, input: JqValue, path: Path | None) -> Any:
        # A chain of indexes and slices, T[k1][k2]...: the keys from the outermost in, each on the
        # input (§6.3: the key before its target), then the innermost target, then each index outward.
        if path is None and node.kind == _INDEX:
            target = node.target  # type: ignore[attr-defined]
            key = node.key  # type: ignore[attr-defined]
            if target.kind == _IDENTITY and key.kind == _LITERAL:
                # .name and .[literal] in value mode: no chain to walk.
                result = _try_index(input, key.value)
                if type(result) is Fail:
                    return Signal.error(result.error)
                self.at = None
                return result
        nodes = self.spine_nodes
        values = self.spine_values
        n0 = len(nodes)
        k0 = len(values)
        signal: Any = None
        while node.kind == _INDEX or node.kind == _SLICE:
            nodes.append(node)
            if node.kind == _INDEX:
                key = self.eval_one(node.key, env, input, None)  # type: ignore[attr-defined]
                if type(key) is Signal:
                    signal = key
                    break
                values.append(key)
            else:
                frm = node.frm  # type: ignore[attr-defined]
                to = node.to  # type: ignore[attr-defined]
                frm_value = None if frm is None else self.eval_one(frm, env, input, None)
                if type(frm_value) is Signal:
                    signal = frm_value
                    break
                to_value = None if to is None else self.eval_one(to, env, input, None)
                if type(to_value) is Signal:
                    signal = to_value
                    break
                values.append(frm_value)
                values.append(to_value)
            node = node.target  # type: ignore[attr-defined]

        value: Any = None
        at = None
        if signal is None:
            value = self.eval_one(node, env, input, path)
            if type(value) is Signal:
                signal = value
            else:
                at = self.at
                k = len(values)
                for n in range(len(nodes) - 1, n0 - 1, -1):
                    if nodes[n].kind == _INDEX:
                        k -= 1
                        key = values[k]
                        if at is not None:
                            if at.length < 0:
                                signal = Signal.error(errors.invalid_path_access(key, value))
                                break
                            at = at.append(key)
                        result = _try_index(value, key)
                        if type(result) is Fail:
                            signal = Signal.error(result.error)
                            break
                        value = result
                    else:
                        k -= 2
                        result, at = _slice_step(value, at, values[k], values[k + 1])
                        if type(result) is Signal:
                            signal = result
                            break
                        value = result
        del nodes[n0:]
        del values[k0:]
        if signal is not None:
            return signal
        self.at = at
        return value

    def _binary_one(self, node: B.Binary, env: Env | None, input: JqValue, path: Path | None) -> Any:
        # A left-deep chain of operators, ((a op1 b) op2 c)...: the right operands from the outermost
        # in (§6.3: the right operand first), then the innermost left one, then each operation outward.
        left = node.left
        if left.kind != _BINARY:
            # One operator: literal and identity operands are read without a call.
            right_node = node.right
            if right_node.kind == _LITERAL:
                right = right_node.value  # type: ignore[attr-defined]
            else:
                right = self.eval_one(right_node, env, input, None)
                if type(right) is Signal:
                    return right
            if left.kind == _IDENTITY:
                value = input
            else:
                value = self.eval_one(left, env, input, None)
                if type(value) is Signal:
                    return value
            result = _APPLY[node.op](value, right)
            if type(result) is Fail:
                return Signal.error(result.error)
            self.at = path and _UNTRACKED
            return result
        nodes = self.spine_nodes
        values = self.spine_values
        n0 = len(nodes)
        o0 = len(values)
        signal: Any = None
        current: Node = node
        while current.kind == _BINARY:
            right = self.eval_one(current.right, env, input, None)  # type: ignore[attr-defined]
            if type(right) is Signal:
                signal = right
                break
            nodes.append(current)
            values.append(right)
            current = current.left  # type: ignore[attr-defined]
        if signal is None:
            value = self.eval_one(current, env, input, None)
            if type(value) is Signal:
                signal = value
            else:
                for n in range(len(nodes) - 1, n0 - 1, -1):
                    value = _APPLY[nodes[n].op](value, values[n - n0 + o0])  # type: ignore[attr-defined]
                    if type(value) is Fail:
                        signal = Signal.error(value.error)
                        break
        del nodes[n0:]
        del values[o0:]
        if signal is not None:
            return signal
        self.at = path and _UNTRACKED
        return value

    def _logic_one(self, node: Node, env: Env | None, input: JqValue, path: Path | None) -> Any:
        # A left-deep chain of and/or: the innermost left operand first, then each operator outward,
        # evaluating its right operand only when the left value does not decide.
        nodes = self.spine_nodes
        n0 = len(nodes)
        while node.kind == _AND or node.kind == _OR:
            nodes.append(node)
            node = node.left  # type: ignore[attr-defined]
        value = self.eval_one(node, env, input, None)
        for n in range(len(nodes) - 1, n0 - 1, -1):
            if type(value) is Signal:
                break
            is_and = nodes[n].kind == _AND
            if (value is not None and value is not False) == is_and:
                value = self.eval_one(nodes[n].right, env, input, None)  # type: ignore[attr-defined]
                if type(value) is Signal:
                    break
            value = value is not None and value is not False
        del nodes[n0:]
        if type(value) is Signal:
            return value
        self.at = path and _UNTRACKED
        return value

    def _negate_one(self, node: Node, env: Env | None, input: JqValue, path: Path | None) -> Any:
        # -...-x: the operand, then each minus.
        count = 0
        while node.kind == _NEGATE:
            count += 1
            node = node.operand  # type: ignore[attr-defined]
        value = self.eval_one(node, env, input, None)
        while count > 0 and type(value) is not Signal:
            result = value_ops.try_negate(value)
            if type(result) is Fail:
                return Signal.error(result.error)
            value = result
            count -= 1
        if type(value) is Signal:
            return value
        self.at = path and _UNTRACKED
        return value

    def _array_one(self, node: B.ArrayExpr, env: Env | None, input: JqValue, path: Path | None) -> Any:
        body = node.body
        if body is None:
            self.at = path and _UNTRACKED
            return JqArray.EMPTY
        if body.at_most_one:
            item = self.eval_one(body, env, input, None)
            if type(item) is Signal:
                if item is not NOTHING:
                    return item
                self.at = path and _UNTRACKED
                return JqArray.EMPTY
            self.at = path and _UNTRACKED
            return JqArray([item], 0, 1)
        # [f] for an f with several outputs.
        items: list = []
        append = items.append

        def collect(item: JqValue, _: Path | None) -> None:
            append(item)
            return None
        stop = self.eval(body, env, input, None, collect)
        if stop is not None:
            return stop
        self.at = path and _UNTRACKED
        return JqArray.from_owned(items)

    def _object_one(self, node: B.ObjectExpr, env: Env | None, input: JqValue, path: Path | None) -> Any:
        builder = JqObjectBuilder()
        eval_one = self.eval_one
        for entry in node.entries:
            name = eval_one(entry.key, env, input, None)
            if type(name) is Signal:
                return name
            if type(name) is not str:
                return Signal.error(errors.cannot_use_as_key(name))
            value_node = entry.value
            if value_node is None:
                member = _try_index(input, name)
                if type(member) is Fail:
                    return Signal.error(member.error)
            else:
                member = eval_one(value_node, env, input, None)
                if type(member) is Signal:
                    return member
            builder.set(name, member)
        self.at = path and _UNTRACKED
        return builder.to_object()

    def _template_one(self, node: B.Template, env: Env | None, input: JqValue, path: Path | None) -> Any:
        parts = node.parts
        texts: list[str] = [""] * len(parts)
        for i in range(len(parts) - 1, -1, -1):
            part = self.eval_one(parts[i], env, input, None)
            if type(part) is Signal:
                return part
            text = _convert(node.format, part)
            if type(text) is Fail:
                return Signal.error(text.error)
            texts[i] = text  # type: ignore[assignment]
        self.at = path and _UNTRACKED
        return _join(node, texts)

    def _native_one(self, node: B.CallNative, env: Env | None, input: JqValue, path: Path | None) -> Any:
        # A value native whose arguments yield at most one output each, or a native that takes
        # closures and gives at most one output (Native.single_when), whose output is kept.
        native = node.native
        if native.takes_closures:
            out: list = []

            def capture(value: JqValue, at: Path | None) -> None:
                out.append(value)
                out.append(at)
                return None
            signal = _CLOSURES[native.index](self, env, node.arguments, input, path, capture)  # type: ignore[misc]
            if signal is not None:
                return signal
            if not out:
                return NOTHING
            self.at = out[1]
            return out[0]
        function = _VALUES[native.index]
        arguments = node.arguments
        n = len(arguments)
        if n == 0:
            result = function(self, input, ())  # type: ignore[misc]
        else:
            values: list = [None] * n
            for i in range(n - 1, -1, -1):
                value = self.eval_one(arguments[i], env, input, None)
                if type(value) is Signal:
                    return value
                values[i] = value
            result = function(self, input, values)  # type: ignore[misc]
        if type(result) is Fail:
            return Signal.error(result.error)
        self.at = path and _UNTRACKED
        return result

    # --- constructs that push -----------------------------------------------------------------

    def _eval_iterate(self, node: B.Iterate, env: Env | None, input: JqValue, path: Path | None, emit: Emit) -> Signal | None:
        # T[]: in path mode each element's path is the target's plus its index or key.
        def each(target: JqValue, at: Path | None) -> Signal | None:
            t = type(target)
            if at is not None and at.length < 0:
                return Signal.error(errors.invalid_path_iterate(target))
            if t is JqArray:
                if at is None:
                    for item in target.to_list():  # type: ignore[union-attr]
                        stop = emit(item, None)
                        if stop is not None:
                            return stop  # type: ignore[no-any-return]
                    return None
                length = at.length + 1
                i = 0.0
                for item in target.to_list():  # type: ignore[union-attr]
                    stop = emit(item, Path(at, i, length))
                    if stop is not None:
                        return stop  # type: ignore[no-any-return]
                    i += 1.0
                return None
            if t is not JqObject:
                return Signal.error(errors.cannot_iterate(target))
            n = target.count  # type: ignore[union-attr]
            keys = target.keys[:n]  # type: ignore[union-attr]
            values = target.values[:n]  # type: ignore[union-attr]
            if at is None:
                for item in values:
                    stop = emit(item, None)
                    if stop is not None:
                        return stop  # type: ignore[no-any-return]
                return None
            length = at.length + 1
            for i in range(n):
                stop = emit(values[i], Path(at, keys[i], length))
                if stop is not None:
                    return stop  # type: ignore[no-any-return]
            return None
        return self.for_each(node.target, env, input, path, each)

    def _eval_index(self, node: B.Index, env: Env | None, input: JqValue, path: Path | None, emit: Emit) -> Signal | None:
        # T[e]: for each key (on the input of T, in value mode), for each target (§6.3).
        target_node = node.target

        def each_key(key: JqValue, _: Path | None) -> Signal | None:
            def each_target(target: JqValue, at: Path | None) -> Signal | None:
                if at is not None and at.length < 0:
                    return Signal.error(errors.invalid_path_access(key, target))
                result = _try_index(target, key)
                if type(result) is Fail:
                    return Signal.error(result.error)
                return emit(result, None if at is None else at.append(key))  # type: ignore[no-any-return]
            return self.for_each(target_node, env, input, path, each_target)
        return self.for_each(node.key, env, input, None, each_key)

    def _eval_slice(self, node: B.Slice, env: Env | None, input: JqValue, path: Path | None, emit: Emit) -> Signal | None:
        # T[from:to]: from outermost, then to, then T (§6.3).
        def each_from(frm: JqValue, _: Path | None) -> Signal | None:
            def each_to(to: JqValue, _: Path | None) -> Signal | None:
                def each_target(target: JqValue, at: Path | None) -> Signal | None:
                    result, nxt = _slice_step(target, at, frm, to)
                    if type(result) is Signal:
                        return result
                    return emit(result, nxt)  # type: ignore[no-any-return]
                return self.for_each(node.target, env, input, path, each_target)
            return self._bound(node.to, env, input, each_to)
        return self._bound(node.frm, env, input, each_from)

    def _bound(self, bound: Node | None, env: Env | None, input: JqValue, each: Emit) -> Signal | None:
        # A slice bound: null when absent.
        if bound is None:
            return each(None, None)  # type: ignore[no-any-return]
        return self.for_each(bound, env, input, None, each)

    def _eval_binary(self, node: B.Binary, env: Env | None, input: JqValue, path: Path | None, emit: Emit) -> Signal | None:
        # a op b: for each right output, for each left output (§6.3); operands in value mode. A right
        # operand that yields at most one value is evaluated first, so the common case has one callback.
        at = path and _UNTRACKED
        if not node.right.at_most_one:
            def each_right(right: JqValue, _: Path | None) -> Signal | None:
                return self._eval_left(node, right, env, input, at, emit)
            return self.eval(node.right, env, input, None, each_right)
        right = self.eval_one(node.right, env, input, None)
        if type(right) is Signal:
            return None if right is NOTHING else right
        return self._eval_left(node, right, env, input, at, emit)

    def _eval_left(self, node: B.Binary, right: JqValue, env: Env | None, input: JqValue, at: Path | None, emit: Emit) -> Signal | None:
        apply = _APPLY[node.op]

        def each(left: JqValue, _: Path | None) -> Signal | None:
            result = apply(left, right)
            if type(result) is Fail:
                return Signal.error(result.error)
            return emit(result, at)  # type: ignore[no-any-return]
        return self.eval(node.left, env, input, None, each)

    def _eval_logic(self, node: Node, env: Env | None, input: JqValue, path: Path | None, emit: Emit) -> Signal | None:
        # a and b, a or b (§7.3): for each left output, the right side only when the left does not decide.
        is_and = node.kind == _AND
        right_node = node.right  # type: ignore[attr-defined]
        at = path and _UNTRACKED

        def each_second(second: JqValue, _: Path | None) -> Signal | None:
            return emit(second is not None and second is not False, at)  # type: ignore[no-any-return]

        def each_first(first: JqValue, _: Path | None) -> Signal | None:
            if (first is not None and first is not False) != is_and:
                return emit(not is_and, at)  # type: ignore[no-any-return]
            return self.for_each(right_node, env, input, None, each_second)
        return self.for_each(node.left, env, input, None, each_first)  # type: ignore[attr-defined]

    def _eval_negate(self, node: B.Negate, env: Env | None, input: JqValue, path: Path | None, emit: Emit) -> Signal | None:
        at = path and _UNTRACKED

        def each(value: JqValue, _: Path | None) -> Signal | None:
            result = value_ops.try_negate(value)
            if type(result) is Fail:
                return Signal.error(result.error)
            return emit(result, at)  # type: ignore[no-any-return]
        return self.eval(node.operand, env, input, None, each)

    def _eval_object_node(self, node: B.ObjectExpr, env: Env | None, input: JqValue, path: Path | None, emit: Emit) -> Signal | None:
        return self._eval_object(node, 0, JqObject.EMPTY, env, input, path and _UNTRACKED, emit)

    def _eval_object(self, node: B.ObjectExpr, i: int, obj: JqObject, env: Env | None, input: JqValue, at: Path | None, emit: Emit) -> Signal | None:
        # {k1: v1, ...} from entry i on, with the entries before it in obj: keys and values in value
        # mode, k1 outermost (§6.3). Entries that yield at most one output are taken in a loop.
        entries = node.entries
        n = len(entries)
        while i < n:
            entry = entries[i]
            if not entry.key.at_most_one or (entry.value is not None and not entry.value.at_most_one):
                break
            name = self.eval_one(entry.key, env, input, None)
            if type(name) is Signal:
                return None if name is NOTHING else name
            if type(name) is not str:
                return Signal.error(errors.cannot_use_as_key(name))
            if entry.value is None:
                member = _try_index(input, name)
                if type(member) is Fail:
                    return Signal.error(member.error)
            else:
                member = self.eval_one(entry.value, env, input, None)
                if type(member) is Signal:
                    return None if member is NOTHING else member
            obj = obj.set(name, member)
            i += 1
        if i == n:
            return emit(obj, at)  # type: ignore[no-any-return]
        nxt = entries[i]
        following = i + 1

        def each_key(key: JqValue, _: Path | None) -> Signal | None:
            if type(key) is not str:
                return Signal.error(errors.cannot_use_as_key(key))
            if nxt.value is None:
                indexed = _try_index(input, key)
                if type(indexed) is Fail:
                    return Signal.error(indexed.error)
                return self._eval_object(node, following, obj.set(key, indexed), env, input, at, emit)

            def each_value(value: JqValue, _: Path | None) -> Signal | None:
                return self._eval_object(node, following, obj.set(key, value), env, input, at, emit)  # type: ignore[arg-type]
            return self.for_each(nxt.value, env, input, None, each_value)
        return self.for_each(nxt.key, env, input, None, each_key)

    def _eval_template_node(self, node: B.Template, env: Env | None, input: JqValue, path: Path | None, emit: Emit) -> Signal | None:
        return self._eval_template(node, len(node.parts) - 1, [""] * len(node.parts), env, input, path and _UNTRACKED, emit)

    def _eval_template(self, node: B.Template, i: int, texts: list[str], env: Env | None, input: JqValue, at: Path | None, emit: Emit) -> Signal | None:
        # "a\(x)b\(y)": the parts from part i down to the first, the last part outermost (§6.3).
        if i < 0:
            return emit(_join(node, texts), at)  # type: ignore[no-any-return]

        def each(part: JqValue, _: Path | None) -> Signal | None:
            text = _convert(node.format, part)
            if type(text) is Fail:
                return Signal.error(text.error)
            texts[i] = text  # type: ignore[assignment]
            return self._eval_template(node, i - 1, texts, env, input, at, emit)
        return self.for_each(node.parts[i], env, input, None, each)

    def _eval_try(self, node: B.Try, env: Env | None, input: JqValue, path: Path | None, emit: Emit) -> Signal | None:
        # try B catch H (§7.13): only B's own errors are caught, never one its consumer returned (§6.2).
        consumer = Consumer(emit)
        signal = self.eval(node.body, env, input, path, consumer.emit)
        if signal is None or signal.kind != _ERROR or signal is consumer.last:
            return signal
        if node.handler is None:
            return None
        return self.eval(node.handler, env, signal.value, path and _UNTRACKED, emit)

    def _eval_label(self, node: B.Label, env: Env | None, input: JqValue, path: Path | None, emit: Emit) -> Signal | None:
        self.labels += 1
        label = self.labels
        signal = self.eval(node.body, Env(env, None, None, label), input, path, emit)
        if signal is not None and signal.kind == _BREAK and signal.label == label:
            return None
        return signal

    def _eval_bind(self, node: B.Bind, env: Env | None, input: JqValue, path: Path | None, emit: Emit) -> Signal | None:
        # E as P1 ?// P2 | B, in general: for each output of E (value mode), each binding, B in path mode.
        binder = node.binder
        consumer = Consumer(emit) if len(binder.patterns) > 1 else None
        body_emit = emit if consumer is None else consumer.emit
        body_node = node.body

        def body(frame: Env) -> Signal | None:
            return self.eval(body_node, frame, input, path, body_emit)

        def each(source: JqValue, _: Path | None) -> Signal | None:
            return self._bind_each(binder, source, env, body, consumer, None)
        return self.for_each(node.source, env, input, None, each)

    def _bind_each(self, binder: B.Binder, value: JqValue, env: Env | None, body: Callable[[Env], Signal | None],
                   consumer: Consumer | None, save: Callable[[], Callable[[], None]] | None) -> Signal | None:
        """Binds ``value`` by the binder's patterns and runs ``body`` in the environment with the new
        frame, once per binding. With ``?//`` alternatives (§7.5), when matching fails or the body
        raises its own error (not one of ``consumer``), the next pattern is tried with the same value,
        all variables null again, after restoring what ``save`` saved before the first (the state of
        reduce and foreach); errors with the last pattern propagate."""
        patterns = binder.patterns
        last = len(patterns) - 1
        if last == 0 and binder.keys_at_most_one:
            slots: list = [None] * binder.slots
            pattern = patterns[0]
            if pattern.kind == _P_VARIABLE:
                if pattern.slot >= 0:  # type: ignore[attr-defined]
                    slots[pattern.slot] = value  # type: ignore[attr-defined]
                return body(Env(env, slots))
            matched = self._match_one(pattern, value, slots, env)
            if matched is None:
                return body(Env(env, slots))
            return None if matched is NOTHING else matched  # type: ignore[no-any-return]
        restore = save() if save is not None else None
        i = 0
        while True:
            if i > 0 and restore is not None:
                restore()
            slots = [None] * binder.slots
            signal: Signal | None
            if binder.keys_at_most_one:
                matched = self._match_one(patterns[i], value, slots, env)
                if matched is None:
                    signal = body(Env(env, slots))
                else:
                    signal = None if matched is NOTHING else matched
            else:
                frame_slots = slots

                def complete() -> Signal | None:
                    return body(Env(env, list(frame_slots)))
                signal = self._match(patterns[i], value, slots, env, complete)
            if signal is None or signal.kind != _ERROR or i == last or (consumer is not None and signal is consumer.last):
                return signal
            i += 1

    def _match_one(self, pattern: B.Pattern, value: JqValue, slots: list, env: Env | None) -> Any:
        # Destructures value by a pattern whose keys yield at most one output: None when matched,
        # NOTHING when a key yields none, or a signal. Array elements from the last, object entries
        # from the first (§7.5); the keys are evaluated in the binding's outer environment, on the
        # value being matched.
        k = pattern.kind
        if k == _P_VARIABLE:
            if pattern.slot >= 0:  # type: ignore[attr-defined]
                slots[pattern.slot] = value  # type: ignore[attr-defined]
            return None
        if k == _P_ARRAY:
            elements = pattern.elements  # type: ignore[attr-defined]
            for i in range(len(elements) - 1, -1, -1):
                element = _try_index(value, float(i))
                if type(element) is Fail:
                    return Signal.error(element.error)
                signal = self._match_one(elements[i], element, slots, env)
                if signal is not None:
                    return signal
            return None
        for entry in pattern.entries:  # type: ignore[attr-defined]
            key = self.eval_one(entry.key, env, value, None)
            if type(key) is Signal:
                return key
            member = _try_index(value, key)
            if type(member) is Fail:
                return Signal.error(member.error)
            if entry.slot >= 0:
                slots[entry.slot] = member
            if entry.value is not None:
                signal = self._match_one(entry.value, member, slots, env)
                if signal is not None:
                    return signal
        return None

    def _match(self, pattern: B.Pattern, value: JqValue, slots: list, env: Env | None, nxt: Callable[[], Signal | None]) -> Signal | None:
        # Destructures value when a key may yield several outputs: nxt runs once per complete binding.
        k = pattern.kind
        if k == _P_VARIABLE:
            if pattern.slot >= 0:  # type: ignore[attr-defined]
                slots[pattern.slot] = value  # type: ignore[attr-defined]
            return nxt()
        if k == _P_ARRAY:
            return self._match_elements(pattern, len(pattern.elements) - 1, value, slots, env, nxt)  # type: ignore[arg-type,attr-defined]
        return self._match_entries(pattern, 0, value, slots, env, nxt)  # type: ignore[arg-type]

    def _match_elements(self, array: B.ArrayPattern, i: int, value: JqValue, slots: list, env: Env | None, nxt: Callable[[], Signal | None]) -> Signal | None:
        if i < 0:
            return nxt()
        element = _try_index(value, float(i))
        if type(element) is Fail:
            return Signal.error(element.error)
        return self._match(array.elements[i], element, slots, env, lambda: self._match_elements(array, i - 1, value, slots, env, nxt))

    def _match_entries(self, obj: B.ObjectPattern, i: int, value: JqValue, slots: list, env: Env | None, nxt: Callable[[], Signal | None]) -> Signal | None:
        if i == len(obj.entries):
            return nxt()
        entry = obj.entries[i]

        def each(key: JqValue, _: Path | None) -> Signal | None:
            member = _try_index(value, key)
            if type(member) is Fail:
                return Signal.error(member.error)
            if entry.slot >= 0:
                slots[entry.slot] = member

            def rest() -> Signal | None:
                return self._match_entries(obj, i + 1, value, slots, env, nxt)
            return rest() if entry.value is None else self._match(entry.value, member, slots, env, rest)
        return self.for_each(entry.key, env, value, None, each)

    def _eval_reduce(self, node: B.Reduce, env: Env | None, input: JqValue, path: Path | None, emit: Emit) -> Signal | None:
        # reduce SRC as P (INIT; UPDATE) (§7.8): one reduction per initial value; the state is the last
        # output of UPDATE, or null. In path mode the state carries its path. With ?// alternatives, a
        # retried alternative starts from the state before the source value.
        binder = node.binder
        update = node.update
        source_node = node.source
        empty_at = path and _UNTRACKED
        # `reduce SRC as $x (...)`: the frame is the value itself, without matching.
        simple = _is_one_variable(binder)

        def each_initial(initial: JqValue, initial_at: Path | None) -> Signal | None:
            state = _State(initial, initial_at)

            if update.at_most_one:
                def body(frame: Env) -> Signal | None:
                    result = self.eval_one(update, frame, state.value, state.at)
                    if type(result) is Signal:
                        if result is not NOTHING:
                            return result
                        state.value = None
                        state.at = empty_at
                        return None
                    state.value = result
                    state.at = self.at
                    return None
            else:
                def body(frame: Env) -> Signal | None:
                    last: list = []

                    def capture(value: JqValue, at: Path | None) -> None:
                        if last:
                            last[0] = value
                            last[1] = at
                        else:
                            last.append(value)
                            last.append(at)
                        return None
                    stop = self.eval(update, frame, state.value, state.at, capture)
                    if stop is None:
                        if last:
                            state.value = last[0]
                            state.at = last[1]
                        else:
                            state.value = None
                            state.at = empty_at
                    return stop

            if simple:
                def each_source(source: JqValue, _: Path | None) -> Signal | None:
                    return body(Env(env, [source]))
            else:
                restart = state.saved if len(binder.patterns) > 1 else None

                def each_source(source: JqValue, _: Path | None) -> Signal | None:
                    return self._bind_each(binder, source, env, body, None, restart)
            signal = self.eval(source_node, env, input, None, each_source)
            if signal is not None:
                return signal
            return emit(state.value, state.at)  # type: ignore[no-any-return]
        return self.for_each(node.init, env, input, path, each_initial)

    def _eval_foreach(self, node: B.Foreach, env: Env | None, input: JqValue, path: Path | None, emit: Emit) -> Signal | None:
        # foreach SRC as P (INIT; UPDATE; EXTRACT) (§7.9).
        binder = node.binder
        consumer = Consumer(emit) if len(binder.patterns) > 1 else None
        output = emit if consumer is None else consumer.emit
        update = node.update
        extract = node.extract
        source_node = node.source
        empty_at = path and _UNTRACKED

        def each_initial(initial: JqValue, initial_at: Path | None) -> Signal | None:
            state = _State(initial, initial_at)

            def body(frame: Env) -> Signal | None:
                updated = False

                def each_update(nxt: JqValue, nxt_at: Path | None) -> Signal | None:
                    nonlocal updated
                    updated = True
                    state.value = nxt
                    state.at = nxt_at
                    return self.eval(extract, frame, nxt, nxt_at, output)
                signal = self.eval(update, frame, state.value, state.at, each_update)
                if signal is None and not updated:
                    state.value = None
                    state.at = empty_at
                return signal

            restart = state.saved if len(binder.patterns) > 1 else None

            def each_source(source: JqValue, _: Path | None) -> Signal | None:
                return self._bind_each(binder, source, env, body, consumer, restart)
            return self.eval(source_node, env, input, None, each_source)
        return self.for_each(node.init, env, input, path, each_initial)

    def _eval_native(self, node: B.CallNative, env: Env | None, input: JqValue, path: Path | None, emit: Emit) -> Signal | None:
        # A native: a closure native decides everything; a value native is called once per combination
        # of its arguments' outputs, the last argument outermost (§6.3, §9.1).
        native = node.native
        if native.takes_closures:
            return _CLOSURES[native.index](self, env, node.arguments, input, path, emit)  # type: ignore[misc,no-any-return]
        arguments: list = [None] * native.arity
        return self._combine(_VALUES[native.index], node.arguments, len(arguments) - 1, arguments, env, input,  # type: ignore[arg-type]
                             path and _UNTRACKED, emit)

    def _combine(self, function: Callable[..., Any], nodes: tuple, i: int, arguments: list, env: Env | None, input: JqValue,
                 at: Path | None, emit: Emit) -> Signal | None:
        if i < 0:
            result = function(self, input, arguments)
            if type(result) is Fail:
                return Signal.error(result.error)
            return emit(result, at)  # type: ignore[no-any-return]

        def each(argument: JqValue, _: Path | None) -> Signal | None:
            arguments[i] = argument
            return self._combine(function, nodes, i - 1, arguments, env, input, at, emit)
        return self.for_each(nodes[i], env, input, None, each)


# The constructs of eval that are never in tail position, by node kind; None for the kinds eval
# handles itself (tail positions) and for the kinds that always yield at most one output.
_MANY: list[Callable[..., Any] | None] = [None] * B.NodeKind.COUNT
_MANY[_K.ITERATE] = Interpreter._eval_iterate
_MANY[_K.INDEX] = Interpreter._eval_index
_MANY[_K.SLICE] = Interpreter._eval_slice
_MANY[_K.BINARY] = Interpreter._eval_binary
_MANY[_K.AND] = Interpreter._eval_logic
_MANY[_K.OR] = Interpreter._eval_logic
_MANY[_K.NEGATE] = Interpreter._eval_negate
_MANY[_K.OBJECT_EXPR] = Interpreter._eval_object_node
_MANY[_K.TEMPLATE] = Interpreter._eval_template_node
_MANY[_K.TRY] = Interpreter._eval_try
_MANY[_K.REDUCE] = Interpreter._eval_reduce
_MANY[_K.FOREACH] = Interpreter._eval_foreach
_MANY[_K.LABEL] = Interpreter._eval_label
_MANY[_K.CALL_NATIVE] = Interpreter._eval_native

# The nodes of eval_one that are not in tail position, by node kind (EvalOther); None for the
# kinds eval_one handles itself.
_OTHER: list[Callable[..., Any] | None] = [None] * B.NodeKind.COUNT
_OTHER[_K.INDEX] = Interpreter._postfix_one
_OTHER[_K.SLICE] = Interpreter._postfix_one
_OTHER[_K.BINARY] = Interpreter._binary_one
_OTHER[_K.AND] = Interpreter._logic_one
_OTHER[_K.OR] = Interpreter._logic_one
_OTHER[_K.NEGATE] = Interpreter._negate_one
_OTHER[_K.ARRAY_EXPR] = Interpreter._array_one
_OTHER[_K.OBJECT_EXPR] = Interpreter._object_one
_OTHER[_K.TEMPLATE] = Interpreter._template_one
_OTHER[_K.CALL_NATIVE] = Interpreter._native_one
_OTHER[_K.LABEL] = Interpreter._label_one
_OTHER[_K.REDUCE] = Interpreter._reduce_one
_OTHER[_K.HOST_VARIABLE] = Interpreter._leaf_host_variable
_OTHER[_K.ENV_VARIABLE] = Interpreter._leaf_env_variable
_OTHER[_K.FORMAT] = Interpreter._leaf_format
_OTHER[_K.BREAK] = Interpreter._leaf_break
