package builtins

import (
	"github.com/devlooped/jqlite/go/internal/runtime"
	"github.com/devlooped/jqlite/go/internal/values"
)

type loopNatives struct{}

// LoopNatives are the natives marked *loop* in §9.4: generators written as loops, so their
// iterations do not consume the call depth (§11). Each follows the jqlite definition the spec gives
// it, including how it compares a non-numeric count, and passes the paths of its argument's outputs.
var LoopNatives loopNatives

var (
	zero = values.FromNumber(0)
	one  = values.FromNumber(1)
)

func (loopNatives) Register() {
	addC("range", 2, rangeNative)
	addC("limit", 2, limit)
	addC("skip", 2, skip)
	addC("while", 2, func(in *interpreter, e *env, arguments []node, input value, p *path, out emit) *signal {
		return iterate(in, e, arguments[0], arguments[1], input, p, out, true)
	})
	addC("until", 2, func(in *interpreter, e *env, arguments []node, input value, p *path, out emit) *signal {
		return iterate(in, e, arguments[0], arguments[1], input, p, out, false)
	})
	addC("repeat", 1, repeat)
}

// rangeNative: range($from; $upto): $from outermost (the exception of §9.4).
func rangeNative(in *interpreter, e *env, arguments []node, input value, p *path, out emit) *signal {
	at := runtime.PathOf(p)
	return in.ForEach(arguments[0], e, input, nil, func(from value, _ *path) *signal {
		return in.ForEach(arguments[1], e, input, nil, func(upto value, _ *path) *signal {
			if from.Kind() != values.KindNumber || upto.Kind() != values.KindNumber {
				return runtime.Fail(values.Errors.RangeNotNumeric())
			}
			end := upto.AsNumber()
			for v := from.AsNumber(); v < end; v++ {
				if stop := runtime.Output(out, values.FromNumber(v), at); stop != nil {
					return stop
				}
			}
			return nil
		})
	})
}

// limit: def limit($n; f): if $n > 0 then label $out | foreach f as $item (0; . + 1; $item, if .
// >= $n then break $out else empty end) elif $n == 0 then empty else error(…) end;
func limit(in *interpreter, e *env, arguments []node, input value, p *path, out emit) *signal {
	return in.ForEach(arguments[0], e, input, nil, func(n value, _ *path) *signal {
		if values.ValueOps.Compare(n, zero) <= 0 {
			if values.ValueOps.AreEqual(n, zero) {
				return nil
			}
			return runtime.Fail(values.Errors.NegativeLimit())
		}
		enough := runtime.BreakSignal(in.NewLabel())
		count := 0
		s := in.Eval(arguments[1], e, input, p, func(v value, at *path) *signal {
			if stop := runtime.Output(out, v, at); stop != nil {
				return stop
			}
			count++
			if values.ValueOps.Compare(values.FromNumber(float64(count)), n) >= 0 {
				return enough
			}
			return nil
		})
		if s == enough {
			return nil
		}
		return s
	})
}

// skip: def skip($n; f): if $n > 0 then foreach f as $item ($n; . - 1; if . < 0 then $item else
// empty end) elif $n == 0 then f else error(…) end;
func skip(in *interpreter, e *env, arguments []node, input value, p *path, out emit) *signal {
	return in.ForEach(arguments[0], e, input, nil, func(n value, _ *path) *signal {
		if values.ValueOps.Compare(n, zero) <= 0 {
			if values.ValueOps.AreEqual(n, zero) {
				return in.Eval(arguments[1], e, input, p, out)
			}
			return runtime.Fail(values.Errors.NegativeSkip())
		}

		count := n
		return in.Eval(arguments[1], e, input, p, func(v value, at *path) *signal {
			next, err, success := values.ValueOps.TrySubtract(count, one)
			if !success {
				return runtime.ErrorSignal(err)
			}
			count = next
			if values.ValueOps.Compare(count, zero) < 0 {
				return runtime.Output(out, v, at)
			}
			return nil
		})
	})
}

type work struct {
	emit  bool
	value value
	at    *path
}

type update struct {
	value value
	at    *path
}

// iterate is while (emitWhenTrue) and until. while: for each output of cond, if true: the value,
// then the same for each output of update. until: for each output of cond, if true: the value;
// else the same for each output of update. When cond and update yield at most one output, a plain
// loop; otherwise a stack of pending work replaces the recursion, the outputs of cond and update
// collected at each step.
func iterate(in *interpreter, e *env, cond, upd node, input value, p *path, out emit, emitWhenTrue bool) *signal {
	if cond.AtMostOne() && upd.AtMostOne() {
		v := input
		at := p
		for {
			s, has, test, _ := in.EvalOne(cond, e, v, nil)
			if s != nil || !has {
				return s
			}
			if test.IsTruthy() {
				// while goes on after the value, until stops at it.
				stop := runtime.Output(out, v, at)
				if stop != nil || !emitWhenTrue {
					return stop
				}
			} else if emitWhenTrue {
				return nil
			}
			if s, has, v, at = in.EvalOne(upd, e, v, at); s != nil || !has {
				return s
			}
		}
	}

	// Work items: emit a value, or iterate from it; the top of the stack is next.
	stack := []work{{false, input, p}}
	for len(stack) > 0 {
		item := stack[len(stack)-1]
		stack = stack[:len(stack)-1]
		if item.emit {
			if stop := runtime.Output(out, item.value, item.at); stop != nil {
				return stop
			}
			continue
		}

		var tests []bool
		s := in.Eval(cond, e, item.value, nil, func(test value, _ *path) *signal {
			tests = append(tests, test.IsTruthy())
			return nil
		})

		// The outputs of update, computed once: each test that continues goes on with them.
		var updates []update
		continues := false
		for _, test := range tests {
			continues = continues || test == emitWhenTrue
		}
		if s == nil && continues {
			s = in.Eval(upd, e, item.value, item.at, func(next value, nextAt *path) *signal {
				updates = append(updates, update{next, nextAt})
				return nil
			})
		}
		if s != nil {
			return s
		}

		// For each test in order: while: if true, the value then each update; until: if true, the
		// value, else each update. Pushed last first, so that they run in order.
		for t := len(tests) - 1; t >= 0; t-- {
			if tests[t] == emitWhenTrue {
				for u := len(updates) - 1; u >= 0; u-- {
					stack = append(stack, work{false, updates[u].value, updates[u].at})
				}
			}
			if tests[t] {
				stack = append(stack, work{true, item.value, item.at})
			}
		}
	}
	return nil
}

// repeat: def repeat(f): def _repeat: f, _repeat; _repeat; — the outputs of f on the same input,
// again and again.
func repeat(in *interpreter, e *env, arguments []node, input value, p *path, out emit) *signal {
	for {
		if s := in.Eval(arguments[0], e, input, p, out); s != nil {
			return s
		}
	}
}
