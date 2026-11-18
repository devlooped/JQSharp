using System.Text;
using Devlooped.Core.Values;

namespace Devlooped.Core.Builtins;

/// <summary>The strftime and strptime directive tables of §9.12: English names, no locale.</summary>
static class Strftime
{
    static readonly string[] days = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    static readonly string[] months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    /// <summary>The zone a time is formatted in: UTC, or the host's local zone at the instant.</summary>
    public readonly record struct Zone(bool Local, long Offset, string? Abbreviation);

    /// <summary>
    /// Formats normalized <paramref name="time"/> (wday and yday consistent with the date);
    /// <paramref name="epoch"/> is the instant in whole seconds, for <c>%s</c>.
    /// </summary>
    public static bool TryFormat(Calendar.Fields time, long epoch, Zone zone, string format, out string text, out JqValue error)
    {
        var builder = new StringBuilder(format.Length + 16);
        error = default;
        text = "";
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c != '%')
            {
                builder.Append(c);
                continue;
            }

            if (++i == format.Length)
            {
                error = Errors.TrailingPercent;
                return false;
            }

            switch (format[i])
            {
                case 'a': builder.Append(days[(int)time.Weekday], 0, 3); break;
                case 'A': builder.Append(days[(int)time.Weekday]); break;
                case 'b' or 'h': builder.Append(months[(int)time.Month], 0, 3); break;
                case 'B': builder.Append(months[(int)time.Month]); break;
                case 'c':
                    builder.Append(days[(int)time.Weekday], 0, 3).Append(' ').Append(months[(int)time.Month], 0, 3).Append(' ');
                    Number(builder, time.Day, 2, ' ');
                    builder.Append(' ');
                    Clock(builder, time);
                    builder.Append(' ');
                    Number(builder, time.Year, 4);
                    break;
                case 'C': Number(builder, Calendar.FloorDiv(time.Year, 100), 2); break;
                case 'd': Number(builder, time.Day, 2); break;
                case 'e': Number(builder, time.Day, 2, ' '); break;
                case 'G': Number(builder, Calendar.IsoWeek(time.Year, time.Weekday, time.YearDay).Year, 4); break;
                case 'H': Number(builder, time.Hours, 2); break;
                case 'I': Number(builder, time.Hours % 12 == 0 ? 12 : time.Hours % 12, 2); break;
                case 'j': Number(builder, time.YearDay + 1, 3); break;
                case 'm': Number(builder, time.Month + 1, 2); break;
                case 'M': Number(builder, time.Minutes, 2); break;
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'p': builder.Append(time.Hours < 12 ? "AM" : "PM"); break;
                case 's': Number(builder, epoch, 1); break;
                case 'S': Number(builder, (long)Math.Floor(time.Seconds), 2); break;
                case 'u': Number(builder, Calendar.FloorMod(time.Weekday + 6, 7) + 1, 1); break;
                case 'w': Number(builder, time.Weekday, 1); break;
                case 'V': Number(builder, Calendar.IsoWeek(time.Year, time.Weekday, time.YearDay).Week, 2); break;
                case 'x':
                    Number(builder, time.Month + 1, 2);
                    builder.Append('/');
                    Number(builder, time.Day, 2);
                    builder.Append('/');
                    Number(builder, Calendar.FloorMod(time.Year, 100), 2);
                    break;
                case 'X': Clock(builder, time); break;
                case 'Y': Number(builder, time.Year, 4); break;
                case 'z': Offset(builder, zone.Local ? zone.Offset : 0, minutes: true); break;
                case 'Z':
                    if (!zone.Local)
                        builder.Append("UTC");
                    else if (zone.Abbreviation is { } abbreviation)
                        builder.Append(abbreviation);
                    else
                        Offset(builder, zone.Offset, minutes: zone.Offset / 60 % 60 != 0);
                    break;
                case '%': builder.Append('%'); break;
                default:
                    var length = char.IsHighSurrogate(format[i]) && i + 1 < format.Length ? 2 : 1;
                    error = Errors.UnsupportedStrftime(format.Substring(i, length));
                    return false;
            }
        }

        text = builder.ToString();
        return true;
    }

    static void Clock(StringBuilder builder, Calendar.Fields time)
    {
        Number(builder, time.Hours, 2);
        builder.Append(':');
        Number(builder, time.Minutes, 2);
        builder.Append(':');
        Number(builder, (long)Math.Floor(time.Seconds), 2);
    }

    // +hhmm (or +hh) from an offset in seconds east of UTC.
    static void Offset(StringBuilder builder, long offset, bool minutes)
    {
        builder.Append(offset < 0 ? '-' : '+');
        var total = Math.Abs(offset) / 60;
        Number(builder, total / 60, 2);
        if (minutes)
            Number(builder, total % 60, 2);
    }

    // Decimal, padded to the width; a negative number is '-' and the padded digits.
    static void Number(StringBuilder builder, long value, int width, char pad = '0')
    {
        if (value < 0)
            builder.Append('-');
        var digits = new StringBuilder();
        var rest = value;
        do
        {
            digits.Insert(0, (char)('0' + Math.Abs(rest % 10)));
            rest /= 10;
        }
        while (rest != 0);
        for (var i = digits.Length; i < width; i++)
            builder.Append(pad);
        builder.Append(digits);
    }

    /// <summary>
    /// Parses <paramref name="input"/> by <paramref name="format"/> into a broken-down time; false on
    /// any mismatch.
    /// </summary>
    public static bool TryParse(string input, string format, out long[] fields)
    {
        long year = 1900, month = 0, day = 0, hours = 0, minutes = 0, seconds = 0;
        long weekday = -1, yearDay = -1;
        fields = [];
        var at = 0;
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c != '%')
            {
                if (IsSpace(c))
                {
                    SkipSpaces(input, ref at);
                }
                else
                {
                    if (at == input.Length || input[at] != c)
                        return false;
                    at++;
                }

                continue;
            }

            if (++i == format.Length)
                return false;
            var ok = format[i] switch
            {
                'Y' => ReadNumber(input, ref at, 4, 0, 9999, out year),
                'm' => ReadNumber(input, ref at, 2, 1, 12, out month) && (month -= 1) >= 0,
                'd' or 'e' => ReadNumber(input, ref at, 2, 1, 31, out day),
                'H' => ReadNumber(input, ref at, 2, 0, 23, out hours),
                'M' => ReadNumber(input, ref at, 2, 0, 59, out minutes),
                'S' => ReadNumber(input, ref at, 2, 0, 60, out seconds),
                'j' => ReadNumber(input, ref at, 3, 1, 366, out yearDay) && (yearDay -= 1) >= 0,
                'a' or 'A' => ReadName(input, ref at, days, out weekday),
                'b' or 'h' or 'B' => ReadName(input, ref at, months, out month),
                'z' => ReadOffset(input, ref at),
                'Z' => ReadWord(input, ref at),
                '%' => at < input.Length && input[at++] == '%',
                _ => false,
            };
            if (!ok)
                return false;
        }

        SkipSpaces(input, ref at);
        if (at != input.Length)
            return false;

        var (computedWeekday, computedYearDay) = Calendar.WeekdayAndYearDay(year, month, day);
        fields = [year, month, day, hours, minutes, seconds, weekday >= 0 ? weekday : computedWeekday, yearDay >= 0 ? yearDay : computedYearDay];
        return true;
    }

    // Skips input whitespace, then reads 1 to `width` ASCII digits in [min, max].
    static bool ReadNumber(string input, ref int at, int width, long min, long max, out long value)
    {
        SkipSpaces(input, ref at);
        value = 0;
        var count = 0;
        while (count < width && at < input.Length && input[at] is >= '0' and <= '9')
        {
            value = value * 10 + (input[at++] - '0');
            count++;
        }

        return count > 0 && value >= min && value <= max;
    }

    // A name of the table, full or its first three letters, ASCII case-insensitive; full names first.
    static bool ReadName(string input, ref int at, string[] names, out long index)
    {
        for (var length = 0; length < 2; length++)
        {
            for (var i = 0; i < names.Length; i++)
            {
                var name = length == 0 ? names[i] : names[i][..3];
                if (at + name.Length <= input.Length && AsciiEqualsIgnoreCase(input, at, name))
                {
                    at += name.Length;
                    index = i;
                    return true;
                }
            }
        }

        index = -1;
        return false;
    }

    static bool AsciiEqualsIgnoreCase(string input, int at, string name)
    {
        for (var i = 0; i < name.Length; i++)
        {
            var a = input[at + i];
            var b = name[i];
            if ((a is >= 'A' and <= 'Z' ? (char)(a + 32) : a) != (b is >= 'A' and <= 'Z' ? (char)(b + 32) : b))
                return false;
        }

        return true;
    }

    // Z, or + or - with hh, hhmm or hh:mm; read and ignored.
    static bool ReadOffset(string input, ref int at)
    {
        if (at < input.Length && input[at] == 'Z')
        {
            at++;
            return true;
        }

        if (at == input.Length || input[at] is not ('+' or '-') || !TwoDigits(input, at + 1))
            return false;
        at += 3;
        if (TwoDigits(input, at))
            at += 2;
        else if (at < input.Length && input[at] == ':' && TwoDigits(input, at + 1))
            at += 3;
        return true;
    }

    static bool TwoDigits(string input, int at)
        => at + 2 <= input.Length && input[at] is >= '0' and <= '9' && input[at + 1] is >= '0' and <= '9';

    // A run of non-whitespace characters; read and ignored.
    static bool ReadWord(string input, ref int at)
    {
        var start = at;
        while (at < input.Length && !IsSpace(input[at]))
            at++;
        return at > start;
    }

    static void SkipSpaces(string input, ref int at)
    {
        while (at < input.Length && IsSpace(input[at]))
            at++;
    }

    static bool IsSpace(char c) => c is ' ' or (>= '\t' and <= '\r');
}
