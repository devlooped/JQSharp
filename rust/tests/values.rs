//! The value layer: what `dotnet/Tests/Core/Values` covers. Kinds and sharing of arrays and
//! objects, ordering of every pair of kinds, code-point slicing and ordering, arithmetic tables,
//! number text and round trips, the reader and the writer.

mod support;

use std::cmp::Ordering;
use std::sync::Arc;

use jqlite::values::value_ops::{self, Fallible};
use jqlite::values::{JqArray, JqKind, JqObject, JqValue, code_points, errors, utf8, value_text};

use support::{json as v, on_thread};

const MEGABYTE: usize = 1024 * 1024;

fn j(value: &JqValue) -> String {
    value_text::to_json(value)
}

// The result as JSON text, or "error: <message>".
fn text(result: Fallible) -> String {
    match result {
        Ok(value) => j(&value),
        Err(error) => format!("error: {}", error.as_string()),
    }
}

fn n(number: f64) -> JqValue {
    JqValue::Number(number)
}

fn key(name: &str) -> Arc<String> {
    Arc::new(name.to_owned())
}

// ---- CodePointTests ----

#[test]
fn length_counts_code_points() {
    for (text, length) in [("", 0), ("abc", 3), ("aé😀b", 4), ("😀😀", 2)] {
        assert_eq!(code_points::length(text), length, "{text}");
    }
}

#[test]
fn slices() {
    let cases = [
        ("\"aé😀b\"", "2", "3", "\"😀\""),
        ("\"aé😀b\"", "-2", "null", "\"😀b\""),
        ("\"aé😀b\"", "null", "2", "\"aé\""),
        ("\"aé😀b\"", "1.5", "2.5", "\"é😀\""),
        ("\"aé😀b\"", "3", "1", "\"\""),
        ("\"aé😀b\"", "-100", "100", "\"aé😀b\""),
        ("\"aé😀b\"", "nan", "nan", "\"aé😀b\""),
        ("[1,2,3,4]", "1", "3", "[2,3]"),
        ("[1,2,3,4]", "-1", "null", "[4]"),
        ("[1,2,3,4]", "null", "-1", "[1,2,3]"),
        ("[1,2,3,4]", "0.2", "0.2", "[1]"),
        ("[1,2,3,4]", "5", "10", "[]"),
        ("null", "1", "2", "null"),
        ("[0,1,2,3,4,5,6,7,8,9]", "1.2", "3.5", "[1,2,3]"),
        ("\"abcdefghi\"", "-2", "null", "\"hi\""),
        ("null", "\"a\"", "{}", "null"),
        ("1", "1", "2", "error: Cannot index number with object ({\"start\":1,\"end\":2})"),
        ("1", "\"a\"", "2", "error: Cannot index number with object ({\"start\":\"a\",\"end\":2})"),
        ("{}", "1", "null", "error: Cannot index object with object ({\"start\":1,\"end\":null})"),
        ("[1]", "\"a\"", "null", "error: Array/string slice indices must be integers"),
        ("\"ab\"", "0", "true", "error: Array/string slice indices must be integers"),
    ];
    for (input, start, end, expected) in cases {
        assert_eq!(text(value_ops::try_slice(v(input), &v(start), &v(end))), expected, "{input}[{start}:{end}]");
    }
}

// jq 1.8.2: `.[{"start":…,"end":…}]`.
#[test]
fn slices_by_object_key() {
    let cases = [
        ("[1,2,3]", "{\"start\":1,\"end\":null}", "[2,3]"),
        ("[1,2,3]", "{\"start\":1,\"end\":2,\"x\":0}", "[2]"),
        ("\"abc\"", "{\"start\":null,\"end\":null}", "\"abc\""),
        ("\"abc\"", "{\"start\":1}", "error: Array/string slice indices must be integers"),
        ("[1,2,3]", "{\"end\":2}", "error: Array/string slice indices must be integers"),
        ("[1,2,3]", "{\"start\":\"a\",\"end\":2}", "error: Array/string slice indices must be integers"),
        ("null", "{\"start\":1}", "null"),
        ("null", "{\"a\":1}", "null"),
        ("1", "{\"start\":1}", "error: Cannot index number with object ({\"start\":1})"),
        ("{\"a\":1}", "{\"start\":1}", "error: Cannot index object with object ({\"start\":1})"),
    ];
    for (input, key, expected) in cases {
        assert_eq!(text(value_ops::try_index_slice(&v(input), &v(key))), expected, "{input}[{key}]");
    }
}

#[test]
fn explode() {
    let cases = [
        ("\"aé😀b\"", "[97,233,128512,98]"),
        ("\"\"", "[]"),
        ("1", "error: explode input must be a string"),
        ("[\"a\"]", "error: explode input must be a string"),
    ];
    for (input, expected) in cases {
        assert_eq!(text(value_ops::try_explode(&v(input))), expected, "{input}");
    }
}

#[test]
fn implode() {
    let cases = [
        ("[97,233,128512,98]", "\"aé😀b\""),
        ("[]", "\"\""),
        ("[65.9]", "\"A\""),
        ("[1114111]", "\"\u{10FFFF}\""),
        // jq 1.8.2: `[-0.5, 97, 1114112, 55296, -1, 97.9] | implode | explode` is [0,97,65533,65533,65533,97].
        ("[-0.5,97,1114112,55296,-1,97.9]", "\"\\u0000a\u{FFFD}\u{FFFD}\u{FFFD}a\""),
        ("[57343,57344,1e300,-1e300]", "\"\u{FFFD}\u{E000}\u{FFFD}\u{FFFD}\""),
        ("[\"a\"]", "error: string (\"a\") can't be imploded, unicode codepoint needs to be numeric"),
        ("[1,[1]]", "error: array ([1]) can't be imploded, unicode codepoint needs to be numeric"),
        ("[nan]", "error: number (null) can't be imploded, unicode codepoint needs to be numeric"),
        ("[null]", "error: null (null) can't be imploded, unicode codepoint needs to be numeric"),
        ("\"a\"", "error: implode input must be an array"),
        ("{}", "error: implode input must be an array"),
    ];
    for (input, expected) in cases {
        assert_eq!(text(value_ops::try_implode(&v(input))), expected, "{input}");
    }
}

