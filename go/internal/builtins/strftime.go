package builtins

import (
	"math"
	"strings"
	"unicode/utf8"

	"github.com/devlooped/jqlite/go/internal/values"
)

type strftime struct{}

// Strftime holds the strftime and strptime directive tables of §9.12: English names, no locale.
var Strftime strftime

var (
	dayNames   = []string{"Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"}
	monthNames = []string{"January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"}
)

// Zone is the zone a time is formatted in: UTC, or the host's local zone at the instant.
type Zone struct {
	Local           bool
	Offset          int64
	Abbreviation    string
	HasAbbreviation bool
}

// TryFormat formats normalized time (wday and yday consistent with the date); epoch is the instant
// in whole seconds, for %s.
func (strftime) TryFormat(time Fields, epoch int64, zone Zone, format string) (string, value, bool) {
	builder := make([]byte, 0, len(format)+16)
	builder, err, success := tryAppend(builder, time, epoch, zone, format)
	if !success {
		return "", err, false
	}
	return string(builder), values.Null, true
}

// expansion gives the directives made of others, by their conversion character; strptime reads
// them the same way.
func expansion(conversion byte) (string, bool) {
	switch conversion {
	case 'c':
		return "%a %b %e %H:%M:%S %Y", true
	case 'D', 'x':
		return "%m/%d/%y", true
	case 'F':
		return "%Y-%m-%d", true
	case 'r':
		return "%I:%M:%S %p", true
	case 'R':
		return "%H:%M", true
	case 'T', 'X':
		return "%H:%M:%S", true
	}
	return "", false
}

func tryAppend(builder []byte, time Fields, epoch int64, zone Zone, format string) ([]byte, value, bool) {
	for i := 0; i < len(format); i++ {
		if format[i] != '%' {
			builder = append(builder, format[i])
			continue
		}

		i++
		start := i
		if start == len(format) {
			return builder, values.Errors.TrailingPercent(), false
		}

		// GNU flags: the last of _ - 0 decides the padding of the numbers, ^ upper-cases.
		var padding byte
		upper := false
		for ; i < len(format) && (format[i] == '_' || format[i] == '-' || format[i] == '0' || format[i] == '^'); i++ {
			if format[i] == '^' {
				upper = true
			} else {
				padding = format[i]
			}
		}

		// E and O select a locale's alternative forms, which jqlite does not have.
		var modifier byte
		if i < len(format) && (format[i] == 'E' || format[i] == 'O') {
			modifier = format[i]
			i++
		}
		if i == len(format) {
			return builder, values.Errors.UnsupportedStrftime(format[start:]), false
		}

		conversion := format[i]
		mark := len(builder)
		known := true
		switch modifier {
		case 'E':
			known = strings.IndexByte("cCxXyY", conversion) >= 0
		case 'O':
			known = strings.IndexByte("deHImMSuUVwWy", conversion) >= 0
		}
		if text, made := expansion(conversion); known && made {
			// Made of directives of the table: it cannot fail.
			builder, _, _ = tryAppend(builder, time, epoch, zone, text)
		} else {
			var done bool
			if known {
				builder, done = tryDirective(builder, conversion, padding, time, epoch, zone)
			}
			if !done {
				_, size := utf8.DecodeRuneInString(format[i:])
				return builder, values.Errors.UnsupportedStrftime(format[start : i+size]), false
			}
		}

		if upper && conversion != 'P' {
			for j := mark; j < len(builder); j++ {
				if builder[j] >= 'a' && builder[j] <= 'z' {
					builder[j] -= 32
				}
			}
		}
	}
	return builder, values.Null, true
}

