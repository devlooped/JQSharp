"""§9.4: keys, membership, sorting and its relatives (CollectionNatives.cs)."""

from __future__ import annotations

from typing import TYPE_CHECKING, Any

from ..runtime.native_table import add_value
from ..values import errors, value_ops
from ..values.jq_array import JqArray
from ..values.jq_object import JqObject
from ..values.jq_value import Fail

if TYPE_CHECKING:
    from ..values.jq_value import JqValue


def register() -> None:
    add_value("keys", 0, lambda interpreter, input, arguments: _keys(input, True))
    add_value("keys_unsorted", 0, lambda interpreter, input, arguments: _keys(input, False))
    add_value("has", 1, _has)
    add_value("sort", 0, lambda interpreter, input, arguments: value_ops.sort(input) if type(input) is JqArray
              else Fail(errors.cannot_be_sorted(input)))
    add_value("unique", 0, _unique)
    add_value("_sort_by_impl", 1, _sort_by)
    add_value("_group_by_impl", 1, _group_by)
    add_value("_min_by_impl", 1, lambda interpreter, input, arguments: _extreme(input, arguments[0], False))
    add_value("_max_by_impl", 1, lambda interpreter, input, arguments: _extreme(input, arguments[0], True))
    add_value("min", 0, lambda interpreter, input, arguments: _extreme(input, input, False))
    add_value("max", 0, lambda interpreter, input, arguments: _extreme(input, input, True))
    add_value("bsearch", 1, _binary_search)


def _keys(input: JqValue, ordered: bool) -> JqValue | Fail:
    t = type(input)
    if t is JqObject:
        obj: JqObject = input  # type: ignore[assignment]
        return JqArray.from_owned(list(obj.sorted_keys()) if ordered else obj.keys[:obj.count])  # type: ignore[arg-type]
    if t is not JqArray:
        return Fail(errors.has_no_keys(input))
    return JqArray.from_owned([float(i) for i in range(input.count)])  # type: ignore[union-attr]


def _has(interpreter: Any, input: JqValue, arguments: Any) -> JqValue | Fail:
    key = arguments[0]
    t = type(input)
    if t is JqObject and type(key) is str:
        return input.contains_key(key)  # type: ignore[union-attr]
    if t is JqArray and type(key) is float:
        # 0 <= trunc(key) < length; false for NaN.
        return -1.0 < key < input.count  # type: ignore[union-attr]
    if input is None:
        return False
    return Fail(errors.cannot_check_has(input, key))


def _unique(interpreter: Any, input: JqValue, arguments: Any) -> JqValue | Fail:
    if type(input) is not JqArray:
        return Fail(errors.cannot_be_sorted(input))
    ordered = value_ops.sort(input).to_list()  # type: ignore[arg-type]
    compare = value_ops.compare
    kept = [ordered[i] for i in range(len(ordered)) if i == 0 or compare(ordered[i - 1], ordered[i]) != 0]
    return JqArray.from_owned(kept)


def _sort_by(interpreter: Any, input: JqValue, arguments: Any) -> JqValue | Fail:
    # _sort_by_impl(keys): the input stably sorted by the corresponding keys.
    keys = arguments[0]
    if type(input) is not JqArray or type(keys) is not JqArray:
        return Fail(errors.cannot_be_sorted_by(input, keys))
    items = input.to_list()  # type: ignore[union-attr]
    return JqArray.from_owned([items[i] for i in value_ops.sort_order(keys)])


def _group_by(interpreter: Any, input: JqValue, arguments: Any) -> JqValue | Fail:
    # _group_by_impl(keys): sorted as _sort_by_impl, then consecutive elements with equal keys grouped.
    keys = arguments[0]
    if type(input) is not JqArray or type(keys) is not JqArray:
        return Fail(errors.cannot_be_sorted_by(input, keys))
    items = input.to_list()  # type: ignore[union-attr]
    by = keys.to_list()  # type: ignore[union-attr]
    order = value_ops.sort_order(keys)
    compare = value_ops.compare
    groups: list = []
    group: list = []
    for i in range(len(order)):
        if i > 0 and compare(by[order[i - 1]], by[order[i]]) != 0:
            groups.append(JqArray.from_owned(group))
            group = []
        group.append(items[order[i]])
    if group:
        groups.append(JqArray.from_owned(group))
    return JqArray.from_owned(groups)


def _extreme(input: JqValue, keys: JqValue, maximum: bool) -> JqValue | Fail:
    # _min_by_impl, _max_by_impl, min, max: null for an empty input; else the first element with
    # the smallest key, or the last with the largest.
    if type(input) is not JqArray or type(keys) is not JqArray:
        return Fail(errors.cannot_be_iterated(input, keys))
    items = input.to_list()  # type: ignore[union-attr]
    by = keys.to_list()  # type: ignore[union-attr]
    if not items:
        return None
    compare = value_ops.compare
    best = 0
    for i in range(1, len(items)):
        c = compare(by[i], by[best])
        if (c >= 0) if maximum else (c < 0):
            best = i
    return items[best]


def _binary_search(interpreter: Any, input: JqValue, arguments: Any) -> JqValue | Fail:
    if type(input) is not JqArray:
        return Fail(errors.cannot_be_searched(input))
    items = input.to_list()  # type: ignore[union-attr]
    target = arguments[0]
    compare = value_ops.compare
    low = 0
    high = len(items) - 1
    while low <= high:
        middle = (low + high) // 2
        c = compare(items[middle], target)
        if c == 0:
            return float(middle)
        if c < 0:
            low = middle + 1
        else:
            high = middle - 1
    return float(-1 - low)
