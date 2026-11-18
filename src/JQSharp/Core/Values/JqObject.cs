namespace Devlooped.Core.Values;

/// <summary>An immutable map from string keys to values that keeps the insertion order of its keys.</summary>
/// <remarks>
/// <para>
/// Storage is a shared buffer of entries plus a count, as for <see cref="JqArray"/>. Adding a new
/// key to the latest version of a buffer claims the next slot with one compare-and-swap: amortized
/// O(1), so <c>reduce ... (.; . + {($k): $v})</c> and <c>setpath</c> on new keys do not copy the
/// object. The keys of a buffer are all distinct (a slot is only claimed for a key the version
/// lacks), so one index (key to slot) serves every version: a version with <c>count</c> entries
/// owns the key when its slot is below <c>count</c>.
/// </para>
/// <para>
/// Replacing or removing a key copies one level. Up to <see cref="LinearLimit"/> entries, lookup
/// scans the keys; above, it uses the index, built lazily and extended under a lock because
/// versions on other threads share it (a single-threaded port needs no lock).
/// </para>
/// </remarks>
sealed class JqObject
{
    internal const int LinearLimit = 8;

    sealed class Buffer(string[] keys, JqValue[] values, int used, Dictionary<string, int>? index)
    {
        public readonly string[] Keys = keys;
        public readonly JqValue[] Values = values;

        // Slots [0, Used) are claimed; only their claimer writes them, before publishing.
        public int Used = used;

        // Key to slot for slots [0, Indexed); guarded by lock (this).
        public Dictionary<string, int>? Index = index;
        public int Indexed = index?.Count ?? 0;
    }

    public static readonly JqObject Empty = new(new Buffer([], [], 0, null), 0);

    readonly Buffer buffer;
    readonly int count;

    // The keys in code point order, computed on first use. A benign race: two threads may both
    // compute the same array, and either result is correct.
    string[]? sortedKeys;

    JqObject(Buffer buffer, int count)
    {
        this.buffer = buffer;
        this.count = count;
    }

    /// <summary>Creates an object over arrays the caller gives up; keys must be distinct; <paramref name="index"/>, if any, maps exactly the first <paramref name="count"/> keys.</summary>
    internal static JqObject FromOwned(string[] keys, JqValue[] values, int count, Dictionary<string, int>? index)
        => count == 0 ? Empty : new(new Buffer(keys, values, count, index), count);

    public int Count => count;

    /// <summary>The key at <paramref name="index"/> in insertion order.</summary>
    public string KeyAt(int index) => (uint)index < (uint)count ? buffer.Keys[index] : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>The value at <paramref name="index"/> in insertion order.</summary>
    public JqValue ValueAt(int index) => (uint)index < (uint)count ? buffer.Values[index] : throw new ArgumentOutOfRangeException(nameof(index));

    public bool ContainsKey(string key) => IndexOf(key) >= 0;

    public bool TryGetValue(string key, out JqValue value)
    {
        var index = IndexOf(key);
        value = index >= 0 ? buffer.Values[index] : default;
        return index >= 0;
    }

    /// <summary>The position of <paramref name="key"/> in insertion order, or -1.</summary>
    public int IndexOf(string key)
    {
        if (count <= LinearLimit)
        {
            for (var i = 0; i < count; i++)
            {
                if (string.Equals(buffer.Keys[i], key, StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }

        lock (buffer)
        {
            var index = buffer.Index ??= new Dictionary<string, int>(StringComparer.Ordinal);
            // Slots below this version's count are written (it was published after them).
            for (; buffer.Indexed < count; buffer.Indexed++)
                index[buffer.Keys[buffer.Indexed]] = buffer.Indexed;
            return index.TryGetValue(key, out var slot) && slot < count ? slot : -1;
        }
    }

    /// <summary>A copy with <paramref name="key"/> set: an existing key keeps its position, a new key goes last.</summary>
    public JqObject Set(string key, JqValue value)
    {
        var index = IndexOf(key);
        if (index < 0)
            return Add(key, value);

        var keys = new string[count];
        var values = new JqValue[count];
        Array.Copy(buffer.Keys, keys, count);
        Array.Copy(buffer.Values, values, count);
        values[index] = value;
        return FromOwned(keys, values, count, null);
    }

    // Appends a key this version does not have.
    JqObject Add(string key, JqValue value)
    {
        if (count < buffer.Keys.Length && Interlocked.CompareExchange(ref buffer.Used, count + 1, count) == count)
        {
            buffer.Keys[count] = key;
            buffer.Values[count] = value;
            return new JqObject(buffer, count + 1);
        }

        var capacity = Math.Max(4, (count + 1) * 2);
        var keys = new string[capacity];
        var values = new JqValue[capacity];
        Array.Copy(buffer.Keys, keys, count);
        Array.Copy(buffer.Values, values, count);
        keys[count] = key;
        values[count] = value;
        return FromOwned(keys, values, count + 1, null);
    }

    /// <summary>A copy without <paramref name="key"/>; the other keys keep their order.</summary>
    public JqObject Remove(string key)
    {
        var index = IndexOf(key);
        if (index < 0)
            return this;

        var keys = new string[count - 1];
        var values = new JqValue[count - 1];
        Array.Copy(buffer.Keys, keys, index);
        Array.Copy(buffer.Values, values, index);
        Array.Copy(buffer.Keys, index + 1, keys, index, count - index - 1);
        Array.Copy(buffer.Values, index + 1, values, index, count - index - 1);
        return FromOwned(keys, values, count - 1, null);
    }

    /// <summary>jq's <c>+</c> on objects: the keys of <paramref name="other"/> win; existing keys keep their position, new ones follow in <paramref name="other"/>'s order.</summary>
    public JqObject Merge(JqObject other)
    {
        if (other.count == 0)
            return this;
        if (count == 0)
            return other;

        var overlaps = false;
        for (var i = 0; i < other.count && !overlaps; i++)
            overlaps = IndexOf(other.buffer.Keys[i]) >= 0;

        if (!overlaps)
        {
            // Only new keys: append them, in place when this is the latest version.
            var result = this;
            for (var i = 0; i < other.count; i++)
                result = result.Add(other.buffer.Keys[i], other.buffer.Values[i]);
            return result;
        }

        var builder = new JqObjectBuilder(this, other.count);
        for (var i = 0; i < other.count; i++)
            builder.Set(other.buffer.Keys[i], other.buffer.Values[i]);
        return builder.ToObject();
    }

    /// <summary>The keys in code point order (jq's <c>keys</c>); computed once per object.</summary>
    public IReadOnlyList<string> SortedKeys()
    {
        var keys = sortedKeys;
        if (keys is null)
        {
            keys = new string[count];
            Array.Copy(buffer.Keys, keys, count);
            Array.Sort(keys, CodePoints.Compare);
            sortedKeys = keys;
        }

        return keys;
    }
}
