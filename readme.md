# ./jq# 

[![Version](https://img.shields.io/nuget/vpre/Devlooped.JQSharp.svg?color=royalblue)](https://www.nuget.org/packages/Devlooped.JQSharp)
[![Downloads](https://img.shields.io/nuget/dt/Devlooped.JQSharp.svg?color=darkmagenta)](https://www.nuget.org/packages/Devlooped.JQSharp)
[![EULA](https://img.shields.io/badge/EULA-OSMF-blue?labelColor=black&color=C9FF30)](https://github.com/devlooped/JQSharp/blob/main/osmfeula.txt)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/devlooped/JQSharp/blob/main/license.txt)


<!-- #description -->
A .NET implementation of the [jq](https://jqlang.org) filter language for querying and transforming JSON.
<!-- #description -->

## Open Source Maintenance Fee

To ensure the long-term sustainability of this project, use of JQSharp requires an 
[Open Source Maintenance Fee](https://opensourcemaintenancefee.org). While the source 
code is freely available under the terms of the [OSMF EULA](./osmfeula.txt), all other 
aspects of the project—including opening or commenting on issues, participating in 
discussions, and downloading releases—require [adherence to the Maintenance Fee](./osmfeula.txt).

In short, if you use this project to generate revenue, the [Maintenance Fee is required](./osmfeula.txt).

To pay the Maintenance Fee, [become a Sponsor](https://github.com/sponsors/devlooped).

<!-- #content -->
## Usage

JQSharp exposes a minimal API surface via the `Jq` static class in the `Devlooped` namespace.

### One-shot evaluation

```csharp
using System.Text.Json;
using Devlooped;

using var doc = JsonDocument.Parse(
    """
    {"name":"Alice","age":30}
    """);

// Returns an IEnumerable<JsonElement> with the matching results
var results = Jq.Evaluate(".name", doc.RootElement);

foreach (var result in results)
    Console.WriteLine(result); // "Alice"
```

### Parse once, evaluate many times

For hot paths or expressions that are applied to many inputs, parse the expression once 
into a reusable `JqExpression`. Parsed expressions are immutable and fully thread-safe, 
so the same instance can be evaluated concurrently from multiple threads.

```csharp
using System.Text.Json;
using Devlooped;

// Parse once — this is the expensive step
JqExpression filter = Jq.Parse(".users[] | select(.active) | .name");

// Evaluate cheaply against many inputs
foreach (var json in GetJsonStream())
{
    using var doc = JsonDocument.Parse(json);
    foreach (var name in filter.Evaluate(doc.RootElement))
        Console.WriteLine(name);
}
```

### Performance vs jq.exe

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8037/25H2/2025Update/HudsonValley2)
AMD Ryzen AI 9 HX 370 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
.NET SDK 10.0.104
  [Host]     : .NET 8.0.25 (8.0.25, 8.0.2526.11203), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 8.0.25 (8.0.25, 8.0.2526.11203), X64 RyuJIT x86-64-v4
```

| Method                  | Mean         | Error      | StdDev     | Ratio | RatioSD | Gen0    | Gen1   | Allocated | Alloc Ratio |
|-------------------------|-------------:|-----------:|-----------:|------:|--------:|--------:|-------:|----------:|------------:|
| jq executable           | 20,774.05 us | 415.148 us | 838.619 us | 1.002 |    0.06 |       - |      - | 122.23 KB |        1.00 |
| JQSharp no caching      |    149.67 us |   2.370 us |   2.911 us | 0.007 |    0.00 | 41.0156 | 8.3008 | 336.23 KB |        2.75 |
| JQSharp w/query caching |     15.52 us |   0.061 us |   0.057 us | 0.001 |    0.00 |  6.7444 | 0.3357 |  55.34 KB |        0.45 |

### Transforming JSON

jq's full filter language is available, including pipes, array/object construction, 
built-in functions, conditionals, and reductions:

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

Both `Jq.Parse` and `JqExpression.Evaluate` throw `JqException` on invalid expressions 
or runtime errors:

```csharp
try
{
    var results = Jq.Evaluate(".foo", doc.RootElement).ToList();
}
catch (JqException ex)
{
    Console.WriteLine($"jq error: {ex.Message}");
}
```

## Streaming JSONL

`Jq.EvaluateAsync` accepts an `IAsyncEnumerable<JsonElement>` input and evaluates the 
filter against each element as it arrives, making it well-suited for processing 
[JSONL](https://jsonlines.org) (newline-delimited JSON) files or any other streaming 
JSON source without buffering the entire dataset in memory.

### Reading a JSONL file

Use `JsonSerializer.DeserializeAsyncEnumerable<JsonElement>` to turn a stream of 
newline-delimited JSON objects into an `IAsyncEnumerable<JsonElement>`:

```csharp
using System.Text.Json;
using Devlooped;

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
// "Alice"
// "Charlie"
```

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

## jq Compatibility

JQSharp targets the [jq 1.8](https://jqlang.org/manual/v1.8/) specification and passes 
the official jq test suite for the supported feature set. 

Supported features include:

- Field access, array/object iteration and slicing
- Pipe (`|`), comma (`,`), and parentheses for grouping
- Array and object constructors (`[]`, `{}`)
- All built-in functions (`map`, `select`, `group_by`, `to_entries`, `from_entries`, `env`, `limit`, `until`, `recurse`, `paths`, `walk`, `ascii`, `format`, `strftime`, `debug`, …)
- String interpolation (`"\(.foo)"`) and format strings (`@base64`, `@uri`, `@csv`, `@tsv`, `@html`, `@json`, `@text`, `@sh`)
- Variables (`as $x`), destructuring, and user-defined functions (`def f(x): …`)
- `reduce`, `foreach`, `label-break`, `try-catch`, `?//` alternative operator
- Optional operator (`?`), path expressions, update (|=`) and assignment operators

<!-- #content -->

## Dogfooding

[![CI Version](https://img.shields.io/endpoint?url=https://shields.kzu.app/vpre/Devlooped.JQSharp/main&label=nuget.ci&color=brightgreen)](https://pkg.kzu.app/index.json)
[![Build](https://github.com/devlooped/JQSharp/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/devlooped/JQSharp/actions/workflows/build.yml)

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
[![Kori Francis](https://avatars.githubusercontent.com/u/67574?u=3991fb983e1c399edf39aebc00a9f9cd425703bd&v=4&s=39 "Kori Francis")](https://github.com/kfrancis)
[![Uno Platform](https://avatars.githubusercontent.com/u/52228309?v=4&s=39 "Uno Platform")](https://github.com/unoplatform)
[![Reuben Swartz](https://avatars.githubusercontent.com/u/724704?u=2076fe336f9f6ad678009f1595cbea434b0c5a41&v=4&s=39 "Reuben Swartz")](https://github.com/rbnswartz)
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
[![Michael Hagedorn](https://avatars.githubusercontent.com/u/61711586?u=8f653dfcb641e8c18cc5f78692ebc6bb3a0c92be&v=4&s=39 "Michael Hagedorn")](https://github.com/Eule02)
[![torutek](https://avatars.githubusercontent.com/u/33917059?v=4&s=39 "torutek")](https://github.com/torutek)
[![mccaffers](https://avatars.githubusercontent.com/u/16667079?u=739e110e62a75870c981640447efa5eb2cb3bc8f&v=4&s=39 "mccaffers")](https://github.com/mccaffers)
[![Seika Logiciel](https://avatars.githubusercontent.com/u/2564602?v=4&s=39 "Seika Logiciel")](https://github.com/SeikaLogiciel)
[![Andrew Grant](https://avatars.githubusercontent.com/devlooped-user?s=39 "Andrew Grant")](https://github.com/wizardness)


<!-- sponsors.md -->
[![Sponsor this project](https://avatars.githubusercontent.com/devlooped-sponsor?s=118 "Sponsor this project")](https://github.com/sponsors/devlooped)

[Learn more about GitHub Sponsors](https://github.com/sponsors)

<!-- https://github.com/devlooped/sponsors/raw/main/footer.md -->