// The reference checks that a lone surrogate of a UTF-16 string becomes U+FFFD; a Rust string has
// none. What can be ill-formed here is UTF-8 bytes: the examples of spec §4.1.
#[test]
fn ill_formed_utf8_becomes_one_replacement_character_per_maximal_subpart() {
    let cases: [(&[u8], &str); 8] = [
        (&[0xC3, 0xA9], "é"),
        (&[0xED, 0xA0, 0x80], "\u{FFFD}\u{FFFD}\u{FFFD}"),
        (&[0xE0, 0x80, 0x80], "\u{FFFD}\u{FFFD}\u{FFFD}"),
        (&[0xF4, 0x90, 0x80, 0x80], "\u{FFFD}\u{FFFD}\u{FFFD}\u{FFFD}"),
        (&[0xC0, 0xA0], "\u{FFFD}\u{FFFD}"),
        (&[0xE2, 0x82, 0x41], "\u{FFFD}A"),
        (&[0xE9, 0x65], "\u{FFFD}e"),
        (&[0xF0, 0x9F, 0x98], "\u{FFFD}"),
    ];
    for (bytes, expected) in cases {
        assert_eq!(utf8::decode_replacing(bytes.to_vec()), expected, "{bytes:X?}");
        assert_eq!(utf8::try_decode(bytes.to_vec()).is_some(), !expected.contains('\u{FFFD}'), "{bytes:X?}");
    }
}

// ---- ValueArithmeticTests ----

fn apply(op: &str, a: &str, b: &str) -> String {
    text(match op {
        "+" => value_ops::try_add(v(a), v(b)),
        "-" => value_ops::try_subtract(v(a), v(b)),
        "*" => value_ops::try_multiply(v(a), v(b)),
        "/" => value_ops::try_divide(v(a), v(b)),
        _ => value_ops::try_modulo(v(a), v(b)),
    })
}

#[test]
fn add() {
    let cases = [
        ("null", "1", "1"),
        ("1", "null", "1"),
        ("null", "null", "null"),
        ("null", "{\"a\":1}", "{\"a\":1}"),
        ("1", "2", "3"),
        ("0.1", "0.2", "0.30000000000000004"),
        ("1e308", "1e308", "1.7976931348623157e+308"),
        ("\"a\"", "\"b\"", "\"ab\""),
        ("[1]", "[2,[3]]", "[1,2,[3]]"),
        ("[]", "[]", "[]"),
        ("{\"a\":1,\"b\":2}", "{\"c\":3,\"a\":4}", "{\"a\":4,\"b\":2,\"c\":3}"),
        ("{\"b\":1}", "{\"a\":2}", "{\"b\":1,\"a\":2}"),
        ("1", "\"a\"", "error: number (1) and string (\"a\") cannot be added"),
        ("{}", "[]", "error: object ({}) and array ([]) cannot be added"),
        ("true", "true", "error: boolean (true) and boolean (true) cannot be added"),
        ("false", "1", "error: boolean (false) and number (1) cannot be added"),
        ("{\"a\":\"very long string\"}", "1", "error: object ({\"a\":\"very long string\"}) and number (1) cannot be added"),
        ("[1,2,3,4,5,6,7,8,9,10,11,12,13]", "1", "error: array ([1,2,3,4,5,6,7,8,9,10,11,...]) and number (1) cannot be added"),
    ];
    for (a, b, expected) in cases {
        assert_eq!(apply("+", a, b), expected, "{a} + {b}");
    }
}

#[test]
fn subtract() {
    let cases = [
        ("5", "3", "2"),
        ("[1,2,3,1,[1]]", "[1,[1]]", "[2,3]"),
        ("[1,2]", "[]", "[1,2]"),
        ("[{\"a\":1,\"b\":2}]", "[{\"b\":2,\"a\":1}]", "[]"),
        ("\"a\"", "\"a\"", "error: string (\"a\") and string (\"a\") cannot be subtracted"),
        ("null", "1", "error: null (null) and number (1) cannot be subtracted"),
        ("{}", "{}", "error: object ({}) and object ({}) cannot be subtracted"),
    ];
    for (a, b, expected) in cases {
        assert_eq!(apply("-", a, b), expected, "{a} - {b}");
    }
}

#[test]
fn multiply() {
    // The reference also has `"ab" * 1e9`, an error there because .NET cannot hold a string of two
    // billion characters; the spec's limit is 2^31 - 1 UTF-8 bytes, which that result does not reach.
    let cases = [
        ("2", "3", "6"),
        ("\"ab\"", "3", "\"ababab\""),
        ("3", "\"ab\"", "\"ababab\""),
        ("\"ab\"", "1", "\"ab\""),
        ("\"ab\"", "1.9", "\"ab\""),
        ("\"ab\"", "1.5", "\"ab\""),
        ("\"x\"", "0", "\"\""),
        ("\"ab\"", "0.5", "\"\""),
        ("\"ab\"", "-0", "\"\""),
        ("0", "\"ab\"", "\"\""),
        ("\"ab\"", "-0.5", "null"),
        ("\"ab\"", "-1", "null"),
        ("\"ab\"", "nan", "null"),
        ("\"\"", "5", "\"\""),
        ("\"\"", "1e300", "\"\""),
        ("\"é\"", "1.5e9", "error: Repeat string result too long"),
        ("\"abc\"", "1e9", "error: Repeat string result too long"),
        ("{\"a\":{\"b\":1},\"c\":1}", "{\"a\":{\"d\":2},\"c\":{\"x\":1}}", "{\"a\":{\"b\":1,\"d\":2},\"c\":{\"x\":1}}"),
        ("{\"a\":{\"b\":{\"c\":1}}}", "{\"a\":{\"b\":{\"d\":2}},\"e\":3}", "{\"a\":{\"b\":{\"c\":1,\"d\":2}},\"e\":3}"),
        ("{\"a\":{\"b\":1}}", "{\"a\":1}", "{\"a\":1}"),
        ("[1]", "2", "error: array ([1]) and number (2) cannot be multiplied"),
        ("\"a\"", "\"b\"", "error: string (\"a\") and string (\"b\") cannot be multiplied"),
        ("null", "2", "error: null (null) and number (2) cannot be multiplied"),
    ];
    for (a, b, expected) in cases {
        assert_eq!(apply("*", a, b), expected, "{a} * {b}");
    }
}

