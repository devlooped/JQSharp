"""What to do about the host stack (StackGuard.cs; spec §11). Host-specific (**H**).

Spec §11 lets a program observe no recursion limit but MaxDepth. The reference continues a deep
evaluation on a fresh thread when its stack runs short; CPython 3.11+ does not need that: a call
of a Python function from Python code does not grow the C stack (checked: 100,000 levels of jqlite
calls on a thread with a 256 KB stack), and nothing on the interpreter's recursive paths calls back
into Python through C. What limits a deep evaluation is the per-thread recursion limit, which
`syntax/recursion_limit.py` raises while the evaluation runs, to a bound computed from the
program and MaxDepth.

The bound: every frame of the evaluation belongs to a node or a pattern of the bound program, and
between two calls that count in the depth (§11) a node holds at most FRAMES_PER_NODE frames of
`eval`, `eval_one`, the handlers and their callbacks (measured: 3 to 8 per call level for the
recursive functions of the tests), so an evaluation needs at most
FRAMES_PER_NODE × size × (MaxDepth + 1) frames. The bound is loose: the limit is a counter, and the
frames only exist when the program recurses that deep.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from ..syntax.parser import PARSE_FRAMES
from ..syntax.recursion_limit import raised

if TYPE_CHECKING:
    from ..binding.bound import BoundProgram

# Frames of eval, eval_one, a handler and its callbacks for one node, with a margin.
FRAMES_PER_NODE = 16


def frames_for(program: BoundProgram, max_depth: int) -> int:
    """The recursion an evaluation of `program` with this MaxDepth may need, in frames; a parse
    (modulemeta reads modules at run time) on top."""
    return FRAMES_PER_NODE * program.size * (max_depth + 1) + PARSE_FRAMES


def evaluation(program: BoundProgram, max_depth: int) -> raised:
    """The recursion limit raised for one evaluation (`with stack_guard.evaluation(...):`)."""
    return raised(frames_for(program, max_depth))
