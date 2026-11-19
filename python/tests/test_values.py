"""The values layer (dotnet/Tests/Core/Values): code points, collections, arithmetic, ordering, text."""

from __future__ import annotations

import math
import random
import struct
import threading
import unittest
from typing import Any

from jqlite.values import errors, value_ops, value_text
from jqlite.values.code_points import sanitize
from jqlite.values.jq_array import JqArray
from jqlite.values.jq_object import JqObject
from jqlite.values.jq_object_builder import JqObjectBuilder
from jqlite.values.jq_string import MIN_LENGTH, JqString
from jqlite.values.jq_value import ARRAY, FALSE, NULL, NUMBER, OBJECT, STRING, TRUE, Fail, is_truthy, kind, type_name


def V(json: str) -> Any:
    """Reads JSON text; fails the test when it is not valid."""
    value = value_text.try_parse(json)
    if type(value) is Fail:
        raise AssertionError(f"Bad test JSON {json}: {value.error}")
    return value


def J(value: Any) -> str:
    """The canonical JSON text of a value."""
    return value_text.to_json(value)  # type: ignore[arg-type]


def text_of(result: Any) -> str:
    return "error: " + result.error if type(result) is Fail else J(result)  # type: ignore[union-attr,operator]


def on_small_stack(test: unittest.TestCase, step: object, size: int = 1024 * 1024) -> object:
    """Runs ``step`` on a thread with a small C stack and returns its result."""
    result: list = []
    failure: list = []

    def run() -> None:
        try:
            result.append(step())  # type: ignore[operator]
        except BaseException as e:  # noqa: BLE001
            failure.append(e)
    previous = threading.stack_size(size)
    try:
        thread = threading.Thread(target=run)
        thread.start()
    finally:
        threading.stack_size(previous)
    thread.join()
    if failure:
        raise failure[0]
    return result[0]


class CodePointTests(unittest.TestCase):
    def test_length_counts_code_points(self) -> None:
        for text, length in [("", 0), ("abc", 3), ("aé😀b", 4), ("😀😀", 2)]:
            self.assertEqual(length, len(text))

    def test_slices(self) -> None:
        cases = [
            ('"aé😀b"', "2", "3", '"😀"'), ('"aé😀b"', "-2", "null", '"😀b"'), ('"aé😀b"', "null", "2", '"aé"'),
            ('"aé😀b"', "1.5", "2.5", '"é😀"'), ('"aé😀b"', "3", "1", '""'), ('"aé😀b"', "-100", "100", '"aé😀b"'),
            ('"aé😀b"', "nan", "nan", '"aé😀b"'), ("[1,2,3,4]", "1", "3", "[2,3]"), ("[1,2,3,4]", "-1", "null", "[4]"),
            ("[1,2,3,4]", "null", "-1", "[1,2,3]"), ("[1,2,3,4]", "0.2", "0.2", "[1]"), ("[1,2,3,4]", "5", "10", "[]"),
            ("null", "1", "2", "null"), ("[0,1,2,3,4,5,6,7,8,9]", "1.2", "3.5", "[1,2,3]"), ('"abcdefghi"', "-2", "null", '"hi"'),
            ("null", '"a"', "{}", "null"),
            ("1", "1", "2", 'error: Cannot index number with object ({"start":1,"end":2})'),
            ("1", '"a"', "2", 'error: Cannot index number with object ({"start":"a","end":2})'),
            ("{}", "1", "null", 'error: Cannot index object with object ({"start":1,"end":null})'),
            ("[1]", '"a"', "null", "error: Array/string slice indices must be integers"),
            ('"ab"', "0", "true", "error: Array/string slice indices must be integers"),
        ]
        for source, start, end, expected in cases:
            with self.subTest(source=source, start=start, end=end):
                self.assertEqual(expected, text_of(value_ops.try_slice(V(source), V(start), V(end))))

    def test_slices_by_object_key(self) -> None:
        cases = [
            ("[1,2,3]", '{"start":1,"end":null}', "[2,3]"), ("[1,2,3]", '{"start":1,"end":2,"x":0}', "[2]"),
            ('"abc"', '{"start":null,"end":null}', '"abc"'),
            ('"abc"', '{"start":1}', "error: Array/string slice indices must be integers"),
            ("[1,2,3]", '{"end":2}', "error: Array/string slice indices must be integers"),
            ("[1,2,3]", '{"start":"a","end":2}', "error: Array/string slice indices must be integers"),
            ("null", '{"start":1}', "null"), ("null", '{"a":1}', "null"),
            ("1", '{"start":1}', 'error: Cannot index number with object ({"start":1})'),
            ('{"a":1}', '{"start":1}', 'error: Cannot index object with object ({"start":1})'),
        ]
        for source, key, expected in cases:
            with self.subTest(source=source, key=key):
                self.assertEqual(expected, text_of(value_ops.try_index_slice(V(source), V(key))))

    def test_explode(self) -> None:
        for source, expected in [('"aé😀b"', "[97,233,128512,98]"), ('""', "[]"), ("1", "error: explode input must be a string"),
                                 ('["a"]', "error: explode input must be a string")]:
            self.assertEqual(expected, text_of(value_ops.try_explode(V(source))))

    def test_implode(self) -> None:
        cases = [
            ("[97,233,128512,98]", '"aé😀b"'), ("[]", '""'), ("[65.9]", '"A"'), ("[1114111]", '"\U0010FFFF"'),
            ("[-0.5,97,1114112,55296,-1,97.9]", '"\\u0000a\ufffd\ufffd\ufffda"'),
            ("[57343,57344,1e300,-1e300]", '"\ufffd\ue000\ufffd\ufffd"'),
            ('["a"]', "error: string (\"a\") can't be imploded, unicode codepoint needs to be numeric"),
            ("[1,[1]]", "error: array ([1]) can't be imploded, unicode codepoint needs to be numeric"),
            ("[nan]", "error: number (null) can't be imploded, unicode codepoint needs to be numeric"),
            ("[null]", "error: null (null) can't be imploded, unicode codepoint needs to be numeric"),
            ('"a"', "error: implode input must be an array"), ("{}", "error: implode input must be an array"),
        ]
        for source, expected in cases:
            with self.subTest(source=source):
                self.assertEqual(expected, text_of(value_ops.try_implode(V(source))))

    def test_lone_surrogates_become_replacement_characters(self) -> None:
        high = "\ud83d"
        low = "\ude00"
        r = "\ufffd"
        self.assertEqual(f"a{r}b", sanitize(f"a{high}b"))
        self.assertEqual(r, sanitize(low))
        self.assertEqual(r, sanitize(high))
        # A Python string can hold a pair as two code points: they are the code point they stand for.
        self.assertEqual(f"😀{r}", sanitize(f"{high}{low}{high}"))
        self.assertEqual(f"{r}{r}", sanitize(f"{low}{high}"))

    def test_well_formed_strings_are_not_copied(self) -> None:
        text = "aé😀b"
        self.assertIs(text, sanitize(text))


