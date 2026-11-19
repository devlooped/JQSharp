"""§9.5 and §8: ``path``, ``getpath``, ``setpath``, ``delpaths`` (PathNatives.cs)."""

from __future__ import annotations

import math
from typing import TYPE_CHECKING, Any

from ..runtime.native_table import add_closure, add_value
from ..runtime.path import Path
from ..runtime.signal import Signal
from ..values import errors, value_ops
from ..values.jq_array import JqArray
from ..values.jq_object import JqObject
from ..values.jq_value import Fail

if TYPE_CHECKING:
    from ..values.jq_value import JqValue

# setpath's index limit (§11).
MAX_INDEX = 536870911.0

_UNTRACKED = Path.UNTRACKED


def register() -> None:
    add_closure("path", 1, _path_of)
    add_closure("getpath", 1, _get_path)
    add_value("setpath", 2, lambda interpreter, input, arguments: try_set_path(input, arguments[0], arguments[1]))
    add_value("delpaths", 1, lambda interpreter, input, arguments: try_delete_paths(input, arguments[0]))


def _path_of(interpreter: Any, env: Any, arguments: Any, input: JqValue, path: Path | None, emit: Any) -> Signal | None:
    # path(f): f in path mode from the empty path; each output must have a path.
    at = path and _UNTRACKED

    def each(value: JqValue, value_at: Path | None) -> Signal | None:
        if value_at is not None and value_at.length >= 0:
            return emit(value_at.to_value(), at)  # type: ignore[no-any-return]
        return Signal.error(errors.invalid_path_result(value))
    return interpreter.eval(arguments[0], env, input, Path.ROOT, each)  # type: ignore[no-any-return]


def _get_path(interpreter: Any, env: Any, arguments: Any, input: JqValue, path: Path | None, emit: Any) -> Signal | None:
    # getpath(p): p in value mode; the output's path is the input's followed by p, even when absent.
    def each(keys: JqValue, _: Path | None) -> Signal | None:
        if type(keys) is not JqArray:
            return Signal.error(errors.PATH_NOT_ARRAY)
        value = input
        for key in keys.to_list():  # type: ignore[union-attr]
            value = value_ops.try_index(value, key)
            if type(value) is Fail:
                return Signal.error(value.error)
        return emit(value, path.append_all(keys) if path is not None and path.length >= 0 else path)  # type: ignore[arg-type,no-any-return]
    return interpreter.for_each(arguments[0], env, input, None, each)  # type: ignore[no-any-return]


def try_set_path(root: JqValue, keys: JqValue, value: JqValue) -> JqValue | Fail:
    """setpath(p; v) (§8.2): each container on the way is indexed first (so its errors come first),
    then the containers are rebuilt from the innermost out, each copied one level."""
    if type(keys) is not JqArray:
        return Fail(errors.PATH_NOT_ARRAY)
    path = keys.to_list()  # type: ignore[union-attr]
    containers: list[JqValue] = []
    current = root
    for key in path:
        containers.append(current)
        current = value_ops.try_index(current, key)
        if type(current) is Fail:
            return current
    for i in range(len(path) - 1, -1, -1):
        value = _try_set(containers[i], path[i], value)
        if type(value) is Fail:
            return value
    return value


def _try_set(container: JqValue, key: JqValue, value: JqValue) -> JqValue | Fail:
    # One level of setpath: the table of §8.2.
    t = type(container)
    kt = type(key)
    if kt is str and (t is JqObject or container is None):
        obj = JqObject.EMPTY if container is None else container
        return obj.set(key, value)  # type: ignore[union-attr,arg-type]
    if kt is float and (t is JqArray or container is None):
        array: JqArray = JqArray.EMPTY if container is None else container  # type: ignore[assignment]
        number: float = key  # type: ignore[assignment]
        if number != number:
            return Fail(errors.NAN_INDEX)
        index = float(math.trunc(number)) if not math.isinf(number) else number
        if index < 0:
            index += array.count
        if index < 0:
            return Fail(errors.NEGATIVE_INDEX)
        if index > MAX_INDEX:
            return Fail(errors.INDEX_TOO_LARGE)
        return array.set_item(int(index), value)
    if kt is JqObject and (t is JqArray or container is None):
        return _try_set_slice(JqArray.EMPTY if container is None else container, key, value)  # type: ignore[arg-type]
    if kt is JqObject and t is str:
        return Fail(errors.CANNOT_UPDATE_STRING_SLICES)
    return Fail(errors.cannot_update_field(key, container))


