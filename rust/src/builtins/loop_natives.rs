//! The natives marked *loop* in §9.4: generators written as loops, so their iterations do not
//! consume the call depth (§11). Each follows the jqlite definition the spec gives it, including
//! how it compares a non-numeric count, and passes the paths of its argument's outputs.

use std::borrow::Cow;
use std::cmp::Ordering;

use crate::binding::bound::Node;
use crate::runtime::env::EnvRef;
use crate::runtime::interpreter::{Emit, Input, Interpreter, fail, output};
use crate::runtime::path::Path;
use crate::runtime::signal::{Flow, Signal};
use crate::values::{JqValue, errors, value_ops};

use super::native_table::Registry;

const ZERO: JqValue = JqValue::Number(0.0);
const ONE: JqValue = JqValue::Number(1.0);

pub fn register(table: &mut Registry) {
    table.closure("range", 2, range);
    table.closure("limit", 2, limit);
    table.closure("skip", 2, skip);
    table
        .closure("while", 2, |interpreter, env, arguments, input, path, emit| iterate(interpreter, env, &arguments[0], &arguments[1], input, path, emit, true));
    table.closure("until", 2, |interpreter, env, arguments, input, path, emit| {
        iterate(interpreter, env, &arguments[0], &arguments[1], input, path, emit, false)
    });
    table.closure("repeat", 1, repeat);
}

// range($from; $upto): $from outermost (the exception of §9.4).
fn range<'p>(interpreter: &Interpreter<'p>, env: &EnvRef<'p>, arguments: &'p [Node], input: Input<'_>, path: Path, emit: Emit<'_>) -> Flow {
    let at = Path::of(&path);
    interpreter.for_each(&arguments[0], env, Cow::Borrowed(&input), Path::Off, &mut |from, _| {
        interpreter.for_each(&arguments[1], env, Cow::Borrowed(&input), Path::Off, &mut |upto, _| {
            let (JqValue::Number(from), JqValue::Number(end)) = (&from, &upto) else {
                return Err(fail(errors::range_not_numeric()));
            };
            let mut value = *from;
            while value < *end {
                output(emit, JqValue::Number(value), at.clone())?;
                value += 1.0;
            }
            Ok(())
        })
    })
}

// def limit($n; f): if $n > 0 then label $out | foreach f as $item (0; . + 1; $item, if . >= $n then break $out else empty end) elif $n == 0 then empty else error(…) end;
fn limit<'p>(interpreter: &Interpreter<'p>, env: &EnvRef<'p>, arguments: &'p [Node], input: Input<'_>, path: Path, emit: Emit<'_>) -> Flow {
    interpreter.for_each(&arguments[0], env, Cow::Borrowed(&input), Path::Off, &mut |n, _| {
        if value_ops::compare(&n, &ZERO) != Ordering::Greater {
            return if value_ops::are_equal(&n, &ZERO) { Ok(()) } else { Err(fail(errors::negative_limit())) };
        }

        let enough = interpreter.new_label();
        let mut count = 0.0;
        let result = interpreter.eval(&arguments[1], env, Cow::Borrowed(&input), path.clone(), &mut |value, at| {
            output(emit, value, at)?;
            count += 1.0;
            if value_ops::compare(&JqValue::Number(count), &n) != Ordering::Less { Err(Signal::break_to(enough)) } else { Ok(()) }
        });
        match result {
            Err(signal) if signal.is_break_to(enough) => Ok(()),
            other => other,
        }
    })
}