def N(number: float) -> float:
    return float(number)


class CollectionTests(unittest.TestCase):
    def test_kinds_and_truthiness(self) -> None:
        self.assertEqual(NULL, kind(None))
        self.assertEqual(FALSE, kind(False))
        self.assertEqual(TRUE, kind(True))
        self.assertEqual(NUMBER, kind(1.0))
        self.assertEqual(STRING, kind(""))
        self.assertEqual(ARRAY, kind(JqArray.EMPTY))
        self.assertEqual(OBJECT, kind(JqObject.EMPTY))
        self.assertFalse(is_truthy(None))
        self.assertFalse(is_truthy(False))
        self.assertTrue(is_truthy(0.0))
        self.assertTrue(is_truthy(""))
        self.assertEqual("boolean", type_name(True))
        # An int is never a jqlite value: the boundary converts it.
        with self.assertRaises(KeyError):
            kind(1)

    def test_appending_twice_to_the_same_base_gives_independent_arrays(self) -> None:
        root = JqArray.EMPTY.append(N(1)).append(N(2))
        a = root.append(N(3))
        b = root.append(N(4))
        c = a.append(N(5))
        d = a.concat(JqArray.of(N(6), N(7)))
        self.assertEqual("[1,2]", J(root))
        self.assertEqual("[1,2,3]", J(a))
        self.assertEqual("[1,2,4]", J(b))
        self.assertEqual("[1,2,3,5]", J(c))
        self.assertEqual("[1,2,3,6,7]", J(d))

    def test_slices_share_storage_but_never_change(self) -> None:
        array = V("[1,2,3,4,5]")
        middle = array.slice(1, 3)  # type: ignore[union-attr]
        tail = array.slice(3, 5)  # type: ignore[union-attr]
        grown = middle.append(N(9))
        longer = tail.append(N(8))
        self.assertEqual("[1,2,3,4,5]", J(array))
        self.assertEqual("[2,3]", J(middle))
        self.assertEqual("[2,3,9]", J(grown))
        self.assertEqual("[4,5,8]", J(longer))
        self.assertEqual("[4,5]", J(tail))

    def test_set_item_copies_one_level_and_pads_with_null(self) -> None:
        array: JqArray = V("[1,[2]]")  # type: ignore[assignment]
        self.assertEqual("[1,7]", J(array.set_item(1, N(7))))
        self.assertEqual("[1,[2],null,5]", J(array.set_item(3, N(5))))
        self.assertEqual("[1,[2],6]", J(array.set_item(2, N(6))))
        self.assertEqual("[1,[2]]", J(array))
        self.assertIs(array.get(1), array.set_item(0, N(0)).get(1))

    def test_concurrent_appends_to_one_base_stay_independent(self) -> None:
        root = JqArray.EMPTY.append(N(-1))
        results: list = [None] * 200

        def work(i: int) -> None:
            array = root.append(N(i))
            for j in range(10):
                array = array.append(N(i * 100 + j))
            results[i] = array
        threads = [threading.Thread(target=work, args=(i,)) for i in range(len(results))]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        self.assertEqual("[-1]", J(root))
        for i, array in enumerate(results):
            self.assertEqual(12, array.count)
            self.assertEqual(-1, array.get(0))
            self.assertEqual(i, array.get(1))
            for j in range(10):
                self.assertEqual(i * 100 + j, array.get(2 + j))

    def test_appending_in_a_loop_does_not_copy_the_array(self) -> None:
        # reduce range(10000) as $i ([]; . + [$i]): each version claims the next slot of one buffer.
        array = JqArray.EMPTY.concat(JqArray.of(N(0)))
        first = array.buffer
        for i in range(1, 10000):
            array = array.concat(JqArray.of(N(i)))
        self.assertEqual(10000, array.count)
        self.assertEqual(9999, array.get(9999))
        self.assertIs(first, array.buffer)

    def test_adding_twice_to_the_same_long_string_gives_independent_strings(self) -> None:
        head = "x" * MIN_LENGTH
        root = value_ops.concat(head, "a")
        a = value_ops.concat(root, "b")
        b = value_ops.concat(root, "c")
        c = value_ops.concat(a, "d")
        self.assertIs(JqString, type(c))
        self.assertEqual(head + "a", root.text)
        self.assertEqual(head + "ab", a.text)
        self.assertEqual(head + "ac", b.text)
        self.assertEqual(head + "abd", c.text)
        self.assertEqual(STRING, kind(c))
        self.assertEqual("string", type_name(c))
        self.assertTrue(value_ops.are_equal(c, head + "abd"))
        self.assertTrue(value_ops.are_equal(head + "abd", c))
        self.assertTrue(value_ops.are_equal(c, value_ops.concat(value_ops.concat(root, "b"), "d")))
        self.assertFalse(value_ops.are_equal(c, b))
        self.assertEqual(0, value_ops.compare(head + "ac", b))
        self.assertEqual(-1, value_ops.compare(a, b))
        self.assertEqual(1, value_ops.compare(c, head))

    def test_adding_an_empty_string_returns_the_other(self) -> None:
        self.assertEqual('"ab"', J(value_ops.concat("", "ab")))
        self.assertEqual('"ab"', J(value_ops.concat("ab", "")))
        self.assertEqual('"ab"', J(value_ops.concat("a", "b")))
        long = value_ops.concat("x" * MIN_LENGTH, "a")
        self.assertIs(long, value_ops.concat(long, ""))
        self.assertIs(long, value_ops.concat("", long))
        self.assertIs(long, value_ops.try_add(long, None))

    def test_concurrent_additions_to_one_long_string_stay_independent(self) -> None:
        head = "x" * MIN_LENGTH
        root = value_ops.concat(head, "-")
        results: list = [None] * 200

        def work(i: int) -> None:
            text = value_ops.concat(root, f"<{i}>")
            for j in range(10):
                text = value_ops.concat(text, f"{i * 100 + j},")
            results[i] = text
        threads = [threading.Thread(target=work, args=(i,)) for i in range(len(results))]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        self.assertEqual(head + "-", root.text)
        for i, text in enumerate(results):
            self.assertEqual(head + f"-<{i}>" + "".join(f"{i * 100 + j}," for j in range(10)), text.text)

    def test_adding_strings_in_a_loop_does_not_copy_the_string(self) -> None:
        # reduce range(100000) as $i (""; . + "ab"): every version past MIN_LENGTH appends a part to
        # one buffer; copying on each addition would copy ~10 GB.
        text: Any = ""
        first = None
        for _ in range(100000):
            text = value_ops.try_add(text, "ab")
            if first is None and type(text) is JqString:
                first = text.parts
        self.assertIs(first, text.parts)
        self.assertEqual(200000, len(text.text))
        self.assertEqual("ab" * 100000, text.text)

    def test_comparing_a_long_string_with_another_kind_does_not_join_it(self) -> None:
        # join tests `. == null` on its accumulator at every step: joining the parts there would
        # make it quadratic again.
        long = value_ops.concat("x" * MIN_LENGTH, "ab")
        self.assertFalse(value_ops.are_equal(long, None))
        self.assertFalse(value_ops.are_equal(N(1), long))
        self.assertEqual(1, value_ops.compare(long, N(1)))
        self.assertEqual(-1, value_ops.compare(long, JqArray.EMPTY))
        self.assertIsNone(long._text)

    def test_the_operations_of_the_interpreter_take_a_long_string(self) -> None:
        # Each operation gives for a JqString what it gives for the equal str (jq_string.py).
        text = "x" * MIN_LENGTH + "ab"
        long = value_ops.concat("x" * MIN_LENGTH, "ab")
        self.assertIs(JqString, type(long))
        obj = JqObject.EMPTY.set(text, N(1))
        slice_key = JqObject.EMPTY.set("start", N(-2)).set("end", None)
        cases: list[tuple[str, Any]] = [
            ("to_json", lambda s: J(s)),
            ("describe", lambda s: errors.describe(s)),
            ("compare with a number", lambda s: value_ops.compare(s, N(1))),
            ("compare with an array", lambda s: value_ops.compare(JqArray.EMPTY, s)),
            ("s * n", lambda s: text_of(value_ops.try_multiply(s, N(2)))),
            ("n * s", lambda s: text_of(value_ops.try_multiply(N(2), s))),
            ("s / t", lambda s: text_of(value_ops.try_divide(s, "a"))),
            ("t / s", lambda s: text_of(value_ops.try_divide("y" + text + "z", s))),
            ("s - n", lambda s: text_of(value_ops.try_subtract(s, N(1)))),
            ("s + n", lambda s: text_of(value_ops.try_add(s, N(1)))),
            ("-s", lambda s: text_of(value_ops.try_negate(s))),
            ("object[s]", lambda s: text_of(value_ops.try_index(obj, s))),
            ("s[n]", lambda s: text_of(value_ops.try_index(s, N(0)))),
            ("s[-3:]", lambda s: text_of(value_ops.try_slice(s, N(-3), None))),
            ("s[slice object]", lambda s: text_of(value_ops.try_index(s, slice_key))),
            ("object[s:]", lambda s: text_of(value_ops.try_slice(JqObject.EMPTY, s, None))),
        ]
        for name, operation in cases:
            with self.subTest(operation=name):
                self.assertEqual(operation(text), operation(long))

    def test_adding_keys_in_a_loop_does_not_copy_the_object(self) -> None:
        obj = JqObject.EMPTY.merge(JqObject.EMPTY.set("k0", N(0)))
        first = obj.keys
        for i in range(1, 10000):
            obj = obj.merge(JqObject.EMPTY.set("k" + str(i), N(i)))
        self.assertEqual(10000, obj.count)
        self.assertEqual(1234, obj.get("k1234"))
        self.assertIs(first, obj.keys)

    def test_object_keeps_insertion_order(self) -> None:
        obj = JqObject.EMPTY.set("b", N(1)).set("a", N(2)).set("c", N(3))
        self.assertEqual('{"b":1,"a":2,"c":3}', J(obj))
        self.assertEqual('{"b":1,"a":9,"c":3}', J(obj.set("a", N(9))))
        self.assertEqual('{"b":1,"c":3}', J(obj.remove("a")))
        self.assertEqual('{"b":1,"c":3,"a":4}', J(obj.remove("a").set("a", N(4))))
        self.assertIs(obj, obj.remove("x"))
        self.assertEqual('{"b":1,"a":2,"c":3}', J(obj))
        self.assertEqual(["a", "b", "c"], obj.sorted_keys())

    def test_object_merge_keeps_insertion_order(self) -> None:
        left: JqObject = V('{"z":1,"y":2,"x":3}')  # type: ignore[assignment]
        self.assertEqual('{"z":1,"y":5,"x":3,"w":4}', J(left.merge(V('{"w":4,"y":5}'))))  # type: ignore[arg-type]
        self.assertEqual('{"z":1,"y":2,"x":3,"a":0,"b":1}', J(left.merge(V('{"a":0,"b":1}'))))  # type: ignore[arg-type]
        self.assertEqual('{"z":1,"y":2,"x":3}', J(left))

    def test_versions_of_a_large_object_see_only_their_keys(self) -> None:
        versions = [JqObject.EMPTY]
        for i in range(1, 21):
            versions.append(versions[i - 1].set("k" + str(i), N(i)))
        # A branch from an older version takes a slot already used by a newer one: it copies.
        branch = versions[10].set("k15", N(-15))
        for i in range(1, 21):
            self.assertEqual(i, versions[i].count)
            self.assertTrue(versions[i].contains_key("k" + str(i)))
            self.assertEqual(i < 20, not versions[i].contains_key("k20"))
            self.assertFalse(versions[i].contains_key("k" + str(i + 1)))
        self.assertEqual(-15, branch.get("k15"))
        self.assertEqual(15, versions[15].get("k15"))
        self.assertFalse(versions[12].contains_key("k15"))
        self.assertEqual(10, branch.index_of("k15"))

    def test_concurrent_key_adds_to_one_base_stay_independent(self) -> None:
        root = JqObject.EMPTY
        for i in range(20):
            root = root.set("base" + str(i), N(i))
        results: list = [None] * 200

        def work(i: int) -> None:
            obj = root.set("own" + str(i), N(i))
            for j in range(5):
                obj = obj.set(f"own{i}_{j}", N(j))
            results[i] = obj
        threads = [threading.Thread(target=work, args=(i,)) for i in range(len(results))]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        self.assertEqual(20, root.count)
        for i, obj in enumerate(results):
            self.assertEqual(26, obj.count)
            self.assertEqual("own" + str(i), obj.key_at(20))
            self.assertEqual(i, obj.get("own" + str(i)))
            self.assertFalse(obj.contains_key("own" + str((i + 1) % len(results))))
            self.assertFalse(root.contains_key("own" + str(i)))

    def test_builder_replaces_in_place_and_removes(self) -> None:
        builder = JqObjectBuilder()
        for i in range(12):
            builder.set("k" + str(i), N(i))
        builder.set("k3", N(30))
        self.assertTrue(builder.remove("k0"))
        self.assertFalse(builder.remove("missing"))
        obj = builder.to_object()
        self.assertEqual(11, obj.count)
        self.assertEqual("k1", obj.key_at(0))
        self.assertEqual(30, obj.get("k3"))
        self.assertFalse(obj.contains_key("k0"))
        with self.assertRaises(RuntimeError):
            builder.to_object()


