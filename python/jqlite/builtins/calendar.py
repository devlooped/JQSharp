"""The proleptic Gregorian calendar of §9.12 and the broken-down time (Calendar.cs).

`[year, month, mday, hours, minutes, seconds, wday, yday]` (month 0-11, wday Sunday = 0). Integer
arithmetic with floor division; no host date type.
"""

from __future__ import annotations

import math
from typing import NamedTuple

# |t| below 2^53: the range of seconds gmtime accepts.
MAX_SECONDS = 9007199254740992.0

SECONDS_PER_DAY = 86400


class Fields(NamedTuple):
    """A broken-down time; seconds may have a fraction."""

    year: int
    month: int
    day: int
    hours: int
    minutes: int
    seconds: float
    weekday: int
    year_day: int


def days(year: int, month: int, day: int) -> int:
    """The days from 1970-01-01 to the date; month is 1-12, day may be 0 or exceed the month."""
    year -= 1 if month <= 2 else 0
    era = year // 400
    yoe = year - era * 400
    doy = (153 * (month + (-3 if month > 2 else 9)) + 2) // 5 + day - 1
    doe = yoe * 365 + yoe // 4 - yoe // 100 + doy
    return era * 146097 + doe - 719468


def in_range(t: float) -> bool:
    """Whether t can be broken down: finite and |t| < 2^53."""
    return t - t == 0.0 and abs(t) < MAX_SECONDS


def break_(t: float) -> Fields:
    """The broken-down UTC time of t seconds since the epoch (in range), the fraction kept in the seconds."""
    day = math.floor(t / SECONDS_PER_DAY)
    rest = t - float(day) * SECONDS_PER_DAY
    # The division may round across a day boundary: bring the rest into [0, 86400).
    if rest < 0:
        day -= 1
        rest += SECONDS_PER_DAY
    elif rest >= SECONDS_PER_DAY:
        day += 1
        rest -= SECONDS_PER_DAY
    year, month, mday = _date(day)
    minutes = float(math.floor(rest / 60))
    return Fields(year, month - 1, mday, math.floor(rest / 3600), int(minutes) % 60, rest - minutes * 60,
                  (day + 4) % 7, day - days(year, 1, 1))


def seconds(year: int, month: int, day: int, hours: int, minutes: int, secs: int) -> float:
    """Seconds since the epoch of broken-down fields read as UTC, out-of-range fields rolling over (C timegm)."""
    y = year + month // 12
    m = month % 12
    return float(days(y, m + 1, 1) + day - 1) * SECONDS_PER_DAY + float(hours) * 3600 + float(minutes) * 60 + float(secs)


def weekday_and_year_day(year: int, month: int, day: int) -> tuple[int, int]:
    """The weekday (Sunday = 0) and day of the year of a date; month 0-11."""
    d = days(year, month + 1, day)
    return (d + 4) % 7, d - days(year, 1, 1)


def iso_week(year: int, weekday: int, year_day: int) -> tuple[int, int]:
    """The ISO 8601 week-based year and week number of a date given by year, wday and yday."""
    iso_weekday = (weekday + 6) % 7 + 1
    week = (year_day + 1 - iso_weekday + 10) // 7
    if week < 1:
        return year - 1, _weeks(year - 1)
    if week > _weeks(year):
        return year + 1, 1
    return year, week


def _weeks(year: int) -> int:
    return 53 if _p(year) == 4 or _p(year - 1) == 3 else 52


def _p(y: int) -> int:
    return (y + y // 4 - y // 100 + y // 400) % 7


def _date(d: int) -> tuple[int, int, int]:
    # The inverse of days: the date (month 1-12) of a day number.
    z = d + 719468
    era = z // 146097
    doe = z - era * 146097
    yoe = (doe - doe // 1460 + doe // 36524 - doe // 146096) // 365
    doy = doe - (365 * yoe + yoe // 4 - yoe // 100)
    mp = (5 * doy + 2) // 153
    day = doy - (153 * mp + 2) // 5 + 1
    month = mp + 3 if mp < 10 else mp - 9
    return yoe + era * 400 + (1 if month <= 2 else 0), month, day