// def skip($n; f): if $n > 0 then foreach f as $item ($n; . - 1; if . < 0 then $item else empty end) elif $n == 0 then f else error(…) end;
fn skip<'p>(interpreter: &Interpreter<'p>, env: &EnvRef<'p>, arguments: &'p [Node], input: Input<'_>, path: Path, emit: Emit<'_>) -> Flow {
    interpreter.for_each(&arguments[0], env, Cow::Borrowed(&input), Path::Off, &mut |n, _| {
        if value_ops::compare(&n, &ZERO) != Ordering::Greater {
            return if value_ops::are_equal(&n, &ZERO) {
                interpreter.eval(&arguments[1], env, Cow::Borrowed(&input), path.clone(), emit)
            } else {
                Err(fail(errors::negative_skip()))
            };
        }

        let mut count = n;
        interpreter.eval(&arguments[1], env, Cow::Borrowed(&input), path.clone(), &mut |value, at| {
            count = value_ops::try_subtract(std::mem::take(&mut count), ONE)?;
            if value_ops::compare(&count, &ZERO) == Ordering::Less { output(emit, value, at) } else { Ok(()) }
        })
    })
}

// def while(cond; update): def _while: if cond then ., (update | _while) else empty end; _while;
// def until(cond; update): def _until: if cond then . else (update | _until) end; _until;
//
// while (emit_when_true): for each output of cond, if true: the value, then the same for each output of update.
// until: for each output of cond, if true: the value; else the same for each output of update.
// When cond and update yield at most one output, a plain loop; otherwise a stack of pending
// work replaces the recursion, the outputs of cond and update collected at each step.
#[allow(clippy::too_many_arguments)]
fn iterate<'p>(
    interpreter: &Interpreter<'p>,
    env: &EnvRef<'p>,
    cond: &'p Node,
    update: &'p Node,
    input: Input<'_>,
    path: Path,
    emit: Emit<'_>,
    emit_when_true: bool,
) -> Flow {
    if cond.at_most_one && update.at_most_one {
        let mut value = input;
        let mut at = path;
        loop {
            let Some((test, _)) = interpreter.eval_one(cond, env, Cow::Borrowed(&value), Path::Off)? else {
                return Ok(());
            };
            if test.is_truthy() {
                // while goes on after the value, until stops at it.
                if !emit_when_true {
                    return output(emit, value.into_owned(), at);
                }
                output(emit, JqValue::clone(&value), at.clone())?;
            } else if emit_when_true {
                return Ok(());
            }

            let Some((next, next_at)) = interpreter.eval_one(update, env, value, at)? else {
                return Ok(());
            };
            value = Cow::Owned(next);
            at = next_at;
        }
    }

    // Work items: emit a value, or iterate from it; the top of the stack is next.
    let mut work: Vec<(bool, JqValue, Path)> = vec![(false, input.into_owned(), path)];
    while let Some((emit_it, value, at)) = work.pop() {
        if emit_it {
            output(emit, value, at)?;
            continue;
        }

        let mut tests = Vec::new();
        interpreter.eval(cond, env, Cow::Borrowed(&value), Path::Off, &mut |test, _| {
            tests.push(test.is_truthy());
            Ok(())
        })?;
        // The outputs of update, computed once: each test that continues goes on with them.
        let mut updates = Vec::new();
        if tests.contains(&emit_when_true) {
            interpreter.eval(update, env, Cow::Borrowed(&value), at.clone(), &mut |next, next_at| {
                updates.push((next, next_at));
                Ok(())
            })?;
        }

        // For each test in order: while: if true, the value then each update; until: if true, the
        // value, else each update. Pushed last first, so that they run in order.
        for test in tests.iter().rev() {
            if *test == emit_when_true {
                for (next, next_at) in updates.iter().rev() {
                    work.push((false, next.clone(), next_at.clone()));
                }
            }
            if *test {
                work.push((true, value.clone(), at.clone()));
            }
        }
    }

    Ok(())
}

// def repeat(f): def _repeat: f, _repeat; _repeat; — the outputs of f on the same input, again and again.
fn repeat<'p>(interpreter: &Interpreter<'p>, env: &EnvRef<'p>, arguments: &'p [Node], input: Input<'_>, path: Path, emit: Emit<'_>) -> Flow {
    loop {
        interpreter.eval(&arguments[0], env, Cow::Borrowed(&input), path.clone(), emit)?;
    }
}