def _try_set_slice(array: JqArray, slice: JqObject, value: JqValue) -> JqValue | Fail:
    # .[s:e] = v on an array: v (an array) replaces the elements the bounds select.
    if not slice.contains_key("start") or not slice.contains_key("end"):
        return Fail(errors.SLICE_INDICES_NOT_INTEGERS)
    bounds = value_ops.try_slice_range(array.count, slice.get("start"), slice.get("end"))
    if type(bounds) is Fail:
        return bounds
    first, last = bounds  # type: ignore[misc]
    if type(value) is not JqArray:
        return Fail(errors.SLICE_NEEDS_ARRAY)
    return array.slice(0, first).concat(value).concat(array.slice(last, array.count))  # type: ignore[arg-type]


def try_delete_paths(root: JqValue, paths: JqValue) -> JqValue | Fail:
    """delpaths(ps) (§8.2): the paths sorted, then deleted level by level."""
    if type(paths) is not JqArray:
        return Fail(errors.PATHS_NOT_ARRAY)
    items = paths.to_list()  # type: ignore[union-attr]
    for item in items:
        if type(item) is not JqArray:
            return Fail(errors.path_element_not_array(item))
        if item.count == 0:  # type: ignore[union-attr]
            return None
    ordered = [p.to_list() for p in value_ops.sort(paths).to_list()]  # type: ignore[union-attr,arg-type]
    return _try_delete(root, ordered, 0, len(ordered), 0)


class _Level:
    # One container being rebuilt by _try_delete: the paths [next, to) still to look at, at depth;
    # the keys deleted at this level; and the key of the child being rebuilt below it, if any.
    __slots__ = ("value", "next", "to", "depth", "deleted", "child_key", "child_end")

    def __init__(self, value: JqValue, frm: int, to: int, depth: int) -> None:
        self.value = value
        self.next = frm
        self.to = to
        self.depth = depth
        self.deleted: list[JqValue] = []
        self.child_key: JqValue = None
        self.child_end = 0


def _try_delete(value: JqValue, paths: list[list[JqValue]], frm: int, to: int, depth: int) -> JqValue | Fail:
    # Deletes paths[frm:to] (sorted, all longer than depth) from value at the given depth: groups by
    # the key at that depth; a group holding the path that ends there deletes the key, the others go
    # into the child first. All the keys of one level are removed at once. The levels being rebuilt
    # are on an explicit stack, so a path of any length is no recursion.
    levels = [_Level(value, frm, to, depth)]
    while True:
        level = levels[-1]
        descended = False
        while level.value is not None and level.next < level.to and not descended:
            d = level.depth
            key = paths[level.next][d]
            end = level.next + 1
            ends = len(paths[level.next]) == d + 1
            while end < level.to and value_ops.compare(paths[end][d], key) == 0:
                ends = ends or len(paths[end]) == d + 1
                end += 1
            if ends:
                level.deleted.append(key)
                level.next = end
                continue
            child = value_ops.try_index(level.value, key)
            if type(child) is Fail:
                return child
            if child is None:
                level.next = end
                continue
            level.child_key = key
            level.child_end = end
            levels.append(_Level(child, level.next, end, d + 1))
            descended = True
        if descended:
            continue
        # This level is done: remove its keys, then put it back into its parent.
        done = _try_remove(level.value, level.deleted)
        if type(done) is Fail:
            return done
        levels.pop()
        if not levels:
            return done
        parent = levels[-1]
        updated = _try_set(parent.value, parent.child_key, done)
        if type(updated) is Fail:
            return updated
        parent.value = updated
        parent.next = parent.child_end


def _try_remove(container: JqValue, keys: list[JqValue]) -> JqValue | Fail:
    # Removes keys from a container, all relative to the container as it is.
    if not keys or container is None:
        return container
    t = type(container)
    if t is JqObject:
        obj: JqObject = container  # type: ignore[assignment]
        for key in keys:
            if type(key) is not str:
                return Fail(errors.cannot_delete_field(key))
            obj = obj.remove(key)  # type: ignore[arg-type]
        return obj
    if t is not JqArray:
        return Fail(errors.cannot_delete_from(container))
    items = container.to_list()  # type: ignore[union-attr]
    n = len(items)
    removed = [False] * n
    for key in keys:
        kt = type(key)
        if kt is float:
            if key != key:
                continue
            if math.isinf(key):  # type: ignore[arg-type]
                continue
            index = math.trunc(key)  # type: ignore[arg-type]
            if index < 0:
                index += n
            if 0 <= index < n:
                removed[index] = True
        elif kt is JqObject:
            if not key.contains_key("start") or not key.contains_key("end"):  # type: ignore[union-attr]
                return Fail(errors.SLICE_INDICES_NOT_INTEGERS)
            bounds = value_ops.try_slice_range(n, key.get("start"), key.get("end"))  # type: ignore[union-attr]
            if type(bounds) is Fail:
                return bounds
            for i in range(bounds[0], bounds[1]):  # type: ignore[index]
                removed[i] = True
        else:
            return Fail(errors.cannot_delete_element(key))
    return JqArray.from_owned([items[i] for i in range(n) if not removed[i]])
