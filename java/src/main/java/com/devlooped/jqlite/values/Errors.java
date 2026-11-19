package com.devlooped.jqlite.values;

/**
 * The message catalogue of the runtime errors of spec §10.3 and of the builtins of §9: every text
 * an evaluation can raise, in one place. It lives in the value layer, the lowest one, because the
 * value operations raise most of these errors; the interpreter and the natives use it too. A
 * jqlite error is a value, usually the message string. (Syntax and binding errors, §10.2, are
 * SyntaxMessages and BindingMessages: they are reported before evaluation, with a position.)
 */
public final class Errors {
    public static final String ADDED = "added";
    public static final String SUBTRACTED = "subtracted";
    public static final String MULTIPLIED = "multiplied";
    public static final String DIVIDED = "divided";
    public static final String REMAINDER = "divided (remainder)";

    // The error dump D(v): the canonical text, cut when longer than DUMP_LIMIT UTF-8 bytes (§10.1).
    private static final int DUMP_LIMIT = 29;
    private static final int DUMP_KEEP = 25;

    private Errors() {
    }

    /** {@code number (1) and string ("a") cannot be added}; {@code operation} is one of the constants above. */
    public static JqValue cannotBe(JqValue a, JqValue b, String operation) {
        return message(describe(a) + " and " + describe(b) + " cannot be " + operation);
    }

    /** {@code ... cannot be divided because the divisor is zero} or {@code ... cannot be divided (remainder) because the divisor is zero}. */
    public static JqValue divisorIsZero(JqValue a, JqValue b, String operation) {
        return message(describe(a) + " and " + describe(b) + " cannot be " + operation + " because the divisor is zero");
    }

    public static JqValue cannotNegate(JqValue value) {
        return message(describe(value) + " cannot be negated");
    }

    public static JqValue repeatTooLong() {
        return message("Repeat string result too long");
    }

    public static JqValue implodeNotArray() {
        return message("implode input must be an array");
    }

    /** A code point that is not a number (or is NaN), described by its own dump. */
    public static JqValue codepointNotNumeric(JqValue codepoint) {
        return message(describe(codepoint) + " can't be imploded, unicode codepoint needs to be numeric");
    }

    public static JqValue explodeNotString() {
        return message("explode input must be a string");
    }

    /** {@code Cannot index number with object ({"start":1,"end":2})}: the dump is the key's. */
    public static JqValue cannotIndex(JqValue input, JqValue key) {
        return message("Cannot index " + input.typeName() + " with " + describe(key));
    }

    public static JqValue sliceIndicesNotIntegers() {
        return message("Array/string slice indices must be integers");
    }

    // JSON reader reasons (implementation-defined text, §5.3); the reader adds the position and
    // whileParsing wraps them.
    public static final String EXPECTED_VALUE = "Expected JSON value";
    public static final String EXTRA_VALUES = "Unexpected extra JSON values";
    public static final String UNFINISHED_AT_EOF = "Unfinished JSON term at EOF";
    public static final String INVALID_LITERAL = "Invalid literal";
    public static final String INVALID_NUMERIC_LITERAL = "Invalid numeric literal";
    public static final String INVALID_ESCAPE = "Invalid escape";
    public static final String INVALID_UNICODE_ESCAPE = "Invalid \\uXXXX escape";
    public static final String CONTROL_CHARACTER = "Invalid string: control characters from U+0000 through U+001F must be escaped";
    public static final String KEYS_MUST_BE_STRINGS = "Object keys must be strings";
    public static final String KEY_VALUE_PAIRS = "Objects must consist of key:value pairs";
    public static final String EXPECTED_SEPARATOR = "Expected separator between values";
    public static final String EXPECTED_ARRAY_ELEMENT = "Expected another array element";
    public static final String DEPTH_LIMIT = "Exceeds depth limit for parsing";

    public static String expectedValueBefore(char c) {
        return "Expected value before '" + c + "'";
    }

    public static String unmatched(char c) {
        return "Unmatched '" + c + "'";
    }

    public static String atPosition(String reason, int line, int column) {
        return reason + " at line " + line + ", column " + column;
    }

    /** A reader error: {@code <reason> (while parsing '<text>')}, {@code text} the whole input (§5.3). */
    public static JqValue whileParsing(String reason, String text) {
        return message(reason + " (while parsing '" + text + "')");
    }

    // The interpreter (§10.3).
    public static JqValue cannotIterate(JqValue value) {
        return message("Cannot iterate over " + describe(value));
    }