#[test]
fn divide() {
    let cases = [
        ("6", "4", "1.5"),
        ("-1", "3", "-0.3333333333333333"),
        ("1", "0", "error: number (1) and number (0) cannot be divided because the divisor is zero"),
        ("0", "-0", "error: number (0) and number (-0) cannot be divided because the divisor is zero"),
        ("\"a,b,c\"", "\",\"", "[\"a\",\"b\",\"c\"]"),
        ("\"a,b,\"", "\",\"", "[\"a\",\"b\",\"\"]"),
        ("\"a, b\"", "\", \"", "[\"a\",\"b\"]"),
        ("\"abc\"", "\"x\"", "[\"abc\"]"),
        ("\"\"", "\",\"", "[]"),
        ("\"aé😀\"", "\"\"", "[\"a\",\"é\",\"😀\"]"),
        ("[]", "1", "error: array ([]) and number (1) cannot be divided"),
        ("\"a\"", "1", "error: string (\"a\") and number (1) cannot be divided"),
    ];
    for (a, b, expected) in cases {
        assert_eq!(apply("/", a, b), expected, "{a} / {b}");
    }
}

#[test]
fn modulo() {
    let cases = [
        ("5", "3", "2"),
        ("-5", "3", "-2"),
        ("5", "-3", "2"),
        ("5.9", "3.9", "2"),
        ("5", "-1", "0"),
        ("-9223372036854775808", "-1", "0"),
        ("1e300", "7", "0"),
        ("nan", "1", "null"),
        ("1", "nan", "null"),
        ("1e300", "-1e300", "9223372036854776000"),
        ("-1e300", "1e300", "-1"),
        ("1", "0", "error: number (1) and number (0) cannot be divided (remainder) because the divisor is zero"),
        ("5", "0.5", "error: number (5) and number (0.5) cannot be divided (remainder) because the divisor is zero"),
        ("\"a\"", "1", "error: string (\"a\") and number (1) cannot be divided (remainder)"),
        ("1", "\"a\"", "error: number (1) and string (\"a\") cannot be divided (remainder)"),
    ];
    for (a, b, expected) in cases {
        assert_eq!(apply("%", a, b), expected, "{a} % {b}");
    }
}

#[test]
fn remainder_of_infinities() {
    // [(infinite, -infinite) % (1, -1, infinite)] is [0,0,0,0,0,-1] (spec §4.4).
    let mut results = Vec::new();
    for a in [f64::INFINITY, f64::NEG_INFINITY] {
        for b in [1.0, -1.0, f64::INFINITY] {
            results.push(text(value_ops::try_modulo(n(a), n(b))));
        }
    }
    assert_eq!(results, ["0", "0", "0", "0", "0", "-1"]);
}

#[test]
fn negation() {
    let cases = [
        ("1", "-1"),
        ("0", "-0"),
        ("-0", "0"),
        ("nan", "null"),
        ("null", "error: null (null) cannot be negated"),
        ("\"a\"", "error: string (\"a\") cannot be negated"),
        ("\"very-long-long-long-long-string\"", "error: string (\"very-long-long-long-long...\") cannot be negated"),
    ];
    for (a, expected) in cases {
        assert_eq!(text(value_ops::try_negate(v(a))), expected, "-{a}");
    }
}

// The operations take their operands by value and change the ones they own; a value that is
// shared (here: kept by the test) never changes.
#[test]
fn operands_never_change() {
    let a = v("{\"a\":1,\"b\":{\"c\":2}}");
    let b = v("{\"b\":{\"d\":3},\"e\":4}");
    assert!(value_ops::try_add(a.clone(), b.clone()).is_ok());
    assert!(value_ops::try_multiply(a.clone(), b.clone()).is_ok());
    assert_eq!(j(&a), "{\"a\":1,\"b\":{\"c\":2}}");
    assert_eq!(j(&b), "{\"b\":{\"d\":3},\"e\":4}");

    let items = v("[1,[2],\"s\"]");
    let text = v("\"abc\"");
    assert_eq!(j(&value_ops::try_add(items.clone(), v("[3]")).unwrap()), "[1,[2],\"s\",3]");
    assert_eq!(j(&value_ops::try_subtract(items.clone(), v("[1]")).unwrap()), "[[2],\"s\"]");
    assert_eq!(j(&value_ops::try_slice(items.clone(), &n(1.0), &JqValue::Null).unwrap()), "[[2],\"s\"]");
    assert_eq!(j(&value_ops::try_add(text.clone(), v("\"d\"")).unwrap()), "\"abcd\"");
    assert_eq!(j(&value_ops::sort(v("[3,1,2]").as_array().clone()).into()), "[1,2,3]");
    assert_eq!(j(&items), "[1,[2],\"s\"]");
    assert_eq!(j(&text), "\"abc\"");
}

#[test]
fn deep_merge_of_ten_thousand_levels_does_not_recurse() {
    // {"a":{"a":…{"x":1}}} * {"a":{"a":…{"y":2}}}, 10,000 levels, on a 1 MB stack.
    fn nest(name: &str, depth: usize) -> JqValue {
        let mut value = JqValue::Object(JqObject::empty().set(key(name), n(depth as f64)));
        for _ in 0..depth {
            value = JqValue::Object(JqObject::empty().set(key("a"), value));
        }
        value
    }

    let innermost = on_thread(MEGABYTE, || {
        let mut result = value_ops::try_multiply(nest("x", 10_000), nest("y", 10_000)).expect("objects merge");
        for _ in 0..10_000 {
            let inner = result.as_object().value_at(0).clone();
            result = inner;
        }
        j(&result)
    });
    assert_eq!(innermost, "{\"x\":10000,\"y\":10000}");
}

// ---- ValueOrderingTests ----

