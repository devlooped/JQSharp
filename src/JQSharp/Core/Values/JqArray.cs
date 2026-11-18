namespace Devlooped.Core.Values;

/// <summary>An immutable sequence of values.</summary>
/// <remarks>
/// <para>
/// Storage is a shared buffer plus a window (<c>start</c>, <c>count</c>) on it, as a Go slice.
/// A slot of a buffer is written once, before any array that covers it exists, and never again,
/// so every <see cref="JqArray"/> is immutable however many arrays share its buffer.
/// </para>
/// <para>
/// Appending (<see cref="Append"/>, <see cref="Concat"/>) to the array whose window ends where the
/// buffer's used part ends (its latest version) claims the next free slots with one
/// compare-and-swap on <c>Used</c> and writes there: amortized O(1), so
/// <c>reduce range(n) as $i ([]; . + [$i])</c> is O(n). When the slots are already taken (an older
/// version, or another thread won the claim) or the buffer is full, the window is copied to a new
/// buffer with twice the room. Replacing one element copies one level. A slice shares the buffer
/// and keeps all of it alive, as in Go.
/// </para>
/// </remarks>
sealed class JqArray
{
    sealed class Buffer(JqValue[] items, int used)
    {
        public readonly JqValue[] Items = items;

        // Slots [0, Used) are claimed. Only the thread that claimed a slot writes it, before it
        // publishes the array that covers it.
        public int Used = used;
    }

    public static readonly JqArray Empty = new(new Buffer([], 0), 0, 0);

    readonly Buffer buffer;
    readonly int start;
    readonly int count;

    JqArray(Buffer buffer, int start, int count)
    {
        this.buffer = buffer;
        this.start = start;
        this.count = count;
    }

    /// <summary>Creates an array with a copy of <paramref name="items"/>.</summary>
    public static JqArray Of(params JqValue[] items)
    {
        if (items.Length == 0)
            return Empty;
        var copy = new JqValue[items.Length];
        Array.Copy(items, copy, items.Length);
        return FromOwned(copy);
    }

    /// <summary>Creates an array over <paramref name="items"/> without copying: the caller gives up the array and never writes it again.</summary>
    public static JqArray FromOwned(JqValue[] items) => items.Length == 0 ? Empty : new(new Buffer(items, items.Length), 0, items.Length);

    /// <summary>Creates an array over the first <paramref name="count"/> items without copying; the rest of the storage is room to append.</summary>
    internal static JqArray FromOwned(JqValue[] items, int count) => count == 0 ? Empty : new(new Buffer(items, count), 0, count);

    public int Count => count;

    /// <summary>The element at <paramref name="index"/>, 0 &lt;= index &lt; Count.</summary>
    public JqValue this[int index] => (uint)index < (uint)count ? buffer.Items[start + index] : throw new ArgumentOutOfRangeException(nameof(index));

    public JqArray Append(JqValue value)
    {
        var end = start + count;
        if (end < buffer.Items.Length && Interlocked.CompareExchange(ref buffer.Used, end + 1, end) == end)
        {
            buffer.Items[end] = value;
            return new JqArray(buffer, start, count + 1);
        }

        var items = new JqValue[Grow(count + 1)];
        Array.Copy(buffer.Items, start, items, 0, count);
        items[count] = value;
        return new JqArray(new Buffer(items, count + 1), 0, count + 1);
    }

    /// <summary>This array followed by <paramref name="other"/> (jq's <c>+</c> on arrays).</summary>
    public JqArray Concat(JqArray other)
    {
        if (other.count == 0)
            return this;
        if (count == 0)
            return other;

        var end = start + count;
        var total = count + other.count;
        if (end + other.count <= buffer.Items.Length && Interlocked.CompareExchange(ref buffer.Used, end + other.count, end) == end)
        {
            Array.Copy(other.buffer.Items, other.start, buffer.Items, end, other.count);
            return new JqArray(buffer, start, total);
        }

        var items = new JqValue[Grow(total)];
        Array.Copy(buffer.Items, start, items, 0, count);
        Array.Copy(other.buffer.Items, other.start, items, count, other.count);
        return new JqArray(new Buffer(items, total), 0, total);
    }

    /// <summary>The elements <paramref name="from"/> (included) to <paramref name="to"/> (excluded), sharing this array's buffer; 0 &lt;= from &lt;= to &lt;= Count.</summary>
    public JqArray Slice(int from, int to)
    {
        if ((uint)from > (uint)count || to < from || to > count)
            throw new ArgumentOutOfRangeException(nameof(from));
        if (from == 0 && to == count)
            return this;
        return to == from ? Empty : new JqArray(buffer, start + from, to - from);
    }

    /// <summary>A copy with <paramref name="value"/> at <paramref name="index"/> (index &gt;= 0); an index past the end pads with nulls, as jq does.</summary>
    public JqArray SetItem(int index, JqValue value)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (index == count)
            return Append(value);

        var items = new JqValue[Math.Max(count, index + 1)];
        Array.Copy(buffer.Items, start, items, 0, count);
        items[index] = value;
        return FromOwned(items);
    }

    public void CopyTo(JqValue[] target, int targetIndex) => Array.Copy(buffer.Items, start, target, targetIndex, count);

    public JqValue[] ToArray()
    {
        var items = new JqValue[count];
        CopyTo(items, 0);
        return items;
    }

    static int Grow(int needed) => Math.Max(4, needed * 2);
}