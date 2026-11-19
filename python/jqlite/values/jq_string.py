"""An immutable string made by jq's ``+`` on strings, which later appends extend in place (JqString.cs).

Storage is a shared buffer (a Python list of ``str`` parts) plus the number of parts this version
covers, as :class:`JqArray` is without its start. A part of a buffer is appended once, before any
string that covers it exists, and never changed, so every :class:`JqString` is immutable however
many strings share its buffer.

Appending (:meth:`append`) to the string that covers every part of its buffer (its latest version)
appends the new part in place: amortized O(1), so ``reduce range(n) as $i (""; . + "ab")``, and with
it ``add`` and ``join``, is O(n). Any other append (an older version) starts a new buffer from this
version's text. The check and the append are one step under ``CLAIM_LOCK``, the lock of arrays and
objects (the reference's compare-and-swap on ``Used``). :attr:`text` joins the parts once per
version, when a reader needs the host string.

Where a JqString may be (guide §2.9, Python row): only the values layer and the interpreter see
one. It is a jqlite string: ``kind`` and ``type_name`` say so, and every operation of
``value_ops`` and ``value_text`` that the interpreter applies (equality, ordering, arithmetic,
indexing, slicing, the JSON text and error messages) takes it where it takes a ``str``. It flows
unchanged through what only holds a value: the input, variables, the state of ``reduce`` and
``foreach``, the outputs of eval and eval_one. It becomes a ``str`` (:func:`plain`) wherever a value
is kept or read as text elsewhere:

- when it enters a container: the interpreter's array and object construction (keys and
  values), a path's key (``Path.append``), and the values ``_modify`` and ``_assign`` write;
- when a native gets it: the input and arguments of a value native, the input of a closure native
  (both at dispatch, in the interpreter), and the closure outputs a closure native reads as text
  (``match``'s regex and flags);
- in string interpolation and formats (``@base64``, ``"\\(.)"``);
- at the host boundary: each output of ``Interpreter.run``.

So arrays, objects, paths, natives and the host only ever see ``str``, and the rest of the engine
tests ``type(v) is str`` as before.
"""

from __future__ import annotations

from .jq_array import CLAIM_LOCK

# The shortest concatenation that gets a buffer: shorter ones are plain str, whose copies cost little
# and which need no join for `text`. In code points (the reference counts UTF-16 units).
MIN_LENGTH = 256


class JqString:
    __slots__ = ("parts", "count", "_text")

    def __init__(self, parts: list[str], count: int) -> None:
        # The buffer: parts [0, count) are this string; parts past count belong to later versions.
        self.parts = parts
        self.count = count
        # The host string of this version, built on first read. Two threads may both build it:
        # they build equal strings, and either may be kept.
        self._text: str | None = None

    @staticmethod
    def concat(left: str, right: str) -> JqString:
        """``left`` followed by ``right``, in a new buffer."""
        return JqString([left, right], 2)

    @property
    def text(self) -> str:
        """The host string, built once per version."""
        text = self._text
        if text is None:
            # The slice, not the whole list: another version may be appending to the buffer.
            text = "".join(self.parts[:self.count])
            self._text = text
        return text

    def append(self, right: str) -> JqString:
        """This string followed by ``right``."""
        parts = self.parts
        count = self.count
        with CLAIM_LOCK:
            if len(parts) == count:
                parts.append(right)
                return JqString(parts, count + 1)
        return JqString([self.text, right], 2)

    def __repr__(self) -> str:
        return f"JqString({self.text!r})"


def plain(value: object) -> object:
    """The value as the rest of the engine sees it: a JqString's text, any other value unchanged."""
    return value.text if type(value) is JqString else value  # type: ignore[attr-defined]
