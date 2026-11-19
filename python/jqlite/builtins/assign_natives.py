"""``_modify/2`` and ``_assign/2`` (§8.3) implemented natively, overriding their prelude definitions (AssignNatives.cs).

Same results, errors, order of evaluation and path behavior, but linear in the number of paths,
where the prelude's one ``setpath`` per path copies the container each time. The value being
updated is a WorkingTree.
"""

from __future__ import annotations

import math
from typing import TYPE_CHECKING, Any

from ..runtime.native_table import add_closure
from ..runtime.path import Path
from ..runtime.signal import Signal
from ..values import errors, value_ops
from ..values.jq_array import JqArray
from ..values.jq_array_builder import JqArrayBuilder
from ..values.jq_object import JqObject
from ..values.jq_object_builder import JqObjectBuilder
from ..values.jq_string import plain
from ..values.jq_value import Fail
from . import path_natives

if TYPE_CHECKING:
    from ..values.jq_value import JqValue

# setpath's index limit (§11).
MAX_INDEX = 536870911.0

_UNTRACKED = Path.UNTRACKED


def register() -> None:
    add_closure("_modify", 2, _modify)
    add_closure("_assign", 2, _assign)


def _modify(interpreter: Any, env: Any, arguments: Any, input: JqValue, path: Path | None, emit: Any) -> Signal | None:
    # def _modify(paths; update): for each path of paths (on the input, lazily), the first output of
    # update applied to the current value there replaces it; no output: the path is deleted at the
    # end. update runs in value mode; the result has no path.
    tree = WorkingTree(input)
    deletions: list[JqValue] = []
    first = Signal.break_(interpreter.new_label())
    update = arguments[1]

    def each(value: JqValue, at: Path | None) -> Signal | None:
        if at is None or at.length < 0:
            return Signal.error(errors.invalid_path_result(value))
        keys = at.to_value()
        current = tree.try_get(keys)
        if type(current) is Fail:
            return Signal.error(current.error)
        out: list = []

        def take(output: JqValue, _: Path | None) -> Signal:
            out.append(output)
            return first
        stop = interpreter.eval(update, env, current, None, take)
        if stop is not None and stop is not first:
            return stop  # type: ignore[no-any-return]
        if not out:
            deletions.append(keys)
            return None
        failed = tree.try_set(keys, plain(out[0]))
        return None if failed is None else Signal.error(failed.error)
    signal = interpreter.eval(arguments[0], env, input, Path.ROOT, each)
    if signal is not None:
        return signal  # type: ignore[no-any-return]
    result = path_natives.try_delete_paths(tree.freeze(), JqArray.from_owned(deletions))
    if type(result) is Fail:
        return Signal.error(result.error)
    return emit(result, path and _UNTRACKED)  # type: ignore[no-any-return]


def _assign(interpreter: Any, env: Any, arguments: Any, input: JqValue, path: Path | None, emit: Any) -> Signal | None:
    # def _assign(paths; $value): for each output of value (on the input), every path of paths set
    # to it. With no path the result is the input itself, with its path (the reduce's initial state).
    def each_value(value: JqValue, _: Path | None) -> Signal | None:
        value = plain(value)
        tree = WorkingTree(input)
        any_path = [False]

        def each_target(target: JqValue, at: Path | None) -> Signal | None:
            if at is None or at.length < 0:
                return Signal.error(errors.invalid_path_result(target))
            any_path[0] = True
            failed = tree.try_set(at.to_value(), value)
            return None if failed is None else Signal.error(failed.error)
        signal = interpreter.eval(arguments[0], env, input, Path.ROOT, each_target)
        if signal is not None:
            return signal  # type: ignore[no-any-return]
        if any_path[0]:
            return emit(tree.freeze(), path and _UNTRACKED)  # type: ignore[no-any-return]
        return emit(input, path)  # type: ignore[no-any-return]
    return interpreter.for_each(arguments[1], env, input, None, each_value)  # type: ignore[no-any-return]


class _Draft:
    # A container copied from a value: its builder, the drafts below it by slot, and where it hangs.
    __slots__ = ("array", "obj", "parent", "index", "key", "items", "fields")

    def __init__(self, array: JqArrayBuilder | None, obj: JqObjectBuilder | None, parent: _Draft | None, index: int, key: str | None) -> None:
        self.array = array
        self.obj = obj
        self.parent = parent
        self.index = index
        self.key = key
        self.items: dict[int, _Draft] | None = None
        self.fields: dict[str, _Draft] | None = None

    def build(self) -> JqValue:
        return self.array.to_array() if self.array is not None else self.obj.to_object()  # type: ignore[union-attr]


