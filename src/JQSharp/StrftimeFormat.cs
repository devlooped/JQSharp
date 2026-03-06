using System;
using System.Globalization;
using System.Text;

namespace Devlooped;

static class StrftimeFormat
{
    public static string Format(DateTimeOffset dt, string format)
    {
        var sb = new StringBuilder(format.Length + 16);

        for (var i = 0; i < format.Length; i++)
        {
            var ch = format[i];
            if (ch != '%')
            {
                sb.Append(ch);
                continue;
            }

            if (i + 1 >= format.Length)
                throw new JqException("Invalid format: trailing %");

            var spec = format[++i];
            switch (spec)
            {
                case 'Y':
                    sb.Append(dt.Year.ToString("D4", CultureInfo.InvariantCulture));
                    break;
                case 'm':
                    sb.Append(dt.Month.ToString("D2", CultureInfo.InvariantCulture));
                    break;
                case 'd':
                    sb.Append(dt.Day.ToString("D2", CultureInfo.InvariantCulture));
                    break;
                case 'e':
                    sb.Append(dt.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2, ' '));
                    break;
                case 'H':
                    sb.Append(dt.Hour.ToString("D2", CultureInfo.InvariantCulture));
                    break;
                case 'I':
                    var hour12 = dt.Hour % 12;
                    if (hour12 == 0)
                        hour12 = 12;
                    sb.Append(hour12.ToString("D2", CultureInfo.InvariantCulture));
                    break;
                case 'M':
                    sb.Append(dt.Minute.ToString("D2", CultureInfo.InvariantCulture));
                    break;
                case 'S':
                    sb.Append(dt.Second.ToString("D2", CultureInfo.InvariantCulture));
                    break;
                case 'p':
                    sb.Append(dt.ToString("tt", CultureInfo.InvariantCulture));
                    break;
                case 'Z':
                    if (dt.Offset == TimeSpan.Zero)
                    {
                        sb.Append("UTC");
                    }
                    else
                    {
                        var local = TimeZoneInfo.Local;
                        sb.Append(local.IsDaylightSavingTime(dt.DateTime) ? local.DaylightName : local.StandardName);
                    }
                    break;
                case 'z':
                    AppendOffset(sb, dt.Offset);
                    break;
                case 'j':
                    sb.Append(dt.DayOfYear.ToString("D3", CultureInfo.InvariantCulture));
                    break;
                case 'u':
                    var isoDay = dt.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)dt.DayOfWeek;
                    sb.Append(isoDay.ToString(CultureInfo.InvariantCulture));
                    break;
                case 'w':
                    sb.Append(((int)dt.DayOfWeek).ToString(CultureInfo.InvariantCulture));
                    break;
                case 'a':
                    sb.Append(dt.ToString("ddd", CultureInfo.InvariantCulture));
                    break;
                case 'A':
                    sb.Append(dt.ToString("dddd", CultureInfo.InvariantCulture));
                    break;
                case 'b':
                case 'h':
                    sb.Append(dt.ToString("MMM", CultureInfo.InvariantCulture));
                    break;
                case 'B':
                    sb.Append(dt.ToString("MMMM", CultureInfo.InvariantCulture));
                    break;
                case 'c':
                    sb.Append(dt.ToString("ddd MMM ", CultureInfo.InvariantCulture));
                    sb.Append(dt.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2, ' '));
                    sb.Append(dt.ToString(" HH:mm:ss yyyy", CultureInfo.InvariantCulture));
                    break;
                case 'C':
                    sb.Append((dt.Year / 100).ToString("D2", CultureInfo.InvariantCulture));
                    break;
                case 'x':
                    sb.Append(dt.ToString("MM/dd/yy", CultureInfo.InvariantCulture));
                    break;
                case 'X':
                    sb.Append(dt.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                    break;
                case 's':
                    sb.Append(dt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
                    break;
                case 'n':
                    sb.Append('\n');
                    break;
                case 't':
                    sb.Append('\t');
                    break;
                case '%':
                    sb.Append('%');
                    break;
                case 'G':
                    sb.Append(ISOWeek.GetYear(dt.DateTime).ToString("D4", CultureInfo.InvariantCulture));
                    break;
                case 'V':
                    sb.Append(ISOWeek.GetWeekOfYear(dt.DateTime).ToString("D2", CultureInfo.InvariantCulture));
                    break;
                default:
                    throw new JqException($"Unsupported strftime format specifier %{spec}");
            }
        }

