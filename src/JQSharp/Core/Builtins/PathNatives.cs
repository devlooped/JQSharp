using System.Collections.Immutable;
using Devlooped.Core.Binding;
using Devlooped.Core.Runtime;
using Devlooped.Core.Values;
using static Devlooped.Core.Builtins.NativeTable;
using Path = Devlooped.Core.Runtime.Path;

namespace Devlooped.Core.Builtins;

/// <summary>§9.5 and §8: <c>path</c>, <c>getpath</c>, <c>setpath</c>, <c>delpaths</c>.</summary>
static class PathNatives
{
    // setpath's index limit (§11).
    const double MaxIndex = 536870911;

    public static void Register()
    {
        Add("path", 1, PathOf);
        Add("getpath", 1, GetPath);
        Add("setpath", 2, static (_, input, arguments, out result, out error) => TrySetPath(input, arguments[0], arguments[1], out result, out error));
        Add("delpaths", 1, static (_, input, arguments, out result, out error) => TryDeletePaths(input, arguments[0], out result, out error));
    }

    // path(f): f in path mode from the empty path; each output must have a path.
    static Signal? PathOf(Interpreter interpreter, Env? env, ImmutableArray<Node> arguments, JqValue input, Path? path, Emit emit)
    {
        var at = Path.Of(path);
        return interpreter.Eval(arguments[0], env, input, Path.Root, (value, valueAt) =>
            valueAt is { IsTracked: true } ? Interpreter.Output(emit, valueAt.ToValue(), at) : Interpreter.Fail(Errors.InvalidPathResult(value)));
    }

    // getpath(p): p in value mode; the output's path is the input's followed by p, even when absent.
    static Signal? GetPath(Interpreter interpreter, Env? env, ImmutableArray<Node> arguments, JqValue input, Path? path, Emit emit)
        => interpreter.ForEach(arguments[0], env, input, null, (keys, _) =>
        {
            if (keys.Kind != JqKind.Array)
                return Interpreter.Fail(Errors.PathNotArray);
            var steps = keys.AsArray();
            var value = input;
            for (var i = 0; i < steps.Count; i++)
            {
                if (!ValueOps.TryIndex(value, steps[i], out value, out var error))
                    return Signal.Error(error);
            }

            return Interpreter.Output(emit, value, path is { IsTracked: true } ? path.Append(steps) : path);
        });

    /// <summary>
    /// setpath(p; v) (§8.2): each container on the way is indexed first (so its errors come
    /// first), then the containers are rebuilt from the innermost out, each copied one level.
    /// </summary>
    public static bool TrySetPath(JqValue root, JqValue keys, JqValue value, out JqValue result, out JqValue error)
    {
        if (keys.Kind != JqKind.Array)
            return Fail(Errors.PathNotArray, out result, out error);
        var path = keys.AsArray();
        var containers = new JqValue[path.Count];
        var current = root;
        for (var i = 0; i < path.Count; i++)
        {
            containers[i] = current;
            if (!ValueOps.TryIndex(current, path[i], out current, out error))
                return Fail(error, out result, out error);
        }

        for (var i = path.Count - 1; i >= 0; i--)
        {
            if (!TrySet(containers[i], path[i], value, out value, out error))
                return Fail(error, out result, out error);
        }

        return Ok(value, out result, out error);
    }

    // One level of setpath: the table of §8.2.
    static bool TrySet(JqValue container, JqValue key, JqValue value, out JqValue result, out JqValue error)
    {
        var kind = container.Kind;
        switch (key.Kind)
        {
            case JqKind.String when kind is JqKind.Object or JqKind.Null:
                var obj = kind == JqKind.Null ? JqObject.Empty : container.AsObject();
                return Ok(JqValue.FromObject(obj.Set(key.AsString(), value)), out result, out error);
            case JqKind.Number when kind is JqKind.Array or JqKind.Null:
                var array = kind == JqKind.Null ? JqArray.Empty : container.AsArray();
                var number = key.AsNumber();
                if (double.IsNaN(number))
                    return Fail(Errors.NaNIndex, out result, out error);
                var index = Math.Truncate(number);
                if (index < 0)
                    index += array.Count;
                if (index < 0)
                    return Fail(Errors.NegativeIndex, out result, out error);
                if (index > MaxIndex)
                    return Fail(Errors.IndexTooLarge, out result, out error);
                return Ok(JqValue.FromArray(array.SetItem((int)index, value)), out result, out error);
            case JqKind.Object when kind is JqKind.Array or JqKind.Null:
                return TrySetSlice(kind == JqKind.Null ? JqArray.Empty : container.AsArray(), key.AsObject(), value, out result, out error);
            case JqKind.Object when kind == JqKind.String:
                return Fail(Errors.CannotUpdateStringSlices, out result, out error);
            default:
                return Fail(Errors.CannotUpdateField(key, container), out result, out error);
        }
    }

