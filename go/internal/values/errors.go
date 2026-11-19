package values

type errorCatalogue struct{}

// Errors is the message catalogue of the runtime errors of spec §10.3 and of the builtins of §9:
// every text an evaluation can raise, in one place. It lives in the value layer, the lowest one,
// because the value operations raise most of these errors; the interpreter and the natives use it
// too. A jqlite error is a value, usually the message string. (Syntax and binding errors, §10.2,
// are SyntaxMessages and BindingMessages: they are reported before evaluation, with a position.)
var Errors errorCatalogue

// The operations of CannotBe and DivisorIsZero.
const (
	Added      = "added"
	Subtracted = "subtracted"
	Multiplied = "multiplied"
	Divided    = "divided"
	Remainder  = "divided (remainder)"
)

// The error dump D(v): the canonical text, cut when longer than dumpLimit UTF-8 bytes (§10.1).
const (
	dumpLimit = 29
	dumpKeep  = 25
)

// CannotBe is "number (1) and string ("a") cannot be added"; operation is one of the constants above.
func (errorCatalogue) CannotBe(a, b Value, operation string) Value {
	return message(Errors.Describe(a) + " and " + Errors.Describe(b) + " cannot be " + operation)
}

// DivisorIsZero is "... cannot be divided because the divisor is zero" (or "divided (remainder)").
func (errorCatalogue) DivisorIsZero(a, b Value, operation string) Value {
	return message(Errors.Describe(a) + " and " + Errors.Describe(b) + " cannot be " + operation + " because the divisor is zero")
}

func (errorCatalogue) CannotNegate(value Value) Value {
	return message(Errors.Describe(value) + " cannot be negated")
}

func (errorCatalogue) RepeatTooLong() Value { return message("Repeat string result too long") }

func (errorCatalogue) ImplodeNotArray() Value { return message("implode input must be an array") }

// CodepointNotNumeric is a code point that is not a number (or is NaN), described by its own dump.
func (errorCatalogue) CodepointNotNumeric(codepoint Value) Value {
	return message(Errors.Describe(codepoint) + " can't be imploded, unicode codepoint needs to be numeric")
}

func (errorCatalogue) ExplodeNotString() Value { return message("explode input must be a string") }

// CannotIndex is "Cannot index number with object ({"start":1,"end":2})": the dump is the key's.
func (errorCatalogue) CannotIndex(input, key Value) Value {
	return message("Cannot index " + input.TypeName() + " with " + Errors.Describe(key))
}

func (errorCatalogue) SliceIndicesNotIntegers() Value {
	return message("Array/string slice indices must be integers")
}

// JSON reader reasons (implementation-defined text, §5.3); the reader adds the position and
// WhileParsing wraps them.
const (
	ExpectedValue         = "Expected JSON value"
	ExtraValues           = "Unexpected extra JSON values"
	UnfinishedAtEof       = "Unfinished JSON term at EOF"
	InvalidLiteral        = "Invalid literal"
	InvalidNumericLiteral = "Invalid numeric literal"
	InvalidEscape         = "Invalid escape"
	InvalidUnicodeEscape  = "Invalid \\uXXXX escape"
	ControlCharacter      = "Invalid string: control characters from U+0000 through U+001F must be escaped"
	KeysMustBeStrings     = "Object keys must be strings"
	KeyValuePairs         = "Objects must consist of key:value pairs"
	ExpectedSeparator     = "Expected separator between values"
	ExpectedArrayElement  = "Expected another array element"
	DepthLimit            = "Exceeds depth limit for parsing"
)

func (errorCatalogue) ExpectedValueBefore(c byte) string {
	return "Expected value before '" + string(c) + "'"
}

func (errorCatalogue) Unmatched(c byte) string { return "Unmatched '" + string(c) + "'" }

func (errorCatalogue) AtPosition(reason string, line, column int) string {
	return reason + " at line " + CodePoints.Decimal(line) + ", column " + CodePoints.Decimal(column)
}

// WhileParsing is a reader error: "<reason> (while parsing '<text>')", text the whole input (§5.3).
func (errorCatalogue) WhileParsing(reason, text string) Value {
	return message(reason + " (while parsing '" + text + "')")
}

// The interpreter (§10.3).

func (errorCatalogue) CannotIterate(value Value) Value {
	return message("Cannot iterate over " + Errors.Describe(value))
}

func (errorCatalogue) CannotUseAsKey(key Value) Value {
	return message("Cannot use " + Errors.Describe(key) + " as object key")
}

func (errorCatalogue) VariableNotDefined(name string) Value {
	return message("$" + name + " is not defined")
}

func (errorCatalogue) InvalidFormat(name string) Value {
	return message(name + " is not a valid format")
}

