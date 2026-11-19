"""The engine of §9.10 (RegexEngine.cs): compiles a pattern through regex_syntax.py and finds the
next match from a position. It is portable code, no host regex engine: none reads the flavor with
one meaning (docs/porting.md §3.3).

The matcher reads the input once, from the start position on, and follows every way the program can
match at the same time (a Pike machine): a thread is an instruction that waits for a code point,
with the captures of the way that led to it. The threads are kept in the order a backtracking
matcher would try them (a greedy quantifier takes all it can first, a lazy one as little, the left
alternative first), and a state reached a second time at the same position is dropped: the first
way to it has priority and the same future. So the time is the length of the input times the size
of the program, and the memory depends on the program alone. The translation bounds the program
(§11), so nothing is refused here: within those limits a program has at most 50,000 instructions, a
hundred levels and 2,002 capture slots per thread. A search for the leftmost match does not start
again at each position: a new thread starts at each position, behind the ones already running,
until a match is found. Nothing recurses.

A loop whose body can match nothing ends after an iteration that matched nothing (Op.MARK and
Op.EMPTY_EXIT). Whether an iteration has matched something yet belongs to the state of a thread, as
its level: of the loops with a mark around its instruction, how many have, counted from the
outermost (if an outer iteration has matched nothing, the inner ones it contains have not either).

A Python `str` is a sequence of code points: the input is read one code point per index, and the
positions are code point indices (the reference reads UTF-16 units, a surrogate pair as one code
point). Compiled programs are immutable and cached per process in a map that is emptied when it
reaches CACHE_LIMIT entries (a `dict`, whose reads and writes are atomic, as the reference's
concurrent map); the memory of a search is a RegexMatcher of its own.
"""

from __future__ import annotations

from .regex_syntax import Op, Options, Translation, try_translate

CACHE_LIMIT = 256

_cache: dict[str, RegexProgram] = {}

_CHAR = Op.CHAR
_SET = Op.SET
_ANY_BUT_LINE_FEED = Op.ANY_BUT_LINE_FEED
_SPLIT = Op.SPLIT
_JUMP = Op.JUMP
_SAVE = Op.SAVE
_BEGIN = Op.BEGIN
_END = Op.END
_WORD_BOUNDARY = Op.WORD_BOUNDARY
_NOT_WORD_BOUNDARY = Op.NOT_WORD_BOUNDARY
_MARK = Op.MARK
_EMPTY_EXIT = Op.EMPTY_EXIT


def try_compile(pattern: str, options: Options) -> RegexProgram | str:
    """The program of `pattern` under `options`, or why the pattern is not of the flavor or beyond its limits."""
    key = ("x" if options.extended else "-") + ("m" if options.dot_all else "-") + ("i" if options.ignore_case else "-") + pattern
    program = _cache.get(key)
    if program is not None:
        return program
    translation = try_translate(pattern, options)
    if type(translation) is str:
        return translation
    program = RegexProgram(translation)  # type: ignore[arg-type]
    if len(_cache) >= CACHE_LIMIT:
        _cache.clear()
    _cache[key] = program
    return program


class RegexProgram:
    """A translated pattern ready to run: immutable, shared by the searches of every thread."""

    __slots__ = ("ops", "a", "b", "sets", "groups", "slots", "states", "readers", "depth", "base", "literal", "anchored")

    def __init__(self, translation: Translation) -> None:
        ops = translation.ops
        self.ops = ops
        self.a = translation.a
        self.b = translation.b
        self.sets = translation.sets
        # One name (or None) per capturing group.
        self.groups = translation.groups
        # The capture slots of a thread: 2 · (groups + 1).
        self.slots = 2 * (len(translation.groups) + 1)
        # Per instruction: the loops with a mark around it, and its first state (the one of level 0).
        self.depth = [0] * len(ops)
        self.base = [0] * len(ops)
        depth = 0
        states = 0
        readers = 0
        for pc in range(len(ops)):
            self.depth[pc] = depth
            self.base[pc] = states
            states += depth + 1
            op = ops[pc]
            if op == _MARK:
                depth += 1
            elif op == _EMPTY_EXIT:
                depth -= 1
            elif op <= _ANY_BUT_LINE_FEED:
                readers += 1
        # The states of the program: its instructions, each at every level it can have.
        self.states = states
        # The instructions that read a code point.
        self.readers = readers
        # The program starts with the instruction that stores the start of the match: what follows
        # it is the first thing every match does. The code point a match must start with (any code
        # point: the input is indexed by code point), or -1; whether a match can only start at the
        # start of the input.
        self.literal = translation.a[1] if ops[1] == _CHAR else -1
        self.anchored = ops[1] == _BEGIN