def apply(op: str, a: str, b: str) -> str:
    functions = {"+": value_ops.try_add, "-": value_ops.try_subtract, "*": value_ops.try_multiply,
                 "/": value_ops.try_divide, "%": value_ops.try_modulo}
    return text_of(functions[op](V(a), V(b)))


class ValueArithmeticTests(unittest.TestCase):
    def check(self, op: str, cases: list[tuple[str, str, str]]) -> None:
        for a, b, expected in cases:
            with self.subTest(a=a, op=op, b=b):
                self.assertEqual(expected, apply(op, a, b))

    def test_add(self) -> None:
        self.check("+", [
            ("null", "1", "1"), ("1", "null", "1"), ("null", "null", "null"), ("null", '{"a":1}', '{"a":1}'), ("1", "2", "3"),
            ("0.1", "0.2", "0.30000000000000004"), ("1e308", "1e308", "1.7976931348623157e+308"), ('"a"', '"b"', '"ab"'),
            ("[1]", "[2,[3]]", "[1,2,[3]]"), ("[]", "[]", "[]"), ('{"a":1,"b":2}', '{"c":3,"a":4}', '{"a":4,"b":2,"c":3}'),
            ('{"b":1}', '{"a":2}', '{"b":1,"a":2}'), ("1", '"a"', 'error: number (1) and string ("a") cannot be added'),
            ("{}", "[]", "error: object ({}) and array ([]) cannot be added"),
            ("true", "true", "error: boolean (true) and boolean (true) cannot be added"),
            ("false", "1", "error: boolean (false) and number (1) cannot be added"),
            ('{"a":"very long string"}', "1", 'error: object ({"a":"very long string"}) and number (1) cannot be added'),
            ("[1,2,3,4,5,6,7,8,9,10,11,12,13]", "1", "error: array ([1,2,3,4,5,6,7,8,9,10,11,...]) and number (1) cannot be added"),
        ])

    def test_subtract(self) -> None:
        self.check("-", [
            ("5", "3", "2"), ("[1,2,3,1,[1]]", "[1,[1]]", "[2,3]"), ("[1,2]", "[]", "[1,2]"), ('[{"a":1,"b":2}]', '[{"b":2,"a":1}]', "[]"),
            ('"a"', '"a"', 'error: string ("a") and string ("a") cannot be subtracted'),
            ("null", "1", "error: null (null) and number (1) cannot be subtracted"),
            ("{}", "{}", "error: object ({}) and object ({}) cannot be subtracted"),
        ])

    def test_multiply(self) -> None:
        self.check("*", [
            ("2", "3", "6"), ('"ab"', "3", '"ababab"'), ("3", '"ab"', '"ababab"'), ('"ab"', "1", '"ab"'), ('"ab"', "1.9", '"ab"'),
            ('"ab"', "1.5", '"ab"'), ('"x"', "0", '""'), ('"ab"', "0.5", '""'), ('"ab"', "-0", '""'), ("0", '"ab"', '""'),
            ('"ab"', "-0.5", "null"), ('"ab"', "-1", "null"), ('"ab"', "nan", "null"), ('""', "5", '""'), ('""', "1e300", '""'),
            ('"é"', "1.5e9", "error: Repeat string result too long"), # 2e9 bytes fit in 2^31 - 1 bytes (the reference's host string limit stops it earlier).
            ('"ab"', "1.1e9", "error: Repeat string result too long"),
            ('{"a":{"b":1},"c":1}', '{"a":{"d":2},"c":{"x":1}}', '{"a":{"b":1,"d":2},"c":{"x":1}}'),
            ('{"a":{"b":{"c":1}}}', '{"a":{"b":{"d":2}},"e":3}', '{"a":{"b":{"c":1,"d":2}},"e":3}'),
            ('{"a":{"b":1}}', '{"a":1}', '{"a":1}'),
            ("[1]", "2", "error: array ([1]) and number (2) cannot be multiplied"),
            ('"a"', '"b"', 'error: string ("a") and string ("b") cannot be multiplied'),
            ("null", "2", "error: null (null) and number (2) cannot be multiplied"),
        ])

    def test_divide(self) -> None:
        self.check("/", [
            ("6", "4", "1.5"), ("-1", "3", "-0.3333333333333333"),
            ("1", "0", "error: number (1) and number (0) cannot be divided because the divisor is zero"),
            ("0", "-0", "error: number (0) and number (-0) cannot be divided because the divisor is zero"),
            ('"a,b,c"', '","', '["a","b","c"]'), ('"a,b,"', '","', '["a","b",""]'), ('"a, b"', '", "', '["a","b"]'),
            ('"abc"', '"x"', '["abc"]'), ('""', '","', "[]"), ('"aé😀"', '""', '["a","é","😀"]'),
            ("[]", "1", "error: array ([]) and number (1) cannot be divided"),
            ('"a"', "1", 'error: string ("a") and number (1) cannot be divided'),
        ])

    def test_modulo(self) -> None:
        self.check("%", [
            ("5", "3", "2"), ("-5", "3", "-2"), ("5", "-3", "2"), ("5.9", "3.9", "2"), ("5", "-1", "0"),
            ("-9223372036854775808", "-1", "0"), ("1e300", "7", "0"), ("nan", "1", "null"), ("1", "nan", "null"),
            ("1e300", "-1e300", "9223372036854776000"), ("-1e300", "1e300", "-1"),
            ("1", "0", "error: number (1) and number (0) cannot be divided (remainder) because the divisor is zero"),
            ("5", "0.5", "error: number (5) and number (0.5) cannot be divided (remainder) because the divisor is zero"),
            ('"a"', "1", 'error: string ("a") and number (1) cannot be divided (remainder)'),
            ("1", '"a"', 'error: number (1) and string ("a") cannot be divided (remainder)'),
        ])

    def test_remainder_of_infinities(self) -> None:
        # [(infinite, -infinite) % (1, -1, infinite)] is [0,0,0,0,0,-1] (spec §4.4).
        results = [J(value_ops.try_modulo(a, b)) for a in (math.inf, -math.inf) for b in (1.0, -1.0, math.inf)]
        self.assertEqual(["0", "0", "0", "0", "0", "-1"], results)

    def test_negation(self) -> None:
        for source, expected in [("1", "-1"), ("0", "-0"), ("-0", "0"), ("nan", "null"), ("null", "error: null (null) cannot be negated"),
                                 ('"a"', 'error: string ("a") cannot be negated'),
                                 ('"very-long-long-long-long-string"', 'error: string ("very-long-long-long-long...") cannot be negated')]:
            self.assertEqual(expected, text_of(value_ops.try_negate(V(source))))

    def test_operands_never_change(self) -> None:
        a = V('{"a":1,"b":{"c":2}}')
        b = V('{"b":{"d":3},"e":4}')
        value_ops.try_add(a, b)
        value_ops.try_multiply(a, b)
        self.assertEqual('{"a":1,"b":{"c":2}}', J(a))
        self.assertEqual('{"b":{"d":3},"e":4}', J(b))

    def test_deep_merge_of_ten_thousand_levels_does_not_recurse(self) -> None:
        def nest(key: str, depth: int) -> object:
            value: object = JqObject.EMPTY.set(key, float(depth))
            for _ in range(depth):
                value = JqObject.EMPTY.set("a", value)
            return value
        result: Any = on_small_stack(self, lambda: value_ops.try_multiply(nest("x", 10_000), nest("y", 10_000)))
        for _ in range(10_000):
            result = result.value_at(0)  # type: ignore[union-attr]
        self.assertEqual('{"x":10000,"y":10000}', J(result))


