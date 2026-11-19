"""The host-specific part of §9.10 (RegexEngine.cs). Host-specific (**H**): Python's `re`.

The only file of the core that uses a host regex engine: it compiles a pattern regex_syntax.py
translated and finds the next match from a position. Every class is written out explicitly and
`.` is a class, so no flag of the engine is used. `pattern.search(s, pos)` sees the whole input: `^` matches only at its real start and
`\\b` looks at the code point before `pos`. Offsets are code points already. `re` backtracks, so a
pattern can be slow on some inputs (the dialect has no backreferences or lookaround). Compiled
patterns are cached per process in a map emptied when it reaches CACHE_LIMIT entries.
"""

from __future__ import annotations

import re

CACHE_LIMIT = 256

# A pattern compiled by the host engine.
HostPattern = re.Pattern

_cache: dict[str, HostPattern] = {}


def try_compile(pattern: str, lookaround: bool) -> HostPattern | str:
    """A compiled pattern, or the engine's reason."""
    compiled = _cache.get(pattern)
    if compiled is not None:
        return compiled
    try:
        compiled = re.compile(pattern)
    except re.error as e:
        # The boundary of the host engine: its exception becomes the dialect's error.
        return str(e)
    if len(_cache) >= CACHE_LIMIT:
        _cache.clear()
    _cache[pattern] = compiled
    return compiled


def try_match(compiled: HostPattern, input: str, start: int, spans: list[int]) -> bool:
    """The first match starting at or after code point `start`. `spans` receives start and end of
    the match, then of each group (-1, -1 for a group that did not participate)."""
    match = compiled.search(input, start)
    if match is None:
        return False
    for g in range(len(spans) // 2):
        s, e = match.span(g)
        spans[2 * g] = s
        spans[2 * g + 1] = e
    return True
