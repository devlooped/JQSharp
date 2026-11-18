using System.Collections.Immutable;
using Devlooped.Core.Binding;
using Devlooped.Core.Runtime;
using Devlooped.Core.Values;
using static Devlooped.Core.Builtins.NativeTable;
using Path = Devlooped.Core.Runtime.Path;

namespace Devlooped.Core.Builtins;

/// <summary>
/// <c>_modify/2</c> and <c>_assign/2</c> (§8.3) implemented natively, overriding their prelude
/// definitions (<see cref="Native.OverridesPrelude"/>): same results, errors, order of evaluation and
/// path behavior, but linear in the number of paths, where the prelude's one <c>setpath</c> per path
/// copies the container each time. The value being updated is a <see cref="WorkingTree"/>.
/// </summary>
static class AssignNatives
{
    public static void Register()
    {
        Add("_modify", 2, Modify);
        Add("_assign", 2, Assign);
    }

    // def _modify(paths; update): for each path of paths (on the input, lazily), the first output
    // of update applied to the current value there replaces it; no output: the path is deleted at
    // the end. update runs in value mode; the result has no path.
    static Signal? Modify(Interpreter interpreter, Env? env, ImmutableArray<Node> arguments, JqValue input, Path? path, Emit emit)
    {
        var tree = new WorkingTree(input);
        var deletions = new List<JqValue>();
        var first = Signal.Break(interpreter.NewLabel());
        var signal = interpreter.Eval(arguments[0], env, input, Path.Root, (value, at) =>
        {
            if (at is not { IsTracked: true })
                return Interpreter.Fail(Errors.InvalidPathResult(value));
            var keys = at.ToValue();
            if (!tree.TryGet(keys.AsArray(), out var current, out var error))
                return Signal.Error(error);
            var updated = false;
            var next = JqValue.Null;
            var stop = interpreter.Eval(arguments[1], env, current, null, (output, _) =>
            {
                (updated, next) = (true, output);
                return first;
            });
            if (stop is not null && !ReferenceEquals(stop, first))
                return stop;
            if (!updated)
            {
                deletions.Add(keys);
                return null;
            }

            return tree.TrySet(keys.AsArray(), next, out error) ? null : Signal.Error(error);
        });
        if (signal is not null)
            return signal;
        var paths = JqValue.FromArray(JqArray.FromOwned([.. deletions]));
        return PathNatives.TryDeletePaths(tree.Freeze(), paths, out var result, out var failed)
            ? Interpreter.Output(emit, result, Path.Of(path))
            : Signal.Error(failed);
    }

    // def _assign(paths; $value): for each output of value (on the input), every path of paths set
    // to it. With no path the result is the input itself, with its path (the reduce's initial state).
    static Signal? Assign(Interpreter interpreter, Env? env, ImmutableArray<Node> arguments, JqValue input, Path? path, Emit emit)
        => interpreter.ForEach(arguments[1], env, input, null, (value, _) =>
        {
            var tree = new WorkingTree(input);
            var any = false;
            var signal = interpreter.Eval(arguments[0], env, input, Path.Root, (target, at) =>
            {
                if (at is not { IsTracked: true })
                    return Interpreter.Fail(Errors.InvalidPathResult(target));
                any = true;
                return tree.TrySet(at.ToValue().AsArray(), value, out var error) ? null : Signal.Error(error);
            });
            return signal ?? (any ? Interpreter.Output(emit, tree.Freeze(), Path.Of(path)) : Interpreter.Output(emit, input, path));
        });
}

/// <summary>
/// A value under update, owned by one call of <c>_modify</c> or <c>_assign</c>. Each node is either
/// an immutable value or a draft: a container the tree copied (one level) and then updates in
/// place, so a container is copied at most once however many paths go through it.
/// </summary>
/// <remarks>
/// Nothing a program can see is ever changed. <see cref="TrySet"/> stores the new value as an
/// immutable leaf and opens drafts only along its path; <see cref="TryGet"/>, which hands a value
/// to the update closure, and <see cref="Freeze"/> turn the drafts below the path back into
/// values, giving them their storage without copying, so a later write there copies that level
/// again. Paths with keys the drafts do not handle (slices, keys that <c>setpath</c> rejects) fall
/// back to <c>getpath</c>/<c>setpath</c> on the frozen value below that point, which keeps their
/// results and errors, in their order, those of the prelude definition. No walk recurses: paths
/// and drafts are followed with loops and an explicit stack.
/// </remarks>
sealed class WorkingTree
{
    // setpath's index limit (§11).
    const double MaxIndex = 536870911;

