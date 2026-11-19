"""The native catalogue and the embedded prelude against spec §9 (BuiltinCatalogueTests.cs,
NativeTableTests.cs): the prelude binds completely, and together they are exactly the builtins the
spec names, natives where the spec says N and prelude definitions where it says P. The lists are
read from the spec, so spec and code cannot drift."""

from __future__ import annotations

import os
import re
import unittest

from jqlite.binding.prelude import Prelude, read_source
from jqlite.builtins import natives
from jqlite.runtime import native_table

from .corpus import ROOT

_SIGNATURES = re.compile(r"`([A-Za-z_][A-Za-z0-9_]*/[0-9]+)`")
_LIBRARY_LIST = re.compile(r"^\*\*Library functions\*\*(.*?`([a-z0-9 ]+)` \(one input\).*)$", re.MULTILINE)
_FIRST_CELLS = re.compile(r"^\| ([^|\n]*) \|", re.MULTILINE)
_HEADINGS = re.compile(r"^`[A-Za-z_][A-Za-z0-9_]*/[0-9]+`(, `[A-Za-z_][A-Za-z0-9_]*/[0-9]+`)*", re.MULTILINE)
_MODULE_GROUP = re.compile(r"A port MAY leave out the whole group:[^.]*\.")
_FORMULAS = re.compile(r"^\s*([a-z][a-z0-9]*)\(x\):", re.MULTILINE)
_KIND_ROWS = re.compile(r"^\| ([^|]*) \| ([NP]) \|", re.MULTILINE)


def _spec_file(name: str) -> str:
    with open(os.path.join(ROOT, "spec", name), encoding="utf-8", newline="") as f:
        return f.read()


def _section(text: str, start: str, end: str) -> str:
    frm = text.index("\n" + start)
    to = text.index("\n" + end, frm + 1)
    return text[frm:to]


def _declarations(text: str) -> set[str]:
    declared: set[str] = set()
    for cell in _FIRST_CELLS.finditer(text):
        declared.update(_SIGNATURES.findall(cell.group(1)))
    for library in _LIBRARY_LIST.finditer(text):
        declared.update(_SIGNATURES.findall(library.group(1)))
    for heading in _HEADINGS.finditer(text):
        declared.update(_SIGNATURES.findall(heading.group(0)))
    return declared


def spec_builtins() -> tuple[set[str], set[str], set[str], set[str]]:
    """All, native, prelude and modules signatures of spec §9 (see BuiltinCatalogueTests.cs)."""
    text = _spec_file("jqlite.md").replace("\r\n", "\n")
    section = _section(text, "## 9. ", "## 10. ")
    math = _section(section, "### 9.8 ", "### 9.9 ")
    math_declared = _declarations(math)
    library = _LIBRARY_LIST.search(math)
    assert library is not None, "The library functions of §9.8 were not found"
    math_declared.update(name + "/0" for name in library.group(2).split(" "))
    math_declared.update(m.group(1) + "/0" for m in _FORMULAS.finditer(math))
    group = _MODULE_GROUP.search(_section(section, "### 9.11 ", "### 9.12 "))
    assert group is not None, "The group sentence of §9.11 was not found"
    modules = set(_SIGNATURES.findall(group.group(0)))
    everything = _declarations(section) | math_declared | modules
    native = set(modules)
    prelude: set[str] = set()
    for row in _KIND_ROWS.finditer(section):
        (native if row.group(2) == "N" else prelude).update(_SIGNATURES.findall(row.group(1)))
    for signature in math_declared:
        (prelude if signature == "abs/0" else native).add(signature)
    return everything, native, prelude, modules


def _signature(item: object) -> str:
    return f"{item.name}/{item.arity}"  # type: ignore[attr-defined]


class BuiltinCatalogueTests(unittest.TestCase):
    def test_the_embedded_prelude_is_spec_prelude_jq(self) -> None:
        # The package's copy (package data) must equal the shared file: copy spec/prelude.jq to
        # python/jqlite/prelude.jq when this fails.
        self.assertEqual(_spec_file("prelude.jq").replace("\r\n", "\n"), read_source().replace("\r\n", "\n"))

    def test_the_prelude_binds_against_the_natives(self) -> None:
        prelude = Prelude.instance()
        self.assertEqual(102, len(prelude.definitions))
        # Nested definitions (recurse's r, walk's w) are functions of the prelude too.
        self.assertGreater(len(prelude.functions), len(prelude.definitions))
        self.assertEqual(len(prelude.definitions), len({_signature(f) for f in prelude.definitions}))

    def test_a_prelude_cannot_use_host_variables(self) -> None:
        with self.assertRaises(RuntimeError) as raised:
            Prelude.load("def f: $x;")
        self.assertIn("$x is not defined at 1:8", str(raised.exception))

    def test_the_catalogue_has_unique_signatures_and_indices(self) -> None:
        self.assertEqual(len(natives.ALL), len({_signature(n) for n in natives.ALL}))
        for i, native in enumerate(natives.ALL):
            self.assertEqual(i, native.index)
            self.assertIs(native, natives.find(native.name, native.arity))
        self.assertIsNone(natives.find("map", 1))

    def test_natives_and_prelude_are_the_builtins_of_the_spec(self) -> None:
        everything, spec_native, spec_prelude, spec_modules = spec_builtins()
        native = {_signature(n) for n in natives.ALL if not n.overrides_prelude}
        prelude = {_signature(f) for f in Prelude.instance().definitions}
        # An override implements a prelude builtin natively: it stays a prelude builtin of §9.
        self.assertLessEqual({_signature(n) for n in natives.ALL if n.overrides_prelude}, prelude)
        self.assertEqual(set(), native & prelude)
        self.assertEqual(sorted(everything), sorted(native | prelude))
        self.assertLessEqual(native, spec_native)
        self.assertLessEqual(prelude, spec_prelude)
        self.assertEqual(sorted(spec_modules),
                         sorted(_signature(n) for n in natives.ALL if n.group == natives.NativeGroup.MODULES))

    def test_internal_builtins_start_with_an_underscore(self) -> None:
        self.assertEqual(["_group_by_impl/1", "_max_by_impl/1", "_min_by_impl/1", "_sort_by_impl/1", "_strindices/1"],
                         sorted(_signature(n) for n in natives.ALL if n.is_internal and not n.overrides_prelude))
        self.assertEqual(["_assign/2", "_flatten/1", "_modify/2"],
                         sorted(_signature(f) for f in Prelude.instance().definitions if f.name.startswith("_")))

    def test_every_native_is_implemented_with_its_kind(self) -> None:
        import jqlite  # noqa: F401 - fills the table
        for native in natives.ALL:
            implemented = native_table.CLOSURES[native.index] if native.takes_closures else native_table.VALUES[native.index]
            self.assertIsNotNone(implemented, f"{_signature(native)} has no implementation")
