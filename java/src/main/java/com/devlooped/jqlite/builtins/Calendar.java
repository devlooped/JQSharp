package com.devlooped.jqlite.builtins;

/**
 * The proleptic Gregorian calendar of §9.12 and the broken-down time
 * {@code [year, month, mday, hours, minutes, seconds, wday, yday]} (month 0–11, wday Sunday = 0).
 * Integer arithmetic with floor division; no host date type.
 */
final class Calendar {
    /** |t| below 2^53: the range of seconds {@code gmtime} accepts. */
    static final double MAX_SECONDS = 9007199254740992.0;

    private static final long SECONDS_PER_DAY = 86400;

    private Calendar() {
    }

    /** A broken-down time; {@code seconds} may have a fraction. */
    record Fields(long year, long month, long day, long hours, long minutes, double seconds, long weekday, long yearDay) {
    }

    /** The days from 1970-01-01 to the date; {@code month} is 1–12, {@code day} may be 0 or exceed the month. */
    static long days(long year, long month, long day) {
        year -= month <= 2 ? 1 : 0;
        long era = floorDiv(year, 400);
        long yoe = year - era * 400;
        long doy = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1;
        long doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return era * 146097 + doe - 719468;
    }

    /** Whether {@code t} can be broken down: finite and |t| &lt; 2^53. */
    static boolean inRange(double t) {
        return !Double.isNaN(t) && !Double.isInfinite(t) && Math.abs(t) < MAX_SECONDS;
    }

    /** The broken-down UTC time of {@code t} seconds since the epoch (in range), the fraction kept in the seconds. */
    static Fields breakDown(double t) {
        long day = (long) Math.floor(t / SECONDS_PER_DAY);
        double rest = t - (double) day * SECONDS_PER_DAY;
        // The division may round across a day boundary: bring the rest into [0, 86400).
        if (rest < 0) {
            day--;
            rest += SECONDS_PER_DAY;
        } else if (rest >= SECONDS_PER_DAY) {
            day++;
            rest -= SECONDS_PER_DAY;
        }

        long[] date = date(day);
        double minutes = Math.floor(rest / 60);
        return new Fields(date[0], date[1] - 1, date[2], (long) Math.floor(rest / 3600), (long) minutes % 60, rest - minutes * 60,
            floorMod(day + 4, 7), day - days(date[0], 1, 1));
    }

    /** Seconds since the epoch of broken-down fields read as UTC, out-of-range fields rolling over (C {@code timegm}). */
    static double seconds(long year, long month, long day, long hours, long minutes, long seconds) {
        long y = year + floorDiv(month, 12);
        long m = floorMod(month, 12);
        return (double) (days(y, m + 1, 1) + day - 1) * SECONDS_PER_DAY + (double) hours * 3600 + (double) minutes * 60 + seconds;
    }

    /** The weekday (Sunday = 0) and day of the year of a date; {@code month} 0–11. */
    static long[] weekdayAndYearDay(long year, long month, long day) {
        long days = days(year, month + 1, day);
        return new long[] {floorMod(days + 4, 7), days - days(year, 1, 1)};
    }

    /** The ISO 8601 week-based year and week number of a date given by year, wday and yday. */
    static long[] isoWeek(long year, long weekday, long yearDay) {
        long isoWeekday = floorMod(weekday + 6, 7) + 1;
        long week = floorDiv(yearDay + 1 - isoWeekday + 10, 7);
        if (week < 1) {
            return new long[] {year - 1, weeks(year - 1)};
        }
        if (week > weeks(year)) {
            return new long[] {year + 1, 1};
        }
        return new long[] {year, week};
    }

    private static long weeks(long year) {
        return p(year) == 4 || p(year - 1) == 3 ? 53 : 52;
    }

    private static long p(long y) {
        return floorMod(y + floorDiv(y, 4) - floorDiv(y, 100) + floorDiv(y, 400), 7);
    }

    // The inverse of days: the date (month 1–12) of a day number, as [year, month, day].
    private static long[] date(long days) {
        long z = days + 719468;
        long era = floorDiv(z, 146097);
        long doe = z - era * 146097;
        long yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
        long doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        long mp = (5 * doy + 2) / 153;
        long day = doy - (153 * mp + 2) / 5 + 1;
        long month = mp < 10 ? mp + 3 : mp - 9;
        return new long[] {yoe + era * 400 + (month <= 2 ? 1 : 0), month, day};
    }

    static long floorDiv(long a, long b) {
        return a / b - (a % b != 0 && (a < 0) != (b < 0) ? 1 : 0);
    }

    static long floorMod(long a, long b) {
        return a - floorDiv(a, b) * b;
    }
}
