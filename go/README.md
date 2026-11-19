# jqlite for Go

The Go port of [jqlite](../spec/jqlite.md), the profile of the jq 1.8 language, carried over file by
file from the .NET reference (`dotnet/JQLite/Core`, see [`docs/porting.md`](../docs/porting.md)).
Standard library only; Go 1.26.

Module `github.com/devlooped/jqlite/go`: the package `jqlite` at the module root is the host
boundary, the engine is in `internal/`.

## Use

```go
import "github.com/devlooped/jqlite/go"

expression, err := jqlite.Parse(`.items[] | select(.price > $min) | {name, price}`)
if err != nil {
	return err // a *jqlite.ParseError, with Line and Column
}

input, _ := jqlite.ParseJSON(`{"items":[{"name":"a","price":5},{"name":"b","price":15}]}`)
min := map[string]jqlite.Value{"min": jqlite.Number(10)}

// All the results at once…
results, err := expression.Evaluate(input, min) // err: *jqlite.Error, *jqlite.HaltError
for _, r := range results {
	fmt.Println(jqlite.ToJSON(r)) // {"name":"b","price":15}
}

// …or as they come: break stops the evaluation.
for r, err := range expression.Results(input, min) {
	if err != nil {
		return err
	}
	fmt.Println(jqlite.ToJSON(r))
}
```

- `Parse(expression)`, `ParseWithResolver(expression, resolver)`, `ParseWithOptions(expression, options)`
  give an `*Expression`: parsed and bound once, immutable, safe to evaluate from many goroutines at
  once. `MustParse` panics on an error. `Evaluate(expression, input, variables)` parses and evaluates.
- `Expression.Evaluate` gives all results; `EvaluateFunc` pushes each result to a callback that
  gives false to stop; `Results` is an `iter.Seq2[Value, error]` for `range`; `Lines` evaluates each
  value of a JSON Lines stream alone.
- Host variables: `map[string]Value`, names without `$` (`"root"` is `$root`), validated before
  evaluation (`*VariableError`; `ENV` is not allowed).
- `Options`: `Resolver` (modules: `FileResolver`, `FSResolver` over an `fs.FS` such as an
  `embed.FS`), `Environment` (`$ENV` as `NAME=value` entries in order; nil is the process
  environment), `Now` and `Location` (`now` and the local zone of `localtime`, `strflocaltime`, `%z`,
  `%Z`), `MaxDepth` (0 is 1000), `Debug` and `Stderr` handlers. The zero `Options` are the defaults.
- Errors are host errors only here: `*ParseError` (`Line`, `Column`, `Module`), `*Error` (the error
  `Value`), `*HaltError` (`Value`, `ExitCode`); `halt` ends normally.
- Values: Go has no ordered JSON value, so `Value` is the engine's own (immutable, keys in insertion
  order), with `ParseJSON`/`ToJSON` (canonical text, §5.1), `FromAny`/`ToAny` (`map[string]any` has
  no order: `FromAny` sorts its keys, `ToAny` loses the order), and `Null`, `Bool`, `Number`,
  `String`, `NewArray`, `NewObject`. Strings are UTF-8; ill-formed input becomes U+FFFD (§4.1).

## Build, test, benchmark

```
cd go
go generate ./...   # copies spec/prelude.jq next to internal/binding/prelude.go for //go:embed
go build ./...
go vet ./...
gofmt -l .          # prints nothing
go test ./...
```

`go test ./...` runs:

- the corpus of `spec/tests/` (read from there, not copied) twice: through the public API
  (`conformance_test.go`, `TestCorpusCase`) and through the core with every prelude builtin bound
  to its definition (`TestCorpusCaseWithoutNativeOverrides`), with `localtime.test` at UTC+02:00 and
  `modules.test` over `spec/tests/modules/`;
- the unit tests of every layer, ported from `dotnet/Tests/Core/`: values, lexer, parser, parser
  and resolver limits, the corpus parsed and bound, the resolver (bound programs printed as hops
  and slots, single functions), the catalogue against §9 of the spec, modules, the interpreter,
  native `_modify`/`_assign` against the prelude, the native table, the depth limit and the
  stack (`internal/runtime/stack_guard_test.go`, below);
