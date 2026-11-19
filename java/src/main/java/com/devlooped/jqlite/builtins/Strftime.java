package com.devlooped.jqlite.builtins;

import com.devlooped.jqlite.values.Errors;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;

/** The strftime and strptime directive tables of §9.12: English names, no locale. */
final class Strftime {
    private static final String[] DAYS = {"Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"};
    private static final String[] MONTHS = {"January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"};

    private Strftime() {
    }

    /** The zone a time is formatted in: UTC, or the host's local zone at the instant. */
    record Zone(boolean local, long offset, String abbreviation) {
    }

    /**
     * Formats normalized {@code time} (wday and yday consistent with the date); {@code epoch} is the
     * instant in whole seconds, for {@code %s}. The result is the text, or the error.
     */
    static Result tryFormat(Calendar.Fields time, long epoch, Zone zone, String format) {
        StringBuilder builder = new StringBuilder(format.length() + 16);
        JqValue error = tryAppend(builder, time, epoch, zone, format);
        return error == null ? JqValue.fromString(builder.toString()) : Result.err(error);
    }

    // The directives made of others, by their conversion character; strptime reads them the same way.
    private static String expansion(char conversion) {
        return switch (conversion) {
            case 'c' -> "%a %b %e %H:%M:%S %Y";
            case 'D', 'x' -> "%m/%d/%y";
            case 'F' -> "%Y-%m-%d";
            case 'r' -> "%I:%M:%S %p";
            case 'R' -> "%H:%M";
            case 'T', 'X' -> "%H:%M:%S";
            default -> null;
        };
    }

    // Appends the formatted text; Java null, or the error.
    private static JqValue tryAppend(StringBuilder builder, Calendar.Fields time, long epoch, Zone zone, String format) {
        for (int i = 0; i < format.length(); i++) {
            if (format.charAt(i) != '%') {
                builder.append(format.charAt(i));
                continue;
            }

            int start = ++i;
            if (start == format.length()) {
                return Errors.trailingPercent();
            }

            // GNU flags: the last of _ - 0 decides the padding of the numbers, ^ upper-cases.
            char padding = 0;
            boolean upper = false;
            for (; i < format.length() && "_-0^".indexOf(format.charAt(i)) >= 0; i++) {
                if (format.charAt(i) == '^') {
                    upper = true;
                } else {
                    padding = format.charAt(i);
                }
            }

            // E and O select a locale's alternative forms, which jqlite does not have.
            char modifier = i < format.length() && (format.charAt(i) == 'E' || format.charAt(i) == 'O') ? format.charAt(i++) : 0;
            if (i == format.length()) {
                return Errors.unsupportedStrftime(format.substring(start));
            }

            char conversion = format.charAt(i);
            int mark = builder.length();
            boolean known = switch (modifier) {
                case 'E' -> "cCxXyY".indexOf(conversion) >= 0;
                case 'O' -> "deHImMSuUVwWy".indexOf(conversion) >= 0;
                default -> true;
            };
            String expansion = known ? expansion(conversion) : null;
            if (expansion != null) {
                // Made of directives of the table: it cannot fail.
                tryAppend(builder, time, epoch, zone, expansion);
            } else if (!known || !tryDirective(builder, conversion, padding, time, epoch, zone)) {
                int length = Character.isHighSurrogate(conversion) && i + 1 < format.length() ? 2 : 1;
                return Errors.unsupportedStrftime(format.substring(start, i + length));
            }

            if (upper && conversion != 'P') {
                for (int j = mark; j < builder.length(); j++) {
                    char c = builder.charAt(j);
                    if (c >= 'a' && c <= 'z') {
                        builder.setCharAt(j, (char) (c - 32));
                    }
                }
            }
        }

        return null;
    }

