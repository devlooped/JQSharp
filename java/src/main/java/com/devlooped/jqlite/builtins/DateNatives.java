package com.devlooped.jqlite.builtins;

import static com.devlooped.jqlite.builtins.NativeTable.add;
import static com.devlooped.jqlite.builtins.NativeTable.fail;

import com.devlooped.jqlite.runtime.Evaluation.IClock;
import com.devlooped.jqlite.runtime.Interpreter;
import com.devlooped.jqlite.values.Errors;
import com.devlooped.jqlite.values.JqArray;
import com.devlooped.jqlite.values.JqKind;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;

/**
 * §9.7 Dates, on the calendar and directive tables of §9.12. The time and the local zone come only
 * from the host's IClock; without one, the local zone is UTC and {@code now} is an error.
 */
final class DateNatives {
    private DateNatives() {
    }

    static void register() {
        add("now", 0, (interpreter, input, arguments) -> interpreter.clock() != null
            ? JqValue.fromNumber(interpreter.clock().now())
            : fail(Errors.noClock()));
        add("gmtime", 0, (interpreter, input, arguments) -> input.kind() != JqKind.NUMBER
            ? fail(Errors.gmtimeNumeric())
            : breakDown(input.asNumber()));
        add("localtime", 0, (interpreter, input, arguments) -> input.kind() != JqKind.NUMBER
            ? fail(Errors.localtimeNumeric())
            : breakDown(local(interpreter, input.asNumber())));
        add("mktime", 0, DateNatives::makeTime);
        add("strftime", 1, (interpreter, input, arguments) -> format(interpreter, input, arguments[0], false));
        add("strflocaltime", 1, (interpreter, input, arguments) -> format(interpreter, input, arguments[0], true));
        add("strptime", 1, DateNatives::parse);
    }

    private static Result breakDown(double t) {
        return Calendar.inRange(t) ? toValue(Calendar.breakDown(t)) : fail(Errors.cannotConvertSeconds());
    }

    // t + offset(t): the local time of t as if it were UTC (NaN and infinities stay out of range).
    private static double local(Interpreter interpreter, double t) {
        return Calendar.inRange(t) ? t + offset(interpreter, t) : t;
    }

    private static long offset(Interpreter interpreter, double t) {
        IClock clock = interpreter.clock();
        return clock == null ? 0 : clock.offset(t);
    }

    private static Result makeTime(Interpreter interpreter, JqValue input, JqValue[] arguments) {
        if (input.kind() != JqKind.ARRAY) {
            return fail(Errors.mktimeArray());
        }
        double[] seconds = new double[1];
        return trySeconds(input.asArray(), seconds) ? JqValue.fromNumber(seconds[0]) : fail(Errors.mktimeParsed());
    }

    // mktime of a broken-down time: missing elements are 0, each truncated toward zero; an element
    // that is not a number, or is NaN or infinite, makes the time invalid.
    private static boolean trySeconds(JqArray time, double[] seconds) {
        long[] fields = new long[6];
        for (int i = 0; i < time.count() && i < 8; i++) {
            JqValue item = time.get(i);
            if (item.kind() != JqKind.NUMBER || Double.isNaN(item.asNumber()) || Double.isInfinite(item.asNumber())) {
                seconds[0] = 0;
                return false;
            }

            if (i < 6) {
                fields[i] = (long) Math.clamp(MathExtra.truncate(item.asNumber()), -Calendar.MAX_SECONDS, Calendar.MAX_SECONDS);
            }
        }

        seconds[0] = Calendar.seconds(fields[0], fields[1], fields[2], fields[3], fields[4], fields[5]);
        return true;
    }

    private static Result format(Interpreter interpreter, JqValue input, JqValue format, boolean local) {
        String name = local ? "strflocaltime" : "strftime";
        if (input.kind() != JqKind.NUMBER && input.kind() != JqKind.ARRAY) {
            return fail(Errors.requiresParsedDatetime(name));
        }
        if (format.kind() != JqKind.STRING) {
            return fail(Errors.requiresStringFormat(name));
        }

        // The local seconds (the broken-down time read as UTC) and the instant.
        double localSeconds;
        double instant;
        if (input.kind() == JqKind.NUMBER) {
            instant = input.asNumber();
            localSeconds = local ? local(interpreter, instant) : instant;
        } else {
            double[] seconds = new double[1];
            if (!trySeconds(input.asArray(), seconds)) {
                return fail(Errors.requiresParsedDatetime(name));
            }
            localSeconds = seconds[0];
            instant = local ? localSeconds - offset(interpreter, localSeconds - offset(interpreter, localSeconds)) : localSeconds;
        }

        if (!Calendar.inRange(localSeconds) || !Calendar.inRange(instant)) {
            return fail(Errors.cannotConvertSeconds());
        }

        Calendar.Fields time = Calendar.breakDown(localSeconds);
        long offset = local ? offset(interpreter, instant) : 0;
        long epoch = (long) Calendar.seconds(time.year(), time.month(), time.day(), time.hours(), time.minutes(), (long) Math.floor(time.seconds())) - offset;
        IClock clock = interpreter.clock();
        Strftime.Zone zone = new Strftime.Zone(local, offset, local && clock != null ? clock.abbreviation(instant) : null);
        return Strftime.tryFormat(time, epoch, zone, format.asString());
    }

    private static Result parse(Interpreter interpreter, JqValue input, JqValue[] arguments) {
        JqValue format = arguments[0];
        if (input.kind() != JqKind.STRING || format.kind() != JqKind.STRING) {
            return fail(Errors.strptimeStrings());
        }
        long[] fields = Strftime.tryParse(input.asString(), format.asString());
        if (fields == null) {
            return fail(Errors.dateDoesNotMatch(input.asString(), format.asString()));
        }
        JqValue[] items = new JqValue[fields.length];
        for (int i = 0; i < items.length; i++) {
            items[i] = JqValue.fromNumber(fields[i]);
        }
        return JqArray.fromOwned(items);
    }

    private static JqValue toValue(Calendar.Fields time) {
        return JqArray.of(
            JqValue.fromNumber(time.year()), JqValue.fromNumber(time.month()), JqValue.fromNumber(time.day()), JqValue.fromNumber(time.hours()),
            JqValue.fromNumber(time.minutes()), JqValue.fromNumber(time.seconds()), JqValue.fromNumber(time.weekday()), JqValue.fromNumber(time.yearDay()));
    }
}
