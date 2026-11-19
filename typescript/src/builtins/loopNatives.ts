// The natives marked *loop* in §9.4: generators written as loops, so their iterations do not
// consume the call depth (§11). Each follows the jqlite definition the spec gives it, including
// how it compares a non-numeric count, and passes the paths of its argument's outputs.
//
// A loop that gives outputs is an After that pushes itself before each output but the last
// (see Interpreter): the reference's `for … { if (Output(…) is { } stop) return stop; }`.

import type { Node } from "../binding/bound.js";
import type { Env } from "../runtime/env.js";
import type { After, Emit, Interpreter } from "../runtime/interpreter.js";
import { Path } from "../runtime/path.js";
import { Signal } from "../runtime/signal.js";
import * as Errors from "../values/errors.js";
import { Fail, type JqValue, isTruthy } from "../values/jqValue.js";
import * as ValueOps from "../values/valueOps.js";
import { addClosure } from "./nativeTable.js";

export function register(): void {
  addClosure("range", 2, range);
  addClosure("limit", 2, limit);
  addClosure("skip", 2, skip);
  addClosure("while", 2, (interpreter, env, args, input, path, emit) => iterate(interpreter, env, args[0]!, args[1]!, input, path, emit, true));
  addClosure("until", 2, (interpreter, env, args, input, path, emit) => iterate(interpreter, env, args[0]!, args[1]!, input, path, emit, false));
  addClosure("repeat", 1, repeat);
}

// range($from; $upto): $from outermost (the exception of §9.4).
function range(interpreter: Interpreter, env: Env | null, args: readonly Node[], input: JqValue, path: Path | null, emit: Emit): void {
  const at = Path.of(path);
  interpreter.forEach(args[0]!, env, input, null, (from) =>
    interpreter.forEach(args[1]!, env, input, null, (upto) => {
      if (typeof from !== "number" || typeof upto !== "number") {
        interpreter.fail(Errors.rangeNotNumeric);
        return;
      }

      let value = from;
      const next: After = (stop) => {
        if (stop !== null || !(value < upto)) {
          interpreter.ret(stop);
          return;
        }

        const current = value++;
        if (value < upto) interpreter.after(next);
        interpreter.output(emit, current, at);
      };
      next(null);
    }),
  );
}

// def limit($n; f): if $n > 0 then label $out | foreach f as $item (0; . + 1; $item, if . >= $n then break $out else empty end) elif $n == 0 then empty else error(…) end;
function limit(interpreter: Interpreter, env: Env | null, args: readonly Node[], input: JqValue, path: Path | null, emit: Emit): void {
  interpreter.forEach(args[0]!, env, input, null, (n) => {
    if (ValueOps.compare(n, 0) <= 0) {
      if (ValueOps.areEqual(n, 0)) interpreter.ret(null);
      else interpreter.fail(Errors.negativeLimit);
      return;
    }

    const enough = Signal.break(interpreter.newLabel());
    let count = 0;
    interpreter.after((signal) => interpreter.ret(signal === enough ? null : signal));
    const counted: After = (stop) => interpreter.ret(stop !== null ? stop : ValueOps.compare(++count, n) >= 0 ? enough : null);
    interpreter.eval(args[1]!, env, input, path, (value, at) => {
      interpreter.after(counted);
      interpreter.output(emit, value, at);
    });
  });
}

// def skip($n; f): if $n > 0 then foreach f as $item ($n; . - 1; if . < 0 then $item else empty end) elif $n == 0 then f else error(…) end;
function skip(interpreter: Interpreter, env: Env | null, args: readonly Node[], input: JqValue, path: Path | null, emit: Emit): void {
  interpreter.forEach(args[0]!, env, input, null, (n) => {
    if (ValueOps.compare(n, 0) <= 0) {
      if (ValueOps.areEqual(n, 0)) interpreter.eval(args[1]!, env, input, path, emit);
      else interpreter.fail(Errors.negativeSkip);
      return;
    }

    let count = n;
    interpreter.eval(args[1]!, env, input, path, (value, at) => {
      const less = ValueOps.trySubtract(count, 1);
      if (less instanceof Fail) {
        interpreter.fail(less.error);
        return;
      }

      count = less;
      if (ValueOps.compare(count, 0) < 0) interpreter.output(emit, value, at);
      else interpreter.ret(null);
    });
  });
}

