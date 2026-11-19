"""UTF-8 encoding and decoding for the formats that work on bytes (Utf8.cs; §9.9).

Python's own codec does exactly what the reference's loops do (guide §3.2): strings of the engine
hold no surrogate, so encoding never fails, and the ``replace`` error handler of the decoder gives
one U+FFFD per maximal ill-formed subpart (§4.1).
"""

from __future__ import annotations


def encode(text: str) -> bytes:
    """The UTF-8 bytes of a well-formed string."""
    return text.encode("utf-8")


def try_decode(data: bytes) -> str | None:
    """The text of well-formed UTF-8 bytes; None when any sequence is ill-formed."""
    text = data.decode("utf-8", "replace")
    # The decoder only adds a U+FFFD for an ill-formed sequence; a well-formed U+FFFD is EF BF BD.
    if "�" in text and text.count("�") != data.count(b"\xef\xbf\xbd"):
        return None
    return text


def decode_replacing(data: bytes) -> str:
    """The text of UTF-8 bytes, each maximal ill-formed subpart replaced by U+FFFD."""
    return data.decode("utf-8", "replace")