    // A directive of the table that is not made of others; false when there is none.
    private static boolean tryDirective(StringBuilder builder, char conversion, char padding, Calendar.Fields time, long epoch, Zone zone) {
        long hours12 = time.hours() % 12 == 0 ? 12 : time.hours() % 12;
        switch (conversion) {
            case 'a' -> builder.append(DAYS[(int) time.weekday()], 0, 3);
            case 'A' -> builder.append(DAYS[(int) time.weekday()]);
            case 'b', 'h' -> builder.append(MONTHS[(int) time.month()], 0, 3);
            case 'B' -> builder.append(MONTHS[(int) time.month()]);
            case 'C' -> number(builder, Calendar.floorDiv(time.year(), 100), 2, '0', padding);
            case 'd' -> number(builder, time.day(), 2, '0', padding);
            case 'e' -> number(builder, time.day(), 2, ' ', padding);
            case 'g' -> number(builder, Calendar.floorMod(Calendar.isoWeek(time.year(), time.weekday(), time.yearDay())[0], 100), 2, '0', padding);
            case 'G' -> number(builder, Calendar.isoWeek(time.year(), time.weekday(), time.yearDay())[0], 4, '0', padding);
            case 'H' -> number(builder, time.hours(), 2, '0', padding);
            case 'I' -> number(builder, hours12, 2, '0', padding);
            case 'k' -> number(builder, time.hours(), 2, ' ', padding);
            case 'l' -> number(builder, hours12, 2, ' ', padding);
            case 'j' -> number(builder, time.yearDay() + 1, 3, '0', padding);
            case 'm' -> number(builder, time.month() + 1, 2, '0', padding);
            case 'M' -> number(builder, time.minutes(), 2, '0', padding);
            case 'n' -> builder.append('\n');
            case 't' -> builder.append('\t');
            case 'p' -> builder.append(time.hours() < 12 ? "AM" : "PM");
            case 'P' -> builder.append(time.hours() < 12 ? "am" : "pm");
            case 's' -> number(builder, epoch, 1, '0', (char) 0);
            case 'S' -> number(builder, (long) Math.floor(time.seconds()), 2, '0', padding);
            case 'u' -> number(builder, Calendar.floorMod(time.weekday() + 6, 7) + 1, 1, '0', padding);
            case 'w' -> number(builder, time.weekday(), 1, '0', padding);
            case 'U' -> number(builder, Calendar.floorDiv(time.yearDay() + 7 - time.weekday(), 7), 2, '0', padding);
            case 'W' -> number(builder, Calendar.floorDiv(time.yearDay() + 7 - Calendar.floorMod(time.weekday() + 6, 7), 7), 2, '0', padding);
            case 'V' -> number(builder, Calendar.isoWeek(time.year(), time.weekday(), time.yearDay())[1], 2, '0', padding);
            case 'y' -> number(builder, Calendar.floorMod(time.year(), 100), 2, '0', padding);
            case 'Y' -> number(builder, time.year(), 4, '0', padding);
            case 'z' -> offset(builder, zone.local() ? zone.offset() : 0, true);
            case 'Z' -> {
                if (!zone.local()) {
                    builder.append("UTC");
                } else if (zone.abbreviation() != null) {
                    builder.append(zone.abbreviation());
                } else {
                    offset(builder, zone.offset(), zone.offset() / 60 % 60 != 0);
                }
            }
            case '%' -> builder.append('%');
            default -> {
                return false;
            }
        }

        return true;
    }

    // +hhmm (or +hh) from an offset in seconds east of UTC.
    private static void offset(StringBuilder builder, long offset, boolean minutes) {
        builder.append(offset < 0 ? '-' : '+');
        long total = Math.abs(offset) / 60;
        number(builder, total / 60, 2, '0', (char) 0);
        if (minutes) {
            number(builder, total % 60, 2, '0', (char) 0);
        }
    }

    // Decimal, padded to the width with pad, unless the flag says otherwise (- no padding, _ spaces,
    // 0 zeros; 0 as a char: no flag); a negative number is '-' and the padded digits.
    private static void number(StringBuilder builder, long value, int width, char pad, char padding) {
        if (padding == '-') {
            width = 1;
        } else if (padding != 0) {
            pad = padding == '_' ? ' ' : '0';
        }
        if (value < 0) {
            builder.append('-');
        }
        StringBuilder digits = new StringBuilder();
        long rest = value;
        do {
            digits.insert(0, (char) ('0' + Math.abs(rest % 10)));
            rest /= 10;
        } while (rest != 0);
        for (int i = digits.length(); i < width; i++) {
            builder.append(pad);
        }
        builder.append(digits);
    }

