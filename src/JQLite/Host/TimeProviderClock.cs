using JQLite.Core.Runtime;

namespace JQLite.Host;

/// <summary>A <see cref="TimeProvider"/> as the clock and zone of the core (spec §12, §9.12).</summary>
sealed class TimeProviderClock(TimeProvider provider) : IClock
{
    static readonly double MinSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    static readonly double MaxSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    public double Now() => (provider.GetUtcNow() - DateTimeOffset.UnixEpoch).Ticks / (double)TimeSpan.TicksPerSecond;

    // The zone's offset at that instant, so daylight saving time follows the date; instants outside
    // what .NET represents take the zone's base offset.
    public int Offset(double seconds)
    {
        var zone = provider.LocalTimeZone;
        if (double.IsNaN(seconds) || seconds < MinSeconds || seconds > MaxSeconds)
            return (int)zone.BaseUtcOffset.TotalSeconds;
        return (int)zone.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds((long)Math.Floor(seconds))).TotalSeconds;
    }

    // .NET has no zone abbreviations (StandardName is a long name): %Z prints the offset (§9.12).
    public string? Abbreviation(double seconds) => null;
}