        return sb.ToString();
    }

    public static int[] Parse(string input, string format)
    {
        var year = 0;
        var month0 = -1;
        var day = 0;
        var hour = 0;
        var min = 0;
        var sec = 0;
        var weekday0Sun = -1;
        var yearday0 = -1;

        var pos = 0;
        for (var i = 0; i < format.Length; i++)
        {
            var ch = format[i];
            if (ch != '%')
            {
                ExpectChar(input, ref pos, ch);
                continue;
            }

            if (i + 1 >= format.Length)
                throw new JqException("Invalid parse format: trailing %");

            var spec = format[++i];
            switch (spec)
            {
                case 'Y':
                    year = ParseNDigits(input, ref pos, 4, "%Y");
                    break;
                case 'm':
                    month0 = ParseNDigits(input, ref pos, 2, "%m") - 1;
                    break;
                case 'd':
                case 'e':
                    day = ParseUpToTwoDigitsAllowLeadingSpaces(input, ref pos, $"%{spec}");
                    break;
                case 'H':
                    hour = ParseNDigits(input, ref pos, 2, "%H");
                    break;
                case 'M':
                    min = ParseNDigits(input, ref pos, 2, "%M");
                    break;
                case 'S':
                    sec = ParseNDigits(input, ref pos, 2, "%S");
                    break;
                case 'Z':
                    while (pos < input.Length && !char.IsDigit(input[pos]))
                        pos++;
                    break;
                case 'z':
                    ParseOffset(input, ref pos);
                    break;
                case 'b':
                case 'h':
                    month0 = ParseName(input, ref pos, CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames, $"%{spec}");
                    break;
                case 'B':
                    month0 = ParseName(input, ref pos, CultureInfo.InvariantCulture.DateTimeFormat.MonthNames, "%B");
                    break;
                case 'j':
                    yearday0 = ParseNDigits(input, ref pos, 3, "%j") - 1;
                    break;
                case 'a':
                    weekday0Sun = ParseName(input, ref pos, CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedDayNames, "%a");
                    break;
                case 'A':
                    weekday0Sun = ParseName(input, ref pos, CultureInfo.InvariantCulture.DateTimeFormat.DayNames, "%A");
                    break;
                case '%':
                    ExpectChar(input, ref pos, '%');
                    break;
                default:
                    throw new JqException($"Unsupported strptime format specifier %{spec}");
            }
        }

        if (pos != input.Length)
            throw new JqException("Input does not match format");

        if (yearday0 < 0 || weekday0Sun < 0)
        {
            if (year == 0 || month0 < 0 || day == 0)
                throw new JqException("Cannot infer weekday/yearday without year, month, and day");

            var date = new DateTime(year, month0 + 1, day);
            if (yearday0 < 0)
                yearday0 = date.DayOfYear - 1;
            if (weekday0Sun < 0)
                weekday0Sun = (int)date.DayOfWeek;
        }

        return [year, month0, day, hour, min, sec, weekday0Sun, yearday0];
    }

    static void AppendOffset(StringBuilder sb, TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var abs = offset.Duration();
        sb.Append(sign);
        sb.Append(abs.Hours.ToString("D2", CultureInfo.InvariantCulture));
        sb.Append(abs.Minutes.ToString("D2", CultureInfo.InvariantCulture));
    }

    static void ExpectChar(string input, ref int pos, char expected)
    {
        if (pos >= input.Length || input[pos] != expected)
            throw new JqException("Input does not match format");

        pos++;
    }

    static int ParseNDigits(string input, ref int pos, int count, string spec)
    {
        if (pos + count > input.Length)
            throw new JqException($"Input too short for {spec}");

        var value = 0;
        for (var i = 0; i < count; i++)
        {
            var ch = input[pos + i];
            if (!char.IsDigit(ch))
                throw new JqException($"Invalid value for {spec}");
            value = (value * 10) + (ch - '0');
        }

        pos += count;
        return value;
    }

    static int ParseUpToTwoDigitsAllowLeadingSpaces(string input, ref int pos, string spec)
    {
        while (pos < input.Length && input[pos] == ' ')
            pos++;

        if (pos >= input.Length || !char.IsDigit(input[pos]))
            throw new JqException($"Invalid value for {spec}");

        var value = input[pos] - '0';
        pos++;

        if (pos < input.Length && char.IsDigit(input[pos]))
        {
            value = (value * 10) + (input[pos] - '0');
            pos++;
        }

        return value;
    }

    static void ParseOffset(string input, ref int pos)
    {
        if (pos >= input.Length)
            throw new JqException("Invalid value for %z");

        var sign = input[pos];
        if (sign != '+' && sign != '-')
            throw new JqException("Invalid value for %z");

        pos++;
        ParseNDigits(input, ref pos, 2, "%z");

        if (pos < input.Length && input[pos] == ':')
            pos++;

        ParseNDigits(input, ref pos, 2, "%z");
    }

    static int ParseName(string input, ref int pos, string[] names, string spec)
    {
        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];
            if (string.IsNullOrEmpty(name))
                continue;

            if (pos + name.Length > input.Length)
                continue;

            if (string.Compare(input, pos, name, 0, name.Length, StringComparison.Ordinal) == 0)
            {
                pos += name.Length;
                return i;
            }
        }

        throw new JqException($"Invalid value for {spec}");
    }
}