    /** Parses {@code input} by {@code format} into a broken-down time; Java null on any mismatch. */
    static long[] tryParse(String input, String format) {
        long year = 1900;
        long month = 0;
        long day = 0;
        long hours = 0;
        long minutes = 0;
        long seconds = 0;
        long weekday = -1;
        long yearDay = -1;
        // %I and %l read a 12-hour clock, which a PM read by %p moves to the afternoon.
        boolean twelveHour = false;
        boolean pm = false;
        format = expand(format);
        int[] at = {0};
        long[] value = new long[1];
        for (int i = 0; i < format.length(); i++) {
            char c = format.charAt(i);
            if (c != '%') {
                if (isSpace(c)) {
                    skipSpaces(input, at);
                } else {
                    if (at[0] == input.length() || input.charAt(at[0]) != c) {
                        return null;
                    }
                    at[0]++;
                }

                continue;
            }

            if (++i == format.length()) {
                return null;
            }
            boolean ok;
            switch (format.charAt(i)) {
                case 'Y' -> {
                    ok = readNumber(input, at, 4, 0, 9999, value);
                    year = value[0];
                }
                case 'y' -> {
                    ok = readNumber(input, at, 2, 0, 99, value);
                    year = value[0] + (value[0] >= 69 ? 1900 : 2000);
                }
                case 'm' -> {
                    ok = readNumber(input, at, 2, 1, 12, value);
                    month = value[0] - 1;
                }
                case 'd', 'e' -> {
                    ok = readNumber(input, at, 2, 1, 31, value);
                    day = value[0];
                }
                case 'H', 'k' -> {
                    ok = readNumber(input, at, 2, 0, 23, value);
                    hours = value[0];
                    twelveHour = false;
                }
                case 'I', 'l' -> {
                    ok = readNumber(input, at, 2, 1, 12, value);
                    hours = value[0] % 12;
                    twelveHour = true;
                }
                case 'p' -> {
                    int meridian = readMeridian(input, at);
                    ok = meridian >= 0;
                    pm = meridian == 1;
                }
                case 'M' -> {
                    ok = readNumber(input, at, 2, 0, 59, value);
                    minutes = value[0];
                }
                case 'S' -> {
                    ok = readNumber(input, at, 2, 0, 60, value);
                    seconds = value[0];
                }
                case 'j' -> {
                    ok = readNumber(input, at, 3, 1, 366, value);
                    yearDay = value[0] - 1;
                }
                case 'a', 'A' -> {
                    ok = readName(input, at, DAYS, value);
                    weekday = value[0];
                }
                case 'u' -> {
                    ok = readNumber(input, at, 1, 1, 7, value);
                    weekday = value[0] % 7;
                }
                case 'w' -> {
                    ok = readNumber(input, at, 1, 0, 6, value);
                    weekday = value[0];
                }
                case 'b', 'h', 'B' -> {
                    ok = readName(input, at, MONTHS, value);
                    month = value[0];
                }
                case 'z' -> ok = readOffset(input, at);
                case 'Z' -> ok = readWord(input, at);
                case 'n', 't' -> {
                    skipSpaces(input, at);
                    ok = true;
                }
                case '%' -> ok = at[0] < input.length() && input.charAt(at[0]++) == '%';
                default -> ok = false;
            }

            if (!ok) {
                return null;
            }
        }

        skipSpaces(input, at);
        if (at[0] != input.length()) {
            return null;
        }
        if (twelveHour && pm) {
            hours += 12;
        }

        long[] computed = Calendar.weekdayAndYearDay(year, month, day);
        return new long[] {year, month, day, hours, minutes, seconds, weekday >= 0 ? weekday : computed[0], yearDay >= 0 ? yearDay : computed[1]};
    }

