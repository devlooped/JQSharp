using System.Text;

namespace Devlooped.Core.Values;

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
    /// their sorted key lists, then by their values in that key order. NaN sorts as null would:
    /// below every number, and <c>nan &lt; nan</c> (Compare(nan, nan) is -1).
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
                    return -1;
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

        return Fail(ValueErrors.CannotBe(a, b, ValueErrors.Added), out result, out error);
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

        return Fail(ValueErrors.CannotBe(a, b, ValueErrors.Subtracted), out result, out error);
    }

    /// <summary>
    /// jq's <c>*</c>: numbers multiply; a string and a number (either order) repeat the string,
    /// the count truncated, and a count below 1 (or NaN) gives null; objects merge recursively.
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

        return Fail(ValueErrors.CannotBe(a, b, ValueErrors.Multiplied), out result, out error);
    }

    static bool TryRepeat(string text, double times, out JqValue result, out JqValue error)
    {
        error = default;
        var count = double.IsNaN(times) || times < 1 ? 0 : times >= int.MaxValue ? int.MaxValue : (int)times;
        if (count == 0)
        {
            result = JqValue.Null;
            return true;
        }

        if ((long)text.Length * count > MaxStringLength)
            return Fail(ValueErrors.RepeatTooLong, out result, out error);

        var builder = new StringBuilder(text.Length * count);
        for (var i = 0; i < count; i++)
            builder.Append(text);
        result = JqValue.FromString(builder.ToString());
        return true;
    }

    // `*` on objects: keys of b win, except that two objects under the same key merge recursively.
    static JqObject DeepMerge(JqObject a, JqObject b)
    {
        var builder = new JqObjectBuilder(a, b.Count);
        for (var i = 0; i < b.Count; i++)
        {
            var key = b.KeyAt(i);
            var value = b.ValueAt(i);
            if (value.Kind == JqKind.Object && builder.TryGetValue(key, out var existing) && existing.Kind == JqKind.Object)
                value = JqValue.FromObject(DeepMerge(existing.AsObject(), value.AsObject()));
            builder.Set(key, value);
        }

        return builder.ToObject();
    }

    /// <summary>jq's <c>/</c>: numbers divide (a zero divisor is an error); strings split on the right-hand string.</summary>
    public static bool TryDivide(JqValue a, JqValue b, out JqValue result, out JqValue error)
    {
        error = default;
        var kind = a.Kind;
        if (kind == JqKind.Number && b.Kind == JqKind.Number)
        {
            var divisor = b.AsNumber();
            if (divisor == 0)
                return Fail(ValueErrors.DivisorIsZero(a, b), out result, out error);
            result = JqValue.FromNumber(a.AsNumber() / divisor);
            return true;
        }

        if (kind == JqKind.String && b.Kind == JqKind.String)
        {
            result = JqValue.FromArray(Split(a.AsString(), b.AsString()));
            return true;
        }

        return Fail(ValueErrors.CannotBe(a, b, ValueErrors.Divided), out result, out error);
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
            return Fail(ValueErrors.CannotBe(a, b, ValueErrors.Divided), out result, out error);

        var x = a.AsNumber();
        var y = b.AsNumber();
        if (double.IsNaN(x) || double.IsNaN(y))
        {
            result = JqValue.FromNumber(double.NaN);
            return true;
        }

        var divisor = ToInteger(y);
        if (divisor == 0)
            return Fail(ValueErrors.DivisorIsZero(a, b), out result, out error);

        // -1 divides everything, and long.MinValue % -1 overflows.
        result = JqValue.FromNumber(divisor == -1 ? 0 : ToInteger(x) % divisor);
        return true;
    }

    // C's (intmax_t) cast as jq 1.8 guards it: truncation, saturating at the 64-bit limits.
    static long ToInteger(double value)
        => value <= long.MinValue ? long.MinValue : value >= long.MaxValue ? long.MaxValue : (long)value;

    /// <summary>
    /// jq's <c>.[start:end]</c> on an array, a string (by code point) or null (gives null). A null
    /// bound is the start or the end; a negative one counts from the end; the start rounds down and
    /// the end up; bounds are clamped.
    /// </summary>
    public static bool TrySlice(JqValue input, JqValue start, JqValue end, out JqValue result, out JqValue error)
    {
        error = default;
        var kind = input.Kind;
        if (kind == JqKind.Null)
        {
            result = JqValue.Null;
            return true;
        }

        if (kind is not JqKind.Array and not JqKind.String)
            return Fail(ValueErrors.CannotIndex(input, "object"), out result, out error);
        if (start.Kind is not JqKind.Null and not JqKind.Number || end.Kind is not JqKind.Null and not JqKind.Number)
            return Fail(ValueErrors.SliceIndicesNotNumbers, out result, out error);

        var text = kind == JqKind.String ? input.AsString() : null;
        var length = text is null ? input.AsArray().Count : CodePoints.Length(text);
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
        var first = (int)Math.Floor(Math.Clamp(from, 0, length));
        var last = (int)Math.Ceiling(Math.Clamp(to, first, length));

        result = text is null
            ? JqValue.FromArray(input.AsArray().Slice(first, last))
            : JqValue.FromString(CodePoints.Substring(text, first, last));
        return true;
    }

    /// <summary>jq's <c>implode</c>: an array of code points (numbers, truncated) to a string; a value that is not a Unicode scalar value is an error.</summary>
    public static bool TryImplode(JqValue input, out JqValue result, out JqValue error)
    {
        error = default;
        if (input.Kind != JqKind.Array)
            return Fail(ValueErrors.CannotImplode(input), out result, out error);

        var codes = input.AsArray();
        var builder = new StringBuilder(codes.Count);
        for (var i = 0; i < codes.Count; i++)
        {
            var code = codes[i];
            if (code.Kind != JqKind.Number || double.IsNaN(code.AsNumber()))
                return Fail(ValueErrors.CodepointNotNumeric(input), out result, out error);
            var number = code.AsNumber();
            if (!(number >= 0 && number < CodePoints.Max + 1) || !CodePoints.IsScalar((int)number))
                return Fail(ValueErrors.InvalidCodepoint(input), out result, out error);
            CodePoints.Append(builder, (int)number);
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