ORDERED = [
    "null", "false", "true", "-1e300", "-1", "0", "0.5", "1", "1e300", '""', '"A"', '"a"', '"ab"', '"b"', '"é"', '"\ue000"',
    '"\uffff"', '"😀"', "[]", "[null]", "[1]", "[1,2]", "[1,3]", "[2]", '["a"]', "[[]]", "{}", '{"a":1}', '{"a":2}', '{"a":[]}',
    '{"a":1,"b":1}', '{"a":2,"b":1}', '{"a":1,"c":0}', '{"b":0}', '{"é":0}', '{"😀":0}',
]


class ValueOrderingTests(unittest.TestCase):
    def test_every_pair_is_ordered(self) -> None:
        for i, left in enumerate(ORDERED):
            a = V(left)
            self.assertEqual(0, value_ops.compare(a, V(left)))
            self.assertTrue(value_ops.are_equal(a, V(left)), left)
            for right in ORDERED[i + 1:]:
                b = V(right)
                self.assertEqual(-1, value_ops.compare(a, b), f"{left} < {right}")
                self.assertEqual(1, value_ops.compare(b, a), f"{right} > {left}")
                self.assertFalse(value_ops.are_equal(a, b), f"{left} != {right}")

    def test_deep_values_compare(self) -> None:
        a = V("[" * 10000 + "]" * 10000)
        b = V("[" * 10000 + "1" + "]" * 10000)
        self.assertTrue(on_small_stack(self, lambda: value_ops.are_equal(a, V(J(a)))))
        self.assertEqual(-1, on_small_stack(self, lambda: value_ops.compare(a, b)))

    def test_objects_order_by_key_sets_first(self) -> None:
        self.assertEqual(-1, value_ops.compare(V('{"a":2,"b":1}'), V('{"a":1,"c":0}')))

    def test_object_key_order_does_not_matter(self) -> None:
        a = V('{"a":1,"b":[1,{"x":2,"y":3}]}')
        b = V('{"b":[1,{"y":3,"x":2}],"a":1}')
        self.assertTrue(value_ops.are_equal(a, b))
        self.assertEqual(0, value_ops.compare(a, b))

    def test_strings_order_by_code_point(self) -> None:
        self.assertEqual(-1, value_ops.compare("\ue000", "😀"))
        self.assertEqual(1, value_ops.compare("a😀", "a\ufffd"))
        self.assertEqual(-1, value_ops.compare("😀", "😁"))
        self.assertEqual(-1, value_ops.compare("a", "a😀"))

    def test_nan_sorts_below_numbers_compares_equal_to_nan_but_is_never_equal(self) -> None:
        nan = math.nan
        self.assertEqual(-1, value_ops.compare(nan, -math.inf))
        self.assertEqual(1, value_ops.compare(-1e300, nan))
        self.assertEqual(1, value_ops.compare(nan, True))
        self.assertEqual(-1, value_ops.compare(nan, ""))
        # A total order (spec §4.3, §13): [nan < nan, nan <= nan, nan > nan, nan >= nan] is [false,true,false,true].
        self.assertEqual(0, value_ops.compare(nan, nan))
        self.assertEqual(0, value_ops.compare(V('[nan,{"a":nan}]'), V('[nan,{"a":nan}]')))
        # == stays IEEE: nan == nan is false, also inside containers.
        self.assertFalse(value_ops.are_equal(nan, nan))
        self.assertFalse(value_ops.are_equal(V("[nan]"), V("[nan]")))
        self.assertEqual(-1, value_ops.compare(V("[nan]"), V("[0]")))

    def test_sort_keeps_nans_together_and_stable(self) -> None:
        self.assertEqual([5, 1, 4, 3, 0, 2], value_ops.sort_order(V('[1,nan,"a",-1,nan,null]')))  # type: ignore[arg-type]

    def test_equal_values(self) -> None:
        for a, b in [("1", "1.0"), ("-0", "0"), ("1e2", "100"), ("null", "null"), ('[1,[2,{"a":null}]]', '[1,[2,{"a":null}]]'),
                     ('"😀"', '"\\ud83d\\ude00"')]:
            self.assertTrue(value_ops.are_equal(V(a), V(b)))
            self.assertEqual(0, value_ops.compare(V(a), V(b)))

    def test_unequal_values(self) -> None:
        for a, b in [("1", '"1"'), ("false", "null"), ("[1,2]", "[2,1]"), ('{"a":1}', '{"a":1,"b":null}'), ('{"a":1}', '{"b":1}')]:
            self.assertFalse(value_ops.are_equal(V(a), V(b)))

    def test_sort_orders_every_kind(self) -> None:
        shuffled = JqArray.of(*[V(x) for x in reversed(ORDERED)])
        self.assertEqual("[" + ",".join(J(V(x)) for x in ORDERED) + "]", J(value_ops.sort(shuffled)))

    def test_sort_order_is_stable(self) -> None:
        self.assertEqual([4, 1, 3, 0, 2, 5], value_ops.sort_order(V("[2,1,2,1,0,2]")))  # type: ignore[arg-type]
        self.assertEqual([], value_ops.sort_order(JqArray.EMPTY))
        self.assertEqual([0], value_ops.sort_order(V("[7]")))  # type: ignore[arg-type]
        # Keys that are not simple (objects) sort by compare as well.
        self.assertEqual([1, 0, 2], value_ops.sort_order(V('[{"b":1},{"a":2},{"b":2}]')))  # type: ignore[arg-type]

    def test_sort_matches_a_reference_sort_on_random_input(self) -> None:
        rng = random.Random(7)
        items = [float(rng.randrange(100)) for _ in range(1000)]
        order = value_ops.sort_order(JqArray.of(*items))
        for i in range(1, len(order)):
            c = value_ops.compare(items[order[i - 1]], items[order[i]])
            self.assertTrue(c < 0 or (c == 0 and order[i - 1] < order[i]))


