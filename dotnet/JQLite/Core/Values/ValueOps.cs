using System.Text;

namespace JQLite.Core.Values;

/// <summary>
/// The one implementation of equality, ordering, arithmetic, slicing and sorting of values, with
/// jq 1.8 semantics.
/// </summary>
/// <remarks>
/// An operation that can fail in jq returns <see langword="false"/> and gives the jqlite error
/// value (the message string) in <c>error</c>; on success it returns <see langword="true"/> and
/// gives <c>result</c>. Nothing here throws as part of jqlite semantics.
/// </remarks>
static class ValueOps
{
    // The longest string a repeat may build (.NET's string length limit).
    const int MaxStringLength = 0x3FFFFFDF;

    /// <summary>
    /// jq's <c>==</c>: same kind and same content. Numbers compare as doubles, so <c>nan == nan</c>
    /// is false and so is <c>[nan] == [nan]</c>; objects are equal whatever their key order.
    /// </summary>
    public static bool AreEqual(JqValue a, JqValue b) => Walk(a, b, equality: true) == 0;

    /// <summary>
    /// jq's total order; returns -1, 0 or 1. Kinds first: null &lt; false &lt; true &lt; numbers &lt;
    /// strings &lt; arrays &lt; objects. Strings by code point; arrays lexicographically; objects by
    /// their sorted key lists, then by their values in that key order. NaN is below every other
    /// number and equal to NaN, so the order is total (jq 1.8 has <c>nan &lt; nan</c>; spec §13).
    /// </summary>
    public static int Compare(JqValue a, JqValue b) => Walk(a, b, equality: false);

    // A pair of containers being walked: children 0 to Count - 1 are compared in turn, then Tail
    // (the length comparison of two arrays) decides.
    struct Frame
    {
        public JqValue A;
        public JqValue B;
        public int Count;
        public int Index;
        public int Tail;
        public IReadOnlyList<string>? Keys;
    }

    // The walk's stack, kept per thread so that comparing allocates nothing (a local .NET
    // optimization: a port can allocate a fresh stack per call). Walk never re-enters itself.
    [ThreadStatic]
    static List<Frame>? walkStack;

    /// <summary>
    /// Compares (equality false) or tests equality (equality true, any non-zero result means
    /// "different") without recursion: the containers being compared are on an explicit stack, so
    /// nesting depth is not limited by the host stack.
    /// </summary>
    static int Walk(JqValue a, JqValue b, bool equality)
    {
        var c = Shallow(a, b, equality, out var frame);
        if (c != 0 || frame.Count == 0)
            return c != 0 ? c : frame.Tail;

        var stack = walkStack ??= [];
        stack.Clear();
        stack.Add(frame);
        if (!Child(frame, equality, out a, out b))
            return 1;

        while (true)
        {
            c = Shallow(a, b, equality, out frame);
            if (c != 0)
                return Done(stack, c);
            if (frame.Count > 0)
            {
                stack.Add(frame);
                if (!Child(frame, equality, out a, out b))
                    return Done(stack, 1);
                continue;
            }

            if (frame.Tail != 0)
                return Done(stack, frame.Tail);

            // The pair is equal: go on with the next pair, leaving the containers that end.
            while (true)
            {
                if (stack.Count == 0)
                    return 0;

                var top = stack[^1];
                top.Index++;
                if (top.Index < top.Count)
                {
                    stack[^1] = top;
                    if (!Child(top, equality, out a, out b))
                        return Done(stack, 1);
                    break;
                }

                stack.RemoveAt(stack.Count - 1);
                if (top.Tail != 0)
                    return Done(stack, top.Tail);
            }
        }
    }

    static int Done(List<Frame> stack, int result)
    {
        stack.Clear();
        return result;
    }

