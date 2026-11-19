# AGENTS.md

## Goal

JQLite is the .NET reference implementation of **jqlite**, the profile of the [jq](https://jqlang.org)
language that is to be ported 1:1 to JavaScript/TypeScript, Python, Java, Go and Rust. Two rules
follow from that goal:

- the code must read the same in every language (no host feature in the semantics);
- jqlite behaves as jq 1.8, except for the differences listed in `spec/jqlite.md` §13.

How the engine is built, and how a port carries it over: [`docs/porting.md`](docs/porting.md).

## Source of truth

| Path | What |
| --- | --- |
| `spec/jqlite.md` | the language: grammar, values, semantics, builtin catalogue (§9), errors (§10), limits (§11), host interface (§12), differences from jq (§13) |
| `spec/prelude.jq` | the builtins written in jqlite (the P entries of §9), embedded unchanged |
| `spec/tests/*.test` | the conformance corpus (format in `spec/tests/README.md`), shared by every port |

To change behaviour: **spec first, then a corpus case, then the code**.

1. Edit `spec/jqlite.md` (and `spec/prelude.jq` if a prelude definition changes).
2. Add or fix the case in `spec/tests/*.test`. Expected outputs are what jq 1.8.2 prints, unless
   §13 lists the difference; then the case states jqlite's output and §13 says why.
3. Change the code until the corpus passes.
4. Carry the change to every port (`docs/porting.md` §5): each runs the same corpus, so a port that lags fails the new cases.

Probing jq byte-safely: write the program to a UTF-8 file and run `jq -f prog.jq input.json`, read
stdout as UTF-8 bytes, never pass non-ASCII text on the command line or through the console code
page. Run from an empty directory (jq also looks for modules in the current directory); for module
cases use `-L spec/tests/modules`.

## Layers

`dotnet/JQLite/Core` is the engine; each folder is one layer and uses only the layers before it, except
the two links of `Builtins` named in 5:

1. `Values`: `JqValue`, arrays, objects, `ValueOps` (equality, ordering, arithmetic),
   `ValueText` (JSON writer, number text, JSON reader), `Errors` (every run-time message).
2. `Syntax`: lexer, AST, parser. Never throws: returns the program or one `SyntaxError`.
3. `Binding`: the resolver (names to frame hops and slots, calls to function indexes), the bound
   program, the prelude, the optional modules group (`Modules.cs`).
4. `Runtime`: the push-based interpreter (`Eval`/`EvalOne`, `Emit`, `Signal`), environments, paths,
   `StackGuard`, the host interface (`Evaluation.cs`).
5. `Builtins`: the native catalogue (`Natives.cs`, plain data, used by `Binding`) and the native
   implementations, registered in `NativeTable` and called by the interpreter.

The public .NET API and the `JsonElement` adapter sit outside `Core` (see Host boundary).

## Portability rules

Code under `dotnet/JQLite/Core` must not use:

- `System.Text.Json`, or any other host JSON library;
- `yield return`, LINQ lazy operators or other iterators to produce outputs (outputs are pushed);
- reflection;
- exceptions as control flow: errors are values (`TryX(…, out result, out error)`), and `Error`,
  `Break`, `Halt`, `Stop` are returned signals;
- host regex, date or culture APIs, except in the files that exist for that purpose:
  `Builtins/RegexEngine.cs` (the regex engine), `Values/ValueText.cs` (invariant-culture number
  text: shortest digits and decimal parsing), `Runtime/StackGuard.cs` (the host stack),
  `Binding/Prelude.cs` (reading the embedded prelude);
- recursion whose depth comes from data or from long sequences: use loops and explicit stacks
  (`docs/porting.md` §2.10). The only recursion limit a program meets is `MaxDepth` (§11).

Values, the bound program and the prelude are immutable and shared between threads; one
`Interpreter` is one evaluation. The library references no run-time package.

## Tests

- `dnx --yes retest` runs everything; `dotnet format whitespace` and `dotnet format style`
  (both with `--verify-no-changes --exclude ~/.nuget`) must pass.
- `dotnet/Tests/ConformanceTests.cs` runs every case of `spec/tests/*.test` twice: through the public
  API (`Jq.Parse` with `JqOptions`), and through the core without the native overrides of prelude
  builtins. Both use an empty environment, a zone fixed at UTC+02:00 (for `localtime.test`) and the
  modules of `spec/tests/modules/` (for `modules.test`).
- `dotnet/Tests/Core/<Layer>/` holds the unit tests of each layer: what the corpus cannot show
  (positions, limits on small host stacks, binding printed as hops and slots, value sharing).
  `BuiltinCatalogueTests` reads the N/P lists of §9 from the spec and checks the catalogue and the
  prelude against them.
- `dotnet/Tests/PortabilityTests.cs` enforces the portability rules above, with a per-file allow-list
  for each exception; `JqConcurrencyTests` and `JqDepthTests` cover thread safety and `MaxDepth`
  through the public API.
- `dotnet/Benchmarks` (BenchmarkDotNet) compares JQLite with `Devlooped.JQ` (the jq executable) on
  the WhatsApp `Message.jq` filter, with and without a cached `JqExpression`, and measures nine
  programs whose cost must stay linear or lazy (`ScalingBenchmarks`). Run one with
  `dotnet run -c Release --project dotnet/Benchmarks -- --filter *Scaling*`.

## Ports

`go/`, `java/`, `python/`, `rust/` and `typescript/` hold the ports of `dotnet/JQLite/Core`, file by file, each with its own host boundary, tests and `README.md` (its differences from the reference, with their reasons); `docs/porting.md` §5 has their test commands. The layers and the portability rules above hold in each, and each has a portability test of its own. Do not fix a port's behaviour in the port alone: when a port and the reference disagree, the spec decides, and the corpus gets the case.

`bench/` is the cross-language benchmark (`bench/README.md`): `pwsh bench/run.ps1` builds every implementation, checks that their outputs equal `bench/cases/*.expected` and writes the times relative to .NET. The .NET runner is `dotnet/Benchmarks/CrossBench.cs` (`Benchmarks cross …`).

## Where a change goes

| Change | Files |
| --- | --- |
| a builtin defined in jqlite | `spec/prelude.jq` and its row in §9 (marked P) |
| a native builtin | its row in §9 (marked N); `Core/Builtins/Natives.cs` in spec order; the implementation in the family's `*Natives.cs`, registered in its `Register()` |
| a faster native for a prelude builtin | `Natives.cs` override list (`OverridesPrelude`); same results, errors and paths; the corpus runs both ways |
| a run-time error message | `Core/Values/Errors.cs` and §10 (or the errors column of §9) |
| a syntax or binding error | `SyntaxMessages` (`Core/Syntax/SyntaxError.cs`) or `BindingMessages` (`Core/Binding/Resolver.cs`); a fixed text also goes in §10.2 |
| a limit | §11 and the constant in the layer that enforces it |
| a host-specific mechanism | only the files named in Portability rules; say in its header what a port does instead |

## Host boundary

Only `dotnet/JQLite` outside `Core` knows .NET's JSON types:

- `Json/JsonElementAdapter.cs` converts `JsonElement` to `JqValue` and back; no other file converts
  values between them. `Host/ResolverSource.cs` adapts a `JqResolver` to the core's `IModuleSource`
  (a resolver's `JqException` or I/O exception becomes the binding error), and
  `Host/TimeProviderClock.cs` adapts a `TimeProvider` to `IClock` (no zone abbreviations: `%Z`
  prints the offset).
- `Jq.Parse(expression, options)` returns a `JqExpression` (sealed, parse once, evaluate many
  times, thread-safe); `Jq.Parse(expression)` uses the default options and `Jq.Parse(expression,
  resolver)` the defaults with that `Resolver`. No parameter is optional or nullable. `Jq.Evaluate` / `JqExpression.Evaluate` return all outputs (buffered);
  the overload with `Func<JsonElement, bool> onResult` streams them and stops when it returns
  false. The JSONL `Jq.EvaluateAsync` helpers take the expression as a string or a
  `JqExpression`; each value is evaluated alone (jqlite has no `input`/`inputs`, spec §13).
- `JqOptions`: `Resolver` (for `import`/`include`/`modulemeta`), `Environment` (`$ENV`; null is
  the process environment), `TimeProvider` (`now` and the local zone), `MaxDepth` (default 1000),
  `Debug` and `Stderr` (the values of `debug` and `stderr`; null drops them).
- `JqResolver`: `GetCanonicalPath(path, JqModuleKind, metadata, fromPath)` returns the module's
  identity or null, `Open(canonicalPath)` reads it. `JqFileResolver` (files, relative to the
  importing module) and `JqResourceResolver` (embedded resources) are the two built in.
- Exceptions exist only here: `JqException` (an uncaught jqlite error, with its `Value`),
  `JqParseException` (`Line`, `Column`) and `JqHaltException` (`halt_error`, with `ExitCode`;
  `halt` ends normally).

## External variable bindings

`Jq.Evaluate(expression, input, variables)` and `JqExpression.Evaluate(input, variables)` accept
`IReadOnlyDictionary<string, JsonElement>?` (spec §7.5, §12):

- Keys are identifiers without `$` (`[A-Za-z_][A-Za-z0-9_]*`); `"root"` binds `$root`. `ENV` is not
  allowed (`$ENV` is the environment). Invalid keys are rejected before evaluation.
- Values are converted to jqlite values once per evaluation, so the caller's documents may be
  disposed afterwards.
- A `$name` with no lexical binding (and not `$ENV` or `$__loc__`) is a host variable, looked up when evaluated;
  if the host did not supply it, the error is `$name is not defined` (a run-time error, catchable
  with `try`). The same holds inside modules.
- `$alias::name` must be bound by a data import; otherwise it is a binding error.
- `as $name` bindings and `$name` parameters shadow host variables of the same name.