using System.Text;
using JQLite.Core.Values;

namespace JQLite.Core.Builtins;

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
        var ok = TryAppend(builder, time, epoch, zone, format, out error);
        text = ok ? builder.ToString() : "";
        return ok;
    }

    // The directives made of others, by their conversion character; strptime reads them the same way.
    static string? Expansion(char conversion) => conversion switch
    {
        'c' => "%a %b %e %H:%M:%S %Y",
        'D' or 'x' => "%m/%d/%y",
        'F' => "%Y-%m-%d",
        'r' => "%I:%M:%S %p",
        'R' => "%H:%M",
        'T' or 'X' => "%H:%M:%S",
        _ => null,
    };

    static bool TryAppend(StringBuilder builder, Calendar.Fields time, long epoch, Zone zone, string format, out JqValue error)
    {
        error = default;
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%')
            {
                builder.Append(format[i]);
                continue;
            }

            var start = ++i;
            if (start == format.Length)
            {
                error = Errors.TrailingPercent;
                return false;
            }

            // GNU flags: the last of _ - 0 decides the padding of the numbers, ^ upper-cases.
            char? padding = null;
            var upper = false;
            for (; i < format.Length && format[i] is '_' or '-' or '0' or '^'; i++)
            {
                if (format[i] == '^')
                    upper = true;
                else
                    padding = format[i];
            }

            // E and O select a locale's alternative forms, which jqlite does not have.
            var modifier = i < format.Length && format[i] is 'E' or 'O' ? format[i++] : '\0';
            if (i == format.Length)
            {
                error = Errors.UnsupportedStrftime(format[start..]);
                return false;
            }

            var conversion = format[i];
            var mark = builder.Length;
            var known = modifier switch
            {
                'E' => "cCxXyY".Contains(conversion),
                'O' => "deHImMSuUVwWy".Contains(conversion),
                _ => true,
            };
            if (known && Expansion(conversion) is { } expansion)
            {
                // Made of directives of the table: it cannot fail.
                TryAppend(builder, time, epoch, zone, expansion, out _);
            }
            else if (!known || !TryDirective(builder, conversion, padding, time, epoch, zone))
            {
                var length = char.IsHighSurrogate(conversion) && i + 1 < format.Length ? 2 : 1;
                error = Errors.UnsupportedStrftime(format.Substring(start, i - start + length));
                return false;
            }

            if (upper && conversion != 'P')
            {
                for (var j = mark; j < builder.Length; j++)
                {
                    if (builder[j] is >= 'a' and <= 'z')
                        builder[j] = (char)(builder[j] - 32);
                }
            }
        }

        return true;
    }

    // A directive of the table that is not made of others; false when there is none.
    static bool TryDirective(StringBuilder builder, char conversion, char? padding, Calendar.Fields time, long epoch, Zone zone)
    {
        var hours12 = time.Hours % 12 == 0 ? 12 : time.Hours % 12;
        switch (conversion)
        {
            case 'a': builder.Append(days[(int)time.Weekday], 0, 3); break;
            case 'A': builder.Append(days[(int)time.Weekday]); break;
            case 'b' or 'h': builder.Append(months[(int)time.Month], 0, 3); break;
            case 'B': builder.Append(months[(int)time.Month]); break;
            case 'C': Number(builder, Calendar.FloorDiv(time.Year, 100), 2, '0', padding); break;
            case 'd': Number(builder, time.Day, 2, '0', padding); break;
            case 'e': Number(builder, time.Day, 2, ' ', padding); break;
            case 'g': Number(builder, Calendar.FloorMod(Calendar.IsoWeek(time.Year, time.Weekday, time.YearDay).Year, 100), 2, '0', padding); break;
            case 'G': Number(builder, Calendar.IsoWeek(time.Year, time.Weekday, time.YearDay).Year, 4, '0', padding); break;
            case 'H': Number(builder, time.Hours, 2, '0', padding); break;
            case 'I': Number(builder, hours12, 2, '0', padding); break;
            case 'k': Number(builder, time.Hours, 2, ' ', padding); break;
            case 'l': Number(builder, hours12, 2, ' ', padding); break;
            case 'j': Number(builder, time.YearDay + 1, 3, '0', padding); break;
            case 'm': Number(builder, time.Month + 1, 2, '0', padding); break;
            case 'M': Number(builder, time.Minutes, 2, '0', padding); break;
            case 'n': builder.Append('\n'); break;
            case 't': builder.Append('\t'); break;
            case 'p': builder.Append(time.Hours < 12 ? "AM" : "PM"); break;
            case 'P': builder.Append(time.Hours < 12 ? "am" : "pm"); break;
            case 's': Number(builder, epoch, 1, '0', null); break;
            case 'S': Number(builder, (long)Math.Floor(time.Seconds), 2, '0', padding); break;
            case 'u': Number(builder, Calendar.FloorMod(time.Weekday + 6, 7) + 1, 1, '0', padding); break;
            case 'w': Number(builder, time.Weekday, 1, '0', padding); break;
            case 'U': Number(builder, Calendar.FloorDiv(time.YearDay + 7 - time.Weekday, 7), 2, '0', padding); break;
            case 'W': Number(builder, Calendar.FloorDiv(time.YearDay + 7 - Calendar.FloorMod(time.Weekday + 6, 7), 7), 2, '0', padding); break;
            case 'V': Number(builder, Calendar.IsoWeek(time.Year, time.Weekday, time.YearDay).Week, 2, '0', padding); break;
            case 'y': Number(builder, Calendar.FloorMod(time.Year, 100), 2, '0', padding); break;
            case 'Y': Number(builder, time.Year, 4, '0', padding); break;
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
            default: return false;
        }

        return true;
    }

    // +hhmm (or +hh) from an offset in seconds east of UTC.
    static void Offset(StringBuilder builder, long offset, bool minutes)
    {
        builder.Append(offset < 0 ? '-' : '+');
        var total = Math.Abs(offset) / 60;
        Number(builder, total / 60, 2, '0', null);
        if (minutes)
            Number(builder, total % 60, 2, '0', null);
    }

    // Decimal, padded to the width with pad, unless the flag says otherwise (- no padding, _ spaces,
    // 0 zeros); a negative number is '-' and the padded digits.
    static void Number(StringBuilder builder, long value, int width, char pad, char? padding)
    {
        if (padding == '-')
            width = 1;
        else if (padding is { } flag)
            pad = flag == '_' ? ' ' : '0';
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
        // %I and %l read a 12-hour clock, which a PM read by %p moves to the afternoon.
        bool twelveHour = false, pm = false;
        fields = [];
        format = Expand(format);
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
            bool ok;
            switch (format[i])
            {
                case 'Y':
                    ok = ReadNumber(input, ref at, 4, 0, 9999, out year);
                    break;
                case 'y':
                    ok = ReadNumber(input, ref at, 2, 0, 99, out year);
                    year += year >= 69 ? 1900 : 2000;
                    break;
                case 'm':
                    ok = ReadNumber(input, ref at, 2, 1, 12, out month);
                    month -= 1;
                    break;
                case 'd' or 'e':
                    ok = ReadNumber(input, ref at, 2, 1, 31, out day);
                    break;
                case 'H' or 'k':
                    ok = ReadNumber(input, ref at, 2, 0, 23, out hours);
                    twelveHour = false;
                    break;
                case 'I' or 'l':
                    ok = ReadNumber(input, ref at, 2, 1, 12, out hours);
                    hours %= 12;
                    twelveHour = true;
                    break;
                case 'p':
                    ok = ReadMeridian(input, ref at, out pm);
                    break;
                case 'M':
                    ok = ReadNumber(input, ref at, 2, 0, 59, out minutes);
                    break;
                case 'S':
                    ok = ReadNumber(input, ref at, 2, 0, 60, out seconds);
                    break;
                case 'j':
                    ok = ReadNumber(input, ref at, 3, 1, 366, out yearDay);
                    yearDay -= 1;
                    break;
                case 'a' or 'A':
                    ok = ReadName(input, ref at, days, out weekday);
                    break;
                case 'u':
                    ok = ReadNumber(input, ref at, 1, 1, 7, out weekday);
                    weekday %= 7;
                    break;
                case 'w':
                    ok = ReadNumber(input, ref at, 1, 0, 6, out weekday);
                    break;
                case 'b' or 'h' or 'B':
                    ok = ReadName(input, ref at, months, out month);
                    break;
                case 'z':
                    ok = ReadOffset(input, ref at);
                    break;
                case 'Z':
                    ok = ReadWord(input, ref at);
                    break;
                case 'n' or 't':
                    SkipSpaces(input, ref at);
                    ok = true;
                    break;
                case '%':
                    ok = at < input.Length && input[at++] == '%';
                    break;
                default:
                    ok = false;
                    break;
            }

            if (!ok)
                return false;
        }

        SkipSpaces(input, ref at);
        if (at != input.Length)
            return false;
        if (twelveHour && pm)
            hours += 12;

        var (computedWeekday, computedYearDay) = Calendar.WeekdayAndYearDay(year, month, day);
        fields = [year, month, day, hours, minutes, seconds, weekday >= 0 ? weekday : computedWeekday, yearDay >= 0 ? yearDay : computedYearDay];
        return true;
    }

    // The format with the directives made of others replaced by what they are made of.
    static string Expand(string format)
    {
        var builder = new StringBuilder(format.Length);
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] == '%' && i + 1 < format.Length)
            {
                var next = format[++i];
                if (Expansion(next) is { } expansion)
                    builder.Append(expansion);
                else
                    builder.Append('%').Append(next);
            }
            else
            {
                builder.Append(format[i]);
            }
        }

        return builder.ToString();
    }

    // AM or PM, ASCII case-insensitive.
    static bool ReadMeridian(string input, ref int at, out bool pm)
    {
        pm = at + 2 <= input.Length && AsciiEqualsIgnoreCase(input, at, "PM");
        if (!pm && !(at + 2 <= input.Length && AsciiEqualsIgnoreCase(input, at, "AM")))
            return false;
        at += 2;
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