// In jq's order; no two are equal.
const ORDERED: [&str; 36] = [
    "null",
    "false",
    "true",
    "-1e300",
    "-1",
    "0",
    "0.5",
    "1",
    "1e300",
    "\"\"",
    "\"A\"",
    "\"a\"",
    "\"ab\"",
    "\"b\"",
    "\"é\"",
    "\"\u{E000}\"",
    "\"\u{FFFF}\"",
    "\"😀\"",
    "[]",
    "[null]",
    "[1]",
    "[1,2]",
    "[1,3]",
    "[2]",
    "[\"a\"]",
    "[[]]",
    "{}",
    "{\"a\":1}",
    "{\"a\":2}",
    "{\"a\":[]}",
    "{\"a\":1,\"b\":1}",
    "{\"a\":2,\"b\":1}",
    "{\"a\":1,\"c\":0}",
    "{\"b\":0}",
    "{\"é\":0}",
    "{\"😀\":0}",
];

#[test]
fn every_pair_is_ordered() {
    for (i, first) in ORDERED.iter().enumerate() {
        let a = v(first);
        assert_eq!(value_ops::compare(&a, &v(first)), Ordering::Equal);
        assert!(value_ops::are_equal(&a, &v(first)), "{first}");
        for second in &ORDERED[i + 1..] {
            let b = v(second);
            assert_eq!(value_ops::compare(&a, &b), Ordering::Less, "{first} < {second}");
            assert_eq!(value_ops::compare(&b, &a), Ordering::Greater, "{second} > {first}");
            assert!(!value_ops::are_equal(&a, &b), "{first} != {second}");
        }
    }
}

// Values 10,000 deep are read, written, compared and released on a 1 MB stack (§11).
#[test]
fn deep_values_compare() {
    on_thread(MEGABYTE, || {
        let a = v(&("[".repeat(10000) + &"]".repeat(10000)));
        let b = v(&("[".repeat(10000) + "1" + &"]".repeat(10000)));
        assert!(value_ops::are_equal(&a, &v(&j(&a))));
        assert_eq!(value_ops::compare(&a, &b), Ordering::Less);
    });
}

// No value is too deep to be released: a million levels, on a 1 MB stack.
#[test]
fn very_deep_values_are_released_without_recursion() {
    on_thread(MEGABYTE, || {
        let mut array = n(1.0);
        let mut object = n(1.0);
        for _ in 0..1_000_000 {
            array = JqValue::Array(JqArray::from_owned(vec![array]));
            object = JqValue::Object(JqObject::empty().set(key("a"), object));
        }
        drop(array);
        drop(object);
    });
}

#[test]
fn objects_order_by_key_sets_first() {
    assert_eq!(value_ops::compare(&v("{\"a\":2,\"b\":1}"), &v("{\"a\":1,\"c\":0}")), Ordering::Less);
}

#[test]
fn object_key_order_does_not_matter() {
    let a = v("{\"a\":1,\"b\":[1,{\"x\":2,\"y\":3}]}");
    let b = v("{\"b\":[1,{\"y\":3,\"x\":2}],\"a\":1}");
    assert!(value_ops::are_equal(&a, &b));
    assert_eq!(value_ops::compare(&a, &b), Ordering::Equal);
}

#[test]
fn strings_order_by_code_point() {
    // U+E000 is below U+1F600, though its UTF-16 unit is above the pair's: UTF-8 bytes agree with code points.
    assert_eq!(code_points::compare("\u{E000}", "😀"), Ordering::Less);
    assert_eq!(code_points::compare("a😀", "a\u{FFFD}"), Ordering::Greater);
    assert_eq!(code_points::compare("😀", "😁"), Ordering::Less);
    assert_eq!(code_points::compare("a", "a😀"), Ordering::Less);
}

#[test]
fn nan_sorts_below_numbers_compares_equal_to_nan_but_is_never_equal() {
    let nan = n(f64::NAN);
    assert_eq!(value_ops::compare(&nan, &n(f64::NEG_INFINITY)), Ordering::Less);
    assert_eq!(value_ops::compare(&n(-1e300), &nan), Ordering::Greater);
    assert_eq!(value_ops::compare(&nan, &JqValue::True), Ordering::Greater);
    assert_eq!(value_ops::compare(&nan, &JqValue::from_text("")), Ordering::Less);
    // A total order (spec §4.3, §13): [nan < nan, nan <= nan, nan > nan, nan >= nan] is [false,true,false,true].
    assert_eq!(value_ops::compare(&nan, &nan), Ordering::Equal);
    assert_eq!(value_ops::compare(&v("[nan,{\"a\":nan}]"), &v("[nan,{\"a\":nan}]")), Ordering::Equal);
    // == stays IEEE: nan == nan is false, also inside containers.
    assert!(!value_ops::are_equal(&nan, &nan));
    assert!(!value_ops::are_equal(&v("[nan]"), &v("[nan]")));
    assert_eq!(value_ops::compare(&v("[nan]"), &v("[0]")), Ordering::Less);
}

#[test]
fn sort_keeps_nans_together_and_stable() {
    assert_eq!(value_ops::sort_order(v("[1,nan,\"a\",-1,nan,null]").as_array()), [5, 1, 4, 3, 0, 2]);
}

#[test]
fn equal_values() {
    let cases =
        [("1", "1.0"), ("-0", "0"), ("1e2", "100"), ("null", "null"), ("[1,[2,{\"a\":null}]]", "[1,[2,{\"a\":null}]]"), ("\"😀\"", "\"\\ud83d\\ude00\"")];
    for (a, b) in cases {
        assert!(value_ops::are_equal(&v(a), &v(b)), "{a} == {b}");
        assert_eq!(value_ops::compare(&v(a), &v(b)), Ordering::Equal, "{a} == {b}");
    }
}

#[test]
fn unequal_values() {
    let cases = [("1", "\"1\""), ("false", "null"), ("[1,2]", "[2,1]"), ("{\"a\":1}", "{\"a\":1,\"b\":null}"), ("{\"a\":1}", "{\"b\":1}")];
    for (a, b) in cases {
        assert!(!value_ops::are_equal(&v(a), &v(b)), "{a} != {b}");
    }
}