def _skipped_nest(levels: int, inner: object) -> object:
    value = inner
    for _ in range(levels):
        value = JqArray.of(value)
    return value


class ValueTextTests(unittest.TestCase):
    def test_number_text(self) -> None:
        cases = [
            (0.0, "0"), (1.0, "1"), (-1.0, "-1"), (1.5, "1.5"), (-1.5, "-1.5"), (0.5, "0.5"), (100.0, "100"),
            (1e15, "1000000000000000"), (1e20, "100000000000000000000"), (123456789012345680000.0, "123456789012345680000"),
            (1e21, "1e+21"), (-1e21, "-1e+21"), (1.5e300, "1.5e+300"), (1e-6, "0.000001"), (1.5e-6, "0.0000015"),
            (1e-7, "1e-7"), (1.23e-18, "1.23e-18"), (0.1 + 0.2, "0.30000000000000004"), (5e-324, "5e-324"),
            (1.7976931348623157e308, "1.7976931348623157e+308"), (9007199254740993.0, "9007199254740992"), (123.456, "123.456"),
            (math.nan, "null"), (math.inf, "1.7976931348623157e+308"), (-math.inf, "-1.7976931348623157e+308"), (-0.0, "-0"),
        ]
        for number, expected in cases:
            with self.subTest(number=number):
                self.assertEqual(expected, value_text.format_number(number))

    def test_number_text_round_trips(self) -> None:
        rng = random.Random(42)
        for _ in range(10000):
            number = struct.unpack("<d", struct.pack("<Q", rng.getrandbits(64)))[0]
            if not math.isfinite(number):
                continue
            text = value_text.format_number(number)
            back = value_text.try_parse_number(text)
            self.assertEqual(number, back, text)

    def test_string_escaping(self) -> None:
        cases = [("é<", '"é<"'), ('a"b\\c/d', '"a\\"b\\\\c/d"'), ("\b\f\n\r\t", '"\\b\\f\\n\\r\\t"'),
                 ("\u0000\u0001\u001f", '"\\u0000\\u0001\\u001f"'), ("\u007f\u0080", '"\\u007f\u0080"'), ("😀", '"😀"'),
                 ("\u2028", '"\u2028"')]
        for text, expected in cases:
            self.assertEqual(expected, J(text))

    def test_reader_round_trips(self) -> None:
        for json in ["null", "true", "false", "[]", "{}", '""', '[1,2.5,"x",null,true,false,{},[[]]]',
                     '{"b":1,"a":[1,{"z":null,"y":"é"}],"c":{"d":[]}}', "-0"]:
            self.assertEqual(json, J(V(json)))

    def test_reader_values(self) -> None:
        cases = [
            (' \t\r\n[ 1 , { "a" : 2 } ] \n', '[1,{"a":2}]'), ("\ufeff1", "1"), ("1.0", "1"), ("1E2", "100"), ("1e-2", "0.01"),
            ("1.", "1"), (".5", "0.5"), ("+1", "1"), ("007", "7"), ("1e1000", "1.7976931348623157e+308"),
            ("-1e1000", "-1.7976931348623157e+308"), ("1e-1000", "0"), ("nan", "null"), ("NaN", "null"), ("[-nan]", "[null]"),
            ("Infinity", "1.7976931348623157e+308"), ("-Infinity", "-1.7976931348623157e+308"),
            ('"\\u00e9\\/\\""', '"é/\\""'), ('"\\ud83d\\ude00"', '"😀"'), ('"\\ud800"', '"\ufffd"'), ('"\\udc00x"', '"\ufffdx"'),
            ('"\\ud800\\ud800"', '"\ufffd\ufffd"'), ('"\\ud800\\u0041"', '"\ufffdA"'),
            ("[nAn,SNaN,-snan,+inf,-INF,iNfInItY]",
             "[null,null,null,1.7976931348623157e+308,-1.7976931348623157e+308,1.7976931348623157e+308]"),
            ("[1,nan]", "[1,null]"), ('{"a":1,"b":2,"a":3}', '{"a":3,"b":2}'), ('"a\x7fb"', '"a\\u007fb"'),
            ('{"a":1,"a":2}', '{"a":2}'), ('{"\\ud800":1}', '{"\ufffd":1}'),
        ]
        for json, expected in cases:
            with self.subTest(json=json):
                self.assertEqual(expected, J(V(json)))

    def test_reader_failures(self) -> None:
        cases = [
            ("", "Expected JSON value"), ("   ", "Expected JSON value"), ("1 2", "Unexpected extra JSON values"),
            ("{", "Unfinished JSON term at EOF at line 1, column 1"), ("[1,2", "Unfinished JSON term at EOF at line 1, column 4"),
            ('"abc', "Unfinished JSON term at EOF at line 1, column 4"), ("[1 2]", "Expected separator between values at line 1, column 4"),
            ("[1,\n2 3]", "Expected separator between values at line 2, column 3"), ("tru", "Invalid literal at line 1, column 3"),
            ("nul", "Invalid literal at line 1, column 3"), ("Null", "Invalid literal at line 1, column 4"),
            ("NaN1", "Invalid literal at line 1, column 4"), ("nanx", "Invalid literal at line 1, column 4"),
            ("[1x]", "Invalid numeric literal at line 1, column 3"), ("1e", "Invalid numeric literal at line 1, column 2"),
            ("'a'", "Invalid numeric literal at line 1, column 3"), ('"\\q"', "Invalid escape at line 1, column 3"),
            ('"\\u12G4"', "Invalid \\uXXXX escape at line 1, column 6"),
            ('"a\nb"', "Invalid string: control characters from U+0000 through U+001F must be escaped at line 2, column 0"),
            ('"a\x01"', "Invalid string: control characters from U+0000 through U+001F must be escaped at line 1, column 3"),
            ('["\t"]', "Invalid string: control characters from U+0000 through U+001F must be escaped at line 1, column 3"),
            ("{1:2}", "Object keys must be strings at line 1, column 2"),
            ('{"a" 1}', "Objects must consist of key:value pairs at line 1, column 6"),
            ("[1,]", "Expected another array element at line 1, column 4"), ("]", "Unmatched ']' at line 1, column 1"),
            ("[1}", "Unmatched '}' at line 1, column 3"), (",", "Expected value before ',' at line 1, column 1"),
        ]
        for json, reason in cases:
            with self.subTest(json=json):
                result = value_text.try_parse(json)
                self.assertIs(Fail, type(result))
                self.assertEqual(f"{reason} (while parsing '{json}')", result.error)  # type: ignore[union-attr]

    def test_reader_depth_limit(self) -> None:
        ok = "[" * 10000 + "]" * 10000
        self.assertEqual(ok, on_small_stack(self, lambda: J(V(ok))))
        result = value_text.try_parse("[" * 10001)
        self.assertTrue(result.error.startswith("Exceeds depth limit for parsing"))  # type: ignore[union-attr]

    def test_writer_skips_values_deeper_than_10001_levels(self) -> None:
        skipped = "<skipped: too deep>"
        self.assertEqual("[" * 10000 + "1" + "]" * 10000, J(_skipped_nest(10000, 1.0)))
        self.assertEqual("[" * 10001 + skipped + "]" * 10001, J(_skipped_nest(10001, 1.0)))
        self.assertEqual("[" * 10001 + "]" * 10001, J(_skipped_nest(10000, JqArray.EMPTY)))
        self.assertEqual("[" * 10001 + skipped + "]" * 10001, J(_skipped_nest(10001, JqArray.EMPTY)))
        self.assertEqual("[" * 10001 + skipped + "," + skipped + "]" * 10001, J(_skipped_nest(10000, V("[1,[2]]"))))

    def test_number_reading(self) -> None:
        for text, expected in [("12", 12.0), ("-1.5e3", -1500.0), ("0.1", 0.1), (".5", 0.5), ("5.", 5.0), ("+3", 3.0),
                               ("inf", math.inf), ("-INFINITY", -math.inf)]:
            self.assertEqual(expected, value_text.try_parse_number(text))

    def test_number_reading_failures(self) -> None:
        for text in ["", " 1", "1 ", "0x10", ".", "-", "1e+", "1,5", "abc", "1_0", "١"]:
            self.assertIsNone(value_text.try_parse_number(text), text)

    def test_nan_words_read_as_nan(self) -> None:
        for text in ["nan", "NaN", "-nan", "+NAN", "snan", "-sNaN"]:
            number = value_text.try_parse_number(text)
            self.assertTrue(number != number, text)

    def test_error_dump(self) -> None:
        cases = [
            ("[1,2]", "[1,2]"), ("[1,2,3,4,5,6,7,8,9,10,11,12,13]", "[1,2,3,4,5,6,7,8,9,10,11,...]"),
            ('{"aaaaaaaaaaaaaaaaaaaaaaaaaa":1}', '{"aaaaaaaaaaaaaaaaaaaaaaa...}'),
            ('"very-long-long-long-long-string"', '"very-long-long-long-long..."'),
            ('["very-long-long-long-long-string"]', '["very-long-long-long-lon...]'),
            ('"abcdefghijklmnopqrstuvwxyz0"', '"abcdefghijklmnopqrstuvwxyz0"'),
            ('"abcdefghijklmnopqrstuvwxyz01"', '"abcdefghijklmnopqrstuvwx..."'),
            ('"ééééééééééééééééééé"', '"éééééééééééé..."'), ('"a😀😀😀😀😀😀😀"', '"a😀😀😀😀😀..."'),
        ]
        for json, expected in cases:
            dump = errors.dump(V(json))
            self.assertEqual(expected, dump)
            self.assertLessEqual(len(dump.encode("utf-8")), 29)
