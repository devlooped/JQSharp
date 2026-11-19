"""The strftime and strptime directive tables of §9.12: English names, no locale (Strftime.cs)."""

from __future__ import annotations

import math
from typing import NamedTuple

from ..values import errors
from ..values.jq_value import Fail
from . import calendar
from .calendar import Fields

_DAYS = ("Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday")
_MONTHS = ("January", "February", "March", "April", "May", "June", "July", "August", "September", "October",
           "November", "December")

# The directives made of others, by their conversion character; strptime reads them the same way.
_EXPANSIONS = {
    "c": "%a %b %e %H:%M:%S %Y",
    "D": "%m/%d/%y", "x": "%m/%d/%y",
    "F": "%Y-%m-%d",
    "r": "%I:%M:%S %p",
    "R": "%H:%M",
    "T": "%H:%M:%S", "X": "%H:%M:%S",
}


class Zone(NamedTuple):
    """The zone a time is formatted in: UTC, or the host's local zone at the instant."""

    local: bool
    offset: int
    abbreviation: str | None


def try_format(time: Fields, epoch: int, zone: Zone, format: str) -> str | Fail:
    """Formats normalized `time` (wday and yday consistent with the date); `epoch` is the instant in
    whole seconds, for %s."""
    out: list[str] = []
    failed = _try_append(out, time, epoch, zone, format)
    return failed if failed is not None else "".join(out)


def _try_append(out: list[str], time: Fields, epoch: int, zone: Zone, format: str) -> Fail | None:
    n = len(format)
    i = 0
    while i < n:
        if format[i] != "%":
            out.append(format[i])
            i += 1
            continue
        i += 1
        start = i
        if start == n:
            return Fail(errors.TRAILING_PERCENT)
        # GNU flags: the last of _ - 0 decides the padding of the numbers, ^ upper-cases.
        padding: str | None = None
        upper = False
        while i < n and format[i] in "_-0^":
            if format[i] == "^":
                upper = True
            else:
                padding = format[i]
            i += 1
        # E and O select a locale's alternative forms, which jqlite does not have.
        modifier = ""
        if i < n and format[i] in "EO":
            modifier = format[i]
            i += 1
        if i == n:
            return Fail(errors.unsupported_strftime(format[start:]))
        conversion = format[i]
        mark = len(out)
        if modifier == "E":
            known = conversion in "cCxXyY"
        elif modifier == "O":
            known = conversion in "deHImMSuUVwWy"
        else:
            known = True
        expansion = _EXPANSIONS.get(conversion) if known else None
        if expansion is not None:
            # Made of directives of the table: it cannot fail.
            _try_append(out, time, epoch, zone, expansion)
        elif not known or not _try_directive(out, conversion, padding, time, epoch, zone):
            return Fail(errors.unsupported_strftime(format[start:i + 1]))
        if upper and conversion != "P":
            for j in range(mark, len(out)):
                out[j] = _ascii_upper(out[j])
        i += 1
    return None


def _ascii_upper(text: str) -> str:
    return "".join(chr(ord(c) - 32) if "a" <= c <= "z" else c for c in text)


