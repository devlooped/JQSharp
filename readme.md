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

using var doc = JsonDocument.Parse("""{"name":"Alice","age":30}""");

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

### Transforming JSON

jq's full filter language is available, including pipes, array/object construction, 
built-in functions, conditionals, and reductions:

```csharp
using var doc = JsonDocument.Parse("""
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
    "[.orders[] | select(.status == \"paid\") | .total] | add",
    doc.RootElement);

Console.WriteLine(results.Single()); // 142.4
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

## jq Compatibility

JQSharp targets the [jq 1.8](https://jqlang.org/manual/v1.8/) specification and passes 
the official jq test suite for the supported feature set. The full jq manual is available 
at [docs/manual.md](./docs/manual.md).

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
[![David JENNI](https://avatars.githubusercontent.com/u/3200210?v=4&s=39 "David JENNI")](https://github.com/davidjenni)
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
