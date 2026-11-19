using JQLite.Core.Runtime;
using JQLite.Core.Values;
using static JQLite.Core.Builtins.NativeTable;

namespace JQLite.Core.Builtins;

/// <summary>§9.4: keys, membership, sorting and its relatives.</summary>
static class CollectionNatives
{
    public static void Register()
    {
        Add("keys", 0, static (_, input, _, out result, out error) => Keys(input, sorted: true, out result, out error));
        Add("keys_unsorted", 0, static (_, input, _, out result, out error) => Keys(input, sorted: false, out result, out error));
        Add("has", 1, Has);
        Add("sort", 0, static (_, input, _, out result, out error) => input.Kind == JqKind.Array
            ? Ok(JqValue.FromArray(ValueOps.Sort(input.AsArray())), out result, out error)
            : Fail(Errors.CannotBeSorted(input), out result, out error));
        Add("unique", 0, Unique);
        Add("_sort_by_impl", 1, SortBy);
        Add("_group_by_impl", 1, GroupBy);
        Add("_min_by_impl", 1, static (_, input, arguments, out result, out error) => Extreme(input, arguments[0], max: false, out result, out error));
        Add("_max_by_impl", 1, static (_, input, arguments, out result, out error) => Extreme(input, arguments[0], max: true, out result, out error));
        Add("min", 0, static (_, input, _, out result, out error) => Extreme(input, input, max: false, out result, out error));
        Add("max", 0, static (_, input, _, out result, out error) => Extreme(input, input, max: true, out result, out error));
        Add("bsearch", 1, BinarySearch);
    }

    static bool Keys(JqValue input, bool sorted, out JqValue result, out JqValue error)
    {
        if (input.Kind == JqKind.Object)
        {
            var obj = input.AsObject();
            var keys = new JqValue[obj.Count];
            var order = sorted ? obj.SortedKeys() : null;
            for (var i = 0; i < keys.Length; i++)
                keys[i] = JqValue.FromString(order is null ? obj.KeyAt(i) : order[i]);
            return Ok(JqValue.FromArray(JqArray.FromOwned(keys)), out result, out error);
        }

        if (input.Kind != JqKind.Array)
            return Fail(Errors.HasNoKeys(input), out result, out error);
        var indices = new JqValue[input.AsArray().Count];
        for (var i = 0; i < indices.Length; i++)
            indices[i] = JqValue.FromNumber(i);
        return Ok(JqValue.FromArray(JqArray.FromOwned(indices)), out result, out error);
    }

    static bool Has(Interpreter interpreter, JqValue input, JqValue[] arguments, out JqValue result, out JqValue error)
    {
        var key = arguments[0];
        if (input.Kind == JqKind.Object && key.Kind == JqKind.String)
            return Ok(JqValue.FromBool(input.AsObject().ContainsKey(key.AsString())), out result, out error);
        if (input.Kind == JqKind.Array && key.Kind == JqKind.Number)
        {
            var index = Math.Truncate(key.AsNumber());
            return Ok(JqValue.FromBool(index >= 0 && index < input.AsArray().Count), out result, out error);
        }

        return input.Kind == JqKind.Null
            ? Ok(JqValue.False, out result, out error)
            : Fail(Errors.CannotCheckHas(input, key), out result, out error);
    }

    static bool Unique(Interpreter interpreter, JqValue input, JqValue[] arguments, out JqValue result, out JqValue error)
    {
        if (input.Kind != JqKind.Array)
            return Fail(Errors.CannotBeSorted(input), out result, out error);
        var sorted = ValueOps.Sort(input.AsArray());
        var kept = new List<JqValue>(sorted.Count);
        for (var i = 0; i < sorted.Count; i++)
        {
            if (i == 0 || ValueOps.Compare(sorted[i - 1], sorted[i]) != 0)
                kept.Add(sorted[i]);
        }

        return Ok(JqValue.FromArray(JqArray.FromOwned([.. kept])), out result, out error);
    }

    // _sort_by_impl(keys): the input stably sorted by the corresponding keys.
    static bool SortBy(Interpreter interpreter, JqValue input, JqValue[] arguments, out JqValue result, out JqValue error)
    {
        if (!BothArrays(input, arguments[0]))
            return Fail(Errors.CannotBeSortedBy(input, arguments[0]), out result, out error);
        var items = input.AsArray();
        var order = ValueOps.SortOrder(arguments[0].AsArray());
        var sorted = new JqValue[order.Length];
        for (var i = 0; i < order.Length; i++)
            sorted[i] = items[order[i]];
        return Ok(JqValue.FromArray(JqArray.FromOwned(sorted)), out result, out error);
    }

    // _group_by_impl(keys): sorted as _sort_by_impl, then consecutive elements with equal keys grouped.
    static bool GroupBy(Interpreter interpreter, JqValue input, JqValue[] arguments, out JqValue result, out JqValue error)
    {
        if (!BothArrays(input, arguments[0]))
            return Fail(Errors.CannotBeSortedBy(input, arguments[0]), out result, out error);
        var items = input.AsArray();
        var keys = arguments[0].AsArray();
        var order = ValueOps.SortOrder(keys);
        var groups = new List<JqValue>();
        var group = new List<JqValue>();
        for (var i = 0; i < order.Length; i++)
        {
            if (i > 0 && ValueOps.Compare(keys[order[i - 1]], keys[order[i]]) != 0)
            {
                groups.Add(JqValue.FromArray(JqArray.FromOwned([.. group])));
                group.Clear();
            }

            group.Add(items[order[i]]);
        }

        if (group.Count > 0)
            groups.Add(JqValue.FromArray(JqArray.FromOwned([.. group])));
        return Ok(JqValue.FromArray(JqArray.FromOwned([.. groups])), out result, out error);
    }

    // _min_by_impl, _max_by_impl, min, max: null for an empty input; else the first element with
    // the smallest key, or the last with the largest.
    static bool Extreme(JqValue input, JqValue keys, bool max, out JqValue result, out JqValue error)
    {
        if (!BothArrays(input, keys))
            return Fail(Errors.CannotBeIterated(input, keys), out result, out error);
        var items = input.AsArray();
        var by = keys.AsArray();
        if (items.Count == 0)
            return Ok(JqValue.Null, out result, out error);
        var best = 0;
        for (var i = 1; i < items.Count; i++)
        {
            var c = ValueOps.Compare(by[i], by[best]);
            if (max ? c >= 0 : c < 0)
                best = i;
        }

        return Ok(items[best], out result, out error);
    }

    static bool BinarySearch(Interpreter interpreter, JqValue input, JqValue[] arguments, out JqValue result, out JqValue error)
    {
        if (input.Kind != JqKind.Array)
            return Fail(Errors.CannotBeSearched(input), out result, out error);
        var items = input.AsArray();
        int low = 0, high = items.Count - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var c = ValueOps.Compare(items[middle], arguments[0]);
            if (c == 0)
                return Ok(JqValue.FromNumber(middle), out result, out error);
            if (c < 0)
                low = middle + 1;
            else
                high = middle - 1;
        }

        return Ok(JqValue.FromNumber(-1 - low), out result, out error);
    }

    static bool BothArrays(JqValue a, JqValue b) => a.Kind == JqKind.Array && b.Kind == JqKind.Array;
}