    public static JqValue cannotUseAsKey(JqValue key) {
        return message("Cannot use " + describe(key) + " as object key");
    }

    public static JqValue variableNotDefined(String name) {
        return message("$" + name + " is not defined");
    }

    public static JqValue invalidFormat(String name) {
        return message(name + " is not a valid format");
    }

    public static JqValue depthExceeded() {
        return message("Maximum call depth exceeded");
    }

    public static JqValue modulemetaNotString() {
        return message("modulemeta input module name must be a string");
    }

    // Paths (§8).
    public static JqValue invalidPathResult(JqValue value) {
        return message("Invalid path expression with result " + dump(value));
    }

    public static JqValue invalidPathAccess(JqValue key, JqValue target) {
        return message("Invalid path expression near attempt to access element " + dump(key) + " of " + dump(target));
    }

    public static JqValue invalidPathIterate(JqValue target) {
        return message("Invalid path expression near attempt to iterate through " + dump(target));
    }

    public static JqValue pathNotArray() {
        return message("Path must be specified as an array");
    }

    public static JqValue pathsNotArray() {
        return message("Paths must be specified as an array");
    }

    public static JqValue pathElementNotArray(JqValue path) {
        return message("Path must be specified as array, not " + path.typeName());
    }

    public static JqValue negativeIndex() {
        return message("Out of bounds negative array index");
    }

    public static JqValue indexTooLarge() {
        return message("Array index too large");
    }

    public static JqValue nanIndex() {
        return message("Cannot set array element at NaN index");
    }

    public static JqValue sliceNeedsArray() {
        return message("A slice of an array can only be assigned another array");
    }

    public static JqValue cannotUpdateStringSlices() {
        return message("Cannot update string slices");
    }

    public static JqValue cannotUpdateField(JqValue key, JqValue container) {
        return message("Cannot update field at " + key.typeName() + " index of " + container.typeName());
    }

    public static JqValue cannotDeleteElement(JqValue key) {
        return message("Cannot delete " + key.typeName() + " element of array");
    }

    public static JqValue cannotDeleteField(JqValue key) {
        return message("Cannot delete " + key.typeName() + " field of object");
    }

    public static JqValue cannotDeleteFrom(JqValue container) {
        return message("Cannot delete fields from " + container.typeName());
    }

    // Builtins (§9), by section.
    public static JqValue hasNoLength(JqValue value) {
        return message(describe(value) + " has no length");
    }

    public static JqValue onlyStringsHaveUtf8Length(JqValue value) {
        return message(describe(value) + " only strings have UTF-8 byte length");
    }

    public static JqValue haltErrorNumberRequired(JqValue input) {
        return message(describe(input) + " halt_error/1: number required");
    }

    public static JqValue hasNoKeys(JqValue value) {
        return message(describe(value) + " has no keys");
    }

    public static JqValue cannotCheckHas(JqValue input, JqValue key) {
        return message("Cannot check whether " + input.typeName() + " has a " + key.typeName() + " key");
    }

    public static JqValue rangeNotNumeric() {
        return message("Range bounds must be numeric");
    }

    public static JqValue negativeLimit() {
        return message("limit doesn't support negative count");
    }

    public static JqValue negativeSkip() {
        return message("skip doesn't support negative count");
    }

    public static JqValue cannotBeSorted(JqValue value) {
        return message(describe(value) + " cannot be sorted, as it is not an array");
    }

    public static JqValue cannotBeSortedBy(JqValue a, JqValue b) {
        return message(describe(a) + " and " + describe(b) + " cannot be sorted, as they are not both arrays");
    }

    public static JqValue cannotBeIterated(JqValue a, JqValue b) {
        return message(describe(a) + " and " + describe(b) + " cannot be iterated over");
    }

    public static JqValue wrongLength(JqValue a, JqValue b) {
        return message(describe(a) + " and " + describe(b) + " have wrong length");
    }

    public static JqValue cannotBeSearched(JqValue value) {
        return message(describe(value) + " cannot be searched from");
    }

    public static JqValue onlyStringsParse(JqValue value) {
        return message(describe(value) + " only strings can be parsed");
    }

    public static JqValue cannotParseNumber(JqValue value) {
        return message(describe(value) + " cannot be parsed as a number");
    }

    public static JqValue cannotParseBoolean(JqValue value) {
        return message(describe(value) + " cannot be parsed as a boolean");
    }

    /** {@code format/1} with a name that is not a string. */
    public static JqValue notAValidFormat(JqValue value) {
        return message(describe(value) + " is not a valid format");
    }