#[test]
fn sort_orders_every_kind() {
    let shuffled: Vec<JqValue> = ORDERED.iter().rev().map(|text| v(text)).collect();
    let sorted = value_ops::sort(JqArray::from_owned(shuffled));
    let expected: Vec<String> = ORDERED.iter().map(|text| j(&v(text))).collect();
    assert_eq!(j(&JqValue::Array(sorted)), format!("[{}]", expected.join(",")));
}

#[test]
fn sort_order_is_stable() {
    assert_eq!(value_ops::sort_order(v("[2,1,2,1,0,2]").as_array()), [4, 1, 3, 0, 2, 5]);
    assert!(value_ops::sort_order(&JqArray::empty()).is_empty());
    assert_eq!(value_ops::sort_order(v("[7]").as_array()), [0]);
}

// A small generator, the same on every run.
struct Random(u64);

impl Random {
    fn next(&mut self) -> u64 {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 7;
        self.0 ^= self.0 << 17;
        self.0
    }
}

#[test]
fn sort_matches_a_reference_sort_on_random_input() {
    let mut random = Random(7);
    let items: Vec<JqValue> = (0..1000).map(|_| n((random.next() % 100) as f64)).collect();
    let order = value_ops::sort_order(&JqArray::from_owned(items.clone()));
    for pair in order.windows(2) {
        let c = value_ops::compare(&items[pair[0]], &items[pair[1]]);
        assert!(c == Ordering::Less || (c == Ordering::Equal && pair[0] < pair[1]));
    }
}

// ---- CollectionTests ----

fn ja(array: &JqArray) -> String {
    j(&JqValue::Array(array.clone()))
}

fn jo(object: &JqObject) -> String {
    j(&JqValue::Object(object.clone()))
}

#[test]
fn value_is_sixteen_bytes() {
    assert_eq!(std::mem::size_of::<JqValue>(), 16);
}

#[test]
fn kinds_and_truthiness() {
    assert_eq!(JqValue::default().kind(), JqKind::Null);
    assert_eq!(JqValue::from_bool(false).kind(), JqKind::False);
    assert_eq!(JqValue::from_bool(true).kind(), JqKind::True);
    assert_eq!(n(1.0).kind(), JqKind::Number);
    assert_eq!(JqValue::from_text("").kind(), JqKind::String);
    assert_eq!(JqValue::Array(JqArray::empty()).kind(), JqKind::Array);
    assert_eq!(JqValue::Object(JqObject::empty()).kind(), JqKind::Object);
    assert!(!JqValue::Null.is_truthy());
    assert!(!JqValue::False.is_truthy());
    assert!(n(0.0).is_truthy());
    assert!(JqValue::from_text("").is_truthy());
    assert_eq!(JqValue::True.type_name(), "boolean");
    assert!(std::panic::catch_unwind(|| n(1.0).as_string().len()).is_err());
}

#[test]
fn appending_twice_to_the_same_base_gives_independent_arrays() {
    let root = JqArray::empty().append(n(1.0)).append(n(2.0));
    let a = root.clone().append(n(3.0));
    let b = root.clone().append(n(4.0));
    let c = a.clone().append(n(5.0));
    let d = a.clone().concat(JqArray::of(&[n(6.0), n(7.0)]));
    assert_eq!(ja(&root), "[1,2]");
    assert_eq!(ja(&a), "[1,2,3]");
    assert_eq!(ja(&b), "[1,2,4]");
    assert_eq!(ja(&c), "[1,2,3,5]");
    assert_eq!(ja(&d), "[1,2,3,6,7]");
}

#[test]
fn slices_never_change_what_they_come_from() {
    let array = v("[1,2,3,4,5]").as_array().clone();
    let middle = array.clone().slice(1, 3);
    let tail = array.clone().slice(3, 5);
    let grown = middle.clone().append(n(9.0));
    let longer = tail.clone().append(n(8.0));
    assert_eq!(ja(&array), "[1,2,3,4,5]");
    assert_eq!(ja(&middle), "[2,3]");
    assert_eq!(ja(&grown), "[2,3,9]");
    assert_eq!(ja(&longer), "[4,5,8]");
    assert_eq!(ja(&tail), "[4,5]");
    // The only owner of an array slices it in place.
    assert_eq!(ja(&v("[1,2,3,4,5]").as_array().clone().slice(1, 4)), "[2,3,4]");
}

#[test]
fn set_item_copies_one_level_and_pads_with_null() {
    let array = v("[1,[2]]").as_array().clone();
    assert_eq!(ja(&array.clone().set_item(1, n(7.0))), "[1,7]");
    assert_eq!(ja(&array.clone().set_item(3, n(5.0))), "[1,[2],null,5]");
    assert_eq!(ja(&array.clone().set_item(2, n(6.0))), "[1,[2],6]");
    assert_eq!(ja(&array), "[1,[2]]");
    // The copy shares the elements it did not change.
    let copy = array.clone().set_item(0, n(0.0));
    assert!(!copy.get(1).as_array().is_unique());
}

#[test]
fn concurrent_appends_to_one_base_stay_independent() {
    let root = JqArray::empty().append(n(-1.0));
    let results: Vec<JqArray> = std::thread::scope(|scope| {
        let handles: Vec<_> = (0..64)
            .map(|i| {
                let root = &root;
                scope.spawn(move || {
                    let mut array = root.clone().append(n(i as f64));
                    for step in 0..10 {
                        array = array.append(n((i * 100 + step) as f64));
                    }
                    array
                })
            })
            .collect();
        handles.into_iter().map(|handle| handle.join().expect("the thread ends")).collect()
    });

    assert_eq!(ja(&root), "[-1]");
    for (i, array) in results.iter().enumerate() {
        assert_eq!(array.count(), 12);
        assert_eq!(array.get(0).as_number(), -1.0);
        assert_eq!(array.get(1).as_number(), i as f64);
        for step in 0..10 {
            assert_eq!(array.get(2 + step).as_number(), (i * 100 + step) as f64);
        }
    }
}

