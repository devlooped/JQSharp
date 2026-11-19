package com.devlooped.jqlite.builtins;

import static com.devlooped.jqlite.builtins.NativeTable.addClosure;

import com.devlooped.jqlite.binding.Bound.Node;
import com.devlooped.jqlite.runtime.Emit;
import com.devlooped.jqlite.runtime.Env;
import com.devlooped.jqlite.runtime.Interpreter;
import com.devlooped.jqlite.runtime.Path;
import com.devlooped.jqlite.runtime.Signal;
import com.devlooped.jqlite.values.Errors;
import com.devlooped.jqlite.values.JqKind;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;
import com.devlooped.jqlite.values.ValueOps;
import java.util.ArrayList;
import java.util.List;

/**
 * The natives marked *loop* in §9.4: generators written as loops, so their iterations do not consume
 * the call depth (§11). Each follows the jqlite definition the spec gives it, including how it
 * compares a non-numeric count, and passes the paths of its argument's outputs.
 */
final class LoopNatives {
    private static final JqValue ZERO = JqValue.fromNumber(0);
    private static final JqValue ONE = JqValue.fromNumber(1);

    private LoopNatives() {
    }

    static void register() {
        addClosure("range", 2, LoopNatives::range);
        addClosure("limit", 2, LoopNatives::limit);
        addClosure("skip", 2, LoopNatives::skip);
        addClosure("while", 2, (interpreter, env, arguments, input, path, emit) -> iterate(interpreter, env, arguments[0], arguments[1], input, path, emit, true));
        addClosure("until", 2, (interpreter, env, arguments, input, path, emit) -> iterate(interpreter, env, arguments[0], arguments[1], input, path, emit, false));
        addClosure("repeat", 1, LoopNatives::repeat);
    }

    // range($from; $upto): $from outermost (the exception of §9.4).
    private static Signal range(Interpreter interpreter, Env env, Node[] arguments, JqValue input, Path path, Emit emit) {
        Path at = Path.of(path);
        return interpreter.forEach(arguments[0], env, input, null, (from, unused1) -> interpreter.forEach(arguments[1], env, input, null, (upto, unused2) -> {
            if (from.kind() != JqKind.NUMBER || upto.kind() != JqKind.NUMBER) {
                return Interpreter.fail(Errors.rangeNotNumeric());
            }
            double end = upto.asNumber();
            for (double value = from.asNumber(); value < end; value++) {
                Signal stop = interpreter.output(emit, JqValue.fromNumber(value), at);
                if (stop != null) {
                    return stop;
                }
            }

            return null;
        }));
    }

    // def limit($n; f): if $n > 0 then label $out | foreach f as $item (0; . + 1; $item, if . >= $n then break $out else empty end) elif $n == 0 then empty else error(…) end;
    private static Signal limit(Interpreter interpreter, Env env, Node[] arguments, JqValue input, Path path, Emit emit) {
        return interpreter.forEach(arguments[0], env, input, null, (n, unused) -> {
            if (ValueOps.compare(n, ZERO) <= 0) {
                return ValueOps.areEqual(n, ZERO) ? null : Interpreter.fail(Errors.negativeLimit());
            }
            Signal enough = Signal.breakTo(interpreter.newLabel());
            int[] count = {0};
            Signal signal = interpreter.eval(arguments[1], env, input, path, (value, at) -> {
                Signal stop = interpreter.output(emit, value, at);
                if (stop != null) {
                    return stop;
                }
                return ValueOps.compare(JqValue.fromNumber(++count[0]), n) >= 0 ? enough : null;
            });
            return signal == enough ? null : signal;
        });
    }