# A slot of the tree: (parent draft or None for the root, index, key or None).
_ROOT_SLOT = (None, 0, None)


def _appends(value: JqValue, key: JqValue) -> bool:
    # Whether setpath with this key adds a key the object lacks, or the element just past the end
    # of the array.
    if type(value) is JqObject:
        return type(key) is str and not value.contains_key(key)
    if type(value) is JqArray:
        return _array_index(key, value.count) == value.count
    return False


def _array_index(key: JqValue, length: int) -> int:
    # setpath's index of an array of the given length: not NaN, negative counted from the end,
    # within the limit; -1 when setpath would not write there without an error.
    if type(key) is not float or key != key:
        return -1
    if math.isinf(key):  # type: ignore[arg-type]
        return -1
    i = math.trunc(key)  # type: ignore[arg-type]
    if i < 0:
        i += length
    if i < 0 or i > MAX_INDEX:
        return -1
    return i


class WorkingTree:
    """A value under update, owned by one call of ``_modify`` or ``_assign``.

    Each node is either an immutable value or a draft: a container the tree copied (one level) and
    then updates in place, so a container is copied at most once however many paths go through it.
    A write that adds a key to an object, or the next element to an array, that holds no draft
    opens none: the value appends in place when it is the latest version of its storage, so
    ``reduce ... ({}; .[$k] = $v)`` copies nothing.
    Nothing a program can see is ever changed (see AssignNatives.cs). Paths with keys the drafts do
    not handle fall back to ``getpath``/``setpath`` on the frozen value below that point. No walk
    recurses: paths and drafts are followed with loops and an explicit stack.
    """

    __slots__ = ("root", "root_draft")

    def __init__(self, root: JqValue) -> None:
        self.root = root
        self.root_draft: _Draft | None = None

    def freeze(self) -> JqValue:
        """The value of the whole tree; the tree holds no draft afterwards."""
        return self._freeze_at(_ROOT_SLOT)

    def try_get(self, keys: JqArray) -> JqValue | Fail:
        """``getpath(keys)`` on the current value; the value returned is frozen in the tree."""
        slot: tuple = _ROOT_SLOT
        items = keys.to_list()
        for i in range(len(items)):
            draft = self._draft_at(slot)
            if draft is not None:
                ok, nxt, missing = _try_read(draft, items[i])
                if ok:
                    if not missing:
                        slot = nxt
                        continue
                    # An array element out of range: null, which the rest of the path indexes.
                    return _try_index(None, items, i + 1)
            return _try_index(self._freeze_at(slot), items, i)
        return self._freeze_at(slot)

    def try_set(self, keys: JqArray, value: JqValue) -> Fail | None:
        """``setpath(keys; value)`` on the current value; None on success."""
        slot: tuple = _ROOT_SLOT
        items = keys.to_list()
        for i in range(len(items)):
            key = items[i]
            draft = self._draft_at(slot)
            if draft is not None:
                nxt = _try_write(draft, key)
                if nxt is not None:
                    slot = nxt
                    continue
            current = self._value_at(slot) if draft is None else self._freeze_at(slot)
            if draft is None and _appends(current, key):
                # A new key or the next element: the rest of the path is set on the null it holds,
                # then added to the value itself, in place when it is the latest version of its
                # storage (§2.9 of the porting guide), as . + {($k): $v} does.
                inner = path_natives.try_set_path(None, keys.slice(i + 1, keys.count), value)
                if type(inner) is Fail:
                    return inner  # type: ignore[return-value]
                self._store(slot, current.set(key, inner) if type(current) is JqObject else current.append(inner))  # type: ignore[union-attr, arg-type]
                return None
            draft = self._try_open(current, key, slot)
            if draft is not None:
                nxt = _try_write(draft, key)
                if nxt is not None:
                    slot = nxt
                    continue
            # A key the drafts do not handle: setpath with the rest of the path, on the value here.
            result = path_natives.try_set_path(current, keys.slice(i, keys.count), value)
            if type(result) is Fail:
                return result
            self._store(slot, result)
            return None
        self._store(slot, value)
        return None

    def _try_open(self, value: JqValue, key: JqValue, slot: tuple) -> _Draft | None:
        # Copies one level of the value at a slot into a draft there, when setpath would write into
        # it with this key (null becomes the container the key asks for).
        t = type(value)
        kt = type(key)
        parent, index, name = slot
        if kt is str and (t is JqObject or value is None):
            draft = _Draft(None, JqObjectBuilder(None if value is None else value), parent, index, name)  # type: ignore[arg-type]
        elif kt is float and (t is JqArray or value is None) and _array_index(key, 0 if value is None else value.count) >= 0:  # type: ignore[union-attr]
            draft = _Draft(JqArrayBuilder(JqArray.EMPTY if value is None else value), None, parent, index, name)  # type: ignore[arg-type]
        else:
            return None
        self._attach(slot, draft)
        return draft

    def _draft_at(self, slot: tuple) -> _Draft | None:
        parent, index, key = slot
        if parent is None:
            return self.root_draft
        if key is not None:
            fields = parent.fields
            return None if fields is None else fields.get(key)
        items = parent.items
        return None if items is None else items.get(index)

    def _value_at(self, slot: tuple) -> JqValue:
        # The value at a slot that holds no draft.
        parent, index, key = slot
        if parent is None:
            return self.root
        if key is not None:
            return parent.obj.get(key)
        array = parent.array
        return array.items[index] if index < len(array.items) else None

    def _store(self, slot: tuple, value: JqValue) -> None:
        # Puts a value at a slot, replacing whatever was there (a value or a draft).
        parent, index, key = slot
        if parent is None:
            self.root = value
            self.root_draft = None
        elif key is not None:
            parent.obj.set(key, value)
            if parent.fields is not None:
                parent.fields.pop(key, None)
        else:
            parent.array.set(index, value)
            if parent.items is not None:
                parent.items.pop(index, None)

    def _attach(self, slot: tuple, draft: _Draft) -> None:
        # Hangs a draft at a slot. Its place in the parent is taken now (a new key goes last now,
        # missing elements become null now), as setpath would; the value is written when it freezes.
        self._store(slot, None)
        parent, index, key = slot
        if parent is None:
            self.root_draft = draft
        elif key is not None:
            if parent.fields is None:
                parent.fields = {}
            parent.fields[key] = draft
        else:
            if parent.items is None:
                parent.items = {}
            parent.items[index] = draft

    def _freeze_at(self, slot: tuple) -> JqValue:
        # The value at a slot, freezing the drafts there first: children before parents, each
        # written into its parent's builder, the top one stored at the slot.
        top = self._draft_at(slot)
        if top is None:
            return self._value_at(slot)
        order: list[_Draft] = []
        pending = [top]
        while pending:
            draft = pending.pop()
            order.append(draft)
            if draft.items is not None:
                pending.extend(draft.items.values())
            if draft.fields is not None:
                pending.extend(draft.fields.values())
        for i in range(len(order) - 1, 0, -1):
            draft = order[i]
            value = draft.build()
            if draft.key is not None:
                draft.parent.obj.set(draft.key, value)  # type: ignore[union-attr]
            else:
                draft.parent.array.set(draft.index, value)  # type: ignore[union-attr]
        frozen = top.build()
        self._store(slot, frozen)
        return frozen