func (errorCatalogue) DepthExceeded() Value { return message("Maximum call depth exceeded") }

func (errorCatalogue) ModulemetaNotString() Value {
	return message("modulemeta input module name must be a string")
}

// Paths (§8).

func (errorCatalogue) InvalidPathResult(value Value) Value {
	return message("Invalid path expression with result " + Errors.Dump(value))
}

func (errorCatalogue) InvalidPathAccess(key, target Value) Value {
	return message("Invalid path expression near attempt to access element " + Errors.Dump(key) + " of " + Errors.Dump(target))
}

func (errorCatalogue) InvalidPathIterate(target Value) Value {
	return message("Invalid path expression near attempt to iterate through " + Errors.Dump(target))
}

func (errorCatalogue) PathNotArray() Value { return message("Path must be specified as an array") }

func (errorCatalogue) PathsNotArray() Value { return message("Paths must be specified as an array") }

func (errorCatalogue) PathElementNotArray(path Value) Value {
	return message("Path must be specified as array, not " + path.TypeName())
}

func (errorCatalogue) NegativeIndex() Value { return message("Out of bounds negative array index") }

func (errorCatalogue) IndexTooLarge() Value { return message("Array index too large") }

func (errorCatalogue) NaNIndex() Value { return message("Cannot set array element at NaN index") }

func (errorCatalogue) SliceNeedsArray() Value {
	return message("A slice of an array can only be assigned another array")
}

func (errorCatalogue) CannotUpdateStringSlices() Value { return message("Cannot update string slices") }

func (errorCatalogue) CannotUpdateField(key, container Value) Value {
	return message("Cannot update field at " + key.TypeName() + " index of " + container.TypeName())
}

func (errorCatalogue) CannotDeleteElement(key Value) Value {
	return message("Cannot delete " + key.TypeName() + " element of array")
}

func (errorCatalogue) CannotDeleteField(key Value) Value {
	return message("Cannot delete " + key.TypeName() + " field of object")
}

func (errorCatalogue) CannotDeleteFrom(container Value) Value {
	return message("Cannot delete fields from " + container.TypeName())
}

// Builtins (§9), by section.

func (errorCatalogue) HasNoLength(value Value) Value {
	return message(Errors.Describe(value) + " has no length")
}

func (errorCatalogue) OnlyStringsHaveUtf8Length(value Value) Value {
	return message(Errors.Describe(value) + " only strings have UTF-8 byte length")
}

func (errorCatalogue) HaltErrorNumberRequired(input Value) Value {
	return message(Errors.Describe(input) + " halt_error/1: number required")
}

func (errorCatalogue) HasNoKeys(value Value) Value {
	return message(Errors.Describe(value) + " has no keys")
}

func (errorCatalogue) CannotCheckHas(input, key Value) Value {
	return message("Cannot check whether " + input.TypeName() + " has a " + key.TypeName() + " key")
}

func (errorCatalogue) RangeNotNumeric() Value { return message("Range bounds must be numeric") }

func (errorCatalogue) NegativeLimit() Value { return message("limit doesn't support negative count") }

func (errorCatalogue) NegativeSkip() Value { return message("skip doesn't support negative count") }

func (errorCatalogue) CannotBeSorted(value Value) Value {
	return message(Errors.Describe(value) + " cannot be sorted, as it is not an array")
}

func (errorCatalogue) CannotBeSortedBy(a, b Value) Value {
	return message(Errors.Describe(a) + " and " + Errors.Describe(b) + " cannot be sorted, as they are not both arrays")
}

func (errorCatalogue) CannotBeIterated(a, b Value) Value {
	return message(Errors.Describe(a) + " and " + Errors.Describe(b) + " cannot be iterated over")
}

func (errorCatalogue) WrongLength(a, b Value) Value {
	return message(Errors.Describe(a) + " and " + Errors.Describe(b) + " have wrong length")
}

func (errorCatalogue) CannotBeSearched(value Value) Value {
	return message(Errors.Describe(value) + " cannot be searched from")
}

func (errorCatalogue) OnlyStringsParse(value Value) Value {
	return message(Errors.Describe(value) + " only strings can be parsed")
}

func (errorCatalogue) CannotParseNumber(value Value) Value {
	return message(Errors.Describe(value) + " cannot be parsed as a number")
}

func (errorCatalogue) CannotParseBoolean(value Value) Value {
	return message(Errors.Describe(value) + " cannot be parsed as a boolean")
}

// NotAValidFormat is format/1 with a name that is not a string.
func (errorCatalogue) NotAValidFormat(value Value) Value {
	return message(Errors.Describe(value) + " is not a valid format")
}

func (errorCatalogue) TrimNotString() Value { return message("trim input must be a string") }

