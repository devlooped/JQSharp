package builtins

import (
	"math"
	"slices"
	"strings"
	"sync"

	"github.com/devlooped/jqlite/go/internal/binding"
	"github.com/devlooped/jqlite/go/internal/runtime"
	"github.com/devlooped/jqlite/go/internal/values"
)

type coreNatives struct{}

// CoreNatives are §9.2 Core and §9.3 Types: the natives of the evaluation model itself and of its
// host (§12).
var CoreNatives coreNatives

// builtinsList is builtins/0: every non-internal builtin, natives and prelude, sorted by code point.
var builtinsList = sync.OnceValue(listBuiltins)

func (coreNatives) Register() {
	addC("empty", 0, func(*interpreter, *env, []node, value, *path, emit) *signal { return nil })
	add("error", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) { return fail(input) })
	add("type", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) {
		return ok(values.FromString(input.TypeName()))
	})
	add("length", 0, length)
	add("utf8bytelength", 0, utf8ByteLength)
	add("builtins", 0, func(*interpreter, value, []value) (value, value, bool) { return ok(builtinsList()) })
	addC("halt", 0, func(*interpreter, *env, []node, value, *path, emit) *signal {
		return runtime.HaltSignal(values.Null, false, 0)
	})
	addC("halt_error", 1, haltError)
	addC("last", 1, last)
	add("debug", 0, func(in *interpreter, input value, _ []value) (value, value, bool) {
		if in.Debug != nil {
			in.Debug(input)
		}
		return ok(input)
	})
	add("stderr", 0, func(in *interpreter, input value, _ []value) (value, value, bool) {
		if in.Stderr != nil {
			in.Stderr(input)
		}
		return ok(input)
	})
	add("infinite", 0, func(*interpreter, value, []value) (value, value, bool) { return ok(values.FromNumber(math.Inf(1))) })
	add("nan", 0, func(*interpreter, value, []value) (value, value, bool) { return ok(values.FromNumber(math.NaN())) })
	add("isinfinite", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) {
		return ok(values.FromBool(input.Kind() == values.KindNumber && math.IsInf(input.AsNumber(), 0)))
	})
	add("isnan", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) {
		return ok(values.FromBool(input.Kind() == values.KindNumber && math.IsNaN(input.AsNumber())))
	})
	add("isnormal", 0, func(_ *interpreter, input value, _ []value) (value, value, bool) {
		return ok(values.FromBool(input.Kind() == values.KindNumber && isNormal(input.AsNumber())))
	})
}

// isNormal: finite, not zero and not subnormal.
func isNormal(x float64) bool {
	x = math.Abs(x)
	return x >= 0x1p-1022 && !math.IsInf(x, 0) && !math.IsNaN(x)
}

func length(_ *interpreter, input value, _ []value) (value, value, bool) {
	switch input.Kind() {
	case values.KindNull:
		return ok(values.FromNumber(0))
	case values.KindNumber:
		return ok(values.FromNumber(math.Abs(input.AsNumber())))
	case values.KindString:
		return ok(values.FromNumber(float64(values.CodePoints.Length(input.AsString()))))
	case values.KindArray:
		return ok(values.FromNumber(float64(input.AsArray().Count())))
	case values.KindObject:
		return ok(values.FromNumber(float64(input.AsObject().Count())))
	default:
		return fail(values.Errors.HasNoLength(input))
	}
}

func utf8ByteLength(_ *interpreter, input value, _ []value) (value, value, bool) {
	if input.Kind() == values.KindString {
		return ok(values.FromNumber(float64(len(input.AsString()))))
	}
	return fail(values.Errors.OnlyStringsHaveUtf8Length(input))
}

// haltError: halt_error(c) halts with the input and trunc(c) for the first output c.
func haltError(in *interpreter, e *env, arguments []node, input value, _ *path, _ emit) *signal {
	first := runtime.BreakSignal(in.NewLabel())
	code := values.Null
	s := in.Eval(arguments[0], e, input, nil, func(v value, _ *path) *signal {
		code = v
		return first
	})
	if s != first {
		return s
	}
	if code.Kind() != values.KindNumber {
		return runtime.Fail(values.Errors.HaltErrorNumberRequired(input))
	}
	number := math.Trunc(code.AsNumber())
	exit := 0
	if !math.IsNaN(number) {
		exit = int(max(math.MinInt32, min(math.MaxInt32, number)))
	}
	return runtime.HaltSignal(input, true, exit)
}

// last: last(f) is the last output of f, with its path. *loop*
func last(in *interpreter, e *env, arguments []node, input value, p *path, out emit) *signal {
	has := false
	lastValue := values.Null
	var lastAt *path
	s := in.Eval(arguments[0], e, input, p, func(v value, at *path) *signal {
		has = true
		lastValue = v
		lastAt = at
		return nil
	})
	if s != nil {
		return s
	}
	if has {
		return runtime.Output(out, lastValue, lastAt)
	}
	return nil
}

func listBuiltins() value {
	var names []string
	for _, native := range binding.Natives.All {
		if !native.IsInternal() && !native.OverridesPrelude {
			names = append(names, native.String())
		}
	}
	for _, function := range binding.PreludeInstance().Definitions {
		if !strings.HasPrefix(function.Name, "_") {
			names = append(names, function.String())
		}
	}
	slices.SortFunc(names, values.CodePoints.Compare)
	items := make([]value, len(names))
	for i, name := range names {
		items[i] = values.FromString(name)
	}
	return values.FromArray(values.ArrayFromOwned(items))
}