    // Compares what can be compared without looking at children. A non-zero result decides. With
    // zero, frame.Count > 0 means the children must be compared, and frame.Tail is the result
    // once they are all equal.
    static int Shallow(JqValue a, JqValue b, bool equality, out Frame frame)
    {
        frame = default;
        var kind = a.Kind;
        var other = b.Kind;
        if (kind != other)
            return kind < other ? -1 : 1;

        switch (kind)
        {
            case JqKind.Number:
                var x = a.AsNumber();
                var y = b.AsNumber();
                if (equality)
                    return x == y ? 0 : 1;
                if (double.IsNaN(x))
                    return double.IsNaN(y) ? 0 : -1;
                if (double.IsNaN(y))
                    return 1;
                return x < y ? -1 : x == y ? 0 : 1;
            case JqKind.String:
                return equality
                    ? string.Equals(a.AsString(), b.AsString(), StringComparison.Ordinal) ? 0 : 1
                    : CodePoints.Compare(a.AsString(), b.AsString());
            case JqKind.Array:
                var p = a.AsArray().Count;
                var q = b.AsArray().Count;
                if (equality && p != q)
                    return 1;
                frame = new Frame { A = a, B = b, Count = Math.Min(p, q), Tail = p == q ? 0 : p < q ? -1 : 1 };
                return 0;
            case JqKind.Object:
                var m = a.AsObject();
                var n = b.AsObject();
                if (equality)
                {
                    // Same size, then each key of a found in b with an equal value: no sorting.
                    if (m.Count != n.Count)
                        return 1;
                    frame = new Frame { A = a, B = b, Count = m.Count };
                    return 0;
                }

                var keys = m.SortedKeys();
                var others = n.SortedKeys();
                var length = Math.Min(keys.Count, others.Count);
                for (var i = 0; i < length; i++)
                {
                    var c = CodePoints.Compare(keys[i], others[i]);
                    if (c != 0)
                        return c;
                }

                if (keys.Count != others.Count)
                    return keys.Count < others.Count ? -1 : 1;
                frame = new Frame { A = a, B = b, Count = keys.Count, Keys = keys };
                return 0;
            default:
                return 0;
        }
    }

    // The children at frame.Index; false when b lacks a key of a (equality of objects only).
    static bool Child(Frame frame, bool equality, out JqValue a, out JqValue b)
    {
        if (frame.A.Kind == JqKind.Array)
        {
            a = frame.A.AsArray()[frame.Index];
            b = frame.B.AsArray()[frame.Index];
            return true;
        }

        var x = frame.A.AsObject();
        var y = frame.B.AsObject();
        if (equality)
        {
            a = x.ValueAt(frame.Index);
            return y.TryGetValue(x.KeyAt(frame.Index), out b);
        }

        var key = frame.Keys![frame.Index];
        x.TryGetValue(key, out a);
        y.TryGetValue(key, out b);
        return true;
    }

    /// <summary>jq's <c>+</c>: null is the identity; numbers add; strings and arrays concatenate; objects merge (right wins, insertion order kept).</summary>
    public static bool TryAdd(JqValue a, JqValue b, out JqValue result, out JqValue error)
    {
        error = default;
        var kind = a.Kind;
        if (kind == JqKind.Null)
        {
            result = b;
            return true;
        }

        if (b.Kind == JqKind.Null)
        {
            result = a;
            return true;
        }

        if (kind == b.Kind)
        {
            switch (kind)
            {
                case JqKind.Number:
                    result = JqValue.FromNumber(a.AsNumber() + b.AsNumber());
                    return true;
                case JqKind.String:
                    result = JqValue.FromString(string.Concat(a.AsString(), b.AsString()));
                    return true;
                case JqKind.Array:
                    result = JqValue.FromArray(a.AsArray().Concat(b.AsArray()));
                    return true;
                case JqKind.Object:
                    result = JqValue.FromObject(a.AsObject().Merge(b.AsObject()));
                    return true;
            }
        }

        return Fail(Errors.CannotBe(a, b, Errors.Added), out result, out error);
    }

    /// <summary>jq's <c>-</c>: numbers subtract; arrays drop every element equal to one of the right-hand array.</summary>
    public static bool TrySubtract(JqValue a, JqValue b, out JqValue result, out JqValue error)
    {
        error = default;
        var kind = a.Kind;
        if (kind == JqKind.Number && b.Kind == JqKind.Number)
        {
            result = JqValue.FromNumber(a.AsNumber() - b.AsNumber());
            return true;
        }

        if (kind == JqKind.Array && b.Kind == JqKind.Array)
        {
            var items = a.AsArray();
            var remove = b.AsArray();
            var kept = JqArray.Empty;
            for (var i = 0; i < items.Count; i++)
            {
                var found = false;
                for (var j = 0; j < remove.Count && !found; j++)
                    found = AreEqual(items[i], remove[j]);
                if (!found)
                    kept = kept.Append(items[i]);
            }

            result = JqValue.FromArray(kept);
            return true;
        }

        return Fail(Errors.CannotBe(a, b, Errors.Subtracted), out result, out error);
    }