class RegexMatcher:
    """The memory of the searches of one caller over one program: one mark per state, a stack of
    at most two entries per state, and two lists of threads of at most one thread per instruction
    that reads a code point. Made at the first search and used again by the next ones; never shared
    between threads."""

    __slots__ = ("program", "_seen", "_generation", "_stack", "_work", "_current", "_next")

    def __init__(self, program: RegexProgram) -> None:
        self.program = program
        # Per state: the generation that reached it last. A generation is one position of one
        # search (a Python int does not overflow: no reset is needed).
        self._seen: list[int] = []
        self._generation = 0
        # Pairs: an instruction and its level, still to follow; or -1 - slot and the value to put back.
        self._stack: list[int] = []
        # The captures of the thread being followed.
        self._work: list[int] = []
        self._current: _Threads | None = None
        self._next: _Threads | None = None

    def try_match(self, input: str, start: int, spans: list[int]) -> bool:
        """The first match starting at or after code point `start` (anchors and word boundaries see
        the whole input). `spans` receives start and end of the match, then of each group (-1, -1
        for a group that did not participate): 2 · (groups + 1) entries. A shorter `spans` receives
        the first entries only; an empty one asks only whether there is a match."""
        program = self.program
        ops = program.ops
        a = program.a
        sets = program.sets
        depth = program.depth
        slots = program.slots
        length = len(input)
        width = min(len(spans), slots)
        if not self._seen:
            self._seen = [0] * program.states
            self._stack = [0] * (2 * program.states)
            self._work = [0] * slots
            capacity = min(8, program.readers + 1)
            self._current = _Threads(capacity, slots)
            self._next = _Threads(capacity, slots)
        work = self._work
        follow = self._follow
        current: _Threads = self._current  # type: ignore[assignment]
        next: _Threads = self._next  # type: ignore[assignment]
        anchored = program.anchored
        literal = chr(program.literal) if program.literal >= 0 else ""
        unset = [-1] * width

        current.count = 0
        matched = False
        pos = start
        self._generation += 1
        while True:
            # The threads at pos are in current. Until a match is found, one more starts here,
            # behind them: a match that starts earlier has priority.
            if not matched:
                if current.count == 0:
                    if anchored and pos > 0:
                        return False
                    if literal:
                        at = input.find(literal, pos)
                        if at < 0:
                            return False
                        if at != pos:
                            pos = at
                            self._generation += 1
                work[:width] = unset
                if follow(current, 0, 0, input, pos, width, spans):
                    if width == 0:
                        return True
                    matched = True

            if pos >= length:
                return matched
            c = ord(input[pos])

            if current.count == 0:
                if matched:
                    return True
                pos += 1
                self._generation += 1
                continue

            # Every thread reads the code point at pos: the ones that accept it go on after it.
            self._generation += 1
            next.count = 0
            pcs = current.pcs
            captures = current.captures
            for i in range(current.count):
                pc = pcs[i]
                op = ops[pc]
                if (a[pc] != c) if op == _CHAR else (not _in_set(sets[a[pc]], c)) if op == _SET else (op == _ANY_BUT_LINE_FEED and c == 0x0A):
                    continue
                if width:
                    frm = i * slots
                    work[:width] = captures[frm:frm + width]
                # It has read a code point: every loop around it has matched something.
                if follow(next, pc + 1, depth[pc + 1], input, pos + 1, width, spans):
                    if width == 0:
                        return True
                    # The threads behind this one have a lower priority than the match.
                    matched = True
                    break

            pos += 1
            current, next = next, current
            self._current = current
            self._next = next

    def _follow(self, threads: _Threads, pc: int, level: int, input: str, pos: int, width: int, spans: list[int]) -> bool:
        # Follows a thread from an instruction at pos, with the captures in work, through the
        # instructions that read nothing: each reading instruction it reaches first becomes a
        # thread of threads, in priority order. True when it reached the end of the program: the
        # match is then in spans, and what it had still to follow has a lower priority.
        program = self.program
        ops = program.ops
        a = program.a
        b = program.b
        depth = program.depth
        states = program.base
        slots = program.slots
        length = len(input)
        seen = self._seen
        generation = self._generation
        stack = self._stack
        work = self._work
        top = 0
        while True:
            op = ops[pc]
            # What an instruction that reads does next does not depend on the level.
            state = states[pc] if op <= _ANY_BUT_LINE_FEED else states[pc] + level
            alive = seen[state] != generation
            if alive:
                seen[state] = generation
                if op <= _ANY_BUT_LINE_FEED:
                    index = threads.count
                    if index == len(threads.pcs):
                        threads.grow()
                    threads.pcs[index] = pc
                    if width:
                        to = index * slots
                        threads.captures[to:to + width] = work[:width]
                    threads.count = index + 1
                    alive = False
                elif op == _SPLIT:
                    stack[top] = pc + b[pc]
                    stack[top + 1] = level
                    top += 2
                    pc += a[pc]
                elif op == _SAVE:
                    slot = a[pc]
                    if slot < width:
                        stack[top] = -1 - slot
                        stack[top + 1] = work[slot]
                        top += 2
                        work[slot] = pos
                    pc += 1
                elif op == _JUMP:
                    pc += a[pc]
                elif op == _MARK:
                    # An iteration starts: it has matched nothing yet, so the level stays.
                    pc += 1
                elif op == _EMPTY_EXIT:
                    # The iteration of the innermost loop ends: it matched something if the level counts it.
                    inner = depth[pc]
                    pc += b[pc] if level < inner else 1
                    if level == inner:
                        level = inner - 1
                elif op == _BEGIN:
                    if pos == 0:
                        pc += 1
                    else:
                        alive = False
                elif op == _END:
                    if pos == length:
                        pc += 1
                    else:
                        alive = False
                elif op == _WORD_BOUNDARY or op == _NOT_WORD_BOUNDARY:
                    boundary = (pos > 0 and _is_word(input[pos - 1])) != (pos < length and _is_word(input[pos]))
                    if boundary == (op == _WORD_BOUNDARY):
                        pc += 1
                    else:
                        alive = False
                else:
                    spans[:width] = work[:width]
                    return True

            if alive:
                continue

            # This way ended: back to the last one left aside, putting back the captures stored since.
            while True:
                if top == 0:
                    return False
                top -= 2
                entry = stack[top]
                value = stack[top + 1]
                if entry >= 0:
                    pc = entry
                    level = value
                    break
                work[-1 - entry] = value


