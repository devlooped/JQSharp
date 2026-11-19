# jqlite for Java

The Java port of [jqlite](../spec/jqlite.md), the profile of the jq language whose reference
implementation is the C# engine in [`dotnet/JQLite/Core`](../dotnet/JQLite/Core). It follows the
reference file by file, as [`docs/porting.md`](../docs/porting.md) describes, and passes the whole
conformance corpus of [`spec/tests`](../spec/tests) both ways (through the public API, and through
the core without the native overrides of prelude builtins).

Java 21, no run-time dependency (JUnit 5 for the tests only), Maven through its wrapper.

## Build and test

The wrapper downloads Maven 3.9 on first use; it needs `JAVA_HOME` (a JDK 21) or `java` on the
`PATH`.

```pwsh
$env:JAVA_HOME = "$HOME\.jdks\jdk-21.0.13+9"
$env:PATH = "$env:JAVA_HOME\bin;$env:PATH"
.\mvnw.cmd package          # compile (javac -Xlint:all -Werror), test, and build the jars
.\mvnw.cmd test             # the tests only
```

On Linux or macOS use `./mvnw`. `target/jqlite.jar` is the library; the prelude inside it is
`spec/prelude.jq`, copied unchanged by Maven from the shared file (a resource folder of the POM).

The tests read the corpus from `../spec/tests` (not a copy):

| Tests | What |
| --- | --- |
| `ConformanceTests` | every case of `spec/tests/*.test`, twice: through `Jq.parse`/`JqExpression`, and through the core without native overrides; `localtime.test` with a clock at UTC+02:00, `modules.test` over `spec/tests/modules/` |
| `core/values`, `core/syntax`, `core/binding`, `core/runtime` | the unit tests of `dotnet/Tests/Core/<Layer>/`, ported with the code they cover (catalogue against §9, limits on 1 MB and 256 KB threads, bound programs printed as hops and slots, native `_modify`/`_assign` against the prelude) |
| `builtins` | the regex engine tests of `dotnet/Tests/Core/Builtins/`, in the engine's package for its package-private types: random patterns against a plain backtracking matcher of the same programs, linear time (on a 256 KB thread), memory, the largest patterns of spec 11 |
| `Jq*Tests` | the host boundary: options, exceptions, streaming, variables, resolvers, `MaxDepth`, 32 threads on one expression |
| `PortabilityTests` | the portability rules of `AGENTS.md` on the core packages: an allow-list of JDK imports per file, and rules on the code (streams, reflection, regex, dates, culture, process, threads, synchronization, throw, catch) with the allowed exceptions by file |

## Benchmark

`bench.json` builds with `cmd /c .\mvnw.cmd -q -DskipTests package` and runs
`java -jar target/jqlite-tests.jar` (the runner `src/test/java/.../bench/CrossBench.java`, packaged
as the test jar with the library jar on its manifest class path). From the repository root:

```pwsh
pwsh bench/run.ps1 -Impl java
```

The runner drives the core engine (its own values, JSON reader and writer), as
[`bench/README.md`](../bench/README.md) asks. On every case of `bench/cases.json` its output equals
`bench/cases/<name>.expected` byte for byte.

## API

```java
import com.devlooped.jqlite.*;
import com.devlooped.jqlite.values.JqValue;

JqExpression expression = Jq.parse("[.items[] | select(.price > $min) | .name]");   // parse once, thread-safe
JqValue input = JqJson.parse("{\"items\":[{\"name\":\"a\",\"price\":5},{\"name\":\"b\",\"price\":15}]}");

List<JqValue> results = expression.evaluate(input, Map.of("min", JqValue.fromNumber(10)));
System.out.println(JqJson.toJson(results.get(0)));                               // ["b"]

expression.evaluate(input, Map.of("min", JqValue.fromNumber(0)), result -> {      // streaming
    System.out.println(JqJson.toJson(result));
    return false;                                                                // stop after the first
});

JqOptions options = JqOptions.defaults()
    .withResolver(new JqFileResolver(Path.of("modules")))   // import / include / modulemeta
    .withEnvironment(Map.of("HOME", "/home/me"))           // $ENV (default: the process environment)
    .withClock(Clock.system(ZoneId.of("Europe/Berlin")))    // now, localtime, %z
    .withMaxDepth(2000)                                     // spec §11
    .withDebug(value -> System.err.println(JqJson.toJson(value)));
JqExpression withModules = Jq.parse("include \"lib\"; f", options);
```