def _try_directive(out: list[str], conversion: str, padding: str | None, time: Fields, epoch: int, zone: Zone) -> bool:
    # A directive of the table that is not made of others; false when there is none.
    hours12 = 12 if time.hours % 12 == 0 else time.hours % 12
    c = conversion
    if c == "a":
        out.append(_DAYS[time.weekday][:3])
    elif c == "A":
        out.append(_DAYS[time.weekday])
    elif c == "b" or c == "h":
        out.append(_MONTHS[time.month][:3])
    elif c == "B":
        out.append(_MONTHS[time.month])
    elif c == "C":
        _number(out, time.year // 100, 2, "0", padding)
    elif c == "d":
        _number(out, time.day, 2, "0", padding)
    elif c == "e":
        _number(out, time.day, 2, " ", padding)
    elif c == "g":
        _number(out, calendar.iso_week(time.year, time.weekday, time.year_day)[0] % 100, 2, "0", padding)
    elif c == "G":
        _number(out, calendar.iso_week(time.year, time.weekday, time.year_day)[0], 4, "0", padding)
    elif c == "H":
        _number(out, time.hours, 2, "0", padding)
    elif c == "I":
        _number(out, hours12, 2, "0", padding)
    elif c == "k":
        _number(out, time.hours, 2, " ", padding)
    elif c == "l":
        _number(out, hours12, 2, " ", padding)
    elif c == "j":
        _number(out, time.year_day + 1, 3, "0", padding)
    elif c == "m":
        _number(out, time.month + 1, 2, "0", padding)
    elif c == "M":
        _number(out, time.minutes, 2, "0", padding)
    elif c == "n":
        out.append("\n")
    elif c == "t":
        out.append("\t")
    elif c == "p":
        out.append("AM" if time.hours < 12 else "PM")
    elif c == "P":
        out.append("am" if time.hours < 12 else "pm")
    elif c == "s":
        _number(out, epoch, 1, "0", None)
    elif c == "S":
        _number(out, math.floor(time.seconds), 2, "0", padding)
    elif c == "u":
        _number(out, (time.weekday + 6) % 7 + 1, 1, "0", padding)
    elif c == "w":
        _number(out, time.weekday, 1, "0", padding)
    elif c == "U":
        _number(out, (time.year_day + 7 - time.weekday) // 7, 2, "0", padding)
    elif c == "W":
        _number(out, (time.year_day + 7 - (time.weekday + 6) % 7) // 7, 2, "0", padding)
    elif c == "V":
        _number(out, calendar.iso_week(time.year, time.weekday, time.year_day)[1], 2, "0", padding)
    elif c == "y":
        _number(out, time.year % 100, 2, "0", padding)
    elif c == "Y":
        _number(out, time.year, 4, "0", padding)
    elif c == "z":
        _offset(out, zone.offset if zone.local else 0, True)
    elif c == "Z":
        if not zone.local:
            out.append("UTC")
        elif zone.abbreviation is not None:
            out.append(zone.abbreviation)
        else:
            _offset(out, zone.offset, _truncated_div(zone.offset, 60) % 60 != 0 if zone.offset >= 0
                    else -_truncated_div(zone.offset, 60) % 60 != 0)
    elif c == "%":
        out.append("%")
    else:
        return False
    return True


def _truncated_div(a: int, b: int) -> int:
    q = abs(a) // abs(b)
    return q if (a < 0) == (b < 0) else -q


def _offset(out: list[str], offset: int, minutes: bool) -> None:
    # +hhmm (or +hh) from an offset in seconds east of UTC.
    out.append("-" if offset < 0 else "+")
    total = abs(offset) // 60
    _number(out, total // 60, 2, "0", None)
    if minutes:
        _number(out, total % 60, 2, "0", None)


def _number(out: list[str], value: int, width: int, pad: str, padding: str | None) -> None:
    # Decimal, padded to the width with pad, unless the flag says otherwise (- no padding, _ spaces,
    # 0 zeros); a negative number is '-' and the padded digits.
    if padding == "-":
        width = 1
    elif padding is not None:
        pad = " " if padding == "_" else "0"
    if value < 0:
        out.append("-")
    digits = str(abs(value))
    if len(digits) < width:
        out.append(pad * (width - len(digits)))
    out.append(digits)


def try_parse(input: str, format: str) -> list[int] | None:
    """Parses `input` by `format` into a broken-down time; None on any mismatch."""
    year, month, day, hours, minutes, secs = 1900, 0, 0, 0, 0, 0
    weekday = -1
    year_day = -1
    # %I and %l read a 12-hour clock, which a PM read by %p moves to the afternoon.
    twelve_hour = False
    pm = False
    format = _expand(format)
    at = [0]
    n = len(format)
    i = 0
    while i < n:
        c = format[i]
        if c != "%":
            if _is_space(c):
                _skip_spaces(input, at)
            else:
                if at[0] == len(input) or input[at[0]] != c:
                    return None
                at[0] += 1
            i += 1
            continue
        i += 1
        if i == n:
            return None
        d = format[i]
        value: int | None
        ok = True
        if d == "Y":
            value = _read_number(input, at, 4, 0, 9999)
            ok = value is not None
            if ok:
                year = value  # type: ignore[assignment]
        elif d == "y":
            value = _read_number(input, at, 2, 0, 99)
            ok = value is not None
            if ok:
                year = value + (1900 if value >= 69 else 2000)  # type: ignore[operator]
        elif d == "m":
            value = _read_number(input, at, 2, 1, 12)
            ok = value is not None
            if ok:
                month = value - 1  # type: ignore[operator]
        elif d == "d" or d == "e":
            value = _read_number(input, at, 2, 1, 31)
            ok = value is not None
            if ok:
                day = value  # type: ignore[assignment]
        elif d == "H" or d == "k":
            value = _read_number(input, at, 2, 0, 23)
            ok = value is not None
            if ok:
                hours = value  # type: ignore[assignment]
            twelve_hour = False
        elif d == "I" or d == "l":
            value = _read_number(input, at, 2, 1, 12)
            ok = value is not None
            if ok:
                hours = value % 12  # type: ignore[operator]
            twelve_hour = True
        elif d == "p":
            meridian = _read_meridian(input, at)
            ok = meridian is not None
            if ok:
                pm = meridian  # type: ignore[assignment]
        elif d == "M":
            value = _read_number(input, at, 2, 0, 59)
            ok = value is not None
            if ok:
                minutes = value  # type: ignore[assignment]
        elif d == "S":
            value = _read_number(input, at, 2, 0, 60)
            ok = value is not None
            if ok:
                secs = value  # type: ignore[assignment]
        elif d == "j":
            value = _read_number(input, at, 3, 1, 366)
            ok = value is not None
            if ok:
                year_day = value - 1  # type: ignore[operator]
        elif d == "a" or d == "A":
            value = _read_name(input, at, _DAYS)
            ok = value is not None
            if ok:
                weekday = value  # type: ignore[assignment]
        elif d == "u":
            value = _read_number(input, at, 1, 1, 7)
            ok = value is not None
            if ok:
                weekday = value % 7  # type: ignore[operator]
        elif d == "w":
            value = _read_number(input, at, 1, 0, 6)
            ok = value is not None
            if ok:
                weekday = value  # type: ignore[assignment]
        elif d == "b" or d == "h" or d == "B":
            value = _read_name(input, at, _MONTHS)
            ok = value is not None
            if ok:
                month = value  # type: ignore[assignment]
        elif d == "z":
            ok = _read_offset(input, at)
        elif d == "Z":
            ok = _read_word(input, at)
        elif d == "n" or d == "t":
            _skip_spaces(input, at)
        elif d == "%":
            ok = at[0] < len(input) and input[at[0]] == "%"
            at[0] += 1
        else:
            ok = False
        if not ok:
            return None
        i += 1
    _skip_spaces(input, at)
    if at[0] != len(input):
        return None
    if twelve_hour and pm:
        hours += 12
    computed_weekday, computed_year_day = calendar.weekday_and_year_day(year, month, day)
    return [year, month, day, hours, minutes, secs, weekday if weekday >= 0 else computed_weekday,
            year_day if year_day >= 0 else computed_year_day]


def _expand(format: str) -> str:
    # The format with the directives made of others replaced by what they are made of.
    out: list[str] = []
    n = len(format)
    i = 0
    while i < n:
        if format[i] == "%" and i + 1 < n:
            i += 1
            nxt = format[i]
            expansion = _EXPANSIONS.get(nxt)
            out.append(expansion if expansion is not None else "%" + nxt)
        else:
            out.append(format[i])
        i += 1
    return "".join(out)


def _read_meridian(input: str, at: list[int]) -> bool | None:
    # AM or PM, ASCII case-insensitive.
    p = at[0]
    if p + 2 > len(input):
        return None
    word = _ascii_lower(input[p:p + 2])
    if word != "pm" and word != "am":
        return None
    at[0] = p + 2
    return word == "pm"


def _read_number(input: str, at: list[int], width: int, low: int, high: int) -> int | None:
    # Skips input whitespace, then reads 1 to width ASCII digits in [low, high].
    _skip_spaces(input, at)
    p = at[0]
    value = 0
    count = 0
    while count < width and p < len(input) and "0" <= input[p] <= "9":
        value = value * 10 + (ord(input[p]) - 48)
        p += 1
        count += 1
    at[0] = p
    return value if count > 0 and low <= value <= high else None


def _read_name(input: str, at: list[int], names: tuple[str, ...]) -> int | None:
    # A name of the table, full or its first three letters, ASCII case-insensitive; full names first.
    p = at[0]
    for full in (True, False):
        for i, name in enumerate(names):
            if not full:
                name = name[:3]
            if p + len(name) <= len(input) and _ascii_lower(input[p:p + len(name)]) == _ascii_lower(name):
                at[0] = p + len(name)
                return i
    return None


def _ascii_lower(text: str) -> str:
    return "".join(chr(ord(c) + 32) if "A" <= c <= "Z" else c for c in text)


def _read_offset(input: str, at: list[int]) -> bool:
    # Z, or + or - with hh, hhmm or hh:mm; read and ignored.
    p = at[0]
    if p < len(input) and input[p] == "Z":
        at[0] = p + 1
        return True
    if p == len(input) or input[p] not in "+-" or not _two_digits(input, p + 1):
        return False
    p += 3
    if _two_digits(input, p):
        p += 2
    elif p < len(input) and input[p] == ":" and _two_digits(input, p + 1):
        p += 3
    at[0] = p
    return True


def _two_digits(input: str, p: int) -> bool:
    return p + 2 <= len(input) and "0" <= input[p] <= "9" and "0" <= input[p + 1] <= "9"


def _read_word(input: str, at: list[int]) -> bool:
    # A run of non-whitespace characters; read and ignored.
    start = p = at[0]
    while p < len(input) and not _is_space(input[p]):
        p += 1
    at[0] = p
    return p > start


def _skip_spaces(input: str, at: list[int]) -> None:
    p = at[0]
    while p < len(input) and _is_space(input[p]):
        p += 1
    at[0] = p


def _is_space(c: str) -> bool:
    return c == " " or "\t" <= c <= "\r"