    /// <summary>jq's unary <c>-</c>: IEEE negation of a number (<c>0 | -.</c> is -0).</summary>
    public static bool TryNegate(JqValue value, out JqValue result, out JqValue error)
    {
        error = default;
        if (value.Kind != JqKind.Number)
            return Fail(Errors.CannotNegate(value), out result, out error);
        result = JqValue.FromNumber(-value.AsNumber());
        return true;
    }

    /// <summary>
    /// jq's <c>*</c>: numbers multiply; a string and a number (either order) repeat the string;
    /// objects merge recursively.
    /// </summary>
    public static bool TryMultiply(JqValue a, JqValue b, out JqValue result, out JqValue error)
    {
        error = default;
        var kind = a.Kind;
        var other = b.Kind;
        if (kind == JqKind.Number && other == JqKind.Number)
        {
            result = JqValue.FromNumber(a.AsNumber() * b.AsNumber());
            return true;
        }

        if ((kind == JqKind.String && other == JqKind.Number) || (kind == JqKind.Number && other == JqKind.String))
        {
            var text = kind == JqKind.String ? a.AsString() : b.AsString();
            var times = kind == JqKind.String ? b.AsNumber() : a.AsNumber();
            return TryRepeat(text, times, out result, out error);
        }

        if (kind == JqKind.Object && other == JqKind.Object)
        {
            result = JqValue.FromObject(DeepMerge(a.AsObject(), b.AsObject()));
            return true;
        }

        return Fail(Errors.CannotBe(a, b, Errors.Multiplied), out result, out error);
    }

    // s * n: null for a NaN or negative n; otherwise s repeated trunc(n) times ("" below 1).
    static bool TryRepeat(string text, double times, out JqValue result, out JqValue error)
    {
        error = default;
        if (double.IsNaN(times) || times < 0)
        {
            result = JqValue.Null;
            return true;
        }

        var count = times >= int.MaxValue ? int.MaxValue : (int)times;
        if (count == 0 || text.Length == 0)
        {
            result = JqValue.FromString("");
            return true;
        }

        // The spec's limit is 2^31 - 1 UTF-8 bytes; .NET's string length limit may be hit first.
        if ((long)Encoding.UTF8.GetByteCount(text) * count > int.MaxValue || (long)text.Length * count > MaxStringLength)
            return Fail(Errors.RepeatTooLong, out result, out error);

        var builder = new StringBuilder(text.Length * count);
        for (var i = 0; i < count; i++)
            builder.Append(text);
        result = JqValue.FromString(builder.ToString());
        return true;
    }

