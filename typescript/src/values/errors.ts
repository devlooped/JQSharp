// The message catalogue of the runtime errors of spec §10.3 and of the builtins of §9: every
// text an evaluation can raise, in one place. It lives in the value layer, the lowest one,
// because the value operations raise most of these errors; the interpreter and the natives use
// it too. A jqlite error is a value, usually the message string. (Syntax and binding errors,
// §10.2, are SyntaxMessages and BindingMessages: they are reported before evaluation, with a
// position.)

import { type JqValue, typeName } from "./jqValue.js";
import * as ValueText from "./valueText.js";

export const Added = "added";
export const Subtracted = "subtracted";
export const Multiplied = "multiplied";
export const Divided = "divided";
export const Remainder = "divided (remainder)";

// The error dump D(v): the canonical text, cut when longer than DumpLimit UTF-8 bytes (§10.1).
const DumpLimit = 29;
const DumpKeep = 25;

/** `number (1) and string ("a") cannot be added`; `operation` is one of the constants above. */
export function cannotBe(a: JqValue, b: JqValue, operation: string): JqValue {
  return `${describe(a)} and ${describe(b)} cannot be ${operation}`;
}

/** `... cannot be divided because the divisor is zero` (`Divided`) or `... cannot be divided (remainder) because the divisor is zero` (`Remainder`). */
export function divisorIsZero(a: JqValue, b: JqValue, operation: string): JqValue {
  return `${describe(a)} and ${describe(b)} cannot be ${operation} because the divisor is zero`;
}

export function cannotNegate(value: JqValue): JqValue {
  return `${describe(value)} cannot be negated`;
}

export const repeatTooLong: JqValue = "Repeat string result too long";

export const implodeNotArray: JqValue = "implode input must be an array";

/** A code point that is not a number (or is NaN), described by its own dump. */
export function codepointNotNumeric(codepoint: JqValue): JqValue {
  return `${describe(codepoint)} can't be imploded, unicode codepoint needs to be numeric`;
}

export const explodeNotString: JqValue = "explode input must be a string";

/** `Cannot index number with object ({"start":1,"end":2})`: the dump is the key's. */
export function cannotIndex(input: JqValue, key: JqValue): JqValue {
  return `Cannot index ${typeName(input)} with ${describe(key)}`;
}

export const sliceIndicesNotIntegers: JqValue = "Array/string slice indices must be integers";

// JSON reader reasons (implementation-defined text, §5.3); the reader adds the position and
// whileParsing wraps them.
export const ExpectedValue = "Expected JSON value";
export const ExtraValues = "Unexpected extra JSON values";
export const UnfinishedAtEof = "Unfinished JSON term at EOF";
export const InvalidLiteral = "Invalid literal";
export const InvalidNumericLiteral = "Invalid numeric literal";
export const InvalidEscape = "Invalid escape";
export const InvalidUnicodeEscape = "Invalid \\uXXXX escape";
export const ControlCharacter = "Invalid string: control characters from U+0000 through U+001F must be escaped";
export const KeysMustBeStrings = "Object keys must be strings";
export const KeyValuePairs = "Objects must consist of key:value pairs";
export const ExpectedSeparator = "Expected separator between values";
export const ExpectedArrayElement = "Expected another array element";
export const DepthLimit = "Exceeds depth limit for parsing";

export function expectedValueBefore(c: string): string {
  return `Expected value before '${c}'`;
}

export function unmatched(c: string): string {
  return `Unmatched '${c}'`;
}

export function atPosition(reason: string, line: number, column: number): string {
  return `${reason} at line ${line}, column ${column}`;
}

/** A reader error: `<reason> (while parsing '<text>')`, `text` the whole input (§5.3). */
export function whileParsing(reason: string, text: string): JqValue {
  return `${reason} (while parsing '${text}')`;
}

// The interpreter (§10.3).
export function cannotIterate(value: JqValue): JqValue {
  return `Cannot iterate over ${describe(value)}`;
}

export function cannotUseAsKey(key: JqValue): JqValue {
  return `Cannot use ${describe(key)} as object key`;
}

