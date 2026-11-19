namespace JQLite.Core.Values;

/// <summary>An immutable string made by jq's <c>+</c> on strings, which later appends extend in place.</summary>
/// <remarks>
/// <para>
/// Storage is a shared buffer of UTF-16 units plus a length, as <see cref="JqArray"/> is without its
/// start. A unit of a buffer is written once, before any string that covers it exists, and never
/// again, so every <see cref="JqString"/> is immutable however many strings share its buffer.
/// </para>
/// <para>
/// Appending (<see cref="Append"/>) to the string whose length is the buffer's used part (its latest
/// version) claims the next free units with one compare-and-swap on <c>Used</c> and writes there:
/// amortized O(length of the appended text), so <c>reduce range(n) as $i (""; . + "ab")</c>, and
/// with it <c>add</c> and <c>join</c>, is O(n). When the units are already taken (an older version,
/// or another thread won the claim) or the buffer is full, the string is copied to a new buffer with
/// twice the room. <see cref="Text"/> builds the host string once per version, when a reader needs it.
/// </para>
/// </remarks>
sealed class JqString
{
    sealed class Buffer(char[] units, int used)
    {
        public readonly char[] Units = units;

        // Units [0, Used) are claimed. Only the thread that claimed a unit writes it, before it
        // publishes the string that covers it.
        public int Used = used;
    }

    /// <summary>
    /// The shortest concatenation that gets a buffer: shorter ones are plain host strings, whose
    /// copies cost little and which need no second copy for <see cref="Text"/>.
    /// </summary>
    public const int MinLength = 256;

    // The longest string the host can hold (.NET's string length limit).
    const int MaxLength = 0x3FFFFFDF;

    readonly Buffer buffer;
    readonly int length;

    // The host string of this version, built on first read. Two threads may both build it: they
    // build equal strings, and either may be kept.
    string? text;

    JqString(Buffer buffer, int length)
    {
        this.buffer = buffer;
        this.length = length;
    }

    /// <summary><paramref name="left"/> followed by <paramref name="right"/>, in a new buffer with room to append.</summary>
    public static JqString Concat(string left, string right)
    {
        var total = left.Length + right.Length;
        var units = new char[Grow(total)];
        left.CopyTo(0, units, 0, left.Length);
        right.CopyTo(0, units, left.Length, right.Length);
        return new JqString(new Buffer(units, total), total);
    }

    /// <summary>The number of UTF-16 units.</summary>
    public int Length => length;

    /// <summary>The host string, built once per version.</summary>
    public string Text => text ??= new string(buffer.Units, 0, length);

    /// <summary>This string followed by <paramref name="right"/>.</summary>
    public JqString Append(string right)
    {
        var total = length + right.Length;
        if (total <= buffer.Units.Length && Interlocked.CompareExchange(ref buffer.Used, total, length) == length)
        {
            right.CopyTo(0, buffer.Units, length, right.Length);
            return new JqString(buffer, total);
        }

        var units = new char[Grow(total)];
        Array.Copy(buffer.Units, units, length);
        right.CopyTo(0, units, length, right.Length);
        return new JqString(new Buffer(units, total), total);
    }

    // Twice the room, within the host's limit; past it the allocation fails, as string.Concat does.
    static int Grow(int needed) => (int)Math.Max(needed, Math.Min(needed * 2L, MaxLength));
}