    // A container copied from a value: its builder, the drafts below it by slot, and where it hangs.
    sealed class Draft(JqArrayBuilder? array, JqObjectBuilder? obj, Draft? parent, int index, string? key)
    {
        public readonly JqArrayBuilder? Array = array;
        public readonly JqObjectBuilder? Object = obj;
        public readonly Draft? Parent = parent;
        public readonly int Index = index;
        public readonly string? Key = key;
        public Dictionary<int, Draft>? Items;
        public Dictionary<string, Draft>? Fields;

        public JqValue Build() => Array is not null ? JqValue.FromArray(Array.ToArray()) : JqValue.FromObject(Object!.ToObject());
    }

    // A slot of the tree: the root (Parent null), an element of an array draft or a field of an object draft.
    readonly record struct Slot(Draft? Parent, int Index, string? Key);

    JqValue root;
    Draft? rootDraft;

    public WorkingTree(JqValue root) => this.root = root;

    /// <summary>The value of the whole tree; the tree holds no draft afterwards.</summary>
    public JqValue Freeze() => FreezeAt(default);

    /// <summary>
    /// <c>getpath(keys)</c> on the current value. The value returned is frozen in the tree, so the
    /// caller may keep it: the tree never changes it.
    /// </summary>
    public bool TryGet(JqArray keys, out JqValue value, out JqValue error)
    {
        var slot = default(Slot);
        for (var i = 0; i < keys.Count; i++)
        {
            if (DraftAt(slot) is { } draft && TryRead(draft, keys[i], out var next, out var missing))
            {
                if (!missing)
                {
                    slot = next;
                    continue;
                }

                // An array element out of range: null, which the rest of the path indexes.
                return TryIndex(JqValue.Null, keys, i + 1, out value, out error);
            }

            return TryIndex(FreezeAt(slot), keys, i, out value, out error);
        }

        value = FreezeAt(slot);
        error = default;
        return true;
    }

    /// <summary><c>setpath(keys; value)</c> on the current value.</summary>
    public bool TrySet(JqArray keys, JqValue value, out JqValue error)
    {
        error = default;
        var slot = default(Slot);
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            var draft = DraftAt(slot);
            if (draft is not null && TryWrite(draft, key, out var next))
            {
                slot = next;
                continue;
            }

            var current = draft is null ? ValueAt(slot) : FreezeAt(slot);
            if (TryOpen(current, key, slot, out draft) && TryWrite(draft, key, out next))
            {
                slot = next;
                continue;
            }

            // A key the drafts do not handle: setpath with the rest of the path, on the value here.
            if (!PathNatives.TrySetPath(current, JqValue.FromArray(keys.Slice(i, keys.Count)), value, out var result, out error))
                return false;
            Store(slot, result);
            return true;
        }