export function variableNotDefined(name: string): JqValue {
  return `$${name} is not defined`;
}

export function invalidFormat(name: string): JqValue {
  return `${name} is not a valid format`;
}

export const depthExceeded: JqValue = "Maximum call depth exceeded";

export const modulemetaNotString: JqValue = "modulemeta input module name must be a string";

// Paths (§8).
export function invalidPathResult(value: JqValue): JqValue {
  return `Invalid path expression with result ${dump(value)}`;
}

export function invalidPathAccess(key: JqValue, target: JqValue): JqValue {
  return `Invalid path expression near attempt to access element ${dump(key)} of ${dump(target)}`;
}

export function invalidPathIterate(target: JqValue): JqValue {
  return `Invalid path expression near attempt to iterate through ${dump(target)}`;
}

export const pathNotArray: JqValue = "Path must be specified as an array";

export const pathsNotArray: JqValue = "Paths must be specified as an array";

export function pathElementNotArray(path: JqValue): JqValue {
  return `Path must be specified as array, not ${typeName(path)}`;
}

export const negativeIndex: JqValue = "Out of bounds negative array index";

export const indexTooLarge: JqValue = "Array index too large";

export const nanIndex: JqValue = "Cannot set array element at NaN index";

export const sliceNeedsArray: JqValue = "A slice of an array can only be assigned another array";

export const cannotUpdateStringSlices: JqValue = "Cannot update string slices";

export function cannotUpdateField(key: JqValue, container: JqValue): JqValue {
  return `Cannot update field at ${typeName(key)} index of ${typeName(container)}`;
}

export function cannotDeleteElement(key: JqValue): JqValue {
  return `Cannot delete ${typeName(key)} element of array`;
}

export function cannotDeleteField(key: JqValue): JqValue {
  return `Cannot delete ${typeName(key)} field of object`;
}

export function cannotDeleteFrom(container: JqValue): JqValue {
  return `Cannot delete fields from ${typeName(container)}`;
}

// Builtins (§9), by section.
export function hasNoLength(value: JqValue): JqValue {
  return `${describe(value)} has no length`;
}

export function onlyStringsHaveUtf8Length(value: JqValue): JqValue {
  return `${describe(value)} only strings have UTF-8 byte length`;
}

export function haltErrorNumberRequired(input: JqValue): JqValue {
  return `${describe(input)} halt_error/1: number required`;
}

export function hasNoKeys(value: JqValue): JqValue {
  return `${describe(value)} has no keys`;
}

export function cannotCheckHas(input: JqValue, key: JqValue): JqValue {
  return `Cannot check whether ${typeName(input)} has a ${typeName(key)} key`;
}

export const rangeNotNumeric: JqValue = "Range bounds must be numeric";

export const negativeLimit: JqValue = "limit doesn't support negative count";

export const negativeSkip: JqValue = "skip doesn't support negative count";

export function cannotBeSorted(value: JqValue): JqValue {
  return `${describe(value)} cannot be sorted, as it is not an array`;
}

export function cannotBeSortedBy(a: JqValue, b: JqValue): JqValue {
  return `${describe(a)} and ${describe(b)} cannot be sorted, as they are not both arrays`;
}

export function cannotBeIterated(a: JqValue, b: JqValue): JqValue {
  return `${describe(a)} and ${describe(b)} cannot be iterated over`;
}

export function wrongLength(a: JqValue, b: JqValue): JqValue {
  return `${describe(a)} and ${describe(b)} have wrong length`;
}

export function cannotBeSearched(value: JqValue): JqValue {
  return `${describe(value)} cannot be searched from`;
}

export function onlyStringsParse(value: JqValue): JqValue {
  return `${describe(value)} only strings can be parsed`;
}

export function cannotParseNumber(value: JqValue): JqValue {
  return `${describe(value)} cannot be parsed as a number`;
}

export function cannotParseBoolean(value: JqValue): JqValue {
  return `${describe(value)} cannot be parsed as a boolean`;
}

/** `format/1` with a name that is not a string. */
export function notAValidFormat(value: JqValue): JqValue {
  return `${describe(value)} is not a valid format`;
}