// tryDirective appends a directive of the table that is not made of others; false when there is none.
func tryDirective(builder []byte, conversion, padding byte, time Fields, epoch int64, zone Zone) ([]byte, bool) {
	hours12 := time.Hours % 12
	if hours12 == 0 {
		hours12 = 12
	}
	switch conversion {
	case 'a':
		builder = append(builder, dayNames[time.Weekday][:3]...)
	case 'A':
		builder = append(builder, dayNames[time.Weekday]...)
	case 'b', 'h':
		builder = append(builder, monthNames[time.Month][:3]...)
	case 'B':
		builder = append(builder, monthNames[time.Month]...)
	case 'C':
		builder = number(builder, floorDiv(time.Year, 100), 2, '0', padding)
	case 'd':
		builder = number(builder, time.Day, 2, '0', padding)
	case 'e':
		builder = number(builder, time.Day, 2, ' ', padding)
	case 'g':
		year, _ := Calendar.IsoWeek(time.Year, time.Weekday, time.YearDay)
		builder = number(builder, floorMod(year, 100), 2, '0', padding)
	case 'G':
		year, _ := Calendar.IsoWeek(time.Year, time.Weekday, time.YearDay)
		builder = number(builder, year, 4, '0', padding)
	case 'H':
		builder = number(builder, time.Hours, 2, '0', padding)
	case 'I':
		builder = number(builder, hours12, 2, '0', padding)
	case 'k':
		builder = number(builder, time.Hours, 2, ' ', padding)
	case 'l':
		builder = number(builder, hours12, 2, ' ', padding)
	case 'j':
		builder = number(builder, time.YearDay+1, 3, '0', padding)
	case 'm':
		builder = number(builder, time.Month+1, 2, '0', padding)
	case 'M':
		builder = number(builder, time.Minutes, 2, '0', padding)
	case 'n':
		builder = append(builder, '\n')
	case 't':
		builder = append(builder, '\t')
	case 'p':
		if time.Hours < 12 {
			builder = append(builder, "AM"...)
		} else {
			builder = append(builder, "PM"...)
		}
	case 'P':
		if time.Hours < 12 {
			builder = append(builder, "am"...)
		} else {
			builder = append(builder, "pm"...)
		}
	case 's':
		builder = number(builder, epoch, 1, '0', 0)
	case 'S':
		builder = number(builder, int64(math.Floor(time.Seconds)), 2, '0', padding)
	case 'u':
		builder = number(builder, floorMod(time.Weekday+6, 7)+1, 1, '0', padding)
	case 'w':
		builder = number(builder, time.Weekday, 1, '0', padding)
	case 'U':
		builder = number(builder, floorDiv(time.YearDay+7-time.Weekday, 7), 2, '0', padding)
	case 'W':
		builder = number(builder, floorDiv(time.YearDay+7-floorMod(time.Weekday+6, 7), 7), 2, '0', padding)
	case 'V':
		_, week := Calendar.IsoWeek(time.Year, time.Weekday, time.YearDay)
		builder = number(builder, week, 2, '0', padding)
	case 'y':
		builder = number(builder, floorMod(time.Year, 100), 2, '0', padding)
	case 'Y':
		builder = number(builder, time.Year, 4, '0', padding)
	case 'z':
		zoneOffset := int64(0)
		if zone.Local {
			zoneOffset = zone.Offset
		}
		builder = appendOffset(builder, zoneOffset, true)
	case 'Z':
		switch {
		case !zone.Local:
			builder = append(builder, "UTC"...)
		case zone.HasAbbreviation:
			builder = append(builder, zone.Abbreviation...)
		default:
			builder = appendOffset(builder, zone.Offset, zone.Offset/60%60 != 0)
		}
	case '%':
		builder = append(builder, '%')
	default:
		return builder, false
	}
	return builder, true
}

// appendOffset appends +hhmm (or +hh) from an offset in seconds east of UTC.
func appendOffset(builder []byte, offset int64, minutes bool) []byte {
	if offset < 0 {
		builder = append(builder, '-')
		offset = -offset
	} else {
		builder = append(builder, '+')
	}
	total := offset / 60
	builder = number(builder, total/60, 2, '0', 0)
	if minutes {
		builder = number(builder, total%60, 2, '0', 0)
	}
	return builder
}

// number appends a decimal, padded to the width with pad, unless the flag says otherwise (- no
// padding, _ spaces, 0 zeros); a negative number is '-' and the padded digits.
func number(builder []byte, v int64, width int, pad, padding byte) []byte {
	switch padding {
	case '-':
		width = 1
	case '_':
		pad = ' '
	case '0':
		pad = '0'
	}
	if v < 0 {
		builder = append(builder, '-')
	}
	var digits [20]byte
	n := len(digits)
	rest := v
	for {
		d := rest % 10
		if d < 0 {
			d = -d
		}
		n--
		digits[n] = byte('0' + d)
		rest /= 10
		if rest == 0 {
			break
		}
	}
	for i := len(digits) - n; i < width; i++ {
		builder = append(builder, pad)
	}
	return append(builder, digits[n:]...)
}