    // The format with the directives made of others replaced by what they are made of.
    private static String expand(String format) {
        StringBuilder builder = new StringBuilder(format.length());
        for (int i = 0; i < format.length(); i++) {
            if (format.charAt(i) == '%' && i + 1 < format.length()) {
                char next = format.charAt(++i);
                String expansion = expansion(next);
                if (expansion != null) {
                    builder.append(expansion);
                } else {
                    builder.append('%').append(next);
                }
            } else {
                builder.append(format.charAt(i));
            }
        }

        return builder.toString();
    }

    // AM (0) or PM (1), ASCII case-insensitive; -1 for neither.
    private static int readMeridian(String input, int[] at) {
        boolean fits = at[0] + 2 <= input.length();
        boolean pm = fits && asciiEqualsIgnoreCase(input, at[0], "PM");
        if (!pm && !(fits && asciiEqualsIgnoreCase(input, at[0], "AM"))) {
            return -1;
        }
        at[0] += 2;
        return pm ? 1 : 0;
    }

    // Skips input whitespace, then reads 1 to width ASCII digits in [min, max].
    private static boolean readNumber(String input, int[] at, int width, long min, long max, long[] value) {
        skipSpaces(input, at);
        value[0] = 0;
        int count = 0;
        while (count < width && at[0] < input.length() && input.charAt(at[0]) >= '0' && input.charAt(at[0]) <= '9') {
            value[0] = value[0] * 10 + (input.charAt(at[0]++) - '0');
            count++;
        }

        return count > 0 && value[0] >= min && value[0] <= max;
    }

    // A name of the table, full or its first three letters, ASCII case-insensitive; full names first.
    private static boolean readName(String input, int[] at, String[] names, long[] index) {
        for (int length = 0; length < 2; length++) {
            for (int i = 0; i < names.length; i++) {
                String name = length == 0 ? names[i] : names[i].substring(0, 3);
                if (at[0] + name.length() <= input.length() && asciiEqualsIgnoreCase(input, at[0], name)) {
                    at[0] += name.length();
                    index[0] = i;
                    return true;
                }
            }
        }

        index[0] = -1;
        return false;
    }

    private static boolean asciiEqualsIgnoreCase(String input, int at, String name) {
        for (int i = 0; i < name.length(); i++) {
            char a = input.charAt(at + i);
            char b = name.charAt(i);
            if ((a >= 'A' && a <= 'Z' ? (char) (a + 32) : a) != (b >= 'A' && b <= 'Z' ? (char) (b + 32) : b)) {
                return false;
            }
        }

        return true;
    }

    // Z, or + or - with hh, hhmm or hh:mm; read and ignored.
    private static boolean readOffset(String input, int[] at) {
        if (at[0] < input.length() && input.charAt(at[0]) == 'Z') {
            at[0]++;
            return true;
        }

        if (at[0] == input.length() || (input.charAt(at[0]) != '+' && input.charAt(at[0]) != '-') || !twoDigits(input, at[0] + 1)) {
            return false;
        }
        at[0] += 3;
        if (twoDigits(input, at[0])) {
            at[0] += 2;
        } else if (at[0] < input.length() && input.charAt(at[0]) == ':' && twoDigits(input, at[0] + 1)) {
            at[0] += 3;
        }
        return true;
    }

    private static boolean twoDigits(String input, int at) {
        return at + 2 <= input.length() && input.charAt(at) >= '0' && input.charAt(at) <= '9'
            && input.charAt(at + 1) >= '0' && input.charAt(at + 1) <= '9';
    }

    // A run of non-whitespace characters; read and ignored.
    private static boolean readWord(String input, int[] at) {
        int start = at[0];
        while (at[0] < input.length() && !isSpace(input.charAt(at[0]))) {
            at[0]++;
        }
        return at[0] > start;
    }

    private static void skipSpaces(String input, int[] at) {
        while (at[0] < input.length() && isSpace(input.charAt(at[0]))) {
            at[0]++;
        }
    }

    private static boolean isSpace(char c) {
        return c == ' ' || (c >= '\t' && c <= '\r');
    }
}
