"""The runner of the cross-language benchmark (bench/README.md; the reference is dotnet/Benchmarks/CrossBench.cs).

    python crossbench.py <program.jq> <input.json> <take> <warmup-ms> <measure-ms> <output-file>

The same command line, the same three measurements and the same line of output in every port. It
drives the core engine (its values, JSON reader and writer), so the numbers do not include the host
boundary.
"""

from __future__ import annotations

import platform
import sys
import time
from collections.abc import Callable
from typing import Any

from jqlite.binding.resolver import Resolver
from jqlite.runtime.evaluation import EvaluationOptions, OutcomeKind
from jqlite.runtime.interpreter import Interpreter
from jqlite.syntax.syntax_error import SyntaxError
from jqlite.values import value_text
from jqlite.values.jq_value import Fail

ROUNDS = 10


def main(args: list[str]) -> int:
    if len(args) != 6:
        print("usage: crossbench.py <program.jq> <input.json> <take> <warmup-ms> <measure-ms> <output-file>", file=sys.stderr)
        return 2
    with open(args[0], encoding="utf-8-sig") as f:
        source = f.read()
    with open(args[1], encoding="utf-8-sig") as f:
        input_text = f.read()
    take = int(args[2])
    warmup_ms = int(args[3])
    measure_ms = int(args[4])

    program = Resolver.try_compile(source)
    if type(program) is SyntaxError:
        print(f"{program.message} at {program.line}:{program.column}", file=sys.stderr)
        return 1
    input = value_text.try_parse(input_text)
    if type(input) is Fail:
        print(f"bad input: {input.error}", file=sys.stderr)
        return 1
    options = EvaluationOptions()

    # The check run: the outputs as canonical text, one per line.
    text: list[str] = []

    def check(output: Any) -> bool:
        text.append(value_text.to_json(output))
        text.append("\n")
        return take == 0 or len(text) < 2 * take
    outcome = Interpreter.run(program, input, options, check)  # type: ignore[arg-type]
    if outcome.kind == OutcomeKind.ERROR or (outcome.kind == OutcomeKind.HALT and outcome.has_value):
        print(f"error: {value_text.to_json(outcome.value)}", file=sys.stderr)
        return 1
    with open(args[5], "wb") as output_file:
        output_file.write("".join(text).encode("utf-8"))
    count = len(text) // 2

    # compile: the program text to a bound program (the prelude is loaded already).
    def compile_once() -> None:
        Resolver.try_compile(source)

    # eval: the bound program on the input value, outputs collected as values.
    def eval_once() -> None:
        outputs: list[Any] = []

        def collect(output: Any) -> bool:
            outputs.append(output)
            return take == 0 or len(outputs) < take
        Interpreter.run(program, input, options, collect)  # type: ignore[arg-type]

    # text: the input text read, the program evaluated, the outputs written as text.
    def text_once() -> None:
        value = value_text.try_parse(input_text)
        written: list[str] = []
        outputs = [0]

        def write(output: Any) -> bool:
            written.append(value_text.to_json(output))
            written.append("\n")
            outputs[0] += 1
            return take == 0 or outputs[0] < take
        Interpreter.run(program, value, options, write)  # type: ignore[arg-type]
        "".join(written)

    compile_ns = measure(warmup_ms, measure_ms, compile_once)
    eval_ns = measure(warmup_ms, measure_ms, eval_once)
    text_ns = measure(warmup_ms, measure_ms, text_once)
    runtime = f"{platform.python_implementation()} {platform.python_version()}"
    print(f'{{"impl":"python","runtime":"{runtime}","compile_ns":{compile_ns:.1f},"eval_ns":{eval_ns:.1f},'
          f'"text_ns":{text_ns:.1f},"outputs":{count}}}')
    return 0


def measure(warmup_ms: int, measure_ms: int, operation: Callable[[], None]) -> float:
    """Nanoseconds per operation: the median of ten rounds of equal size, sized from the warm-up."""
    clock = time.perf_counter_ns
    start = clock()
    warmups = 0
    while True:
        operation()
        warmups += 1
        if (clock() - start) / 1e6 >= warmup_ms:
            break
    per_operation_ms = (clock() - start) / 1e6 / warmups
    batch = max(1, int(measure_ms / ROUNDS / per_operation_ms)) if per_operation_ms > 0 else 1
    rounds = []
    for _ in range(ROUNDS):
        begin = clock()
        for _ in range(batch):
            operation()
        rounds.append((clock() - begin) / batch)
    rounds.sort()
    return (rounds[ROUNDS // 2 - 1] + rounds[ROUNDS // 2]) / 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
