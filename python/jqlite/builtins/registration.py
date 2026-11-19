"""Fills the native table of the runtime at start-up (NativeTable's static constructor).

Each family registers its implementations by name and arity; the table then checks that every
native of the catalogue has one (guide §1.1: the runtime package holds the table, the builtins
package fills it, so the two packages do not import each other in a cycle).
"""

from __future__ import annotations

import threading

from ..runtime import native_table
from . import (
    assign_natives,
    collection_natives,
    containment,
    core_natives,
    date_natives,
    formats,
    loop_natives,
    math_natives,
    module_natives,
    path_natives,
    regex_natives,
    string_natives,
)

_lock = threading.Lock()
_done = False


def register_all() -> None:
    """Registers every family once."""
    global _done
    if _done:
        return
    with _lock:
        if _done:
            return
        core_natives.register()
        loop_natives.register()
        path_natives.register()
        collection_natives.register()
        string_natives.register()
        assign_natives.register()
        module_natives.register()
        containment.register()
        math_natives.register()
        date_natives.register()
        regex_natives.register()
        native_table.set_formats(formats.try_apply)
        native_table.check()
        _done = True
