using System.Collections.Immutable;
using Devlooped.Core.Binding;
using Devlooped.Core.Runtime;
using Devlooped.Core.Values;
using static Devlooped.Core.Builtins.NativeTable;
using Path = Devlooped.Core.Runtime.Path;

namespace Devlooped.Core.Builtins;

/// <summary>
/// The natives marked *loop* in §9.4: generators written as loops, so their iterations do not
/// consume the call depth (§11). Each follows the jqlite definition the spec gives it, including
/// how it compares a non-numeric count, and passes the paths of its argument's outputs.
/// </summary>
static class LoopNatives
{
    static readonly JqValue zero = JqValue.FromNumber(0);
    static readonly JqValue one = JqValue.FromNumber(1);

    public static void Register()
    {
        Add("range", 2, Range);
        Add("limit", 2, Limit);
        Add("skip", 2, Skip);
        Add("while", 2, While);
        Add("until", 2, Until);
        Add("repeat", 1, Repeat);
    }

    // range($from; $upto): $from outermost (the exception of §9.4).
    static Signal? Range(Interpreter interpreter, Env? env, ImmutableArray<Node> arguments, JqValue input, Path? path, Emit emit)
    {
        var at = Path.Of(path);
        return interpreter.ForEach(arguments[0], env, input, null, (from, _) => interpreter.ForEach(arguments[1], env, input, null, (upto, _) =>
        {
            if (from.Kind != JqKind.Number || upto.Kind != JqKind.Number)
                return Interpreter.Fail(Errors.RangeNotNumeric);
            var end = upto.AsNumber();
            for (var value = from.AsNumber(); value < end; value++)
            {
                if (Interpreter.Output(emit, JqValue.FromNumber(value), at) is { } stop)
                    return stop;
            }

            return null;
        }));
    }

    // def limit($n; f): if $n > 0 then label $out | foreach f as $item (0; . + 1; $item, if . >= $n then break $out else empty end) elif $n == 0 then empty else error(…) end;
    static Signal? Limit(Interpreter interpreter, Env? env, ImmutableArray<Node> arguments, JqValue input, Path? path, Emit emit)
        => interpreter.ForEach(arguments[0], env, input, null, (n, _) =>
        {
            if (ValueOps.Compare(n, zero) <= 0)
                return ValueOps.AreEqual(n, zero) ? null : Interpreter.Fail(Errors.NegativeLimit);
            var enough = Signal.Break(interpreter.NewLabel());
            var count = 0;
            var signal = interpreter.Eval(arguments[1], env, input, path, (value, at) =>
                Interpreter.Output(emit, value, at) ?? (ValueOps.Compare(JqValue.FromNumber(++count), n) >= 0 ? enough : null));
            return ReferenceEquals(signal, enough) ? null : signal;
        });

    // def skip($n; f): if $n > 0 then foreach f as $item ($n; . - 1; if . < 0 then $item else empty end) elif $n == 0 then f else error(…) end;
    static Signal? Skip(Interpreter interpreter, Env? env, ImmutableArray<Node> arguments, JqValue input, Path? path, Emit emit)
        => interpreter.ForEach(arguments[0], env, input, null, (n, _) =>
        {
            if (ValueOps.Compare(n, zero) <= 0)
            {
                return ValueOps.AreEqual(n, zero)
                    ? interpreter.Eval(arguments[1], env, input, path, emit)
                    : Interpreter.Fail(Errors.NegativeSkip);
            }

            var count = n;
            return interpreter.Eval(arguments[1], env, input, path, (value, at) =>
            {
                if (!ValueOps.TrySubtract(count, one, out count, out var error))
                    return Signal.Error(error);
                return ValueOps.Compare(count, zero) < 0 ? Interpreter.Output(emit, value, at) : null;
            });
        });

    // def while(cond; update): def _while: if cond then ., (update | _while) else empty end; _while;
    static Signal? While(Interpreter interpreter, Env? env, ImmutableArray<Node> arguments, JqValue input, Path? path, Emit emit)
        => Iterate(interpreter, env, arguments[0], arguments[1], input, path, emit, emitWhenTrue: true);

    // def until(cond; update): def _until: if cond then . else (update | _until) end; _until;
    static Signal? Until(Interpreter interpreter, Env? env, ImmutableArray<Node> arguments, JqValue input, Path? path, Emit emit)
        => Iterate(interpreter, env, arguments[0], arguments[1], input, path, emit, emitWhenTrue: false);

    // while (emitWhenTrue): for each output of cond, if true: the value, then the same for each output of update.
    // until: for each output of cond, if true: the value; else the same for each output of update.
    // When cond and update yield at most one output, a plain loop; otherwise a stack of pending
    // work replaces the recursion, the outputs of cond and update collected at each step.
    static Signal? Iterate(Interpreter interpreter, Env? env, Node cond, Node update, JqValue input, Path? path, Emit emit, bool emitWhenTrue)
    {
        if (cond.AtMostOne && update.AtMostOne)
        {
            var value = input;
            var at = path;
            while (true)
            {
                var signal = interpreter.EvalOne(cond, env, value, null, out var has, out var test, out _);
                if (signal is not null || !has)
                    return signal;
                if (test.IsTruthy)
                {
                    // while goes on after the value, until stops at it.
                    var stop = Interpreter.Output(emit, value, at);
                    if (stop is not null || !emitWhenTrue)
                        return stop;
                }
                else if (emitWhenTrue)
                {
                    return null;
                }
                signal = interpreter.EvalOne(update, env, value, at, out has, out value, out at);
                if (signal is not null || !has)
                    return signal;
            }
        }

        // Work items: emit a value, or iterate from it; the top of the stack is next.
        var work = new List<(bool Emit, JqValue Value, Path? At)> { (false, input, path) };
        while (work.Count > 0)
        {
            var (emitIt, value, at) = work[^1];
            work.RemoveAt(work.Count - 1);
            if (emitIt)
            {
                if (Interpreter.Output(emit, value, at) is { } stop)
                    return stop;
                continue;
            }

            var tests = new List<bool>();
            var signal = interpreter.Eval(cond, env, value, null, (test, _) =>
            {
                tests.Add(test.IsTruthy);
                return null;
            });

            // The outputs of update, computed once: each test that continues goes on with them.
            var updates = new List<(JqValue Value, Path? At)>();
            if (signal is null && tests.Contains(emitWhenTrue))
            {
                signal = interpreter.Eval(update, env, value, at, (next, nextAt) =>
                {
                    updates.Add((next, nextAt));
                    return null;
                });
            }

            if (signal is not null)
                return signal;

            // For each test in order: while: if true, the value then each update; until: if true,
            // the value, else each update. Pushed last first, so that they run in order.
            for (var t = tests.Count - 1; t >= 0; t--)
            {
                if (tests[t] == emitWhenTrue)
                {
                    for (var u = updates.Count - 1; u >= 0; u--)
                        work.Add((false, updates[u].Value, updates[u].At));
                }

                if (tests[t])
                    work.Add((true, value, at));
            }
        }

        return null;
    }

    // def repeat(f): def _repeat: f, _repeat; _repeat; — the outputs of f on the same input, again and again.
    static Signal? Repeat(Interpreter interpreter, Env? env, ImmutableArray<Node> arguments, JqValue input, Path? path, Emit emit)
    {
        while (true)
        {
            if (interpreter.Eval(arguments[0], env, input, path, emit) is { } signal)
                return signal;
        }
    }
}