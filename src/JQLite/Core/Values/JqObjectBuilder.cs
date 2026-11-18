namespace JQLite.Core.Values;

/// <summary>
/// A private, mutable object under construction (object literals, <c>*</c>, the JSON reader,
/// merges that replace keys). <see cref="ToObject"/> hands its storage to the new
/// <see cref="JqObject"/> without copying; the builder must not be used afterwards.
/// </summary>
sealed class JqObjectBuilder
{
    string[] keys;
    JqValue[] values;
    int count;
    Dictionary<string, int>? index;
    bool built;

    public JqObjectBuilder(int capacity = 4)
    {
        keys = new string[Math.Max(capacity, 1)];
        values = new JqValue[keys.Length];
    }

    /// <summary>Starts from the entries of <paramref name="source"/>, with room for <paramref name="extra"/> more.</summary>
    public JqObjectBuilder(JqObject source, int extra = 4)
        : this(source.Count + extra)
    {
        for (var i = 0; i < source.Count; i++)
        {
            keys[i] = source.KeyAt(i);
            values[i] = source.ValueAt(i);
        }

        count = source.Count;
        if (count > JqObject.LinearLimit)
            BuildIndex();
    }

    public int Count => count;

    public bool TryGetValue(string key, out JqValue value)
    {
        var i = IndexOf(key);
        value = i >= 0 ? values[i] : default;
        return i >= 0;
    }

    /// <summary>Sets <paramref name="key"/>: an existing key keeps its position, a new key goes last.</summary>
    public void Set(string key, JqValue value)
    {
        EnsureNotBuilt();
        var i = IndexOf(key);
        if (i >= 0)
        {
            values[i] = value;
            return;
        }

        if (count == keys.Length)
        {
            Array.Resize(ref keys, count * 2);
            Array.Resize(ref values, count * 2);
        }

        keys[count] = key;
        values[count] = value;
        count++;
        if (index is not null)
            index[key] = count - 1;
        else if (count > JqObject.LinearLimit)
            BuildIndex();
    }

    public bool Remove(string key)
    {
        EnsureNotBuilt();
        var i = IndexOf(key);
        if (i < 0)
            return false;

        Array.Copy(keys, i + 1, keys, i, count - i - 1);
        Array.Copy(values, i + 1, values, i, count - i - 1);
        count--;
        keys[count] = null!;
        values[count] = default;
        index = null;
        if (count > JqObject.LinearLimit)
            BuildIndex();
        return true;
    }

    public JqObject ToObject()
    {
        EnsureNotBuilt();
        built = true;
        return JqObject.FromOwned(keys, values, count, index);
    }

    int IndexOf(string key)
    {
        if (index is not null)
            return index.TryGetValue(key, out var i) ? i : -1;

        for (var i = 0; i < count; i++)
        {
            if (string.Equals(keys[i], key, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    void BuildIndex()
    {
        index = new Dictionary<string, int>(count * 2, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
            index[keys[i]] = i;
    }

    void EnsureNotBuilt()
    {
        if (built)
            throw new InvalidOperationException("The builder was already turned into an object.");
    }
}
