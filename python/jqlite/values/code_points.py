"""Strings by Unicode code point (CodePoints.cs).

A Python ``str`` already is a sequence of code points: length, slices and ordering need no work
here, unlike the UTF-16 hosts. What remains is that a ``str`` can hold lone surrogates (code
points U+D800-U+DFFF), which jqlite strings never contain: :func:`sanitize` replaces each with
U+FFFD wherever text enters the engine (a high surrogate followed by a low one is joined into the
code point the pair stands for, as a UTF-16 host would read them).
"""

from __future__ import annotations

from .jq_array import JqArray

MAX = 0x10FFFF
REPLACEMENT = "�"


def sanitize(text: str) -> str:
    """Replaces each lone surrogate with U+FFFD; returns ``text`` itself when it has none."""
    if text.isascii() or max(text) < "\ud800":
        return text
    # Surrogates encode as their 16-bit units; the decoder joins pairs and replaces lone units.
    if not _has_surrogate(text):
        return text
    return text.encode("utf-16-le", "surrogatepass").decode("utf-16-le", "replace")


def _has_surrogate(text: str) -> bool:
    for c in text:
        if "\ud800" <= c <= "\udfff":
            return True
    return False


def length(text: str) -> int:
    """The number of code points."""
    return len(text)


def substring(text: str, start: int, end: int) -> str:
    """The code points ``start`` (included) to ``end`` (excluded)."""
    return text[start:end]


def compare(a: str, b: str) -> int:
    """Orders two strings by code point; returns -1, 0 or 1."""
    return -1 if a < b else (1 if a > b else 0)


def explode(text: str) -> JqArray:
    """The code points of a string, as numbers."""
    return JqArray.from_owned([float(ord(c)) for c in text])


def is_scalar(code_point: int) -> bool:
    """Whether ``code_point`` is a Unicode scalar value (a code point that is not a surrogate)."""
    return 0 <= code_point <= MAX and not (0xD800 <= code_point <= 0xDFFF)


def from_code_point(code_point: int) -> str:
    """The string of one Unicode scalar value."""
    return chr(code_point)
