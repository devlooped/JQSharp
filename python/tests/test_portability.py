"""The portability rules of AGENTS.md as tests (PortabilityTests.cs): the core (jqlite/values,
syntax, binding, runtime, builtins) must read the same in every language, so a host feature in its
code fails here, naming the file and the line. Comments and strings are not code: the files are
read with Python's tokenizer. Where a rule has exceptions, they are listed by file with the reason."""

from __future__ import annotations

import io
import os
import re
import tokenize
import unittest

PACKAGE = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "jqlite"))
CORE = ("values", "syntax", "binding", "runtime", "builtins")

# Rule name, pattern on code, and the files (relative to jqlite/) allowed to match, with the reason.
CORE_RULES: list[tuple[str, str, dict[str, str]]] = [
    ("a host JSON library", r"\bimport\s+json\b|\bfrom\s+json\b|\bjson\s*\.", {}),
    ("generators", r"\byield\b", {}),
    ("host regex", r"\bimport\s+re\b|\bfrom\s+re\b|\bre\s*\.", {
        "builtins/regex_engine.py": "the regex engine: the one place the host regex runs",
    }),
    ("reflection", r"\b(getattr|setattr|hasattr|importlib|inspect|__dict__)\b|(?<![\w.])(?<!def )(eval|exec)\s*\(", {
        "binding/prelude.py": "reads the prelude shipped with the package: importlib.resources",
        "builtins/math_natives.py": "math.fma exists from Python 3.13 only: getattr(math, \"fma\", None)",
    }),
    ("dates", r"^\s*(import\s+(datetime|time|calendar|zoneinfo)\b|from\s+(datetime|time|calendar|zoneinfo)\s+import)", {}),
    ("culture", r"\blocale\b", {}),
    ("raise", r"\braise\b", {
        "binding/prelude.py": "the embedded prelude does not bind: a build defect",
        "binding/resolver.py": "a node kind the resolver does not know: unreachable",
        "runtime/interpreter.py": "a node kind the interpreter does not know: unreachable",
        "runtime/native_table.py": "start-up checks of the registrations against the catalogue",
        "values/jq_array.py": "index guards of the host caller",
        "values/jq_array_builder.py": "use after to_array(): a programming error",
        "values/jq_object.py": "index guards of the host caller",
        "values/jq_object_builder.py": "use after to_object(): a programming error",
    }),
    ("except", r"\bexcept\b", {
        "builtins/regex_engine.py": "the host regex's exception becomes the dialect's error (one host boundary)",
        "builtins/math_natives.py": "Python's math raises where IEEE gives a value: the IEEE result (guide §3.5)",
    }),
]


def code_lines(path: str) -> list[str]:
    """The lines of the code of a file: comments and strings (docstrings included) blanked out."""
    with open(path, encoding="utf-8") as f:
        source = f.read()
    lines = [list(line) for line in source.split("\n")]
    for token in tokenize.generate_tokens(io.StringIO(source).readline):
        if token.type in (tokenize.COMMENT, tokenize.STRING) or token.type == getattr(tokenize, "FSTRING_MIDDLE", -1):
            (start_line, start_column), (end_line, end_column) = token.start, token.end
            for row in range(start_line, end_line + 1):
                line = lines[row - 1]
                frm = start_column if row == start_line else 0
                to = end_column if row == end_line else len(line)
                for i in range(frm, min(to, len(line))):
                    line[i] = " "
    return ["".join(line) for line in lines]


def core_files() -> list[str]:
    files = []
    for folder in CORE:
        for name in sorted(os.listdir(os.path.join(PACKAGE, folder))):
            if name.endswith(".py"):
                files.append(f"{folder}/{name}")
    return files


def matches(relative: str, pattern: str) -> list[int]:
    return [i + 1 for i, line in enumerate(code_lines(os.path.join(PACKAGE, relative))) if re.search(pattern, line)]


class PortabilityTests(unittest.TestCase):
    def test_the_core_does_not_use_host_features(self) -> None:
        for name, pattern, allowed in CORE_RULES:
            with self.subTest(rule=name):
                violations = [f"jqlite/{f}:{line}" for f in core_files() if f not in allowed for line in matches(f, pattern)]
                self.assertEqual([], violations, f"{name} is not allowed in the core outside {', '.join(allowed) or 'nowhere'}")

    def test_core_allowances_are_still_used(self) -> None:
        # An allowance nothing uses any more is removed, so the lists stay the truth.
        for name, pattern, allowed in CORE_RULES:
            for f in allowed:
                self.assertTrue(matches(f, pattern), f"{f} no longer uses {name}: remove it from the allowed files.")

    def test_the_core_does_not_import_the_host_boundary(self) -> None:
        boundary = r"from\s+\.\.\s+import|from\s+\.\.(json_adapter|expression|options|resolvers|exceptions)\b|\bimport\s+jqlite\b"
        violations = [f"jqlite/{f}:{line}" for f in core_files() for line in matches(f, boundary)]
        self.assertEqual([], violations)

    def test_the_runtime_does_not_import_the_builtins_implementations(self) -> None:
        # The table is filled at start-up (guide §1.1): the runtime knows the catalogue, not the families.
        pattern = r"from\s+\.\.builtins\s+import\s+(?!natives\b)|from\s+\.\.builtins\.(?!natives\b)"
        violations = [f"jqlite/{f}:{line}" for f in core_files() if f.startswith("runtime/") for line in matches(f, pattern)]
        self.assertEqual([], violations)

    def test_code_is_what_remains_without_comments_and_strings(self) -> None:
        path = os.path.join(os.path.dirname(__file__), "_portability_sample.py")
        with open(path, "w", encoding="utf-8") as f:
            f.write('a = "raise"  # raise\nb = """\nyield\n"""\nc = f"x {1} raise"\nraise ValueError()\n')
        try:
            lines = code_lines(path)
        finally:
            os.remove(path)
        joined = "\n".join(lines)
        self.assertEqual(1, len(re.findall(r"\braise\b", joined)))
        self.assertNotIn("yield", joined)
        self.assertIn("raise ValueError()", lines[5])