    // def skip($n; f): if $n > 0 then foreach f as $item ($n; . - 1; if . < 0 then $item else empty end) elif $n == 0 then f else error(…) end;
    private static Signal skip(Interpreter interpreter, Env env, Node[] arguments, JqValue input, Path path, Emit emit) {
        return interpreter.forEach(arguments[0], env, input, null, (n, unused) -> {
            if (ValueOps.compare(n, ZERO) <= 0) {
                return ValueOps.areEqual(n, ZERO)
                    ? interpreter.eval(arguments[1], env, input, path, emit)
                    : Interpreter.fail(Errors.negativeSkip());
            }

            JqValue[] count = {n};
            return interpreter.eval(arguments[1], env, input, path, (value, at) -> {
                Result next = ValueOps.trySubtract(count[0], ONE);
                if (next instanceof Result.Err err) {
                    return Signal.error(err.error());
                }
                count[0] = (JqValue) next;
                return ValueOps.compare(count[0], ZERO) < 0 ? interpreter.output(emit, value, at) : null;
            });
        });
    }

    private record Work(boolean emit, JqValue value, Path at) {
    }

    private record Update(JqValue value, Path at) {
    }

    // while (emitWhenTrue): for each output of cond, if true: the value, then the same for each output of update.
    // until: for each output of cond, if true: the value; else the same for each output of update.
    // When cond and update yield at most one output, a plain loop; otherwise a stack of pending
    // work replaces the recursion, the outputs of cond and update collected at each step.
    private static Signal iterate(Interpreter interpreter, Env env, Node cond, Node update, JqValue input, Path path, Emit emit, boolean emitWhenTrue) {
        if (cond.atMostOne && update.atMostOne) {
            JqValue value = input;
            Path at = path;
            Interpreter.One one = new Interpreter.One();
            while (true) {
                Signal signal = interpreter.evalOne(cond, env, value, null, one);
                if (signal != null || !one.has) {
                    return signal;
                }
                if (one.value.isTruthy()) {
                    // while goes on after the value, until stops at it.
                    Signal stop = interpreter.output(emit, value, at);
                    if (stop != null || !emitWhenTrue) {
                        return stop;
                    }
                } else if (emitWhenTrue) {
                    return null;
                }
                signal = interpreter.evalOne(update, env, value, at, one);
                if (signal != null || !one.has) {
                    return signal;
                }
                value = one.value;
                at = one.at;
            }
        }

        // Work items: emit a value, or iterate from it; the top of the stack is next.
        List<Work> work = new ArrayList<>();
        work.add(new Work(false, input, path));
        while (!work.isEmpty()) {
            Work item = work.remove(work.size() - 1);
            JqValue value = item.value;
            Path at = item.at;
            if (item.emit) {
                Signal stop = interpreter.output(emit, value, at);
                if (stop != null) {
                    return stop;
                }
                continue;
            }

            List<Boolean> tests = new ArrayList<>();
            Signal signal = interpreter.eval(cond, env, value, null, (test, unused) -> {
                tests.add(test.isTruthy());
                return null;
            });

            // The outputs of update, computed once: each test that continues goes on with them.
            List<Update> updates = new ArrayList<>();
            if (signal == null && tests.contains(emitWhenTrue)) {
                signal = interpreter.eval(update, env, value, at, (next, nextAt) -> {
                    updates.add(new Update(next, nextAt));
                    return null;
                });
            }

            if (signal != null) {
                return signal;
            }

            // For each test in order: while: if true, the value then each update; until: if true,
            // the value, else each update. Pushed last first, so that they run in order.
            for (int t = tests.size() - 1; t >= 0; t--) {
                if (tests.get(t) == emitWhenTrue) {
                    for (int u = updates.size() - 1; u >= 0; u--) {
                        work.add(new Work(false, updates.get(u).value, updates.get(u).at));
                    }
                }

                if (tests.get(t)) {
                    work.add(new Work(true, value, at));
                }
            }
        }

        return null;
    }

    // def repeat(f): def _repeat: f, _repeat; _repeat; — the outputs of f on the same input, again and again.
    private static Signal repeat(Interpreter interpreter, Env env, Node[] arguments, JqValue input, Path path, Emit emit) {
        while (true) {
            Signal signal = interpreter.eval(arguments[0], env, input, path, emit);
            if (signal != null) {
                return signal;
            }
        }
    }
}
