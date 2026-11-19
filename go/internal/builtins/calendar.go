package builtins

import "math"

type calendar struct{}

// Calendar is the proleptic Gregorian calendar of §9.12 and the broken-down time [year, month,
// mday, hours, minutes, seconds, wday, yday] (month 0–11, wday Sunday = 0). Integer arithmetic with
// floor division; no host date type.
var Calendar calendar

// maxSeconds: |t| below 2^53 is the range of seconds gmtime accepts.
const maxSeconds = 9007199254740992.0

const secondsPerDay = 86400

// Fields is a broken-down time; Seconds may have a fraction.
type Fields struct {
	Year, Month, Day, Hours, Minutes int64
	Seconds                          float64
	Weekday, YearDay                 int64
}

// Days gives the days from 1970-01-01 to the date; month is 1–12, day may be 0 or exceed the month.
func (calendar) Days(year, month, day int64) int64 {
	if month <= 2 {
		year--
	}
	era := floorDiv(year, 400)
	yoe := year - era*400
	shift := int64(9)
	if month > 2 {
		shift = -3
	}
	doy := (153*(month+shift)+2)/5 + day - 1
	doe := yoe*365 + yoe/4 - yoe/100 + doy
	return era*146097 + doe - 719468
}

// InRange tells whether t can be broken down: finite and |t| < 2^53.
func (calendar) InRange(t float64) bool {
	return !math.IsNaN(t) && !math.IsInf(t, 0) && math.Abs(t) < maxSeconds
}

// Break gives the broken-down UTC time of t seconds since the epoch (in range), the fraction kept
// in the seconds.
func (calendar) Break(t float64) Fields {
	day := int64(math.Floor(t / secondsPerDay))
	rest := t - float64(day)*secondsPerDay
	// The division may round across a day boundary: bring the rest into [0, 86400).
	if rest < 0 {
		day--
		rest += secondsPerDay
	} else if rest >= secondsPerDay {
		day++
		rest -= secondsPerDay
	}

	year, month, mday := date(day)
	minutes := math.Floor(rest / 60)
	return Fields{year, month - 1, mday, int64(math.Floor(rest / 3600)), int64(minutes) % 60, rest - minutes*60,
		floorMod(day+4, 7), day - Calendar.Days(year, 1, 1)}
}

// Seconds gives the seconds since the epoch of broken-down fields read as UTC, out-of-range fields
// rolling over (C timegm).
func (calendar) Seconds(year, month, day, hours, minutes, seconds int64) float64 {
	y := year + floorDiv(month, 12)
	m := floorMod(month, 12)
	return float64(Calendar.Days(y, m+1, 1)+day-1)*secondsPerDay + float64(hours)*3600 + float64(minutes)*60 + float64(seconds)
}

// WeekdayAndYearDay gives the weekday (Sunday = 0) and day of the year of a date; month 0–11.
func (calendar) WeekdayAndYearDay(year, month, day int64) (weekday, yearDay int64) {
	days := Calendar.Days(year, month+1, day)
	return floorMod(days+4, 7), days - Calendar.Days(year, 1, 1)
}

// IsoWeek gives the ISO 8601 week-based year and week number of a date given by year, wday and yday.
func (calendar) IsoWeek(year, weekday, yearDay int64) (isoYear, week int64) {
	isoWeekday := floorMod(weekday+6, 7) + 1
	week = floorDiv(yearDay+1-isoWeekday+10, 7)
	if week < 1 {
		return year - 1, weeks(year - 1)
	}
	if week > weeks(year) {
		return year + 1, 1
	}
	return year, week
}

func weeks(year int64) int64 {
	if p(year) == 4 || p(year-1) == 3 {
		return 53
	}
	return 52
}

func p(y int64) int64 { return floorMod(y+floorDiv(y, 4)-floorDiv(y, 100)+floorDiv(y, 400), 7) }

// date is the inverse of Days: the date (month 1–12) of a day number.
func date(days int64) (year, month, day int64) {
	z := days + 719468
	era := floorDiv(z, 146097)
	doe := z - era*146097
	yoe := (doe - doe/1460 + doe/36524 - doe/146096) / 365
	doy := doe - (365*yoe + yoe/4 - yoe/100)
	mp := (5*doy + 2) / 153
	day = doy - (153*mp+2)/5 + 1
	if mp < 10 {
		month = mp + 3
	} else {
		month = mp - 9
	}
	year = yoe + era*400
	if month <= 2 {
		year++
	}
	return year, month, day
}

func floorDiv(a, b int64) int64 {
	q := a / b
	if a%b != 0 && (a < 0) != (b < 0) {
		q--
	}
	return q
}

func floorMod(a, b int64) int64 { return a - floorDiv(a, b)*b }
