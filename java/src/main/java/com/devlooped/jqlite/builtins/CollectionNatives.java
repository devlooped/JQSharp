package com.devlooped.jqlite.builtins;

import static com.devlooped.jqlite.builtins.NativeTable.add;
import static com.devlooped.jqlite.builtins.NativeTable.fail;

import com.devlooped.jqlite.runtime.Interpreter;
import com.devlooped.jqlite.values.Errors;
import com.devlooped.jqlite.values.JqArray;
import com.devlooped.jqlite.values.JqKind;
import com.devlooped.jqlite.values.JqObject;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;
import com.devlooped.jqlite.values.ValueOps;
import java.util.ArrayList;
import java.util.List;

/** §9.4: keys, membership, sorting and its relatives. */
final class CollectionNatives {
    private static final JqValue[] NONE = new JqValue[0];

    private CollectionNatives() {
    }

    static void register() {
        add("keys", 0, (interpreter, input, arguments) -> keys(input, true));
        add("keys_unsorted", 0, (interpreter, input, arguments) -> keys(input, false));
        add("has", 1, CollectionNatives::has);
        add("sort", 0, (interpreter, input, arguments) -> input.kind() == JqKind.ARRAY
            ? ValueOps.sort(input.asArray())
            : fail(Errors.cannotBeSorted(input)));
        add("unique", 0, CollectionNatives::unique);
        add("_sort_by_impl", 1, CollectionNatives::sortBy);
        add("_group_by_impl", 1, CollectionNatives::groupBy);
        add("_min_by_impl", 1, (interpreter, input, arguments) -> extreme(input, arguments[0], false));
        add("_max_by_impl", 1, (interpreter, input, arguments) -> extreme(input, arguments[0], true));
        add("min", 0, (interpreter, input, arguments) -> extreme(input, input, false));
        add("max", 0, (interpreter, input, arguments) -> extreme(input, input, true));
        add("bsearch", 1, CollectionNatives::binarySearch);
    }

    private static Result keys(JqValue input, boolean sorted) {
        if (input.kind() == JqKind.OBJECT) {
            JqObject obj = input.asObject();
            JqValue[] keys = new JqValue[obj.count()];
            String[] order = sorted ? obj.sortedKeys() : null;
            for (int i = 0; i < keys.length; i++) {
                keys[i] = JqValue.fromString(order == null ? obj.keyAt(i) : order[i]);
            }
            return JqArray.fromOwned(keys);
        }

        if (input.kind() != JqKind.ARRAY) {
            return fail(Errors.hasNoKeys(input));
        }
        JqValue[] indices = new JqValue[input.asArray().count()];
        for (int i = 0; i < indices.length; i++) {
            indices[i] = JqValue.fromNumber(i);
        }
        return JqArray.fromOwned(indices);
    }

    private static Result has(Interpreter interpreter, JqValue input, JqValue[] arguments) {
        JqValue key = arguments[0];
        if (input.kind() == JqKind.OBJECT && key.kind() == JqKind.STRING) {
            return JqValue.fromBool(input.asObject().containsKey(key.asString()));
        }
        if (input.kind() == JqKind.ARRAY && key.kind() == JqKind.NUMBER) {
            double n = key.asNumber();
            double index = n < 0 ? Math.ceil(n) : Math.floor(n);
            return JqValue.fromBool(index >= 0 && index < input.asArray().count());
        }

        return input.kind() == JqKind.NULL ? JqValue.FALSE : fail(Errors.cannotCheckHas(input, key));
    }

    private static Result unique(Interpreter interpreter, JqValue input, JqValue[] arguments) {
        if (input.kind() != JqKind.ARRAY) {
            return fail(Errors.cannotBeSorted(input));
        }
        JqArray sorted = ValueOps.sort(input.asArray());
        List<JqValue> kept = new ArrayList<>(sorted.count());
        for (int i = 0; i < sorted.count(); i++) {
            if (i == 0 || ValueOps.compare(sorted.get(i - 1), sorted.get(i)) != 0) {
                kept.add(sorted.get(i));
            }
        }

        return JqArray.fromOwned(kept.toArray(NONE));
    }

    // _sort_by_impl(keys): the input stably sorted by the corresponding keys.
    private static Result sortBy(Interpreter interpreter, JqValue input, JqValue[] arguments) {
        if (!bothArrays(input, arguments[0])) {
            return fail(Errors.cannotBeSortedBy(input, arguments[0]));
        }
        JqArray items = input.asArray();
        int[] order = ValueOps.sortOrder(arguments[0].asArray());
        JqValue[] sorted = new JqValue[order.length];
        for (int i = 0; i < order.length; i++) {
            sorted[i] = items.get(order[i]);
        }
        return JqArray.fromOwned(sorted);
    }

    // _group_by_impl(keys): sorted as _sort_by_impl, then consecutive elements with equal keys grouped.
    private static Result groupBy(Interpreter interpreter, JqValue input, JqValue[] arguments) {
        if (!bothArrays(input, arguments[0])) {
            return fail(Errors.cannotBeSortedBy(input, arguments[0]));
        }
        JqArray items = input.asArray();
        JqArray keys = arguments[0].asArray();
        int[] order = ValueOps.sortOrder(keys);
        List<JqValue> groups = new ArrayList<>();
        List<JqValue> group = new ArrayList<>();
        for (int i = 0; i < order.length; i++) {
            if (i > 0 && ValueOps.compare(keys.get(order[i - 1]), keys.get(order[i])) != 0) {
                groups.add(JqArray.fromOwned(group.toArray(NONE)));
                group.clear();
            }

            group.add(items.get(order[i]));
        }

        if (!group.isEmpty()) {
            groups.add(JqArray.fromOwned(group.toArray(NONE)));
        }
        return JqArray.fromOwned(groups.toArray(NONE));
    }

    // _min_by_impl, _max_by_impl, min, max: null for an empty input; else the first element with
    // the smallest key, or the last with the largest.
    private static Result extreme(JqValue input, JqValue keys, boolean max) {
        if (!bothArrays(input, keys)) {
            return fail(Errors.cannotBeIterated(input, keys));
        }
        JqArray items = input.asArray();
        JqArray by = keys.asArray();
        if (items.count() == 0) {
            return JqValue.NULL;
        }
        int best = 0;
        for (int i = 1; i < items.count(); i++) {
            int c = ValueOps.compare(by.get(i), by.get(best));
            if (max ? c >= 0 : c < 0) {
                best = i;
            }
        }

        return items.get(best);
    }

    private static Result binarySearch(Interpreter interpreter, JqValue input, JqValue[] arguments) {
        if (input.kind() != JqKind.ARRAY) {
            return fail(Errors.cannotBeSearched(input));
        }
        JqArray items = input.asArray();
        int low = 0;
        int high = items.count() - 1;
        while (low <= high) {
            int middle = (low + high) / 2;
            int c = ValueOps.compare(items.get(middle), arguments[0]);
            if (c == 0) {
                return JqValue.fromNumber(middle);
            }
            if (c < 0) {
                low = middle + 1;
            } else {
                high = middle - 1;
            }
        }

        return JqValue.fromNumber(-1 - low);
    }

    private static boolean bothArrays(JqValue a, JqValue b) {
        return a.kind() == JqKind.ARRAY && b.kind() == JqKind.ARRAY;
    }
}