// One piece of work of the general iterate: emit a value, or iterate from it.
interface Work {
  readonly emit: boolean;
  readonly value: JqValue;
  readonly at: Path | null;
}

// def while(cond; update): def _while: if cond then ., (update | _while) else empty end; _while;
// def until(cond; update): def _until: if cond then . else (update | _until) end; _until;
// while (emitWhenTrue): for each output of cond, if true: the value, then the same for each output of update.
// until: for each output of cond, if true: the value; else the same for each output of update.
// When cond and update are direct, a plain loop; otherwise a stack of pending work replaces the
// recursion, the outputs of cond and update collected at each step.
function iterate(interpreter: Interpreter, env: Env | null, cond: Node, update: Node, input: JqValue, path: Path | null, emit: Emit, emitWhenTrue: boolean): void {
  if (cond.direct && update.direct) {
    let value = input;
    let at = path;
    // The next value, from update; false when there is none (the return is scheduled).
    const advance = (): boolean => {
      const signal = interpreter.evalOne(update, env, value, at);
      if (signal !== null || !interpreter.has) {
        interpreter.ret(signal);
        return false;
      }

      value = interpreter.value;
      at = interpreter.at;
      return true;
    };
    const test = (): void => {
      while (true) {
        const signal = interpreter.evalOne(cond, env, value, null);
        if (signal !== null || !interpreter.has) {
          interpreter.ret(signal);
          return;
        }

        if (isTruthy(interpreter.value)) {
          // while goes on after the value, until stops at it.
          if (emitWhenTrue) interpreter.after(resume);
          interpreter.output(emit, value, at);
          return;
        }

        if (emitWhenTrue) {
          interpreter.ret(null);
          return;
        }

        if (!advance()) return;
      }
    };
    const resume: After = (stop) => {
      if (stop !== null) interpreter.ret(stop);
      else if (advance()) test();
    };
    test();
    return;
  }

  // Work items: emit a value, or iterate from it; the top of the stack is next.
  const work: Work[] = [{ emit: false, value: input, at: path }];
  const next: After = (stop) => {
    if (stop !== null || work.length === 0) {
      interpreter.ret(stop);
      return;
    }

    const item = work.pop()!;
    if (item.emit) {
      if (work.length > 0) interpreter.after(next);
      interpreter.output(emit, item.value, item.at);
      return;
    }

    const tests: boolean[] = [];
    // The outputs of update, computed once: each test that continues goes on with them.
    const updates: Work[] = [];
    const finish: After = (signal) => {
      if (signal !== null) {
        interpreter.ret(signal);
        return;
      }

      // For each test in order: while: if true, the value then each update; until: if true,
      // the value, else each update. Pushed last first, so that they run in order.
      for (let t = tests.length - 1; t >= 0; t--) {
        if (tests[t] === emitWhenTrue) {
          for (let u = updates.length - 1; u >= 0; u--) work.push(updates[u]!);
        }

        if (tests[t]) work.push({ emit: true, value: item.value, at: item.at });
      }

      next(null);
    };
    interpreter.after((signal) => {
      if (signal !== null || !tests.includes(emitWhenTrue)) {
        finish(signal);
        return;
      }

      interpreter.after(finish);
      interpreter.eval(update, env, item.value, item.at, (value, at) => {
        updates.push({ emit: false, value, at });
        interpreter.ret(null);
      });
    });
    interpreter.eval(cond, env, item.value, null, (test) => {
      tests.push(isTruthy(test));
      interpreter.ret(null);
    });
  };
  next(null);
}

// def repeat(f): def _repeat: f, _repeat; _repeat; — the outputs of f on the same input, again and again.
function repeat(interpreter: Interpreter, env: Env | null, args: readonly Node[], input: JqValue, path: Path | null, emit: Emit): void {
  const again: After = (signal) => {
    if (signal !== null) {
      interpreter.ret(signal);
      return;
    }

    interpreter.after(again);
    interpreter.eval(args[0]!, env, input, path, emit);
  };
  again(null);
}