#[test]
fn object_keeps_insertion_order() {
    let object = JqObject::empty().set(key("b"), n(1.0)).set(key("a"), n(2.0)).set(key("c"), n(3.0));
    assert_eq!(jo(&object), "{\"b\":1,\"a\":2,\"c\":3}");
    assert_eq!(jo(&object.clone().set(key("a"), n(9.0))), "{\"b\":1,\"a\":9,\"c\":3}");
    assert_eq!(jo(&object.clone().remove("a")), "{\"b\":1,\"c\":3}");
    assert_eq!(jo(&object.clone().remove("a").set(key("a"), n(4.0))), "{\"b\":1,\"c\":3,\"a\":4}");
    assert_eq!(jo(&object.clone().remove("x")), "{\"b\":1,\"a\":2,\"c\":3}");
    assert_eq!(jo(&object), "{\"b\":1,\"a\":2,\"c\":3}");
    let sorted: Vec<&str> = object.sorted_order().iter().map(|index| object.key_at(*index as usize).as_str()).collect();
    assert_eq!(sorted, ["a", "b", "c"]);
}

#[test]
fn object_merge_keeps_insertion_order() {
    let left = v("{\"z\":1,\"y\":2,\"x\":3}").as_object().clone();
    assert_eq!(jo(&left.clone().merge(v("{\"w\":4,\"y\":5}").as_object().clone())), "{\"z\":1,\"y\":5,\"x\":3,\"w\":4}");
    assert_eq!(jo(&left.clone().merge(v("{\"a\":0,\"b\":1}").as_object().clone())), "{\"z\":1,\"y\":2,\"x\":3,\"a\":0,\"b\":1}");
    assert_eq!(jo(&left), "{\"z\":1,\"y\":2,\"x\":3}");
}

#[test]
fn the_reader_keeps_the_first_position_and_the_last_value_of_a_repeated_key() {
    assert_eq!(text(value_text::try_parse("{\"a\":1,\"b\":2,\"a\":3}")), "{\"a\":3,\"b\":2}");
    assert_eq!(text(value_text::try_parse("{\"a\":1,\"a\":{\"a\":2,\"a\":[]},\"a\":3,\"b\":[{\"c\":1,\"c\":2}]}")), "{\"a\":3,\"b\":[{\"c\":2}]}");

    // An object with more keys than are scanned.
    let mut source = String::from("{");
    for i in 0..40 {
        source += &format!("\"k{}\":{i},", i % 20);
    }
    source += "\"z\":0}";
    let value = v(&source);
    let object = value.as_object();
    assert_eq!(object.count(), 21);
    assert_eq!(object.key_at(0).as_str(), "k0");
    assert_eq!(object.key_at(20).as_str(), "z");
    assert_eq!(object.get("k0").map(JqValue::as_number), Some(20.0));
    assert_eq!(object.get("k19").map(JqValue::as_number), Some(39.0));
}

#[test]
fn equal_keys_of_a_document_are_one_string() {
    let value = v("[{\"name\":1},{\"name\":2,\"na\\u006de\":3}]");
    let items = value.as_array();
    assert!(Arc::ptr_eq(items.get(0).as_object().key_at(0), items.get(1).as_object().key_at(0)));
    assert_eq!(j(&value), "[{\"name\":1},{\"name\":3}]");

    // A key is shared, never changed: appending to a string that is also a key copies it.
    let key = JqValue::String(Arc::clone(items.get(0).as_object().key_at(0)));
    assert_eq!(text(value_ops::try_add(key, v("\"s\""))), "\"names\"");
    assert_eq!(j(&v("{\"name\":0}")), "{\"name\":0}");
}

#[test]
fn versions_of_a_large_object_see_only_their_keys() {
    let mut versions = vec![JqObject::empty()];
    for i in 1..=20 {
        let next = versions[i - 1].clone().set(key(&format!("k{i}")), n(i as f64));
        versions.push(next);
    }
    // A branch from an older version does not touch the newer ones.
    let branch = versions[10].clone().set(key("k15"), n(-15.0));
    for (i, version) in versions.iter().enumerate().skip(1) {
        assert_eq!(version.count(), i);
        assert!(version.contains_key(&format!("k{i}")));
        assert_eq!(i < 20, !version.contains_key("k20"));
        assert!(!version.contains_key(&format!("k{}", i + 1)));
    }

    assert_eq!(branch.get("k15").map(JqValue::as_number), Some(-15.0));
    assert_eq!(versions[15].get("k15").map(JqValue::as_number), Some(15.0));
    assert!(!versions[12].contains_key("k15"));
    assert_eq!(branch.index_of("k15"), Some(10));
}

#[test]
fn concurrent_key_adds_to_one_base_stay_independent() {
    let mut root = JqObject::empty();
    for i in 0..20 {
        root.insert(key(&format!("base{i}")), n(i as f64));
    }
    let results: Vec<JqObject> = std::thread::scope(|scope| {
        let handles: Vec<_> = (0..64)
            .map(|i| {
                let root = &root;
                scope.spawn(move || {
                    let mut object = root.clone().set(key(&format!("own{i}")), n(i as f64));
                    for step in 0..5 {
                        object = object.set(key(&format!("own{i}_{step}")), n(step as f64));
                    }
                    object
                })
            })
            .collect();
        handles.into_iter().map(|handle| handle.join().expect("the thread ends")).collect()
    });

    assert_eq!(root.count(), 20);
    for (i, object) in results.iter().enumerate() {
        assert_eq!(object.count(), 26);
        assert_eq!(object.key_at(20).as_str(), format!("own{i}"));
        assert_eq!(object.get(&format!("own{i}")).map(JqValue::as_number), Some(i as f64));
        assert!(!object.contains_key(&format!("own{}", (i + 1) % results.len())));
        assert!(!root.contains_key(&format!("own{i}")));
    }
}