def _try_index(value: JqValue, keys: list[JqValue], frm: int) -> JqValue | Fail:
    # keys[frm:] indexed from value, as getpath does.
    for i in range(frm, len(keys)):
        value = value_ops.try_index(value, keys[i])
        if type(value) is Fail:
            return value
    return value


def _try_read(draft: _Draft, key: JqValue) -> tuple[bool, tuple, bool]:
    # The slot a read goes to next: a field (present or not) or an element in range; an element out
    # of range is missing (the value there is null). False for keys a draft does not read itself.
    if draft.obj is not None:
        if type(key) is str:
            return True, (draft, 0, key), False
        return False, _ROOT_SLOT, False
    if type(key) is not float:
        return False, _ROOT_SLOT, False
    count = len(draft.array.items)  # type: ignore[union-attr]
    if key != key or math.isinf(key):  # type: ignore[arg-type]
        return True, (draft, 0, None), True
    index = math.trunc(key)  # type: ignore[arg-type]
    if index < 0:
        index += count
    if index < 0 or index >= count:
        return True, (draft, 0, None), True
    return True, (draft, index, None), False


def _try_write(draft: _Draft, key: JqValue) -> tuple | None:
    # The slot a write goes to next, when setpath accepts the key for this container without error.
    if draft.obj is not None:
        return (draft, 0, key) if type(key) is str else None
    index = _array_index(key, len(draft.array.items))  # type: ignore[union-attr]
    return None if index < 0 else (draft, index, None)