    public static JqValue trimNotString() {
        return message("trim input must be a string");
    }

    public static JqValue startsWithStrings() {
        return message("startswith() requires string inputs");
    }

    public static JqValue endsWithStrings() {
        return message("endswith() requires string inputs");
    }

    public static JqValue splitStrings() {
        return message("split input and separator must be strings");
    }

    public static JqValue cannotSearchNotString(JqValue value) {
        return message(describe(value) + " cannot be searched, as it is not a string");
    }

    public static JqValue notAString(JqValue value) {
        return message(describe(value) + " is not a string");
    }

    /** "num req" (§9.1): {@code string ("a") number required}. */
    public static JqValue numberRequired(JqValue value) {
        return message(describe(value) + " number required");
    }

    // Regular expressions (§9.10).
    public static JqValue cannotBeMatched(JqValue value) {
        return message(describe(value) + " cannot be matched, as it is not a string");
    }

    public static JqValue invalidModifiers(String flags) {
        return message(flags + " is not a valid modifier string");
    }

    /** A pattern outside the dialect or that the engine rejects; the reason is implementation-defined. */
    public static JqValue regexFailure(String reason) {
        return message("Regex failure: " + reason);
    }

    // Dates (§9.7, §9.12).
    public static JqValue gmtimeNumeric() {
        return message("gmtime requires numeric inputs");
    }

    public static JqValue localtimeNumeric() {
        return message("localtime requires numeric inputs");
    }

    public static JqValue cannotConvertSeconds() {
        return message("error converting number of seconds since epoch to datetime");
    }

    public static JqValue mktimeArray() {
        return message("mktime requires array inputs");
    }

    public static JqValue mktimeParsed() {
        return message("mktime requires parsed datetime inputs");
    }

    /** {@code name} is {@code strftime} or {@code strflocaltime}. */
    public static JqValue requiresParsedDatetime(String name) {
        return message(name + "/1 requires parsed datetime inputs");
    }

    public static JqValue requiresStringFormat(String name) {
        return message(name + "/1 requires a string format");
    }

    public static JqValue trailingPercent() {
        return message("Invalid format: trailing %");
    }

    public static JqValue unsupportedStrftime(String directive) {
        return message("Unsupported strftime format specifier %" + directive);
    }

    public static JqValue strptimeStrings() {
        return message("strptime/1 requires string inputs and arguments");
    }

    public static JqValue dateDoesNotMatch(String input, String format) {
        return message("date \"" + input + "\" does not match format \"" + format + "\"");
    }

    /** {@code now} in an evaluation whose host gave no clock. */
    public static JqValue noClock() {
        return message("now/0 requires a host clock");
    }

    public static JqValue cannotCheckContainment(JqValue a, JqValue b) {
        return message(describe(a) + " and " + describe(b) + " cannot have their containment checked");
    }

    // Formats (§9.9).
    public static JqValue notValidUriEncoding(JqValue text) {
        return message(describe(text) + " is not a valid uri encoding");
    }

    public static JqValue cannotBeCsvFormatted(JqValue value) {
        return message(describe(value) + " cannot be csv-formatted, only array");
    }

    public static JqValue cannotBeTsvFormatted(JqValue value) {
        return message(describe(value) + " cannot be tsv-formatted, only array");
    }

    /** An array or object in a row of {@code @csv} or {@code @tsv} (the text says csv for both). */
    public static JqValue notValidInCsvRow(JqValue value) {
        return message(describe(value) + " is not valid in a csv row");
    }

    public static JqValue cannotBeEscapedForShell(JqValue value) {
        return message(describe(value) + " can not be escaped for shell");
    }

    public static JqValue notValidBase64(JqValue text) {
        return message(describe(text) + " is not valid base64 data");
    }

    public static JqValue trailingBase64Byte(JqValue text) {
        return message(describe(text) + " trailing base64 byte found");
    }

    /** {@code T(v) (D(v))}, as in {@code object ({"a":1})}. */
    public static String describe(JqValue value) {
        return value.typeName() + " (" + dump(value) + ")";
    }

    /**
     * The error dump D(v) (§10.1): the canonical text; if longer than 29 UTF-8 bytes, its longest
     * prefix of at most 25 bytes ending on a code point boundary, then "..." and the value's closing delimiter.
     */
    public static String dump(JqValue value) {
        return ValueText.toJsonTruncated(value, DUMP_LIMIT, DUMP_KEEP);
    }

    private static JqValue message(String text) {
        return JqValue.fromString(text);
    }
}
