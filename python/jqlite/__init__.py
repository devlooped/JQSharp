"""jqlite: the Python port of jqlite, the profile of jq that JQLite implements.

The host boundary (spec §12): parse an expression once into an `Expression`, then evaluate it on
Python JSON values (what `json.loads` gives) as many times as needed, from any thread.

    >>> import jqlite
    >>> jqlite.evaluate(".a + 1", {"a": 41})
    [42]
    >>> expression = jqlite.parse("[.[] | select(. > $min)]")
    >>> expression.evaluate([1, 5, 10], {"min": 4})
    [[5, 10]]

The engine itself is the packages `values`, `syntax`, `binding`, `runtime` and `builtins`.
"""

from __future__ import annotations

from collections.abc import AsyncIterable, AsyncIterator, Iterable, Iterator, Mapping
from typing import Any

from .builtins.registration import register_all

register_all()

from .binding.resolver import Resolver as _CoreResolver  # noqa: E402
from .exceptions import JqError, JqHaltError, JqParseError  # noqa: E402
from .expression import Expression  # noqa: E402
from .options import Clock, Options, SystemClock  # noqa: E402
from .resolvers import FileResolver, ModuleKind, Resolver, ResolverSource  # noqa: E402
from .syntax.syntax_error import SyntaxError as _SyntaxError  # noqa: E402

__all__ = [
    "Clock", "Expression", "FileResolver", "JqError", "JqHaltError", "JqParseError", "ModuleKind", "Options", "Resolver",
    "SystemClock", "evaluate", "evaluate_each", "evaluate_each_async", "parse",
]

_DEFAULTS = Options()


def parse(expression: str, options: Options | None = None) -> Expression:
    """Parses and binds `expression` (an empty one is `.`), loading its modules through the options'
    resolver, into an expression to evaluate any number of times, concurrently if need be.

    Raises JqParseError for a syntax or binding error (an unknown function, label or module, a
    cycle of imports), with its line and column.
    """
    if type(expression) is not str:
        raise TypeError("the expression must be a string")
    options = _DEFAULTS if options is None else options
    source = None if options.resolver is None else ResolverSource(options.resolver)
    program = _CoreResolver.try_compile(expression, True, source)
    if type(program) is _SyntaxError:
        where = "" if program.module is None else f"{program.module}, "
        raise JqParseError(f"{program.message} at {where}line {program.line}, column {program.column}",
                           program.line, program.column, program.module)
    return Expression(program, options)  # type: ignore[arg-type]


def evaluate(expression: str, input: Any, variables: Mapping[str, Any] | None = None, options: Options | None = None) -> list[Any]:
    """Parses `expression` and evaluates it on `input`, returning all its results."""
    return parse(expression, options).evaluate(input, variables)


def evaluate_each(expression: str | Expression, inputs: Iterable[Any], variables: Mapping[str, Any] | None = None) -> Iterator[Any]:
    """Evaluates `expression` on each value of `inputs` in turn (a JSON Lines stream, for
    example), yielding the results of each; the results of one input are computed before they are
    yielded. Each value is evaluated alone: `halt` ends only the evaluation of its value."""
    parsed = parse(expression) if isinstance(expression, str) else expression
    for value in inputs:
        yield from parsed.evaluate(value, variables)


async def evaluate_each_async(expression: str | Expression, inputs: AsyncIterable[Any],
                              variables: Mapping[str, Any] | None = None) -> AsyncIterator[Any]:
    """`evaluate_each` over an asynchronous source of values."""
    parsed = parse(expression) if isinstance(expression, str) else expression
    async for value in inputs:
        for result in parsed.evaluate(value, variables):
            yield result