- the regex engine (`internal/builtins/regex_engine_test.go`): random patterns against a plain
  backtracking matcher of the same programs, patterns and inputs that need linear time, the memory
  of a search, the largest patterns within the limits of spec §11 (the translator checks them, the
  engine refuses nothing);
- the host boundary: options, errors, streaming, variables, values, depth on a fresh goroutine,
  one expression from 32 goroutines at once, and the portability rules of `AGENTS.md` on the core
  (`portability_test.go`: the core imports only `math`, `strings`, `unicode/utf8` and `slices`,
  every other package by file with its reason; no `recover`, goroutines or channels but in
  `runtime/stack_guard.go`; `panic` only for the programming errors it lists).

`internal/corpus` (the reader of the `.test` files and the structural comparison of spec §14) and
`internal/corpus/modules` (the module sources of the tests, over a directory or a map) are test
support, not part of the engine: they use the host's file system and `regexp` freely.

## Recursion and the stack

`MaxDepth` is the only recursion limit a program meets (spec §11): up to it, an evaluation never
fails because of the Go stack. A goroutine's stack grows, but only to `runtime/debug.SetMaxStack`
(1 GB on 64-bit systems), and going past it is a fatal error that no `recover` catches. One call
level takes about 1.6 KB of stack for a single call (`EvalOne`) and 2.5 KB for a call that is not
single (`Eval` through callbacks), measured at 50,000 levels (`TestTheStackOfACallLevel`); a
recursive call written inside a deeply nested body costs that nesting at every level, so no fixed
`MaxDepth` would be safe for every program. So the interpreter does what the reference does with
threads (`runtime/stack_guard.go`): it counts the `Eval` and `EvalOne` frames active on the current
goroutine, and at 10,000 the step continues on a new goroutine while the current one waits (a panic
of the step, such as one of the host's callback, comes back to the caller's goroutine). Each
goroutine's stack stays at a few MB whatever `MaxDepth` is; what remains is memory: about 1.6 to
2.5 KB per call level in all, 160–250 MB at `MaxDepth` 100,000. The tests run every recursion of
the reference's `StackGuardTests` at `MaxDepth` 1,000, 5,000 and 20,000, each with the default hop
and with a hop every 50 frames, a recursion 1,000 calls deep through 900 levels of nesting
(900,000 frames), and 100,000 levels (`TestAHundredThousandLevels`, 0.2 s), the largest tested.

`TestTheEmbeddedPreludeIsTheSpecs` fails when `internal/binding/prelude.jq` differs from
`spec/prelude.jq`: run `go generate ./...` again. The race detector (`go test -race ./...`) needs
cgo and a C compiler.

The benchmark runner of [`bench/README.md`](../bench/README.md) is `cmd/bench`, on the core engine:

```
go build -o bin/jqlite-bench.exe ./cmd/bench
bin/jqlite-bench.exe <program.jq> <input.json> <take> <warmup-ms> <measure-ms> <output-file>
pwsh ../bench/run.ps1 -Impl dotnet,go
```

## Differences from the reference's structure

Each file of `dotnet/JQLite/Core` is a file of the same name in snake case, in the package of its
layer (`Values/ValueOps.cs` is `internal/values/value_ops.go`), with the same type and function
names. Where Go asks for something else:

| Where | Difference | Why |
| --- | --- | --- |
| everywhere | A static class is a package variable of an empty struct type: `ValueOps.Compare(a, b)`, `Errors.CannotIndex(t, k)`, `CodePoints.Length(s)` | calls read as in C#, and the functions of `CodePoints` and `ValueOps` (both have `Compare`) do not collide in one package |
| everywhere | `TryX(…, out result, out error)` is `TryX(…) (result, err Value, ok bool)`; `out` parameters are results; optional strings are a flag (`HasFormat`, `HasAlias`, `HasVariable`) or `""` (no module, the main program as `from`) | Go has no `out` and no nullable strings |
| `binding/natives.go` | The native catalogue (`Builtins/Natives.cs`) is in the binding package | Go forbids import cycles: the resolver binds to the catalogue, and the builtins import the binding and runtime packages |
| `runtime/native_table.go`, `builtins/register.go` | The table and the two native types are in the runtime package; `builtins` fills them in its `init` (the static constructor of `NativeTable`), and registers `Formats.TryApply` too, which the interpreter calls for `@name` and templates | the same import cycle (guide §1.1) |
| `runtime/stack_guard.go` | `HasStack` counts the `Eval`/`EvalOne` frames of the current goroutine (10,000) instead of probing the stack; `OnFreshStack` runs the step on a new goroutine | Go cannot ask how much stack is left; see Recursion and the stack |
| `values/code_points.go` | `Hex` and `Decimal` write numbers for messages | the core uses no `fmt` or `strconv` beyond `value_text.go` |
| `values/` | Strings are UTF-8: `CodePoints.Compare` is a byte comparison, `Advance` maps code points to bytes, `Utf8Length` is gone; `FromString` replaces each maximal ill-formed subpart with U+FFFD (`Utf8.DecodeReplacing`) | Go strings are bytes; Go's own decoders replace per byte or per run (guide §3.2) |
| `values/value_text.go` | The reader combines `\u` surrogate pairs itself and sanitizes ill-formed UTF-8; reader error columns count code points | the reference reads UTF-16 text and lets `FromString` fix lone surrogates; the two counts differ only for characters above U+FFFF |
| `values/jq_value.go` | `Value` is a struct of a kind, a `float64` and an `any` (32 bytes, not 16) | guide §2.1 |
| `values/jq_object.go`, `values/jq_string.go` | `SortedKeys` and the text of a grown string are cached in an `atomic.Pointer` | the reference's benign race is a data race in Go's memory model |
| `syntax/parser.go` | Partial nodes of `parsePipe`, `if` and `reduce`/`foreach` get their hole filled in place | Go has no `with` copies; a node is not changed after parsing |
| `syntax/lexer.go` | The source is sanitized first (ill-formed UTF-8 to U+FFFD) | as the reference's lone surrogates |
| `binding/prelude.go` | `//go:embed prelude.jq`, a copy made by `go generate` (`copy_prelude.go`) | embed sees only files of the package's directory |
| `builtins/regex_*.go` | The engine reads UTF-8 and its positions are byte offsets, which `RegexNatives` converts to code points; a match that starts with a fixed code point skips ahead with `strings.Index` on its UTF-8 bytes, whatever the code point; `RegexSyntax.Op` is `RegexOp` (`OpChar`…); the program cache counts its entries in an `atomic.Int64` | Go strings are bytes (guide §3.3); `sync.Map` has no count |
| `builtins/math_extra.go` | Each product that is added is converted with `float64(…)`; the polynomials go through a `horner` helper | Go may fuse `x*y + z` (it does on arm64); an explicit conversion rounds first |
| `builtins/assign_natives.go` | A draft keeps its children in attachment order | the reference walks dictionaries; Go map order is random |
| `builtins/strftime.go` | Formatting appends to a `[]byte` | the reference upper-cases a `StringBuilder` in place |
| tests | `OnSmallStack` cases run on a new goroutine; the `"ab" * 1e9` case is `"ab" * 1.1e9`; allocation bounds are doubled | goroutine stacks start small and grow; Go strings reach the spec's 2^31 − 1 bytes, where .NET's string limit stops sooner; values are twice as large |
| host | `Options` with `Now`/`Location` instead of `TimeProvider`, `Environment` as `NAME=value` entries, `FSResolver` instead of `JqResourceResolver`, `Results`/`Lines` as `iter.Seq2` instead of `IAsyncEnumerable` | the idioms of Go |

Not ported: the .NET-only tests of `JsonElement` conversion, `IObservable` and the async JSON Lines
helpers (`JqObservableTests`, `JqJsonlStreamingTests`); their Go counterparts are the tests of
`value.go`, `Results` and `Lines`.
