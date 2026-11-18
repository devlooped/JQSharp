namespace Devlooped.Core.Values;

/// <summary>
/// A private, mutable array under construction, the array counterpart of <see cref="JqObjectBuilder"/>.
/// <see cref="ToArray"/> hands its storage to the new <see cref="JqArray"/> without copying; the
/// builder must not be used afterwards.
/// </summary>
sealed class JqArrayBuilder
{
    JqValue[] items;
    int count;
    bool built;

    /// <summary>Starts from the elements of <paramref name="source"/> (one level copied).</summary>
    public JqArrayBuilder(JqArray source)
    {
        items = new JqValue[Math.Max(4, source.Count)];
        source.CopyTo(items, 0);
        count = source.Count;
    }

    public int Count => count;

    /// <summary>The element at <paramref name="index"/>, 0 &lt;= index &lt; Count.</summary>
    public JqValue this[int index] => (uint)index < (uint)count ? items[index] : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>Sets the element at <paramref name="index"/> (&gt;= 0); an index past the end pads with nulls, as jq does.</summary>
    public void Set(int index, JqValue value)
    {
        if (built)
            throw new InvalidOperationException("The builder was already turned into an array.");
        if (index >= items.Length)
            Array.Resize(ref items, Math.Max(index + 1, items.Length * 2));
        items[index] = value;
        if (index >= count)
            count = index + 1;
    }

    public JqArray ToArray()
    {
        if (built)
            throw new InvalidOperationException("The builder was already turned into an array.");
        built = true;
        return JqArray.FromOwned(items, count);
    }
}