    // `*` on objects: keys of b win, except that two objects under the same key merge recursively.
    // a * b on objects: like a + b, except that a key whose two values are objects gets their merge.
    // The objects being merged are on an explicit stack, so nesting depth is not limited by the host stack.
    static JqObject DeepMerge(JqObject a, JqObject b)
    {
        var stack = new List<Merge> { new(a, b) };
        while (true)
        {
            var top = stack[^1];
            if (top.Next < top.Right.Count)
            {
                var key = top.Right.KeyAt(top.Next);
                var value = top.Right.ValueAt(top.Next++);
                if (value.Kind == JqKind.Object && top.Result.TryGetValue(key, out var existing) && existing.Kind == JqKind.Object)
                {
                    top.Key = key;
                    stack.Add(new Merge(existing.AsObject(), value.AsObject()));
                }
                else
                {
                    top.Result.Set(key, value);
                }

                continue;
            }

            var merged = top.Result.ToObject();
            stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 0)
                return merged;
            stack[^1].Result.Set(stack[^1].Key, JqValue.FromObject(merged));
        }
    }

    // One pair of objects being merged: the result so far, the right side's next key, and the key
    // whose values are being merged below.
    sealed class Merge(JqObject left, JqObject right)
    {
        public readonly JqObjectBuilder Result = new(left, right.Count);
        public readonly JqObject Right = right;
        public int Next;
        public string Key = "";
    }

    /// <summary>jq's <c>/</c>: numbers divide (a zero divisor is an error, <c>0/0</c> too); strings split on the right-hand string.</summary>
    public static bool TryDivide(JqValue a, JqValue b, out JqValue result, out JqValue error)
    {
        error = default;
        var kind = a.Kind;
        if (kind == JqKind.Number && b.Kind == JqKind.Number)
        {
            var divisor = b.AsNumber();
            if (divisor == 0)
                return Fail(Errors.DivisorIsZero(a, b, Errors.Divided), out result, out error);
            result = JqValue.FromNumber(a.AsNumber() / divisor);
            return true;
        }

        if (kind == JqKind.String && b.Kind == JqKind.String)
        {
            result = JqValue.FromArray(Split(a.AsString(), b.AsString()));
            return true;
        }

        return Fail(Errors.CannotBe(a, b, Errors.Divided), out result, out error);
    }

    /// <summary>
    /// <c>split/1</c> and <c>/</c> on strings: the empty string gives []; an empty separator splits
    /// into code points.
    /// </summary>
    public static JqArray Split(string text, string separator)
    {
        var parts = JqArray.Empty;
        if (text.Length == 0)
            return parts;

        if (separator.Length == 0)
        {
            for (var i = 0; i < text.Length; i++)
            {
                var units = char.IsHighSurrogate(text[i]) ? 2 : 1;
                parts = parts.Append(JqValue.FromString(text.Substring(i, units)));
                i += units - 1;
            }

            return parts;
        }

        var start = 0;
        int found;
        while ((found = text.IndexOf(separator, start, StringComparison.Ordinal)) >= 0)
        {
            parts = parts.Append(JqValue.FromString(text[start..found]));
            start = found + separator.Length;
        }

        return parts.Append(JqValue.FromString(text[start..]));
    }

    /// <summary>
    /// jq's <c>%</c>: both numbers truncate to integers (saturating at the 64-bit range), the sign
    /// follows the dividend; NaN gives NaN; a divisor that truncates to 0 is an error.
    /// </summary>
    public static bool TryModulo(JqValue a, JqValue b, out JqValue result, out JqValue error)
    {
        error = default;
        if (a.Kind != JqKind.Number || b.Kind != JqKind.Number)
            return Fail(Errors.CannotBe(a, b, Errors.Remainder), out result, out error);

        var x = a.AsNumber();
        var y = b.AsNumber();
        if (double.IsNaN(x) || double.IsNaN(y))
        {
            result = JqValue.FromNumber(double.NaN);
            return true;
        }

        var divisor = ToInteger(y);
        if (divisor == 0)
            return Fail(Errors.DivisorIsZero(a, b, Errors.Remainder), out result, out error);

        // -1 divides everything, and long.MinValue % -1 overflows.
        result = JqValue.FromNumber(divisor == -1 ? 0 : ToInteger(x) % divisor);
        return true;
    }

    // Truncation toward zero, clamped to [-2^63, 2^63 - 1] (±Infinity clamp to the ends).
    static long ToInteger(double value)
        => value <= long.MinValue ? long.MinValue : value >= long.MaxValue ? long.MaxValue : (long)value;

    /// <summary>
    /// <c>.[key]</c> (§7.6): an object by a string (absent: null), an array by a number (NaN and out
    /// of range: null; negative counts from the end) or by an array (the indices where it occurs), an
    /// array or a string by a slice object, null by a string, a number or an object (null);
    /// anything else is <c>Cannot index T with T (D)</c>.
    /// </summary>
    public static bool TryIndex(JqValue target, JqValue key, out JqValue result, out JqValue error)
    {
        error = default;
        var kind = target.Kind;
        switch (key.Kind)
        {
            case JqKind.String when kind == JqKind.Object:
                result = target.AsObject().TryGetValue(key.AsString(), out var value) ? value : JqValue.Null;
                return true;
            case JqKind.Number when kind == JqKind.Array:
                result = Element(target.AsArray(), key.AsNumber());
                return true;
            case JqKind.Array when kind == JqKind.Array:
                result = JqValue.FromArray(Indices(target.AsArray(), key.AsArray()));
                return true;
            case JqKind.Object when kind is JqKind.Array or JqKind.String or JqKind.Null:
                return TryIndexSlice(target, key, out result, out error);
            case JqKind.String or JqKind.Number when kind == JqKind.Null:
                result = JqValue.Null;
                return true;
            default:
                return Fail(Errors.CannotIndex(target, key), out result, out error);
        }
    }

    // .[n] on an array: NaN and out of range give null, a negative index counts from the end.
    static JqValue Element(JqArray array, double index)
    {
        if (double.IsNaN(index))
            return JqValue.Null;
        var i = Math.Truncate(index);
        if (i < 0)
            i += array.Count;
        return i >= 0 && i < array.Count ? array[(int)i] : JqValue.Null;
    }

    // The positions at which needle occurs in items as a contiguous sub-array; none for an empty needle.
    static JqArray Indices(JqArray items, JqArray needle)
    {
        var found = new List<JqValue>();
        for (var start = 0; needle.Count > 0 && start + needle.Count <= items.Count; start++)
        {
            var i = 0;
            while (i < needle.Count && AreEqual(items[start + i], needle[i]))
                i++;
            if (i == needle.Count)
                found.Add(JqValue.FromNumber(start));
        }

        return JqArray.FromOwned([.. found]);
    }

    /// <summary>
    /// <c>.[start:end]</c> (§7.6) on an array, a string (by code point) or null (gives null). A
    /// null or NaN start is 0, a null or NaN end the length; a negative bound counts from the end;
    /// both are clamped, then the start rounds down and the end up.
    /// </summary>
    public static bool TrySlice(JqValue input, JqValue start, JqValue end, out JqValue result, out JqValue error)
    {
        if (input.Kind is not JqKind.Null and not JqKind.Array and not JqKind.String)
        {
            // The key in the message is the slice object that `.[s:e]` stands for.
            var key = JqObject.Empty.Set("start", start).Set("end", end);
            return Fail(Errors.CannotIndex(input, JqValue.FromObject(key)), out result, out error);
        }

        return TrySliceBounds(input, start, end, out result, out error);
    }

    /// <summary>
    /// <c>.[k]</c> with an object key: on an array or a string the key is a slice when it has both
    /// <c>start</c> and <c>end</c> (other keys are ignored, as in jq), else the error
    /// <c>Array/string slice indices must be integers</c>; null input gives null whatever the key;
    /// any other input is <c>Cannot index T with object (D)</c>.
    /// </summary>
    public static bool TryIndexSlice(JqValue input, JqValue key, out JqValue result, out JqValue error)
    {
        var kind = input.Kind;
        if (kind == JqKind.Null)
            return TrySliceBounds(input, default, default, out result, out error);
        if (kind is not JqKind.Array and not JqKind.String)
            return Fail(Errors.CannotIndex(input, key), out result, out error);

        var slice = key.AsObject();
        if (!slice.TryGetValue("start", out var start) || !slice.TryGetValue("end", out var end))
            return Fail(Errors.SliceIndicesNotIntegers, out result, out error);
        return TrySliceBounds(input, start, end, out result, out error);
    }

    // Slices an array, a string or null (gives null).
    static bool TrySliceBounds(JqValue input, JqValue start, JqValue end, out JqValue result, out JqValue error)
    {
        var kind = input.Kind;
        if (kind == JqKind.Null)
        {
            error = default;
            result = JqValue.Null;
            return true;
        }

        var text = kind == JqKind.String ? input.AsString() : null;
        var length = text is null ? input.AsArray().Count : CodePoints.Length(text);
        if (!TrySliceRange(length, start, end, out var first, out var last, out error))
        {
            result = default;
            return false;
        }

        result = text is null
            ? JqValue.FromArray(input.AsArray().Slice(first, last))
            : JqValue.FromString(CodePoints.Substring(text, first, last));
        return true;
    }

    /// <summary>
    /// The positions [<paramref name="first"/>, <paramref name="last"/>) that the bounds of a slice
    /// select in a sequence of <paramref name="length"/> items (§7.6): null or NaN start is 0, null or
    /// NaN end the length, negative bounds count from the end, both are clamped, the start rounds
    /// down and the end up. Bounds that are not numbers or null: <c>Array/string slice indices must be integers</c>.
    /// </summary>
    public static bool TrySliceRange(int length, JqValue start, JqValue end, out int first, out int last, out JqValue error)
    {
        first = last = 0;
        error = default;
        if (start.Kind is not JqKind.Null and not JqKind.Number || end.Kind is not JqKind.Null and not JqKind.Number)
        {
            error = Errors.SliceIndicesNotIntegers;
            return false;
        }

        var from = start.Kind == JqKind.Null ? 0 : start.AsNumber();
        var to = end.Kind == JqKind.Null ? length : end.AsNumber();
        if (double.IsNaN(from))
            from = 0;
        if (double.IsNaN(to))
            to = length;
        if (from < 0)
            from += length;
        if (to < 0)
            to += length;
        first = (int)Math.Floor(Math.Clamp(from, 0, length));
        last = (int)Math.Ceiling(Math.Clamp(to, first, length));
        return true;
    }

    /// <summary>jq's <c>explode</c>: the code points of a string.</summary>
    public static bool TryExplode(JqValue input, out JqValue result, out JqValue error)
    {
        error = default;
        if (input.Kind != JqKind.String)
            return Fail(Errors.ExplodeNotString, out result, out error);
        result = JqValue.FromArray(CodePoints.Explode(input.AsString()));
        return true;
    }

    /// <summary>
    /// jq's <c>implode</c>: an array of numbers to the string of the code points <c>trunc(n)</c>;
    /// one outside [0, 0x10FFFF] or a surrogate becomes U+FFFD (<c>-0.5</c> truncates to U+0000).
    /// A non-number or NaN element is an error that describes the element.
    /// </summary>
    public static bool TryImplode(JqValue input, out JqValue result, out JqValue error)
    {
        error = default;
        if (input.Kind != JqKind.Array)
            return Fail(Errors.ImplodeNotArray, out result, out error);

        var codes = input.AsArray();
        var builder = new StringBuilder(codes.Count);
        for (var i = 0; i < codes.Count; i++)
        {
            var code = codes[i];
            if (code.Kind != JqKind.Number || double.IsNaN(code.AsNumber()))
                return Fail(Errors.CodepointNotNumeric(code), out result, out error);
            var number = Math.Truncate(code.AsNumber());
            var scalar = number >= 0 && number <= CodePoints.Max && CodePoints.IsScalar((int)number);
            CodePoints.Append(builder, scalar ? (int)number : CodePoints.Replacement);
        }

        result = JqValue.FromString(builder.ToString());
        return true;
    }

    /// <summary>A stably sorted copy of <paramref name="items"/> (jq's <c>sort</c>).</summary>
    public static JqArray Sort(JqArray items)
    {
        var order = SortOrder(items);
        var sorted = new JqValue[order.Length];
        for (var i = 0; i < order.Length; i++)
            sorted[i] = items[order[i]];
        return JqArray.FromOwned(sorted);
    }

    /// <summary>
    /// The positions of <paramref name="keys"/> in sorted order, equal keys in their original
    /// order (a stable sort, for <c>sort_by</c>, <c>group_by</c> and <c>unique_by</c>): a
    /// bottom-up merge sort.
    /// </summary>
    public static int[] SortOrder(JqArray keys)
    {
        var n = keys.Count;
        var order = new int[n];
        for (var i = 0; i < n; i++)
            order[i] = i;
        var merged = new int[n];
        for (var width = 1; width < n; width *= 2)
        {
            for (var low = 0; low < n; low += 2 * width)
            {
                var middle = Math.Min(low + width, n);
                var high = Math.Min(low + 2 * width, n);
                int left = low, right = middle, target = low;
                while (left < middle && right < high)
                {
                    // Take the left one unless the right one is strictly smaller: stable.
                    merged[target++] = Compare(keys[order[left]], keys[order[right]]) <= 0 ? order[left++] : order[right++];
                }

                while (left < middle)
                    merged[target++] = order[left++];
                while (right < high)
                    merged[target++] = order[right++];
            }

            (order, merged) = (merged, order);
        }

        return order;
    }

    static bool Fail(JqValue message, out JqValue result, out JqValue error)
    {
        result = default;
        error = message;
        return false;
    }
}