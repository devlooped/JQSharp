"""What the programs of regex_syntax.py mean, as the simplest machine that runs them
(BacktrackingMatcher.cs): a backtracking matcher that tries each start position in turn and each
alternative in priority order, with the positions of the marks as they are stored. It can take time
exponential in the input, so it is not the engine; test_builtins.py compares the engine with it on
small patterns and inputs, and gives up on a search that takes it more than a budget of steps."""

from __future__ import annotations

from jqlite.builtins.regex_syntax import Op, Translation

# What the stack holds, three numbers an entry: an alternative to try (its instruction and
# position), or the value of a slot or of a mark to put back when going back past it.
_ALTERNATIVE = 0
_SLOT = 1
_MARK = 2


def try_match(program: Translation, input: str, start: int, spans: list[int], budget: int) -> bool | None:
    """The first match starting at or after code point `start`: its spans, as
    RegexMatcher.try_match gives them; None when the search is not over after `budget` instructions."""
    steps = [budget]
    for at in range(start, len(input) + 1):
        if _run(program, input, at, spans, steps):
            return True
        if steps[0] < 0:
            return None
    return False


def _run(program: Translation, input: str, at: int, spans: list[int], steps: list[int]) -> bool:
    # Whether the program matches from at; the spans are then in spans.
    ops = program.ops
    marks = [-1] * program.marks
    for i in range(len(spans)):
        spans[i] = -1
    stack: list[int] = []
    pc = 0
    pos = at
    while True:
        steps[0] -= 1
        if steps[0] < 0:
            return False
        ok = True
        a = program.a[pc]
        op = ops[pc]
        if op <= Op.ANY_BUT_LINE_FEED:
            c = ord(input[pos]) if pos < len(input) else -1
            if c >= 0 and (c == a if op == Op.CHAR else _in_set(program.sets[a], c) if op == Op.SET else op == Op.ANY or c != 0x0A):
                pos += 1
                pc += 1
            else:
                ok = False
        elif op == Op.SPLIT:
            stack += [_ALTERNATIVE, pc + program.b[pc], pos]
            pc += a
        elif op == Op.JUMP:
            pc += a
        elif op == Op.SAVE:
            stack += [_SLOT, a, spans[a]]
            spans[a] = pos
            pc += 1
        elif op == Op.BEGIN:
            ok = pos == 0
            pc += 1
        elif op == Op.END:
            ok = pos == len(input)
            pc += 1
        elif op == Op.WORD_BOUNDARY or op == Op.NOT_WORD_BOUNDARY:
            boundary = (pos > 0 and _is_word(input[pos - 1])) != (pos < len(input) and _is_word(input[pos]))
            ok = boundary == (op == Op.WORD_BOUNDARY)
            pc += 1
        elif op == Op.MARK:
            stack += [_MARK, a, marks[a]]
            marks[a] = pos
            pc += 1
        elif op == Op.EMPTY_EXIT:
            pc += program.b[pc] if pos == marks[a] else 1
        else:
            return True

        if ok:
            continue

        # Back to the last alternative, undoing what was stored since.
        while True:
            if not stack:
                return False
            value = stack.pop()
            index = stack.pop()
            kind = stack.pop()
            if kind == _ALTERNATIVE:
                pc = index
                pos = value
                break
            if kind == _SLOT:
                spans[index] = value
            else:
                marks[index] = value


def _is_word(c: str) -> bool:
    return ("0" <= c <= "9") or ("A" <= c <= "Z") or ("a" <= c <= "z") or c == "_"


def _in_set(ranges: list[int], c: int) -> bool:
    for i in range(0, len(ranges), 2):
        if ranges[i] <= c <= ranges[i + 1]:
            return True
    return False