Java has no standard ordered JSON value, so the host works with the engine's immutable `JqValue`
(a sealed interface: `JqValue.NULL`, `TRUE`, `FALSE`, `JqValue.fromNumber`, `fromString`,
`JqArray.of`, `JqObjectBuilder`), and `JqJson` converts from and to JSON text (jqlite's reader and
writer: key order kept, canonical number text). Errors: `JqParseException` (`line()`, `column()`),
`JqException` (`value()`, the jqlite error value), `JqHaltException` (`exitCode()`); invalid host
variable names (`$x`, `ENV`, `a-b`) are an `IllegalArgumentException` before evaluation.
`Jq.evaluateLines` evaluates each value of a JSON Lines reader alone.

## What the port guarantees

- **No host stack failure** (spec §11). Recursion in a program meets only `MaxDepth`: deep
  evaluations continue on threads of 64 MB stacks (see Hops below); parsing continues on a fresh
  stack every 128 levels of nesting; values of any depth are read, written, compared and merged
  with explicit stacks.
- **Regular expressions in linear time, without recursion.** `builtins/RegexEngine.java` is the
  reference's engine (guide §3.3), not `java.util.regex`: time is O(input × pattern), memory
  O(pattern), and 4,000,000 characters match on a 256 KB thread. The limits of a pattern (spec §11)
  are the translator's (`RegexSyntax.Measure`), so the engine refuses nothing within them; past them
  the error is `Regex failure: the pattern is too large`, which `try` catches.
- **`MaxDepth`** is between 1 and `JqOptions.MAX_DEPTH_LIMIT` (100,000); `withMaxDepth` rejects
  other values. A level of recursion takes about 2 KB of thread stack, up to 4 KB, committed as it is
  used: 100,000 levels may take up to 400 MB.

### Hops

When an evaluation goes deeper than the stack budget of its thread, it continues on a thread of the
pool `jqlite-stack-N` (64 MB of stack) while the caller's thread waits. Then:

- the host's callbacks (`onResult`, `debug`, `stderr`, a resolver called by `modulemeta`) run on
  that thread, without the caller's thread-locals; an exception they throw comes back to the caller;
- an interrupt of the caller's thread does not stop the hopped evaluation: the caller keeps waiting
  for it (and its interrupt flag is set again afterwards). Stop an evaluation by returning false
  from `onResult`.

The budget is counted per thread, not per evaluation: an evaluation the host starts from a callback
of another, on the same thread, goes on from what the outer one uses. A program whose evaluation hops
4 times or more is marked (one volatile flag of the bound program), and its later evaluations start
on a pool thread at once.

## Layout

| Package | Reference |
| --- | --- |
| `com.devlooped.jqlite` | `dotnet/JQLite/*.cs`, `Host/` and `Json/`: the host boundary |
| `com.devlooped.jqlite.values` | `Core/Values` |
| `com.devlooped.jqlite.syntax` | `Core/Syntax` |
| `com.devlooped.jqlite.binding` | `Core/Binding` |
| `com.devlooped.jqlite.runtime` | `Core/Runtime` |
| `com.devlooped.jqlite.builtins` | `Core/Builtins` |

Each reference file has a Java file of the same name, with the same type and function names in
Java's case (`TryAdd` is `tryAdd`, enum members are upper case). Files without a reference file:
`values/Result.java` (the outcome of a fallible operation, guide §2.2), `ClockSource.java` (a
`java.time.Clock` as the core's `IClock`, the reference's `Host/TimeProviderClock.cs`),
`JqJson.java` (host values from and to JSON text, in place of `Json/JsonElementAdapter.cs`),
`ResolverSource.java` (`Host/ResolverSource.cs`), and the files split off a C# file listed below.

