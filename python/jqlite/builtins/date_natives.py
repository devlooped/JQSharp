"""§9.7 Dates, on the calendar and directive tables of §9.12 (DateNatives.cs).

The time and the local zone come only from the host's IClock; without one, the local zone is UTC
and `now` is an error.
"""

from __future__ import annotations

import math
from typing import TYPE_CHECKING, Any

from ..runtime.native_table import add_value
from ..values import errors
from ..values.jq_array import JqArray
from ..values.jq_value import Fail
from . import calendar, strftime

if TYPE_CHECKING:
    from ..values.jq_value import JqValue


def register() -> None:
    add_value("now", 0, lambda interpreter, input, arguments: float(interpreter.clock.now())
              if interpreter.clock is not None else Fail(errors.NO_CLOCK))
    add_value("gmtime", 0, lambda interpreter, input, arguments: _break_down(input) if type(input) is float
              else Fail(errors.GMTIME_NUMERIC))
    add_value("localtime", 0, lambda interpreter, input, arguments: _break_down(_local(interpreter, input))
              if type(input) is float else Fail(errors.LOCALTIME_NUMERIC))
    add_value("mktime", 0, _make_time)
    add_value("strftime", 1, lambda interpreter, input, arguments: _format(interpreter, input, arguments[0], False))
    add_value("strflocaltime", 1, lambda interpreter, input, arguments: _format(interpreter, input, arguments[0], True))
    add_value("strptime", 1, _parse)


def _break_down(t: float) -> JqValue | Fail:
    if not calendar.in_range(t):
        return Fail(errors.CANNOT_CONVERT_SECONDS)
    return _to_value(calendar.break_(t))


def _local(interpreter: Any, t: float) -> float:
    # t + offset(t): the local time of t as if it were UTC (NaN and infinities stay out of range).
    return t + _offset(interpreter, t) if calendar.in_range(t) else t


def _offset(interpreter: Any, t: float) -> int:
    clock = interpreter.clock
    return int(clock.offset(t)) if clock is not None else 0


def _make_time(interpreter: Any, input: JqValue, arguments: Any) -> JqValue | Fail:
    if type(input) is not JqArray:
        return Fail(errors.MKTIME_ARRAY)
    secs = _try_seconds(input)  # type: ignore[arg-type]
    return Fail(errors.MKTIME_PARSED) if secs is None else secs


def _try_seconds(time: JqArray) -> float | None:
    # mktime of a broken-down time: missing elements are 0, each truncated toward zero; an element
    # that is not a number, or is NaN or infinite, makes the time invalid.
    fields = [0] * 6
    items = time.to_list()
    for i in range(min(len(items), 8)):
        item = items[i]
        if type(item) is not float or item - item != 0.0:
            return None
        if i < 6:
            fields[i] = int(max(-calendar.MAX_SECONDS, min(calendar.MAX_SECONDS, float(math.trunc(item)))))
    return calendar.seconds(fields[0], fields[1], fields[2], fields[3], fields[4], fields[5])


def _format(interpreter: Any, input: JqValue, format: JqValue, local: bool) -> JqValue | Fail:
    name = "strflocaltime" if local else "strftime"
    t = type(input)
    if t is not float and t is not JqArray:
        return Fail(errors.requires_parsed_datetime(name))
    if type(format) is not str:
        return Fail(errors.requires_string_format(name))
    # The local seconds (the broken-down time read as UTC) and the instant.
    if t is float:
        instant: float = input  # type: ignore[assignment]
        local_seconds = _local(interpreter, instant) if local else instant
    else:
        secs = _try_seconds(input)  # type: ignore[arg-type]
        if secs is None:
            return Fail(errors.requires_parsed_datetime(name))
        local_seconds = secs
        instant = local_seconds - _offset(interpreter, local_seconds - _offset(interpreter, local_seconds)) if local else local_seconds
    if not calendar.in_range(local_seconds) or not calendar.in_range(instant):
        return Fail(errors.CANNOT_CONVERT_SECONDS)
    time = calendar.break_(local_seconds)
    offset = _offset(interpreter, instant) if local else 0
    epoch = int(calendar.seconds(time.year, time.month, time.day, time.hours, time.minutes, math.floor(time.seconds))) - offset
    clock = interpreter.clock
    zone = strftime.Zone(local, offset, clock.abbreviation(instant) if local and clock is not None else None)
    return strftime.try_format(time, epoch, zone, format)  # type: ignore[arg-type]


def _parse(interpreter: Any, input: JqValue, arguments: Any) -> JqValue | Fail:
    format = arguments[0]
    if type(input) is not str or type(format) is not str:
        return Fail(errors.STRPTIME_STRINGS)
    fields = strftime.try_parse(input, format)  # type: ignore[arg-type]
    if fields is None:
        return Fail(errors.date_does_not_match(input, format))  # type: ignore[arg-type]
    return JqArray.from_owned([float(f) for f in fields])


def _to_value(time: calendar.Fields) -> JqArray:
    return JqArray.of(float(time.year), float(time.month), float(time.day), float(time.hours), float(time.minutes),
                      float(time.seconds), float(time.weekday), float(time.year_day))