    // .[s:e] = v on an array: v (an array) replaces the elements the bounds select.
    static bool TrySetSlice(JqArray array, JqObject slice, JqValue value, out JqValue result, out JqValue error)
    {
        if (!slice.TryGetValue("start", out var start) || !slice.TryGetValue("end", out var end))
            return Fail(Errors.SliceIndicesNotIntegers, out result, out error);
        if (!ValueOps.TrySliceRange(array.Count, start, end, out var first, out var last, out error))
            return Fail(error, out result, out error);
        if (value.Kind != JqKind.Array)
            return Fail(Errors.SliceNeedsArray, out result, out error);
        var replaced = array.Slice(0, first).Concat(value.AsArray()).Concat(array.Slice(last, array.Count));
        return Ok(JqValue.FromArray(replaced), out result, out error);
    }

    /// <summary>delpaths(ps) (§8.2): the paths sorted, then deleted level by level.</summary>
    public static bool TryDeletePaths(JqValue root, JqValue paths, out JqValue result, out JqValue error)
    {
        if (paths.Kind != JqKind.Array)
            return Fail(Errors.PathsNotArray, out result, out error);
        var list = paths.AsArray();
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Kind != JqKind.Array)
                return Fail(Errors.PathElementNotArray(list[i]), out result, out error);
            if (list[i].AsArray().Count == 0)
                return Ok(JqValue.Null, out result, out error);
        }

        var sorted = ValueOps.Sort(list);
        return TryDelete(root, sorted, 0, sorted.Count, 0, out result, out error);
    }

    // Deletes paths[from, to) (sorted, all longer than depth) from value at the given depth: groups
    // by the key at that depth; a group holding the path that ends there deletes the key, the
    // others recurse into the child. All the keys of one level are removed at once.
    // Recursion goes one level per key of the longest path.
    static bool TryDelete(JqValue value, JqArray paths, int from, int to, int depth, out JqValue result, out JqValue error)
    {
        if (value.Kind == JqKind.Null)
            return Ok(value, out result, out error);
        var deleted = new List<JqValue>();
        for (var group = from; group < to;)
        {
            var key = paths[group].AsArray()[depth];
            var end = group + 1;
            var ends = paths[group].AsArray().Count == depth + 1;
            for (; end < to && ValueOps.Compare(paths[end].AsArray()[depth], key) == 0; end++)
                ends |= paths[end].AsArray().Count == depth + 1;

            if (ends)
            {
                deleted.Add(key);
            }
            else
            {
                if (!ValueOps.TryIndex(value, key, out var child, out error))
                    return Fail(error, out result, out error);
                if (child.Kind != JqKind.Null)
                {
                    if (!TryDelete(child, paths, group, end, depth + 1, out child, out error) || !TrySet(value, key, child, out value, out error))
                        return Fail(error, out result, out error);
                }
            }

            group = end;
        }

        return TryRemove(value, deleted, out result, out error);
    }

    // Removes keys from a container, all relative to the container as it is.
    static bool TryRemove(JqValue container, List<JqValue> keys, out JqValue result, out JqValue error)
    {
        if (keys.Count == 0 || container.Kind == JqKind.Null)
            return Ok(container, out result, out error);
        if (container.Kind == JqKind.Object)
        {
            var obj = container.AsObject();
            foreach (var key in keys)
            {
                if (key.Kind != JqKind.String)
                    return Fail(Errors.CannotDeleteField(key), out result, out error);
                obj = obj.Remove(key.AsString());
            }

            return Ok(JqValue.FromObject(obj), out result, out error);
        }

        if (container.Kind != JqKind.Array)
            return Fail(Errors.CannotDeleteFrom(container), out result, out error);

        var array = container.AsArray();
        var removed = new bool[array.Count];
        foreach (var key in keys)
        {
            if (key.Kind == JqKind.Number)
            {
                var number = key.AsNumber();
                if (double.IsNaN(number))
                    continue;
                var index = Math.Truncate(number);
                if (index < 0)
                    index += array.Count;
                if (index >= 0 && index < array.Count)
                    removed[(int)index] = true;
            }
            else if (key.Kind == JqKind.Object)
            {
                var slice = key.AsObject();
                if (!slice.TryGetValue("start", out var start) || !slice.TryGetValue("end", out var end))
                    return Fail(Errors.SliceIndicesNotIntegers, out result, out error);
                if (!ValueOps.TrySliceRange(array.Count, start, end, out var first, out var last, out error))
                    return Fail(error, out result, out error);
                for (var i = first; i < last; i++)
                    removed[i] = true;
            }
            else
            {
                return Fail(Errors.CannotDeleteElement(key), out result, out error);
            }
        }

        var kept = new List<JqValue>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            if (!removed[i])
                kept.Add(array[i]);
        }

        return Ok(JqValue.FromArray(JqArray.FromOwned([.. kept])), out result, out error);
    }
}