// TryParse parses input by format into a broken-down time; false on any mismatch.
func (strftime) TryParse(input, format string) ([]int64, bool) {
	year, month, day, hours, minutes, seconds := int64(1900), int64(0), int64(0), int64(0), int64(0), int64(0)
	weekday, yearDay := int64(-1), int64(-1)
	// %I and %l read a 12-hour clock, which a PM read by %p moves to the afternoon.
	twelveHour, pm := false, false
	format = expand(format)
	at := 0
	for i := 0; i < len(format); i++ {
		c := format[i]
		if c != '%' {
			if isSpace(c) {
				at = skipSpaces(input, at)
			} else {
				if at == len(input) || input[at] != c {
					return nil, false
				}
				at++
			}
			continue
		}

		i++
		if i == len(format) {
			return nil, false
		}
		var success bool
		switch format[i] {
		case 'Y':
			at, year, success = readNumber(input, at, 4, 0, 9999)
		case 'y':
			at, year, success = readNumber(input, at, 2, 0, 99)
			if year >= 69 {
				year += 1900
			} else {
				year += 2000
			}
		case 'm':
			at, month, success = readNumber(input, at, 2, 1, 12)
			month--
		case 'd', 'e':
			at, day, success = readNumber(input, at, 2, 1, 31)
		case 'H', 'k':
			at, hours, success = readNumber(input, at, 2, 0, 23)
			twelveHour = false
		case 'I', 'l':
			at, hours, success = readNumber(input, at, 2, 1, 12)
			hours %= 12
			twelveHour = true
		case 'p':
			at, pm, success = readMeridian(input, at)
		case 'M':
			at, minutes, success = readNumber(input, at, 2, 0, 59)
		case 'S':
			at, seconds, success = readNumber(input, at, 2, 0, 60)
		case 'j':
			at, yearDay, success = readNumber(input, at, 3, 1, 366)
			yearDay--
		case 'a', 'A':
			at, weekday, success = readName(input, at, dayNames)
		case 'u':
			at, weekday, success = readNumber(input, at, 1, 1, 7)
			weekday %= 7
		case 'w':
			at, weekday, success = readNumber(input, at, 1, 0, 6)
		case 'b', 'h', 'B':
			at, month, success = readName(input, at, monthNames)
		case 'z':
			at, success = readOffset(input, at)
		case 'Z':
			at, success = readWord(input, at)
		case 'n', 't':
			at = skipSpaces(input, at)
			success = true
		case '%':
			success = at < len(input) && input[at] == '%'
			at++
		}
		if !success {
			return nil, false
		}
	}

	at = skipSpaces(input, at)
	if at != len(input) {
		return nil, false
	}
	if twelveHour && pm {
		hours += 12
	}

	computedWeekday, computedYearDay := Calendar.WeekdayAndYearDay(year, month, day)
	if weekday < 0 {
		weekday = computedWeekday
	}
	if yearDay < 0 {
		yearDay = computedYearDay
	}
	return []int64{year, month, day, hours, minutes, seconds, weekday, yearDay}, true
}

// expand gives the format with the directives made of others replaced by what they are made of.
func expand(format string) string {
	var builder strings.Builder
	for i := 0; i < len(format); i++ {
		if format[i] == '%' && i+1 < len(format) {
			i++
			next := format[i]
			if text, made := expansion(next); made {
				builder.WriteString(text)
			} else {
				builder.WriteByte('%')
				builder.WriteByte(next)
			}
		} else {
			builder.WriteByte(format[i])
		}
	}
	return builder.String()
}

// readMeridian reads AM or PM, ASCII case-insensitive.
func readMeridian(input string, at int) (int, bool, bool) {
	if at+2 > len(input) {
		return at, false, false
	}
	if strings.EqualFold(input[at:at+2], "PM") {
		return at + 2, true, true
	}
	if strings.EqualFold(input[at:at+2], "AM") {
		return at + 2, false, true
	}
	return at, false, false
}

// readNumber skips input whitespace, then reads 1 to width ASCII digits in [low, high].
func readNumber(input string, at, width int, low, high int64) (int, int64, bool) {
	at = skipSpaces(input, at)
	v := int64(0)
	count := 0
	for count < width && at < len(input) && input[at] >= '0' && input[at] <= '9' {
		v = v*10 + int64(input[at]-'0')
		at++
		count++
	}
	return at, v, count > 0 && v >= low && v <= high
}

// readName reads a name of the table, full or its first three letters, ASCII case-insensitive;
// full names first.
func readName(input string, at int, names []string) (int, int64, bool) {
	for length := 0; length < 2; length++ {
		for i, name := range names {
			if length == 1 {
				name = name[:3]
			}
			if at+len(name) <= len(input) && asciiEqualsIgnoreCase(input[at:at+len(name)], name) {
				return at + len(name), int64(i), true
			}
		}
	}
	return at, -1, false
}

func asciiEqualsIgnoreCase(a, b string) bool {
	lower := func(c byte) byte {
		if c >= 'A' && c <= 'Z' {
			return c + 32
		}
		return c
	}
	for i := 0; i < len(b); i++ {
		if lower(a[i]) != lower(b[i]) {
			return false
		}
	}
	return true
}

// readOffset reads Z, or + or - with hh, hhmm or hh:mm; read and ignored.
func readOffset(input string, at int) (int, bool) {
	if at < len(input) && input[at] == 'Z' {
		return at + 1, true
	}
	if at == len(input) || (input[at] != '+' && input[at] != '-') || !twoDigits(input, at+1) {
		return at, false
	}
	at += 3
	if twoDigits(input, at) {
		at += 2
	} else if at < len(input) && input[at] == ':' && twoDigits(input, at+1) {
		at += 3
	}
	return at, true
}

func twoDigits(input string, at int) bool {
	return at+2 <= len(input) && input[at] >= '0' && input[at] <= '9' && input[at+1] >= '0' && input[at+1] <= '9'
}

// readWord reads a run of non-whitespace characters; read and ignored.
func readWord(input string, at int) (int, bool) {
	start := at
	for at < len(input) && !isSpace(input[at]) {
		at++
	}
	return at, at > start
}

func skipSpaces(input string, at int) int {
	for at < len(input) && isSpace(input[at]) {
		at++
	}
	return at
}

func isSpace(c byte) bool { return c == ' ' || (c >= '\t' && c <= '\r') }