// The reference's builder: here an object owned once is changed in place.
#[test]
fn an_owned_object_replaces_in_place_and_removes() {
    let mut object = JqObject::empty();
    for i in 0..12 {
        object.insert(key(&format!("k{i}")), n(i as f64));
    }
    assert!(object.contains_key("k11"));
    object.insert(key("k3"), n(30.0));
    let object = object.remove("k0").remove("missing");
    assert_eq!(object.count(), 11);
    assert_eq!(object.key_at(0).as_str(), "k1");
    assert_eq!(object.get("k3").map(JqValue::as_number), Some(30.0));
    assert!(!object.contains_key("k0"));
    assert_eq!(object.index_of("k11"), Some(10));
}

// ---- ValueTextTests ----

#[test]
fn number_text() {
    let cases = [
        (0.0, "0"),
        (1.0, "1"),
        (-1.0, "-1"),
        (1.5, "1.5"),
        (-1.5, "-1.5"),
        (0.5, "0.5"),
        (100.0, "100"),
        (1e15, "1000000000000000"),
        (1e20, "100000000000000000000"),
        (123456789012345680000.0, "123456789012345680000"),
        (1e21, "1e+21"),
        (-1e21, "-1e+21"),
        (1.5e300, "1.5e+300"),
        (1e-6, "0.000001"),
        (1.5e-6, "0.0000015"),
        (1e-7, "1e-7"),
        (1.23e-18, "1.23e-18"),
        (0.1 + 0.2, "0.30000000000000004"),
        (5e-324, "5e-324"),
        (1.7976931348623157e308, "1.7976931348623157e+308"),
        (9007199254740993.0, "9007199254740992"),
        (123.456, "123.456"),
        (f64::NAN, "null"),
        (f64::INFINITY, "1.7976931348623157e+308"),
        (f64::NEG_INFINITY, "-1.7976931348623157e+308"),
        (-0.0, "-0"),
    ];
    for (number, expected) in cases {
        assert_eq!(value_text::format_number(number), expected);
    }
}

#[test]
fn number_text_round_trips() {
    let mut random = Random(42);
    for _ in 0..10000 {
        let number = f64::from_bits(random.next());
        if !number.is_finite() {
            continue;
        }
        let text = value_text::format_number(number);
        assert_eq!(value_text::try_parse_number(&text), Some(number), "{text}");
    }
}

#[test]
fn string_escaping() {
    let cases = [
        ("é<", "\"é<\""),
        ("a\"b\\c/d", "\"a\\\"b\\\\c/d\""),
        ("\u{8}\u{c}\n\r\t", "\"\\b\\f\\n\\r\\t\""),
        ("\u{0}\u{1}\u{1f}", "\"\\u0000\\u0001\\u001f\""),
        ("\u{7f}\u{80}", "\"\\u007f\u{80}\""),
        ("😀", "\"😀\""),
        ("\u{2028}", "\"\u{2028}\""),
    ];
    for (text, expected) in cases {
        assert_eq!(j(&JqValue::from_text(text)), expected);
    }
}

#[test]
fn reader_round_trips() {
    let cases = [
        "null",
        "true",
        "false",
        "[]",
        "{}",
        "\"\"",
        "[1,2.5,\"x\",null,true,false,{},[[]]]",
        "{\"b\":1,\"a\":[1,{\"z\":null,\"y\":\"é\"}],\"c\":{\"d\":[]}}",
        "-0",
    ];
    for json in cases {
        assert_eq!(j(&v(json)), json);
    }
}

#[test]
fn reader_values() {
    let cases = [
        (" \t\r\n[ 1 , { \"a\" : 2 } ] \n", "[1,{\"a\":2}]"),
        ("\u{FEFF}1", "1"),
        ("1.0", "1"),
        ("1E2", "100"),
        ("1e-2", "0.01"),
        ("1.", "1"),
        (".5", "0.5"),
        ("+1", "1"),
        ("007", "7"),
        ("1e1000", "1.7976931348623157e+308"),
        ("-1e1000", "-1.7976931348623157e+308"),
        ("1e-1000", "0"),
        ("nan", "null"),
        ("NaN", "null"),
        ("[-nan]", "[null]"),
        ("Infinity", "1.7976931348623157e+308"),
        ("-Infinity", "-1.7976931348623157e+308"),
        ("\"\\u00e9\\/\\\"\"", "\"é/\\\"\""),
        ("\"\\ud83d\\ude00\"", "\"😀\""),
        ("\"\\ud800\"", "\"\u{FFFD}\""),
        ("\"\\udc00x\"", "\"\u{FFFD}x\""),
        ("\"\\ud800\\ud800\"", "\"\u{FFFD}\u{FFFD}\""),
        ("\"\\ud800\\u0041\"", "\"\u{FFFD}A\""),
        ("\"\\ud800\\ud800\\udc00\"", "\"\u{FFFD}\u{10000}\""),
        ("[nAn,SNaN,-snan,+inf,-INF,iNfInItY]", "[null,null,null,1.7976931348623157e+308,-1.7976931348623157e+308,1.7976931348623157e+308]"),
        ("[1,nan]", "[1,null]"),
        ("{\"a\":1,\"b\":2,\"a\":3}", "{\"a\":3,\"b\":2}"),
        ("\"a\u{7F}b\"", "\"a\\u007fb\""),
        ("{\"a\":1,\"a\":2}", "{\"a\":2}"),
        ("{\"\\ud800\":1}", "{\"\u{FFFD}\":1}"),
    ];
    for (json, expected) in cases {
        assert_eq!(j(&v(json)), expected, "{json}");
    }
}

