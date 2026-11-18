using Devlooped.Core.Runtime;
using Devlooped.Core.Values;
using static Devlooped.Core.Builtins.NativeTable;

namespace Devlooped.Core.Builtins;

/// <summary>
/// §9.7 Dates, on the calendar and directive tables of §9.12. The time and the local zone come only
/// from the host's <see cref="IClock"/>; without one, the local zone is UTC and <c>now</c> is an error.
/// </summary>
static class DateNatives
{
    public static void Register()
    {
        Add("now", 0, static (interpreter, _, _, out result, out error) => interpreter.Clock is { } clock
            ? Ok(JqValue.FromNumber(clock.Now()), out result, out error)
            : Fail(Errors.NoClock, out result, out error));
        Add("gmtime", 0, static (_, input, _, out result, out error) => input.Kind != JqKind.Number
            ? Fail(Errors.GmtimeNumeric, out result, out error)
            : BreakDown(input.AsNumber(), out result, out error));
        Add("localtime", 0, static (interpreter, input, _, out result, out error) => input.Kind != JqKind.Number
            ? Fail(Errors.LocaltimeNumeric, out result, out error)
            : BreakDown(Local(interpreter, input.AsNumber()), out result, out error));
        Add("mktime", 0, MakeTime);
        Add("strftime", 1, static (interpreter, input, arguments, out result, out error) => Format(interpreter, input, arguments[0], local: false, out result, out error));
        Add("strflocaltime", 1, static (interpreter, input, arguments, out result, out error) => Format(interpreter, input, arguments[0], local: true, out result, out error));
        Add("strptime", 1, Parse);
    }

    static bool BreakDown(double t, out JqValue result, out JqValue error)
        => Calendar.InRange(t)
            ? Ok(ToValue(Calendar.Break(t)), out result, out error)
            : Fail(Errors.CannotConvertSeconds, out result, out error);

    // t + offset(t): the local time of t as if it were UTC (NaN and infinities stay out of range).
    static double Local(Interpreter interpreter, double t)
        => Calendar.InRange(t) ? t + Offset(interpreter, t) : t;

    static long Offset(Interpreter interpreter, double t) => interpreter.Clock?.Offset(t) ?? 0;

    static bool MakeTime(Interpreter interpreter, JqValue input, JqValue[] arguments, out JqValue result, out JqValue error)
    {
        if (input.Kind != JqKind.Array)
            return Fail(Errors.MktimeArray, out result, out error);
        return TrySeconds(input.AsArray(), out var seconds)
            ? Ok(JqValue.FromNumber(seconds), out result, out error)
            : Fail(Errors.MktimeParsed, out result, out error);
    }

    // mktime of a broken-down time: missing elements are 0, each truncated toward zero.
    static bool TrySeconds(JqArray time, out double seconds)
    {
        var fields = new long[6];
        for (var i = 0; i < time.Count && i < 8; i++)
        {
            if (time[i].Kind != JqKind.Number)
            {
                seconds = 0;
                return false;
            }

            if (i < 6)
                fields[i] = (long)Math.Clamp(Math.Truncate(time[i].AsNumber()), -Calendar.MaxSeconds, Calendar.MaxSeconds);
        }

        seconds = Calendar.Seconds(fields[0], fields[1], fields[2], fields[3], fields[4], fields[5]);
        return true;
    }

    static bool Format(Interpreter interpreter, JqValue input, JqValue format, bool local, out JqValue result, out JqValue error)
    {
        var name = local ? "strflocaltime" : "strftime";
        if (input.Kind is not (JqKind.Number or JqKind.Array))
            return Fail(Errors.RequiresParsedDatetime(name), out result, out error);
        if (format.Kind != JqKind.String)
            return Fail(Errors.RequiresStringFormat(name), out result, out error);

        // The local seconds (the broken-down time read as UTC) and the instant.
        double localSeconds, instant;
        if (input.Kind == JqKind.Number)
        {
            instant = input.AsNumber();
            localSeconds = local ? Local(interpreter, instant) : instant;
        }
        else
        {
            if (!TrySeconds(input.AsArray(), out localSeconds))
                return Fail(Errors.RequiresParsedDatetime(name), out result, out error);
            instant = local ? localSeconds - Offset(interpreter, localSeconds - Offset(interpreter, localSeconds)) : localSeconds;
        }

        if (!Calendar.InRange(localSeconds) || !Calendar.InRange(instant))
            return Fail(Errors.CannotConvertSeconds, out result, out error);

        var time = Calendar.Break(localSeconds);
        var offset = local ? Offset(interpreter, instant) : 0;
        var epoch = (long)Calendar.Seconds(time.Year, time.Month, time.Day, time.Hours, time.Minutes, (long)Math.Floor(time.Seconds)) - offset;
        var zone = new Strftime.Zone(local, offset, local ? interpreter.Clock?.Abbreviation(instant) : null);
        if (!Strftime.TryFormat(time, epoch, zone, format.AsString(), out var text, out error))
            return Fail(error, out result, out error);
        return Ok(JqValue.FromString(text), out result, out error);
    }

    static bool Parse(Interpreter interpreter, JqValue input, JqValue[] arguments, out JqValue result, out JqValue error)
    {
        var format = arguments[0];
        if (input.Kind != JqKind.String || format.Kind != JqKind.String)
            return Fail(Errors.StrptimeStrings, out result, out error);
        if (!Strftime.TryParse(input.AsString(), format.AsString(), out var fields))
            return Fail(Errors.DateDoesNotMatch(input.AsString(), format.AsString()), out result, out error);
        var items = new JqValue[fields.Length];
        for (var i = 0; i < items.Length; i++)
            items[i] = JqValue.FromNumber(fields[i]);
        return Ok(JqValue.FromArray(JqArray.FromOwned(items)), out result, out error);
    }

    static JqValue ToValue(Calendar.Fields time)
        => JqValue.FromArray(JqArray.Of(
            JqValue.FromNumber(time.Year), JqValue.FromNumber(time.Month), JqValue.FromNumber(time.Day), JqValue.FromNumber(time.Hours),
            JqValue.FromNumber(time.Minutes), JqValue.FromNumber(time.Seconds), JqValue.FromNumber(time.Weekday), JqValue.FromNumber(time.YearDay)));
}
