namespace Devlooped.Core.Builtins;

/// <summary>
/// The proleptic Gregorian calendar of §9.12 and the broken-down time
/// <c>[year, month, mday, hours, minutes, seconds, wday, yday]</c> (month 0–11, wday Sunday = 0).
/// Integer arithmetic with floor division; no host date type.
/// </summary>
static class Calendar
{
    /// <summary>|t| below 2^53: the range of seconds <c>gmtime</c> accepts.</summary>
    public const double MaxSeconds = 9007199254740992;

    const long SecondsPerDay = 86400;

    /// <summary>A broken-down time; <see cref="Seconds"/> may have a fraction.</summary>
    public record struct Fields(long Year, long Month, long Day, long Hours, long Minutes, double Seconds, long Weekday, long YearDay);

    /// <summary>The days from 1970-01-01 to the date; <paramref name="month"/> is 1–12, <paramref name="day"/> may be 0 or exceed the month.</summary>
    public static long Days(long year, long month, long day)
    {
        year -= month <= 2 ? 1 : 0;
        var era = FloorDiv(year, 400);
        var yoe = year - era * 400;
        var doy = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1;
        var doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return era * 146097 + doe - 719468;
    }

    /// <summary>Whether <paramref name="t"/> can be broken down: finite and |t| &lt; 2^53.</summary>
    public static bool InRange(double t) => double.IsFinite(t) && Math.Abs(t) < MaxSeconds;

    /// <summary>The broken-down UTC time of <paramref name="t"/> seconds since the epoch (in range), the fraction kept in the seconds.</summary>
    public static Fields Break(double t)
    {
        var day = (long)Math.Floor(t / SecondsPerDay);
        var rest = t - (double)day * SecondsPerDay;
        // The division may round across a day boundary: bring the rest into [0, 86400).
        if (rest < 0)
        {
            day--;
            rest += SecondsPerDay;
        }
        else if (rest >= SecondsPerDay)
        {
            day++;
            rest -= SecondsPerDay;
        }

        var (year, month, mday) = Date(day);
        var minutes = Math.Floor(rest / 60);
        return new Fields(year, month - 1, mday, (long)Math.Floor(rest / 3600), (long)minutes % 60, rest - minutes * 60,
            FloorMod(day + 4, 7), day - Days(year, 1, 1));
    }

    /// <summary>Seconds since the epoch of broken-down fields read as UTC, out-of-range fields rolling over (C <c>timegm</c>).</summary>
    public static double Seconds(long year, long month, long day, long hours, long minutes, long seconds)
    {
        var y = year + FloorDiv(month, 12);
        var m = FloorMod(month, 12);
        return (double)(Days(y, m + 1, 1) + day - 1) * SecondsPerDay + (double)hours * 3600 + (double)minutes * 60 + seconds;
    }

    /// <summary>The weekday (Sunday = 0) and day of the year of a date; <paramref name="month"/> 0–11.</summary>
    public static (long Weekday, long YearDay) WeekdayAndYearDay(long year, long month, long day)
    {
        var days = Days(year, month + 1, day);
        return (FloorMod(days + 4, 7), days - Days(year, 1, 1));
    }

    /// <summary>The ISO 8601 week-based year and week number of a date given by year, wday and yday.</summary>
    public static (long Year, long Week) IsoWeek(long year, long weekday, long yearDay)
    {
        var isoWeekday = FloorMod(weekday + 6, 7) + 1;
        var week = FloorDiv(yearDay + 1 - isoWeekday + 10, 7);
        if (week < 1)
            return (year - 1, Weeks(year - 1));
        if (week > Weeks(year))
            return (year + 1, 1);
        return (year, week);
    }

    static long Weeks(long year) => P(year) == 4 || P(year - 1) == 3 ? 53 : 52;

    static long P(long y) => FloorMod(y + FloorDiv(y, 4) - FloorDiv(y, 100) + FloorDiv(y, 400), 7);

    // The inverse of Days: the date (month 1–12) of a day number.
    static (long Year, long Month, long Day) Date(long days)
    {
        var z = days + 719468;
        var era = FloorDiv(z, 146097);
        var doe = z - era * 146097;
        var yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
        var doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        var mp = (5 * doy + 2) / 153;
        var day = doy - (153 * mp + 2) / 5 + 1;
        var month = mp < 10 ? mp + 3 : mp - 9;
        return (yoe + era * 400 + (month <= 2 ? 1 : 0), month, day);
    }

    public static long FloorDiv(long a, long b) => a / b - (a % b != 0 && (a < 0) != (b < 0) ? 1 : 0);

    public static long FloorMod(long a, long b) => a - FloorDiv(a, b) * b;
}