## Differences from the reference's structure

| Difference | Why |
| --- | --- |
| A C# file holding several public types is one Java file with nested types (`Ast`, `Bound`, `Modules`, `Evaluation`, `Env.Closure`, `Signal.SignalKind`, `Native.NativeGroup`, `RegexEngine.RegexProgram`, `RegexEngine.RegexMatcher`), or a file of its own for the few the whole code names (`JqKind`, `TokenKind`, `SyntaxMessages`, `BindingMessages`, `Native`, `Emit`) | Java allows one public top-level type per file |
| `JqValue` is a sealed interface; `JqArray` and `JqObject` are values themselves, numbers and strings are records | the guide's §2.1 shape, without a wrapper per container |
| A fallible operation returns `Result`: the value itself on success (every `JqValue` is a `Result`), `Result.Err` on failure; `tryParseNumber` returns a `Double` or null, `trySliceRange` writes its range to an `int[]` and returns the error or null | Java has no `out` parameters; success allocates nothing |
| `EvalOne` writes its output to a mutable `One` holder (`has`, `value`, `at`, plus `frame` for `BindOne` and `key` for `EvalEntryOne`) | the guide's §2.3 for Java |
| `JqObject.get(key)` returns Java null for an absent key (the reference's `TryGetValue`) | no `out` parameters |
| Bound nodes are final classes with public final fields rather than records | `AtMostOne` is computed when a node is built, which a record cannot keep beside its components |
| `StackGuard.hasStack(budget)`: the interpreter counts the guarded steps active on the current thread (a `Budget` per thread), 120 on the host's threads (fits a 256 KB stack under every JIT tier) and 16,384 on its own 64 MB threads, taken from a cached pool | the JVM tells neither the stack size of a thread nor what is left of it (.NET has `TryEnsureSufficientExecutionStack`) |
| `BoundProgram` is a class with one volatile flag (`startsDeep`, `markDeep`): a program whose evaluation hopped 4 times or more starts its later evaluations on a fresh stack | a recursion applied to each element of a stream would otherwise pay two thread hand-offs per element |
| `Parser` continues on a thread with a stack of its own every 128 levels of nesting | a C1-compiled frame of the parser is up to 4 times larger than the reference's: 1,000 levels took up to 850 KB, too close to a 1 MB thread stack |
| `ValueText.formatNumber` corrects Java's two-digit results to one digit when one reads back (`Double.toString(4.9E-324)`) | JDK 19+ `Double.toString` picks the closest of the shortest decimals of at least two digits |
| `MathExtra` adds `pow` (C99's `pow(1, y)` and `pow(-1, ±Infinity)` are 1), `acosh`, `asinh`, `atanh` (fdlibm formulas) and `log2` (exact for powers of two) | `StrictMath` lacks the last four and departs from C99 in `pow` |
| `JqOptions` is immutable with `with…` methods; the clock is a `java.time.Clock` (instant and zone); `%Z` prints the offset, as in .NET; `withMaxDepth` accepts 1 to 100,000 | Java idiom; no abbreviations, as in the reference; the stack a level takes (see above) |
| No observable or asynchronous API (the reference's `IObservable` and `IAsyncEnumerable` helpers): `evaluate` with an `onResult` callback streams, and `Jq.evaluateLines` evaluates JSON Lines from a `Reader` | the callback is Java's natural push API; a host wraps it in its own reactive or async types |
| The modules part of the resolver (`BindImports`, `BindModule`) is at the end of `Resolver.java` | Java has no partial classes |
| No `JqResourceResolver` over assemblies: it reads resources of a `ClassLoader` under a slash-separated prefix | Java's embedded resources |
| `CollectionTests` checks linear time where the reference counts allocated bytes; `Value_is_sixteen_bytes` is not ported | Java has no portable allocation counter or value size |