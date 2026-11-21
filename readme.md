# ./jqlite

[![Version](https://img.shields.io/nuget/vpre/JQLite.svg?color=royalblue)](https://www.nuget.org/packages/JQLite)
[![Downloads](https://img.shields.io/nuget/dt/JQLite.svg?color=darkmagenta)](https://www.nuget.org/packages/JQLite)
[![EULA](https://img.shields.io/badge/EULA-OSMF-blue?labelColor=black&color=C9FF30)](https://github.com/devlooped/jqlite/blob/main/osmfeula.txt)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/devlooped/jqlite/blob/main/license.txt)


<!-- #description -->
A .NET implementation of the [jq](https://jqlang.org) filter language for querying and transforming JSON.
<!-- #description -->

## Open Source Maintenance Fee

To ensure the long-term sustainability of this project, use of JQLite requires an 
[Open Source Maintenance Fee](https://opensourcemaintenancefee.org). While the source 
code is freely available under the terms of the [OSMF EULA](./osmfeula.txt), all other 
aspects of the project—including opening or commenting on issues, participating in 
discussions, and downloading releases—require [adherence to the Maintenance Fee](./osmfeula.txt).

In short, if you use this project to generate revenue, the [Maintenance Fee is required](./osmfeula.txt).

To pay the Maintenance Fee, [become a Sponsor](https://github.com/sponsors/devlooped).

<!-- #content -->
## Usage

JQLite exposes a minimal API surface via the `Jq` static class in the `JQLite` namespace.

### One-shot evaluation

```csharp
using System.Text.Json;
using JQLite;

using var doc = JsonDocument.Parse(
    """
    {"name":"Alice","age":30}
    """);

// Returns every result, computed before the list is returned
IReadOnlyList<JsonElement> results = Jq.Evaluate(".name", doc.RootElement);

Console.WriteLine(results[0]); // Alice
```

Results are `JsonElement`s of their own: they stay valid after the input's document is disposed.
An empty expression is the identity, `.`.

### Parse once, evaluate many times

For hot paths or expressions that are applied to many inputs, parse the expression once 
into a reusable `JqExpression`. Parsed expressions are immutable and fully thread-safe, 
so the same instance can be evaluated concurrently from multiple threads.

```csharp
string[] payloads =
[
    """{"users":[{"name":"Alice","active":true},{"name":"Bob","active":false}]}""",
    """{"users":[{"name":"Carol","active":true}]}""",
];

// Parse once — this is the expensive step
JqExpression filter = Jq.Parse(".users[] | select(.active) | .name");

// Evaluate cheaply against many inputs
foreach (var json in payloads)
{
    using var doc = JsonDocument.Parse(json);
    foreach (var name in filter.Evaluate(doc.RootElement))
        Console.WriteLine(name);
}
// Alice
// Carol
```
### Performance vs jq.exe
The `WhatsApp/Message.jq` filter on one WhatsApp message, against the `jq` executable (run through `Devlooped.JQ`), with and without a cached `JqExpression`:

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26300.9711)
AMD Ryzen AI 9 HX 370 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
.NET SDK 11.0.100-rc.3.26522.148
  [Host]     : .NET 8.0.34 (8.0.34, 8.0.3426.52217), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 8.0.34 (8.0.34, 8.0.3426.52217), X64 RyuJIT x86-64-v4
```

| Method                  | Mean         | Error      | StdDev       | Ratio | RatioSD | Gen0    | Gen1   | Allocated | Alloc Ratio |
|-------------------------|-------------:|-----------:|-------------:|------:|--------:|--------:|-------:|----------:|------------:|
| jq executable           | 27,689.45 us | 548.792 us | 1,017.221 us | 1.001 |    0.05 |       - |      - | 122.15 KB |        1.00 |
| JQLite no caching       |    148.33 us |   2.953 us |     7.674 us | 0.005 |    0.00 | 30.1514 | 5.4932 | 246.83 KB |        2.02 |
| JQLite w/query caching  |     12.46 us |   0.249 us |     0.601 us | 0.000 |    0.00 |  1.9684 | 0.0458 |  16.15 KB |        0.13 |

Programs whose cost must grow in step with their size, each on the input `null` with a cached expression. "1.x" is the previous engine, measured as one warm run each (not with BenchmarkDotNet), so compare orders of magnitude, not digits:

| Program                                              | Mean (1.x) | Allocated (1.x) |      Mean |    Allocated |
|------------------------------------------------------|-----------:|----------------:|----------:|-------------:|
| `[range(100000)] \| map(. + 1) \| add`               |      0.5 s |          577 MB |  22.33 ms | 26,167.56 KB |
| `reduce range(10000) as $i ([]; . + [$i]) \| length` |      3.1 s |          3.0 GB |   1.68 ms |  3,589.61 KB |
| `[range(4000)] \| .[] \|= . + 1 \| length`           |      1.5 s |          2.0 GB | 588.31 us |  1,287.53 KB |
| `range(2000000)`, only the first result              |      1.1 s |          1.5 GB |  422.8 ns |      1.53 KB |

### Streaming results

`JqExpression.Evaluate` also takes a callback that receives each result as soon as it is 
produced; returning `false` stops the evaluation (stopping is not an error). Prefer it when 
an expression can produce many or endless results, when you only need the first few, or when 
you want to handle results while the rest are still being computed:

```csharp
using var doc = JsonDocument.Parse("""{"limit": 1000000}""");
var multiples = new List<int>();

Jq.Parse("range(.limit) | select(. % 7 == 0)").Evaluate(doc.RootElement, result =>
{
    multiples.Add(result.GetInt32());
    return multiples.Count < 3; // false stops the evaluation
});

Console.WriteLine(string.Join(", ", multiples)); // 0, 7, 14
```

If an error ends the evaluation, the callback has already received the results produced before 
it; the list-returning `Evaluate` throws without returning any.

### Transforming JSON

The jq filter language is available, including pipes, array/object construction, 
built-in functions, conditionals, and reductions (see [The jqlite profile](#the-jqlite-profile)):

```csharp
using var doc = JsonDocument.Parse(
    """
    {
        "orders": [
            { "id": 1, "total": 42.5, "status": "paid" },
            { "id": 2, "total": 17.0, "status": "pending" },
            { "id": 3, "total": 99.9, "status": "paid" }
        ]
    }
    """);

// Sum paid order totals
var results = Jq.Evaluate(
    """
    [.orders[] | select(.status == "paid") | .total] | add
    """,
    doc.RootElement);

Console.WriteLine(results.Single()); // 142.4
```

### Reshaping objects

Use object construction to project, rename, or combine fields from the input:

```csharp
using var doc = JsonDocument.Parse(
    """
    {
        "user": { "id": 1, "name": "Alice", "email": "alice@example.com" },
        "role": "admin"
    }
    """);

// Pick and flatten specific fields into a new object
var results = Jq.Evaluate("{id: .user.id, name: .user.name, role: .role}", doc.RootElement);

Console.WriteLine(results.Single()); // {"id":1,"name":"Alice","role":"admin"}
```

### Optional fields and defaults

Use the alternative operator `//` to supply a fallback when a field is missing or `null`,
and the optional operator `?` to silence errors on mismatched types:

```csharp
using var doc = JsonDocument.Parse(
    """
    [
        { "name": "Alice", "email": "alice@example.com" },
        { "name": "Bob" },
        { "name": "Charlie", "email": "charlie@example.com" }
    ]
    """);

// .email // "n/a" returns the fallback when the field is absent
var results = Jq.Evaluate(
    """
    .[] | {name: .name, email: (.email // "n/a")}
    """,
    doc.RootElement);

foreach (var result in results)
    Console.WriteLine(result);
// {"name":"Alice","email":"alice@example.com"}
// {"name":"Bob","email":"n/a"}
// {"name":"Charlie","email":"charlie@example.com"}
```

### Array operations

Built-in functions like `map`, `select`, `sort_by`, and `group_by` make it easy to 
slice and reshape collections:

```csharp
using var doc = JsonDocument.Parse(
    """
    [
        { "name": "Alice", "dept": "Engineering", "salary": 95000 },
        { "name": "Bob",   "dept": "Marketing",   "salary": 72000 },
        { "name": "Carol", "dept": "Engineering", "salary": 105000 },
        { "name": "Dave",  "dept": "Marketing",   "salary": 68000 }
    ]
    """);

// Group by department and compute average salary per group
var results = Jq.Evaluate(
    "group_by(.dept) | map({dept: .[0].dept, avg_salary: (map(.salary) | add / length)})",
    doc.RootElement);

Console.WriteLine(results.Single());
// [{"dept":"Engineering","avg_salary":100000},{"dept":"Marketing","avg_salary":70000}]
```

### Error handling

Errors are exceptions deriving from `JqException`:

- `JqParseException`, thrown by `Jq.Parse` (and so by `Jq.Evaluate`) before any evaluation, for a 
  syntax error or a function, label or module that is not defined, with its `Line` and `Column` 
  (counting code points);
- `JqException`, thrown by `Evaluate` for an error no `try` caught: `Value` is the error value, 
  and `Message` is that value when it is a string, else its JSON text followed by `(not a string)`;
- `JqHaltException`, thrown by `Evaluate` for `halt_error`, with its `Value` and `ExitCode`. 
  (`halt` ends the evaluation normally, keeping the results produced before it.)

```csharp
using var doc = JsonDocument.Parse("""{"name":"Bob","age":12}""");

try
{
    Jq.Parse(".name |\n  shout");
}
catch (JqParseException ex)
{
    Console.WriteLine(ex.Message); // shout/0 is not defined at line 2, column 3
}

try
{
    Jq.Evaluate("""if .age < 18 then error({reason: "minor", age}) else . end""", doc.RootElement);
}
catch (JqException ex)
{
    Console.WriteLine(ex.Message);                      // {"reason":"minor","age":12} (not a string)
    Console.WriteLine(ex.Value?.GetProperty("reason")); // minor
}

try
{
    Jq.Evaluate("""{status: "rejected"} | halt_error(3)""", doc.RootElement);
}
catch (JqHaltException ex)
{
    Console.WriteLine($"{ex.ExitCode}: {ex.Value}"); // 3: {"status":"rejected"}
}
```

### Options

`Jq.Parse` takes a `JqOptions` with what an expression gets from its host besides its input. 
Options are read when the expression is parsed and apply to every evaluation of it:

| Option | Default | Used for |
| --- | --- | --- |
| `Resolver` | none: no module exists | `import`, `include` and `modulemeta` (see [Modules](#modules)) |
| `Environment` | the process environment | `$ENV` and `env`, in the dictionary's order |
| `TimeProvider` | `TimeProvider.System` | `now`, and the local time zone of `localtime`, `strflocaltime`, `%z` and `%Z` |
| `MaxDepth` | 1000 | the calls of jq-defined functions active at once; beyond it, the catchable error `Maximum call depth exceeded` (deep recursion never overflows the .NET stack) |
| `Debug` | none: values are dropped | receives the input of each `debug` and each message of `debug(msgs)`, as is: `1 \| debug("m")` gives it `"m"` (jq prints `["DEBUG:","m"]` to standard error) |
| `Stderr` | none: values are dropped | receives the input of each `stderr`, as is |

```csharp
using var doc = JsonDocument.Parse("null");

var options = new JqOptions
{
    Environment = new Dictionary<string, string> { ["STAGE"] = "prod" },
    TimeProvider = new FixedTime(
        new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero),
        TimeZoneInfo.CreateCustomTimeZone("UTC-3", TimeSpan.FromHours(-3), "UTC-3", "UTC-3")),
    MaxDepth = 200,
};

var expression = Jq.Parse(
    """{stage: $ENV.STAGE, utc: (now | todate), local: (now | strflocaltime("%H:%M %z"))}""",
    options);

Console.WriteLine(expression.Evaluate(doc.RootElement)[0]);
// {"stage":"prod","utc":"2025-06-01T12:00:00Z","local":"09:00 -0300"}

class FixedTime(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;

    public override TimeZoneInfo LocalTimeZone => zone;
}
```

.NET has no time zone abbreviations, so `%Z` in `strflocaltime` prints the offset (`-03`).

### External variables

Pass a dictionary of variables when evaluating to inject values available as `$name`
in the filter—similar to jq's `--arg` / `--argjson`:

```csharp
using var doc = JsonDocument.Parse("""{"name":"Alice"}""");
using var root = JsonDocument.Parse("""{"id":42,"role":"admin"}""");

var variables = new Dictionary<string, JsonElement>
{
    ["root"] = root.RootElement, // binds $root
};

// $root is available as if it had been bound with `as`, but supplied from host code
var results = Jq.Evaluate(
    "{name, id: $root.id, role: $root.role}",
    doc.RootElement,
    variables);

Console.WriteLine(results.Single()); // {"name":"Alice","id":42,"role":"admin"}
```

The same dictionary can be passed to `JqExpression.Evaluate` and the `EvaluateAsync` overloads.

- Keys are jq identifiers (`[A-Za-z_][A-Za-z0-9_]*`) without the leading `$`; `ENV` is not 
  allowed (`$ENV` comes from `JqOptions.Environment`). Any other key throws `ArgumentException` 
  before the evaluation starts.
- A `$name` the expression does not bind itself is looked up among these variables when it is 
  evaluated; a missing one is the run-time error `$name is not defined` (a `JqException`, which 
  `try` can catch). `as $name` bindings and `$name` parameters shadow a variable of the same name.

## Streaming JSONL

`Jq.EvaluateAsync` accepts an `IAsyncEnumerable<JsonElement>` input and evaluates the 
filter against each element as it arrives, making it well-suited for processing 
[JSONL](https://jsonlines.org) (newline-delimited JSON) files or any other streaming 
JSON source without buffering the entire dataset in memory. The results of one element 
are computed before they are yielded, and each element is evaluated alone (`halt` ends only 
its own evaluation).

### Reading a JSONL file

Use `JsonSerializer.DeserializeAsyncEnumerable<JsonElement>` (System.Text.Json 9 or later 
for `topLevelValues`) to turn a stream of newline-delimited JSON objects into an 
`IAsyncEnumerable<JsonElement>`:

```csharp
using System.Text.Json;
using JQLite;

// users.jsonl — one JSON object per line:
// {"id":1,"name":"Alice","dept":"Engineering"}
// {"id":2,"name":"Bob","dept":"Marketing"}
// {"id":3,"name":"Charlie","dept":"Engineering"}

using var stream = File.OpenRead("users.jsonl");
var elements = JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
    stream, topLevelValues: true);

await foreach (var result in Jq.EvaluateAsync(
    """
    select(.dept == "Engineering") | .name
    """,
    elements))
    Console.WriteLine(result);
// Alice
// Charlie
```

This overload parses the expression when the enumeration starts, so a `JqParseException` 
surfaces at the first `await foreach` step.

### Reusing a parsed expression across multiple streams

Parse the expression once and pass it to `EvaluateAsync` to avoid re-parsing on every call:

```csharp
JqExpression filter = Jq.Parse(
    """
    select(.level == "error") | .message
    """);

foreach (var logFile in Directory.GetFiles("logs", "*.jsonl"))
{
    using var stream = File.OpenRead(logFile);
    var elements = JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
        stream, topLevelValues: true);

    await foreach (var result in Jq.EvaluateAsync(filter, elements))
        Console.WriteLine(result);
}
```

Both overloads take optional variables and a `CancellationToken`.

## Modules

`import "path" as name;` and `include "path";` load jq definitions from code modules, and 
`import "path" as $name;` loads a data module: its JSON values, bound as an array to `$name` 
(and `$name::name`). Modules are found by the `JqOptions.Resolver`, and read once per module 
when the expression is parsed; a module that cannot be found is a `JqParseException` 
(`module not found: path`).

### Files

`JqFileResolver` resolves a path against the directory of the module that names it, or against 
its base directory for the expression itself, and adds `.jq` (code) or `.json` (data) when the 
path has no extension:

```csharp
// filters/common.jq:  def cents: . * 100 | round / 100;
// filters/rates.json: {"usd": 1, "eur": 0.92}
var options = new JqOptions { Resolver = new JqFileResolver("filters") };

var toEuros = Jq.Parse(
    """
    include "common"; import "rates" as $rates;
    .amount * $rates[0].eur | cents
    """,
    options);

using var doc = JsonDocument.Parse("""{"amount": 12.345}""");
Console.WriteLine(toEuros.Evaluate(doc.RootElement)[0]); // 11.36
```

### Embedded resources

`JqResourceResolver` reads the embedded resources of an assembly: the resource name is the 
prefix, then the path with `/` and `\` turned into dots, then `.jq` or `.json` unless the path 
already ends with it. A directive inside a module is relative to that module's name without 
its last segment:

```csharp
// Embedded as MyApp.Filters.utils.jq:      include "sub/helper"; def shout: twice | ascii_upcase;
// Embedded as MyApp.Filters.sub.helper.jq: def twice: . + " " + .;
var options = new JqOptions { Resolver = new JqResourceResolver(typeof(Program).Assembly, "MyApp.Filters") };

using var doc = JsonDocument.Parse("\"hey\"");
Console.WriteLine(Jq.Parse("""include "utils"; shout""", options).Evaluate(doc.RootElement)[0]); // HEY HEY
```

### Your own resolver

Derive from `JqResolver` to keep modules anywhere else. `GetCanonicalPath` maps a path, as 
written in the directive, to the module's identity, or to `null` when there is no such module; 
it also receives the kind (`JqModuleKind.Code` or `Data`), the directive's metadata and the 
identity of the importing module (`null` for the expression itself). `Open` reads the module 
of an identity it returned. The same identity means the same module. `modulemeta` calls the 
resolver at evaluation time, possibly from several threads at once.

```csharp
var options = new JqOptions
{
    Resolver = new DictionaryResolver(new Dictionary<string, string>
    {
        ["greet.jq"] = """def greet: "hello " + .;""",
    }),
};

using var doc = JsonDocument.Parse("\"world\"");
Console.WriteLine(Jq.Parse("""import "greet" as g; g::greet""", options).Evaluate(doc.RootElement)[0]); // hello world

class DictionaryResolver(IReadOnlyDictionary<string, string> modules) : JqResolver
{
    public override string? GetCanonicalPath(string path, JqModuleKind kind, JsonElement? metadata, string? fromPath)
    {
        var name = path + (kind == JqModuleKind.Code ? ".jq" : ".json");
        return modules.ContainsKey(name) ? name : null;
    }

    public override TextReader Open(string canonicalPath) => new StringReader(modules[canonicalPath]);
}
```

## The jqlite profile

JQLite is the .NET reference implementation of **jqlite**, the profile of [jq 1.8](https://jqlang.org/manual/v1.8/) 
specified in [`spec/jqlite.md`](https://github.com/devlooped/jqlite/blob/main/spec/jqlite.md). The 
builtins written in jq ([`spec/prelude.jq`](https://github.com/devlooped/jqlite/blob/main/spec/prelude.jq)) 
and the conformance tests ([`spec/tests`](https://github.com/devlooped/jqlite/tree/main/spec/tests)) 
are shared by every implementation, and the engine is ported file by file 
([`docs/porting.md`](https://github.com/devlooped/jqlite/blob/main/docs/porting.md)) to 
[Go](https://github.com/devlooped/jqlite/tree/main/go), [Java](https://github.com/devlooped/jqlite/tree/main/java), 
[Python](https://github.com/devlooped/jqlite/tree/main/python), [Rust](https://github.com/devlooped/jqlite/tree/main/rust) 
and [TypeScript](https://github.com/devlooped/jqlite/tree/main/typescript).

The profile has the jq language: paths, pipes, generators, array and object construction, 
string interpolation, variables and destructuring (including `?//`), `def`, `reduce`, `foreach`, 
`label`/`break`, `try`/`catch` and `?`, path expressions and every assignment operator, and modules. 
Its [builtins](https://github.com/devlooped/jqlite/blob/main/spec/jqlite.md#9-builtins) cover types and 
selection, objects, arrays and collections, paths, strings, formats (`@base64`, `@uri`, `@csv`, 
`@tsv`, `@html`, `@json`, `@text`, `@sh`), dates, math and regular expressions.

jq programs behave as in jq 1.8, except for the [differences](https://github.com/devlooped/jqlite/blob/main/spec/jqlite.md#13-differences-from-jq-18) 
that make the profile portable. Those you are most likely to notice:

- Numbers are doubles, written in one canonical form (`1.0` is `1`; `1e-7`, `1e+21`, `-0`); NaN is 
  written as `null` and infinities as ±`1.7976931348623157e+308`.
- Regular expressions use a flavor common to every port instead of Oniguruma's: no lookaround, backreferences or inline flags; `\d`, `\w`, 
  `\s` and `\b` are ASCII; the `i` flag folds ASCII letters only.
- An unbound `$name` is a host variable (see [External variables](#external-variables)), not a parse error.
- Recursion depth is limited by `MaxDepth`, with no tail calls.
- Dates use their own English tables, with no locale.
- `debug` and `stderr` hand their values to `JqOptions.Debug` and `JqOptions.Stderr`; 
  `input_filename` is `null`.
- An evaluation has one input: APIs that take several values (such as `Jq.EvaluateAsync`) 
  evaluate each one alone.
- Every builtin of jq 1.8 is in, except `get_search_list`, `get_jq_origin` and `get_prog_origin`, 
  which describe the jq command line, and `input` and `inputs`, which read the next input.

## Upgrading from 1.x

- The package is now `JQLite` (it was `Devlooped.JQSharp`), and its namespace is `JQLite` (it was `Devlooped`).
- `Jq.Evaluate` and `JqExpression.Evaluate` return an `IReadOnlyList<JsonElement>`; the new 
  callback overload streams results.
- `JqParseException` and `JqHaltException` derive from `JqException`, which is no longer sealed.
- An empty expression means `.`.
- Unknown functions, labels and modules fail at `Jq.Parse` rather than when evaluated.
- `JqResolver` has a new contract (`GetCanonicalPath` and `Open`), and data imports bind an array.
- Output numbers are written in the canonical form described in [The jqlite profile](#the-jqlite-profile).
- Lone surrogates in strings become U+FFFD.
- `$ENV` and `env` come from `JqOptions.Environment`, and the clock and time zone from `JqOptions.TimeProvider`.
- Deep recursion fails with `Maximum call depth exceeded` (`JqOptions.MaxDepth`) instead of a stack overflow.
- Semantic fixes: lexical scoping of functions and variables, the precedence of `as` and `//`, 
  `try` keeping the outputs produced before an error, object construction over generators, 
  short-circuit `and`/`or`, stable sorts, and string slices by code point.
- Regular expressions follow the flavor described in [The jqlite profile](#the-jqlite-profile).

<!-- #content -->

## Dogfooding

[![CI Version](https://img.shields.io/endpoint?url=https://shields.kzu.app/vpre/JQLite/main&label=nuget.ci&color=brightgreen)](https://pkg.kzu.app/index.json)
[![Build](https://github.com/devlooped/jqlite/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/devlooped/jqlite/actions/workflows/build.yml)

CI packages are produced from every branch and pull request so you can dogfood builds as 
quickly as they are produced.

The CI feed is `https://pkg.kzu.app/index.json`.

The versioning scheme for packages is:

- PR builds: *42.42.42-pr*`[NUMBER]`
- Branch builds: *42.42.42-*`[BRANCH]`.`[COMMITS]`

<!-- include https://github.com/devlooped/sponsors/raw/main/footer.md -->
# Sponsors 

<!-- sponsors.md -->
[![Clarius Org](https://avatars.githubusercontent.com/u/71888636?v=4&s=39 "Clarius Org")](https://github.com/clarius)
[![MFB Technologies, Inc.](https://avatars.githubusercontent.com/u/87181630?v=4&s=39 "MFB Technologies, Inc.")](https://github.com/MFB-Technologies-Inc)
[![SandRock](https://avatars.githubusercontent.com/u/321868?u=99e50a714276c43ae820632f1da88cb71632ec97&v=4&s=39 "SandRock")](https://github.com/sandrock)
[![DRIVE.NET, Inc.](https://avatars.githubusercontent.com/u/15047123?v=4&s=39 "DRIVE.NET, Inc.")](https://github.com/drivenet)
[![Keith Pickford](https://avatars.githubusercontent.com/u/16598898?u=64416b80caf7092a885f60bb31612270bffc9598&v=4&s=39 "Keith Pickford")](https://github.com/Keflon)
[![Thomas Bolon](https://avatars.githubusercontent.com/u/127185?u=7f50babfc888675e37feb80851a4e9708f573386&v=4&s=39 "Thomas Bolon")](https://github.com/tbolon)
[![Jacob Foshee](https://avatars.githubusercontent.com/u/480334?v=4&s=39 "Jacob Foshee")](https://github.com/jfoshee)
[![](https://avatars.githubusercontent.com/u/33566379?u=bf62e2b46435a267fa246a64537870fd2449410f&v=4&s=39 "")](https://github.com/Mrxx99)
[![Eric Johnson](https://avatars.githubusercontent.com/u/26369281?u=41b560c2bc493149b32d384b960e0948c78767ab&v=4&s=39 "Eric Johnson")](https://github.com/eajhnsn1)
[![Jonathan ](https://avatars.githubusercontent.com/u/5510103?u=98dcfbef3f32de629d30f1f418a095bf09e14891&v=4&s=39 "Jonathan ")](https://github.com/Jonathan-Hickey)
[![Ken Bonny](https://avatars.githubusercontent.com/u/6417376?u=569af445b6f387917029ffb5129e9cf9f6f68421&v=4&s=39 "Ken Bonny")](https://github.com/KenBonny)
[![Simon Cropp](https://avatars.githubusercontent.com/u/122666?v=4&s=39 "Simon Cropp")](https://github.com/SimonCropp)
[![agileworks-eu](https://avatars.githubusercontent.com/u/5989304?v=4&s=39 "agileworks-eu")](https://github.com/agileworks-eu)
[![Zheyu Shen](https://avatars.githubusercontent.com/u/4067473?v=4&s=39 "Zheyu Shen")](https://github.com/arsdragonfly)
[![Vezel](https://avatars.githubusercontent.com/u/87844133?v=4&s=39 "Vezel")](https://github.com/vezel-dev)
[![ChilliCream](https://avatars.githubusercontent.com/u/16239022?v=4&s=39 "ChilliCream")](https://github.com/ChilliCream)
[![4OTC](https://avatars.githubusercontent.com/u/68428092?v=4&s=39 "4OTC")](https://github.com/4OTC)
[![domischell](https://avatars.githubusercontent.com/u/66068846?u=0a5c5e2e7d90f15ea657bc660f175605935c5bea&v=4&s=39 "domischell")](https://github.com/DominicSchell)
[![Adrian Alonso](https://avatars.githubusercontent.com/u/2027083?u=129cf516d99f5cb2fd0f4a0787a069f3446b7522&v=4&s=39 "Adrian Alonso")](https://github.com/adalon)
[![torutek](https://avatars.githubusercontent.com/u/33917059?v=4&s=39 "torutek")](https://github.com/torutek)
[![Ryan McCaffery](https://avatars.githubusercontent.com/u/16667079?u=c0daa64bb5c1b572130e05ae2b6f609ecc912d4d&v=4&s=39 "Ryan McCaffery")](https://github.com/mccaffers)
[![Seika Logiciel](https://avatars.githubusercontent.com/u/2564602?v=4&s=39 "Seika Logiciel")](https://github.com/SeikaLogiciel)
[![Andrew Grant](https://avatars.githubusercontent.com/devlooped-user?s=39 "Andrew Grant")](https://github.com/wizardness)
[![eska-gmbh](https://avatars.githubusercontent.com/devlooped-team?s=39 "eska-gmbh")](https://github.com/eska-gmbh)
[![Geodata AS](https://avatars.githubusercontent.com/u/5946299?v=4&s=39 "Geodata AS")](https://github.com/geodata-no)
[![Röbbek Capital](https://avatars.githubusercontent.com/u/116765313?v=4&s=39 "Röbbek Capital")](https://github.com/Roebbek-Capital)
[![Schüco](https://avatars.githubusercontent.com/u/125355474?v=4&s=39 "Schüco")](https://github.com/schueco)
[![Vertica Open Source Fund](https://avatars.githubusercontent.com/u/359072813?v=4&s=39 "Vertica Open Source Fund")](https://github.com/Vertica-Open-Source-Fund)


<!-- sponsors.md -->
[![Sponsor this project](https://avatars.githubusercontent.com/devlooped-sponsor?s=118 "Sponsor this project")](https://github.com/sponsors/devlooped)

[Learn more about GitHub Sponsors](https://github.com/sponsors)

<!-- https://github.com/devlooped/sponsors/raw/main/footer.md -->