        Store(slot, value);
        return true;
    }

    // keys[from..] indexed from value, as getpath does.
    static bool TryIndex(JqValue value, JqArray keys, int from, out JqValue result, out JqValue error)
    {
        error = default;
        result = value;
        for (var i = from; i < keys.Count; i++)
        {
            if (!ValueOps.TryIndex(result, keys[i], out result, out error))
                return false;
        }

        return true;
    }

    // The slot a read goes to next: a field (present or not) or an element in range; an element out
    // of range is missing (the value there is null). False for keys a draft does not read itself.
    static bool TryRead(Draft draft, JqValue key, out Slot next, out bool missing)
    {
        next = default;
        missing = false;
        if (draft.Object is not null)
        {
            next = new Slot(draft, 0, key.Kind == JqKind.String ? key.AsString() : null);
            return key.Kind == JqKind.String;
        }

        if (key.Kind != JqKind.Number)
            return false;
        var number = key.AsNumber();
        var index = double.IsNaN(number) ? -1 : Math.Truncate(number);
        if (index < 0)
            index += draft.Array!.Count;
        missing = double.IsNaN(number) || index < 0 || index >= draft.Array!.Count;
        next = new Slot(draft, missing ? 0 : (int)index, null);
        return true;
    }

    // The slot a write goes to next, when setpath accepts the key for this container without error.
    static bool TryWrite(Draft draft, JqValue key, out Slot next)
    {
        next = default;
        if (draft.Object is not null)
        {
            next = new Slot(draft, 0, key.Kind == JqKind.String ? key.AsString() : null);
            return key.Kind == JqKind.String;
        }

        if (!TryArrayIndex(key, draft.Array!.Count, out var index))
            return false;
        next = new Slot(draft, index, null);
        return true;
    }

    // setpath's index of an array of the given length: not NaN, negative counted from the end, within the limit.
    static bool TryArrayIndex(JqValue key, int length, out int index)
    {
        index = 0;
        if (key.Kind != JqKind.Number || double.IsNaN(key.AsNumber()))
            return false;
        var i = Math.Truncate(key.AsNumber());
        if (i < 0)
            i += length;
        if (i < 0 || i > MaxIndex)
            return false;
        index = (int)i;
        return true;
    }

    // Copies one level of the value at a slot into a draft there, when setpath would write into it
    // with this key (null becomes the container the key asks for).
    bool TryOpen(JqValue value, JqValue key, Slot slot, out Draft draft)
    {
        draft = null!;
        var kind = value.Kind;
        if (key.Kind == JqKind.String && kind is JqKind.Object or JqKind.Null)
            draft = new Draft(null, new JqObjectBuilder(kind == JqKind.Null ? JqObject.Empty : value.AsObject()), slot.Parent, slot.Index, slot.Key);
        else if (key.Kind == JqKind.Number && kind is JqKind.Array or JqKind.Null && TryArrayIndex(key, kind == JqKind.Null ? 0 : value.AsArray().Count, out _))
            draft = new Draft(new JqArrayBuilder(kind == JqKind.Null ? JqArray.Empty : value.AsArray()), null, slot.Parent, slot.Index, slot.Key);
        else
            return false;

        Attach(slot, draft);
        return true;
    }

    Draft? DraftAt(Slot slot)
    {
        if (slot.Parent is null)
            return rootDraft;
        if (slot.Key is not null)
            return slot.Parent.Fields?.GetValueOrDefault(slot.Key);
        return slot.Parent.Items?.GetValueOrDefault(slot.Index);
    }

    // The value at a slot that holds no draft.
    JqValue ValueAt(Slot slot)
    {
        if (slot.Parent is null)
            return root;
        if (slot.Key is not null)
            return slot.Parent.Object!.TryGetValue(slot.Key, out var field) ? field : JqValue.Null;
        return slot.Index < slot.Parent.Array!.Count ? slot.Parent.Array[slot.Index] : JqValue.Null;
    }

    // Puts a value at a slot, replacing whatever was there (a value or a draft).
    void Store(Slot slot, JqValue value)
    {
        if (slot.Parent is null)
        {
            (root, rootDraft) = (value, null);
        }
        else if (slot.Key is not null)
        {
            slot.Parent.Object!.Set(slot.Key, value);
            slot.Parent.Fields?.Remove(slot.Key);
        }
        else
        {
            slot.Parent.Array!.Set(slot.Index, value);
            slot.Parent.Items?.Remove(slot.Index);
        }
    }

    // Hangs a draft at a slot. Its place in the parent is taken now (a new key goes last now,
    // missing elements become null now), as setpath would; the value is written when it freezes.
    void Attach(Slot slot, Draft draft)
    {
        Store(slot, JqValue.Null);
        if (slot.Parent is null)
            rootDraft = draft;
        else if (slot.Key is not null)
            (slot.Parent.Fields ??= [])[slot.Key] = draft;
        else
            (slot.Parent.Items ??= [])[slot.Index] = draft;
    }

    // The value at a slot, freezing the drafts there first: children before parents, each written
    // into its parent's builder, the top one stored at the slot.
    JqValue FreezeAt(Slot slot)
    {
        if (DraftAt(slot) is not { } top)
            return ValueAt(slot);

        var order = new List<Draft>();
        var pending = new Stack<Draft>();
        pending.Push(top);
        while (pending.Count > 0)
        {
            var draft = pending.Pop();
            order.Add(draft);
            foreach (var child in draft.Items?.Values ?? (IEnumerable<Draft>)[])
                pending.Push(child);
            foreach (var child in draft.Fields?.Values ?? (IEnumerable<Draft>)[])
                pending.Push(child);
        }

        for (var i = order.Count - 1; i > 0; i--)
        {
            var draft = order[i];
            var value = draft.Build();
            if (draft.Key is not null)
                draft.Parent!.Object!.Set(draft.Key, value);
            else
                draft.Parent!.Array!.Set(draft.Index, value);
        }

        var frozen = top.Build();
        Store(slot, frozen);
        return frozen;
    }
}