export const trimNotString: JqValue = "trim input must be a string";

export const startsWithStrings: JqValue = "startswith() requires string inputs";

export const endsWithStrings: JqValue = "endswith() requires string inputs";

export const splitStrings: JqValue = "split input and separator must be strings";

export function cannotSearchNotString(value: JqValue): JqValue {
  return `${describe(value)} cannot be searched, as it is not a string`;
}

export function notAString(value: JqValue): JqValue {
  return `${describe(value)} is not a string`;
}

/** "num req" (§9.1): `string ("a") number required`. */
export function numberRequired(value: JqValue): JqValue {
  return `${describe(value)} number required`;
}

// Regular expressions (§9.10).
export function cannotBeMatched(value: JqValue): JqValue {
  return `${describe(value)} cannot be matched, as it is not a string`;
}

export function invalidModifiers(flags: string): JqValue {
  return `${flags} is not a valid modifier string`;
}

/** A pattern outside the flavor or too large; the reason is implementation-defined. */
export function regexFailure(reason: string): JqValue {
  return `Regex failure: ${reason}`;
}

// Dates (§9.7, §9.12).
export const gmtimeNumeric: JqValue = "gmtime requires numeric inputs";

export const localtimeNumeric: JqValue = "localtime requires numeric inputs";

export const cannotConvertSeconds: JqValue = "error converting number of seconds since epoch to datetime";

export const mktimeArray: JqValue = "mktime requires array inputs";

export const mktimeParsed: JqValue = "mktime requires parsed datetime inputs";

/** `name` is `strftime` or `strflocaltime`. */
export function requiresParsedDatetime(name: string): JqValue {
  return `${name}/1 requires parsed datetime inputs`;
}

export function requiresStringFormat(name: string): JqValue {
  return `${name}/1 requires a string format`;
}

export const trailingPercent: JqValue = "Invalid format: trailing %";

export function unsupportedStrftime(directive: string): JqValue {
  return `Unsupported strftime format specifier %${directive}`;
}

export const strptimeStrings: JqValue = "strptime/1 requires string inputs and arguments";

export function dateDoesNotMatch(input: string, format: string): JqValue {
  return `date "${input}" does not match format "${format}"`;
}

/** `now` in an evaluation whose host gave no clock. */
export const noClock: JqValue = "now/0 requires a host clock";

export function cannotCheckContainment(a: JqValue, b: JqValue): JqValue {
  return `${describe(a)} and ${describe(b)} cannot have their containment checked`;
}

// Formats (§9.9).
export function notValidUriEncoding(text: JqValue): JqValue {
  return `${describe(text)} is not a valid uri encoding`;
}

export function cannotBeCsvFormatted(value: JqValue): JqValue {
  return `${describe(value)} cannot be csv-formatted, only array`;
}

export function cannotBeTsvFormatted(value: JqValue): JqValue {
  return `${describe(value)} cannot be tsv-formatted, only array`;
}

/** An array or object in a row of `@csv` or `@tsv` (the text says csv for both). */
export function notValidInCsvRow(value: JqValue): JqValue {
  return `${describe(value)} is not valid in a csv row`;
}

export function cannotBeEscapedForShell(value: JqValue): JqValue {
  return `${describe(value)} can not be escaped for shell`;
}

export function notValidBase64(text: JqValue): JqValue {
  return `${describe(text)} is not valid base64 data`;
}

export function trailingBase64Byte(text: JqValue): JqValue {
  return `${describe(text)} trailing base64 byte found`;
}

/** `T(v) (D(v))`, as in `object ({"a":1})`. */
export function describe(value: JqValue): string {
  return `${typeName(value)} (${dump(value)})`;
}

/**
 * The error dump D(v) (§10.1): the canonical text; if longer than 29 UTF-8 bytes, its longest
 * prefix of at most 25 bytes ending on a code point boundary, then "..." and the value's closing delimiter.
 */
export function dump(value: JqValue): string {
  return ValueText.toJsonTruncated(value, DumpLimit, DumpKeep);
}
