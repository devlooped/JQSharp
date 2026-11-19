"""The Python recursion limit while jqlite runs. Host-specific (**H**); used by the parser, the
resolver and the interpreter (runtime/stack_guard.py), the counterpart of the reference's StackGuard.

CPython 3.11+ runs a call of a Python function from Python code without growing the C stack (its
frames live on the heap), and counts those frames against the per-thread recursion limit
(`sys.setrecursionlimit`, 1,000 by default), which the 1,000 levels of program nesting and the
call depth of §11 exceed. So while a parse, a binding or an evaluation of jqlite runs, the limit is
raised to what it needs above the current depth, and when the last one that runs ends (threads may
parse and evaluate at once: they are counted) the limit is put back to what it was. The limit is a
setting of the process: while jqlite runs, the host's own code (its callbacks included) gets the
raised limit too.
"""

from __future__ import annotations

import sys
import threading
from types import TracebackType

# The highest limit sys.setrecursionlimit takes (a C int), with a margin.
LIMIT_CAP = 2**31 - 1 - 2**16

# Frames above the caller's depth for the code between jqlite's own frames (helpers, the host's
# callbacks), whatever the program.
RESERVE = 1000

_lock = threading.Lock()
_active = 0
_saved: int | None = None


def _depth() -> int:
    # The number of Python frames of the calling thread.
    frame = sys._getframe(1)
    depth = 1
    while frame is not None:
        frame = frame.f_back  # type: ignore[assignment]
        depth += 1
    return depth


class raised:  # noqa: N801 - used as `with recursion_limit.raised(frames):`
    """Raises the recursion limit to the current depth plus `frames` (and RESERVE) while the
    block runs; the last block that ends puts back the limit it found."""

    __slots__ = ("frames",)

    def __init__(self, frames: int) -> None:
        self.frames = frames

    def __enter__(self) -> None:
        global _active, _saved
        needed = min(_depth() + RESERVE + self.frames, LIMIT_CAP)
        with _lock:
            _active += 1
            limit = sys.getrecursionlimit()
            if needed > limit:
                if _saved is None:
                    _saved = limit
                sys.setrecursionlimit(needed)

    def __exit__(self, kind: type[BaseException] | None, error: BaseException | None, trace: TracebackType | None) -> None:
        global _active, _saved
        with _lock:
            _active -= 1
            if _active == 0 and _saved is not None:
                sys.setrecursionlimit(_saved)
                _saved = None


def active() -> int:
    """How many parses, bindings and evaluations run now (for the tests)."""
    return _active