// The reason (implementation-defined, §5.3); every message ends with " (while parsing '<input>')".
#[test]
fn reader_failures() {
    let cases = [
        ("", "Expected JSON value"),
        ("   ", "Expected JSON value"),
        ("1 2", "Unexpected extra JSON values"),
        ("{", "Unfinished JSON term at EOF at line 1, column 1"),
        ("[1,2", "Unfinished JSON term at EOF at line 1, column 4"),
        ("\"abc", "Unfinished JSON term at EOF at line 1, column 4"),
        ("[1 2]", "Expected separator between values at line 1, column 4"),
        ("[1,\n2 3]", "Expected separator between values at line 2, column 3"),
        ("tru", "Invalid literal at line 1, column 3"),
        ("nul", "Invalid literal at line 1, column 3"),
        ("Null", "Invalid literal at line 1, column 4"),
        ("NaN1", "Invalid literal at line 1, column 4"),
        ("nanx", "Invalid literal at line 1, column 4"),
        ("[1x]", "Invalid numeric literal at line 1, column 3"),
        ("1e", "Invalid numeric literal at line 1, column 2"),
        ("'a'", "Invalid numeric literal at line 1, column 3"),
        ("\"\\q\"", "Invalid escape at line 1, column 3"),
        ("\"\\u12G4\"", "Invalid \\uXXXX escape at line 1, column 6"),
        ("\"a\nb\"", "Invalid string: control characters from U+0000 through U+001F must be escaped at line 2, column 0"),
        ("\"a\u{1}\"", "Invalid string: control characters from U+0000 through U+001F must be escaped at line 1, column 3"),
        ("[\"\t\"]", "Invalid string: control characters from U+0000 through U+001F must be escaped at line 1, column 3"),
        ("{1:2}", "Object keys must be strings at line 1, column 2"),
        ("{\"a\" 1}", "Objects must consist of key:value pairs at line 1, column 6"),
        ("[1,]", "Expected another array element at line 1, column 4"),
        ("]", "Unmatched ']' at line 1, column 1"),
        ("[1}", "Unmatched '}' at line 1, column 3"),
        (",", "Expected value before ',' at line 1, column 1"),
        // Columns count code points.
        ("[\"é😀\" 1]", "Expected separator between values at line 1, column 7"),
    ];
    for (json, reason) in cases {
        let error = value_text::try_parse(json).expect_err(json);
        assert_eq!(error.as_string(), format!("{reason} (while parsing '{json}')"));
    }
}

#[test]
fn reader_depth_limit() {
    on_thread(MEGABYTE, || {
        let ok = "[".repeat(10000) + &"]".repeat(10000);
        let deep = value_text::try_parse(&ok).expect("10,000 levels are read");
        assert_eq!(j(&deep), ok);
        let error = value_text::try_parse(&"[".repeat(10001)).expect_err("10,001 levels are too deep");
        assert!(error.as_string().starts_with("Exceeds depth limit for parsing"));
    });
}

#[test]
fn reader_of_sequences() {
    assert_eq!(text(value_text::try_parse_sequence("")), "[]");
    assert_eq!(text(value_text::try_parse_sequence(" 1 [2] \"a\"\n{\"b\":null} ")), "[1,[2],\"a\",{\"b\":null}]");
    assert!(value_text::try_parse_sequence("1 [").is_err());
}

// Arrays nested `levels` deep around `inner`.
fn nest(levels: usize, inner: JqValue) -> JqValue {
    let mut value = inner;
    for _ in 0..levels {
        value = JqValue::Array(JqArray::from_owned(vec![value]));
    }
    value
}

#[test]
fn writer_skips_values_deeper_than_10001_levels() {
    on_thread(MEGABYTE, || {
        let skipped = "<skipped: too deep>";
        let (open, close) = ("[".repeat(10001), "]".repeat(10001));
        // jq 1.8.2: `reduce range(n) as $i (x; [.]) | tojson`.
        assert_eq!(j(&nest(10000, n(1.0))), "[".repeat(10000) + "1" + &"]".repeat(10000));
        assert_eq!(j(&nest(10001, n(1.0))), format!("{open}{skipped}{close}"));
        assert_eq!(j(&nest(10000, JqValue::Array(JqArray::empty()))), format!("{open}{close}"));
        assert_eq!(j(&nest(10001, JqValue::Array(JqArray::empty()))), format!("{open}{skipped}{close}"));
        assert_eq!(j(&nest(10000, v("[1,[2]]"))), format!("{open}{skipped},{skipped}{close}"));
    });
}

#[test]
fn number_reading() {
    let cases =
        [("12", 12.0), ("-1.5e3", -1500.0), ("0.1", 0.1), (".5", 0.5), ("5.", 5.0), ("+3", 3.0), ("inf", f64::INFINITY), ("-INFINITY", f64::NEG_INFINITY)];
    for (text, expected) in cases {
        assert_eq!(value_text::try_parse_number(text), Some(expected), "{text}");
    }
}

#[test]
fn number_reading_failures() {
    for text in ["", " 1", "1 ", "0x10", ".", "-", "1e+", "1,5", "abc", "1_0", "infinit", "+-1"] {
        assert_eq!(value_text::try_parse_number(text), None, "{text}");
    }
}

#[test]
fn nan_words_read_as_nan() {
    for text in ["nan", "NaN", "-nan", "+NAN", "snan", "-sNaN"] {
        assert!(value_text::try_parse_number(text).is_some_and(f64::is_nan), "{text}");
    }
}

#[test]
fn error_dump() {
    let cases = [
        ("[1,2]", "[1,2]"),
        ("[1,2,3,4,5,6,7,8,9,10,11,12,13]", "[1,2,3,4,5,6,7,8,9,10,11,...]"),
        ("{\"aaaaaaaaaaaaaaaaaaaaaaaaaa\":1}", "{\"aaaaaaaaaaaaaaaaaaaaaaa...}"),
        // A cut value ends with "..." and its own closing delimiter, not those of what it contains.
        ("\"very-long-long-long-long-string\"", "\"very-long-long-long-long...\""),
        ("[\"very-long-long-long-long-string\"]", "[\"very-long-long-long-lon...]"),
        // 27 characters: 29 bytes with the quotes, not cut; one more is cut.
        ("\"abcdefghijklmnopqrstuvwxyz0\"", "\"abcdefghijklmnopqrstuvwxyz0\""),
        ("\"abcdefghijklmnopqrstuvwxyz01\"", "\"abcdefghijklmnopqrstuvwx...\""),
        // Two-byte é: the quote and 12 of them make 25 bytes.
        ("\"ééééééééééééééééééé\"", "\"éééééééééééé...\""),
        ("\"a😀😀😀😀😀😀😀\"", "\"a😀😀😀😀😀...\""),
    ];
    for (json, expected) in cases {
        let dump = errors::dump(&v(json));
        assert_eq!(dump, expected);
        assert!(dump.len() <= 29);
    }
}