def _is_word(c: str) -> bool:
    # The ASCII word characters of §9.10.
    return ("0" <= c <= "9") or ("A" <= c <= "Z") or ("a" <= c <= "z") or c == "_"


def _in_set(ranges: list[int], c: int) -> bool:
    # Whether a code point is in a set: sorted pairs low, high.
    count = len(ranges)
    if count <= 8:
        for i in range(0, count, 2):
            if c < ranges[i]:
                return False
            if c <= ranges[i + 1]:
                return True
        return False
    low = 0
    high = count // 2 - 1
    while low <= high:
        middle = (low + high) >> 1
        if c < ranges[2 * middle]:
            high = middle - 1
        elif c > ranges[2 * middle + 1]:
            low = middle + 1
        else:
            return True
    return False


class _Threads:
    # The threads at one position, in priority order: the instruction of each and its captures.
    __slots__ = ("pcs", "captures", "count")

    def __init__(self, capacity: int, slots: int) -> None:
        self.pcs = [0] * capacity
        self.captures = [0] * (capacity * slots)
        self.count = 0

    def grow(self) -> None:
        # Room for one more thread: there are never more than the program has reading instructions.
        # Doubling both lists keeps `slots` captures per instruction.
        self.pcs.extend([0] * len(self.pcs))
        self.captures.extend([0] * len(self.captures))
