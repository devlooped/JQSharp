package builtins

import (
	"math"

	"github.com/devlooped/jqlite/go/internal/values"
)

type dateNatives struct{}

// DateNatives are §9.7 Dates, on the calendar and directive tables of §9.12. The time and the local
// zone come only from the host's Clock; without one, the local zone is UTC and now is an error.
var DateNatives dateNatives

func (dateNatives) Register() {
	add("now", 0, func(in *interpreter, _ value, _ []value) (value, value, bool) {
		if in.Clock == nil {
			return fail(values.Errors.NoClock())
		}
		return ok(values.FromNumber(in.Clock.Now()))
	})
	add("gmtime", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) {
		if input.Kind() != values.KindNumber {
			return fail(values.Errors.GmtimeNumeric())
		}
		return breakDown(input.AsNumber())
	})
	add("localtime", 0, func(in *interpreter, input value, _ []value) (value, value, bool) {
		if input.Kind() != values.KindNumber {
			return fail(values.Errors.LocaltimeNumeric())
		}
		return breakDown(local(in, input.AsNumber()))
	})
	add("mktime", 0, makeTime)
	add("strftime", 1, func(in *interpreter, input value, arguments []value) (value, value, bool) {
		return formatTime(in, input, arguments[0], false)
	})
	add("strflocaltime", 1, func(in *interpreter, input value, arguments []value) (value, value, bool) {
		return formatTime(in, input, arguments[0], true)
	})
	add("strptime", 1, parseTime)
}

func breakDown(t float64) (value, value, bool) {
	if !Calendar.InRange(t) {
		return fail(values.Errors.CannotConvertSeconds())
	}
	return ok(toValue(Calendar.Break(t)))
}

// local gives t + offset(t): the local time of t as if it were UTC (NaN and infinities stay out of range).
func local(in *interpreter, t float64) float64 {
	if Calendar.InRange(t) {
		return t + float64(offset(in, t))
	}
	return t
}

func offset(in *interpreter, t float64) int64 {
	if in.Clock == nil {
		return 0
	}
	return int64(in.Clock.Offset(t))
}

func makeTime(_ *interpreter, input value, _ []value) (value, value, bool) {
	if input.Kind() != values.KindArray {
		return fail(values.Errors.MktimeArray())
	}
	seconds, valid := trySeconds(input.AsArray())
	if !valid {
		return fail(values.Errors.MktimeParsed())
	}
	return ok(values.FromNumber(seconds))
}

// trySeconds is mktime of a broken-down time: missing elements are 0, each truncated toward zero;
// an element that is not a number, or is NaN or infinite, makes the time invalid.
func trySeconds(time *values.Array) (float64, bool) {
	var fields [6]int64
	for i := 0; i < time.Count() && i < 8; i++ {
		item := time.At(i)
		if item.Kind() != values.KindNumber || math.IsNaN(item.AsNumber()) || math.IsInf(item.AsNumber(), 0) {
			return 0, false
		}
		if i < 6 {
			fields[i] = int64(max(-maxSeconds, min(maxSeconds, math.Trunc(item.AsNumber()))))
		}
	}
	return Calendar.Seconds(fields[0], fields[1], fields[2], fields[3], fields[4], fields[5]), true
}

func formatTime(in *interpreter, input, format value, isLocal bool) (value, value, bool) {
	name := "strftime"
	if isLocal {
		name = "strflocaltime"
	}
	if input.Kind() != values.KindNumber && input.Kind() != values.KindArray {
		return fail(values.Errors.RequiresParsedDatetime(name))
	}
	if format.Kind() != values.KindString {
		return fail(values.Errors.RequiresStringFormat(name))
	}

	// The local seconds (the broken-down time read as UTC) and the instant.
	var localSeconds, instant float64
	if input.Kind() == values.KindNumber {
		instant = input.AsNumber()
		localSeconds = instant
		if isLocal {
			localSeconds = local(in, instant)
		}
	} else {
		var valid bool
		if localSeconds, valid = trySeconds(input.AsArray()); !valid {
			return fail(values.Errors.RequiresParsedDatetime(name))
		}
		instant = localSeconds
		if isLocal {
			instant = localSeconds - float64(offset(in, localSeconds-float64(offset(in, localSeconds))))
		}
	}

	if !Calendar.InRange(localSeconds) || !Calendar.InRange(instant) {
		return fail(values.Errors.CannotConvertSeconds())
	}

	time := Calendar.Break(localSeconds)
	zoneOffset := int64(0)
	abbreviation, hasAbbreviation := "", false
	if isLocal {
		zoneOffset = offset(in, instant)
		if in.Clock != nil {
			abbreviation, hasAbbreviation = in.Clock.Abbreviation(instant)
		}
	}
	epoch := int64(Calendar.Seconds(time.Year, time.Month, time.Day, time.Hours, time.Minutes, int64(math.Floor(time.Seconds)))) - zoneOffset
	zone := Zone{isLocal, zoneOffset, abbreviation, hasAbbreviation}
	text, err, formatted := Strftime.TryFormat(time, epoch, zone, format.AsString())
	if !formatted {
		return fail(err)
	}
	return ok(values.FromString(text))
}

func parseTime(_ *interpreter, input value, arguments []value) (value, value, bool) {
	format := arguments[0]
	if input.Kind() != values.KindString || format.Kind() != values.KindString {
		return fail(values.Errors.StrptimeStrings())
	}
	fields, parsed := Strftime.TryParse(input.AsString(), format.AsString())
	if !parsed {
		return fail(values.Errors.DateDoesNotMatch(input.AsString(), format.AsString()))
	}
	items := make([]value, len(fields))
	for i, field := range fields {
		items[i] = values.FromNumber(float64(field))
	}
	return ok(values.FromArray(values.ArrayFromOwned(items)))
}

func toValue(time Fields) value {
	n := func(x int64) value { return values.FromNumber(float64(x)) }
	return values.FromArray(values.ArrayOf(n(time.Year), n(time.Month), n(time.Day), n(time.Hours),
		n(time.Minutes), values.FromNumber(time.Seconds), n(time.Weekday), n(time.YearDay)))
}