func (errorCatalogue) StartsWithStrings() Value {
	return message("startswith() requires string inputs")
}

func (errorCatalogue) EndsWithStrings() Value { return message("endswith() requires string inputs") }

func (errorCatalogue) SplitStrings() Value {
	return message("split input and separator must be strings")
}

func (errorCatalogue) CannotSearchNotString(value Value) Value {
	return message(Errors.Describe(value) + " cannot be searched, as it is not a string")
}

func (errorCatalogue) NotAString(value Value) Value {
	return message(Errors.Describe(value) + " is not a string")
}

// NumberRequired is "num req" (§9.1): string ("a") number required.
func (errorCatalogue) NumberRequired(value Value) Value {
	return message(Errors.Describe(value) + " number required")
}

// Regular expressions (§9.10).

func (errorCatalogue) CannotBeMatched(value Value) Value {
	return message(Errors.Describe(value) + " cannot be matched, as it is not a string")
}

func (errorCatalogue) InvalidModifiers(flags string) Value {
	return message(flags + " is not a valid modifier string")
}

// RegexFailure is a pattern outside the dialect or that the engine rejects; the reason is
// implementation-defined.
func (errorCatalogue) RegexFailure(reason string) Value { return message("Regex failure: " + reason) }

// Dates (§9.7, §9.12).

func (errorCatalogue) GmtimeNumeric() Value { return message("gmtime requires numeric inputs") }

func (errorCatalogue) LocaltimeNumeric() Value { return message("localtime requires numeric inputs") }

func (errorCatalogue) CannotConvertSeconds() Value {
	return message("error converting number of seconds since epoch to datetime")
}

func (errorCatalogue) MktimeArray() Value { return message("mktime requires array inputs") }

func (errorCatalogue) MktimeParsed() Value { return message("mktime requires parsed datetime inputs") }

// RequiresParsedDatetime: name is strftime or strflocaltime.
func (errorCatalogue) RequiresParsedDatetime(name string) Value {
	return message(name + "/1 requires parsed datetime inputs")
}

func (errorCatalogue) RequiresStringFormat(name string) Value {
	return message(name + "/1 requires a string format")
}

func (errorCatalogue) TrailingPercent() Value { return message("Invalid format: trailing %") }

func (errorCatalogue) UnsupportedStrftime(directive string) Value {
	return message("Unsupported strftime format specifier %" + directive)
}

func (errorCatalogue) StrptimeStrings() Value {
	return message("strptime/1 requires string inputs and arguments")
}

func (errorCatalogue) DateDoesNotMatch(input, format string) Value {
	return message("date \"" + input + "\" does not match format \"" + format + "\"")
}

// NoClock is now in an evaluation whose host gave no clock.
func (errorCatalogue) NoClock() Value { return message("now/0 requires a host clock") }

func (errorCatalogue) CannotCheckContainment(a, b Value) Value {
	return message(Errors.Describe(a) + " and " + Errors.Describe(b) + " cannot have their containment checked")
}

// Formats (§9.9).

func (errorCatalogue) NotValidUriEncoding(text Value) Value {
	return message(Errors.Describe(text) + " is not a valid uri encoding")
}

func (errorCatalogue) CannotBeCsvFormatted(value Value) Value {
	return message(Errors.Describe(value) + " cannot be csv-formatted, only array")
}

func (errorCatalogue) CannotBeTsvFormatted(value Value) Value {
	return message(Errors.Describe(value) + " cannot be tsv-formatted, only array")
}

// NotValidInCsvRow is an array or object in a row of @csv or @tsv (the text says csv for both).
func (errorCatalogue) NotValidInCsvRow(value Value) Value {
	return message(Errors.Describe(value) + " is not valid in a csv row")
}

func (errorCatalogue) CannotBeEscapedForShell(value Value) Value {
	return message(Errors.Describe(value) + " can not be escaped for shell")
}

func (errorCatalogue) NotValidBase64(text Value) Value {
	return message(Errors.Describe(text) + " is not valid base64 data")
}

func (errorCatalogue) TrailingBase64Byte(text Value) Value {
	return message(Errors.Describe(text) + " trailing base64 byte found")
}

// Describe is T(v) (D(v)), as in object ({"a":1}).
func (errorCatalogue) Describe(value Value) string {
	return value.TypeName() + " (" + Errors.Dump(value) + ")"
}

// Dump is the error dump D(v) (§10.1): the canonical text; if longer than 29 UTF-8 bytes, its
// longest prefix of at most 25 bytes ending on a code point boundary, then "..." and the value's
// closing delimiter.
func (errorCatalogue) Dump(value Value) string {
	return ValueText.ToJsonTruncated(value, dumpLimit, dumpKeep)
}

func message(text string) Value { return FromString(text) }
