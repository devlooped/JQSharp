# jqlite for Rust

The Rust port of **jqlite**, the profile of the [jq](https://jqlang.org) language defined by
[`spec/jqlite.md`](../spec/jqlite.md). It is a port of the .NET reference (`dotnet/JQLite/Core`),
file by file, following [`docs/porting.md`](../docs/porting.md): the same layers, the same names
in Rust case, the same prelude (`spec/prelude.jq`, embedded unchanged) and the same conformance
corpus (`spec/tests`).

- Crate `jqlite`, edition 2024, Rust 1.85 or later (built and tested with 1.96).
- Dependencies: `regex` (the engine behind `test`, `match`, `sub`…) and `stacker` (a fresh stack
  when the host stack runs short). Nothing else, and no `unsafe` code.

## Build, test, benchmark

Run in this folder:

| Command | What |
| --- | --- |
| `cargo build --release` | the library and the benchmark runner (`target/release/jqlite-bench`) |
| `cargo test` | the conformance corpus both ways, the unit tests of each layer, the API tests |
| `cargo fmt --check` | formatting (`rustfmt.toml`: 160 columns) |
| `cargo clippy --all-targets -- -D warnings` | lints |
| `pwsh ../bench/run.ps1 -Impl rust` | the cross-language benchmark against .NET ([`bench/README.md`](../bench/README.md)) |

The tests:

| File | Tests | What |
| --- | ---: | --- |
| `tests/conformance.rs` | 2 | every case of `spec/tests/*.test` (1,108), once through the public API and once through the core without the native overrides of prelude builtins; `$ENV` empty, the zone at UTC+02:00, the modules of `spec/tests/modules` |
| `tests/values.rs` | 54 | values: sharing, ordering, arithmetic, code points, number text, reader and writer, 10,000 levels read, written and compared |
| `tests/syntax.rs` | 61 | lexer and parser: tokens, positions, the AST printed, every syntax error, 1,000 levels of nesting |
| `tests/binding.rs` | 39 | resolver: hops and slots printed, single calls, binding errors, modules, the catalogue checked against the N and P lists of spec §9 |
| `tests/runtime.rs` | 27 | interpreter: `MaxDepth`, deep recursion on a small stack, order of outputs, native and prelude `_modify`/`_assign` agree, every native is implemented, the limits of a regex |
| `tests/api.rs` | 32 | the host boundary: options, variables, errors, resolvers, 64 threads on one expression |
| `tests/linear.rs` | 3 | what must stay linear: bytes allocated while an array, an object or a string grows in a loop |
| `tests/portability.rs` | 6 | the portability rules of `AGENTS.md` on the sources of the five layers, with the files allowed each exception and why; the order of the layers; the two dependencies |

`cargo test` also runs the example of the crate's documentation.

## Using it

```rust
use std::collections::HashMap;
use std::sync::Arc;

use jqlite::{JqError, JqFileResolver, JqOptions, JqValue};

fn main() -> Result<(), JqError> {
    // Parse once: the expression is immutable, `Send + Sync`, and evaluates on any thread.
    let expression = jqlite::parse(".items | map(select(.price > $min) | .name)")?;

    let input = JqValue::parse(r#"{"items":[{"name":"a","price":1},{"name":"b","price":5}]}"#)?;
    let variables = HashMap::from([("min".to_owned(), JqValue::from(2.0))]);

    // All the outputs…
    for output in expression.evaluate_with(input.clone(), &variables)? {
        println!("{}", output.to_json()); // ["b"]
    }
    // …or one at a time, stopping when the callback returns false.
    expression.for_each(input, Some(&variables), |output| {
        println!("{}", output.to_json());
        false
    })?;

    // What an expression takes from its host.
    let options = JqOptions {
        resolver: Some(Arc::new(JqFileResolver::new("modules"))), // import, include, modulemeta
        environment: Some(vec![("HOME".to_owned(), "/home/me".to_owned())]), // $ENV; None: the process's
        max_depth: 500,
        debug: Some(Arc::new(|value| eprintln!("debug: {}", value.to_json()))),
        ..JqOptions::default() // clock: the system clock in UTC; stderr: dropped
    };
    let expression = jqlite::parse_with("$ENV.HOME | debug", &options)?;
    assert_eq!(expression.evaluate(JqValue::Null)?[0].to_json(), "\"/home/me\"");
    Ok(())
}
```

- `jqlite::parse(text)`, `jqlite::parse_with(text, &options)` give a `JqExpression`;
  `jqlite::evaluate(text, input)` parses and evaluates once.
- `JqExpression`: `evaluate(input)`, `evaluate_with(input, &variables)` (all outputs) and
  `for_each(input, variables, on_result)` (streamed). Variable names are jq identifiers without
  `$`; `ENV` is refused.
- `JqValue`: `Null`, `False`, `True`, `Number(f64)`, `String`, `Array(JqArray)`,
  `Object(JqObject)`; `JqValue::parse(json)`, `to_json()`, and `From` for the usual Rust types.
- `JqError`: `Parse { message, line, column }`, `Runtime { message, value }` (an uncaught error),
  `Halt { message, value, exit_code }` (`halt_error`; `halt` ends normally), `Variable`, `Json`.
- `JqResolver` (trait: `canonical_path`, `read`) with `JqFileResolver`; `JqClock` (trait: now and
  the zone) with `SystemClock` and `FixedOffsetClock`.

The five layers (`jqlite::values`, `syntax`, `binding`, `runtime`, `builtins`) are public so that
the tests and the benchmark runner can drive the engine; they are not a stable interface.

## Why `Arc`

Values are counted with `Arc`, not `Rc`. A literal of a program is a value inside the bound
program, the bound program is shared by every evaluation of the expression, and an expression
must evaluate on several threads at once: a value has to be `Send + Sync`. With `Rc`, either each
evaluation would copy the literals and its input, or there would be two kinds of values.

The cost is known: on the machine this was written on, a clone and a drop of an `Arc` take 7.8 ns,
of an `Rc` 0.5 ns. So the interpreter avoids counting rather than values being cheaper to count:
an input is passed borrowed or owned (`Cow`), the last use of a value moves it, and a chain of
keys (`.a.b.c`) is followed by reference. What lives inside one evaluation only, and so on one
thread, is counted with `Rc`: environment frames and path steps.

## Files

The five layers are folders of `src/`, one file per file of `dotnet/JQLite/Core` with the name of
`docs/porting.md` §1.3 (`Values/ValueOps.cs` is `values/value_ops.rs`), except:

| Reference | Here | Why |
| --- | --- | --- |
| `Values/JqArrayBuilder.cs`, `Values/JqObjectBuilder.cs` | no counterpart | An array or an object with one owner is changed in place: it is its own builder. |
| `Runtime/StackGuard.cs` | `src/stack_guard.rs`, beside the layers | The parser and the regex engine use it too, and a layer uses only the layers before it (see Differences). |

Outside the core, the host boundary:

| Reference | Here |
| --- | --- |
| `Jq.cs`, `JqExpression.cs`, `JqOptions.cs` | `jq.rs`, `jq_expression.rs`, `jq_options.rs` |
| `JqException.cs` (`JqException`, `JqParseException`, `JqHaltException`) | `jq_error.rs` (`JqError`) |
| `JqResolver.cs`, `JqFileResolver.cs` | `jq_resolver.rs`, `jq_file_resolver.rs` |
| `JqResourceResolver.cs` | no counterpart |
| `Json/JsonElementAdapter.cs` | `json.rs`: the one place where host values and JSON text become `JqValue` and back |
| `Host/ResolverSource.cs`, `Host/TimeProviderClock.cs` | `host/resolver_source.rs`, `host/clock.rs` |

The prelude is `include_str!("../../../spec/prelude.jq")` (`binding/prelude.rs`): the file shared
by every port, outside this folder. To publish the crate (`cargo package`), copy it into the crate
and change that path first: a package cannot include a file above its root.

## Differences from the reference

The engine is the reference's, layer by layer and file by file. What differs is listed here, with
the reason.

### Values

| Difference | Reason |
| --- | --- |
| `JqValue` is an enum of 16 bytes; a string is `Arc<String>`, an array `Arc<Vec<JqValue>>`, an object `Arc` of its entries. The operations that give a new value take the old one by value and change it in place when it has one owner (`Arc::make_mut`); a shared one is copied first. | Guide §2.9. The reference gets the same effect with a shared buffer, a window and a `Used` counter for arrays, and builders for objects; ownership makes both unnecessary: there is no builder (an owned container is its own), and `reduce range(n) as $i ([]; . + [$i])` is linear. |
| A slice of a shared array copies its elements. | It follows from the line above: there is no window on a shared buffer. A slice of an array owned once is taken in place. |
| A string is appended to in place when it has one owner. | Free with `Arc<String>`, and what jq does; the reference's strings are the host's. |
| Objects scan their keys up to 16 entries (the reference: 8) before they build an index. | An index is two allocations; see Performance. |
| The JSON reader gives equal keys one shared string (a table of 256 short keys per thread), and creates each container once, with its size. | A string is two allocations, and keys repeat from one object to the next. |
| Values, AST nodes, bound nodes, frames and path steps are dropped by loops, not by recursion (`DropLevel`, `drop_pending`, the `Drop` of `Node` and `Env`). | Rust drops recursively by default: a value 10,000 levels deep, or a pipe of 100,000 bindings, would exhaust the stack when it is released. The reference has a garbage collector. |
| Equality and ordering recurse for 32 levels, then go on with an explicit stack (`walk_deep`). | The explicit stack alone costs an allocation per comparison of containers. |
| A fallible operation returns `Result<JqValue, JqValue>` (`Fallible`), the error being the error value. | Rust's form of `TryX(…, out result, out error)`. |
| `utf8.rs` keeps the reference's function names over `std`. | A Rust string is UTF-8, and `from_utf8_lossy` replaces maximal subparts as spec §4.1 says. |

### Syntax and binding

| Difference | Reason |
| --- | --- |
| The parser returns `Option` and records its one error (`fail`); the lexer works on a `Vec<char>`. | Positions are code points; no exceptions. |
| The parser asks the stack guard before each nested expression. The guard is therefore not in `runtime` but beside the layers (`src/stack_guard.rs`): it is host-specific, knows nothing of jqlite, and three layers use it (parser, interpreter, regex engine). | A level of nesting takes about 2 KB of stack in a release build and 9 KB in a debug build: the 1,000 levels of spec §11 do not fit the 1 MB stack of a Windows thread otherwise. The reference keeps the parser's frames small enough instead. |
| The text of a token is `Cow<'static, str>`: punctuation borrows its text from the lexer's table. | One allocation less per operator. |
| `Binder::is_variable` marks the binder `$x` alone. | See frames, below. |

### Runtime

| Difference | Reason |
| --- | --- |
| A signal is a boxed value with a serial number, returned as `Result<(), Signal>` (`Flow`); a consumer stamps the signals it returns (`Consumer`, `Interpreter::mark`). | Guide §2.4: signals move by value, so they have no identity to compare. `Result` rather than the guide's `ControlFlow` because `?` then propagates a signal. |
| An input is `Cow<JqValue>`; `eval_one` returns `Result<Option<(JqValue, Path)>, Signal>`. | The last use of a value moves it, which keeps accumulators owned once (and so changed in place) and saves atomic counts. |
| `Path` is an enum: `Off`, `Untracked`, `At(steps)`. | The three states of the reference's nullable path and mode flag. |
| A frame holds one value (`Frame::Value`), a list of values, the arguments of a call with the caller's environment (`Frame::Closures`), or a label. A loop (`E as $x`, `reduce`, `foreach`) uses its frame again for the next value when nothing kept it (`Env::rebind`). | One allocation per binding instead of two, and none in a loop. One environment for all the arguments of a call instead of a closure object per argument. |
| The interpreter is used through `&self`; its counters and stacks are `Cell` and `RefCell`. | The callbacks of nested evaluations all hold the interpreter. |
| The stack guard continues on a new stack segment (`stacker::grow`, 8 MB) instead of a new thread. | The host's mechanism (guide §2.10); the check is one comparison with the address of a local. |
| How an evaluation ended is the enum `Outcome`. | Rust's form of the reference's result with a kind. |
| `.[]?` on what cannot be iterated yields nothing without creating the error. | The error would be written (a message with the value's text) and dropped unseen, at every leaf that `..`, `paths` and `walk` visit. |
| In value mode, a chain of literal keys is followed by reference; a literal key or right operand is used where it is; `.[]` on an array owned once moves its elements out. | Fewer atomic counts; the results are the same. |
| `foreach` sets its state after EXTRACT has run on it. | EXTRACT then does not share the state, which stays owned once. |

### Builtins

| Difference | Reason |
| --- | --- |
| The natives are function pointers in a `Registry`, of two types: value natives and closure natives. | No reflection; the catalogue (`natives.rs`) is the reference's data. |
| `_modify` and `_assign`: the value under update is changed in place by `setpath`; there are no drafts. The last write waits until the next read or write, or until the paths have all been generated (`WorkingTree` in `assign_natives.rs`); an update with one path at most takes the value out of the tree first. | The reference's working tree exists because its values cannot be changed in place. The write waits because the paths are generated lazily from the input, which shares its containers with the tree until the last path is out: writing at once would copy a container per path (`INDEX` was quadratic). Errors are raised when the reference raises them (`try_prepare_set`). |
| Regular expressions are translated (`regex_syntax.rs`) for an engine that works on code points: `\x{…}` escapes, explicit classes, `(?s:.)`, `\z`, `(?-u:\b)`; the engine's offsets are bytes and are converted to code points. | Guide §3.3. No surrogate pairs to handle. |
| A pattern has two limits, with one reason: `Regex failure: the pattern is too large`. At most 1,000 groups are open at once (`regex_syntax::MAX_NESTING`), and the compiled pattern takes at most 10 MiB (`regex_engine::SIZE_LIMIT`: about 200,000 repeated literals, or 10,000 repeated classes as wide as `.`). | The `regex` crate unrolls counted repetitions and compiles groups by recursion, and the dialect bounds neither a nest of `{n,m}` nor the nesting of groups. The crate's own defaults (250 levels of its syntax tree, its message) are replaced: its nesting limit is lifted and a nested pattern is compiled on a stack of its size. The reference has other limits: any nesting, but .NET's linear-time engine refuses a pattern of more than 1,000 automaton nodes (`a{1000}`). |
| `regex_natives.rs` keeps the last prepared searches per thread; `regex_engine.rs` caches compiled patterns per process behind a mutex. | A program applies the same pattern to every record. |
| `_min_by_impl` and `_max_by_impl` with fewer keys than elements use the elements that have a key. | The reference indexes out of range there (an unhandled host exception); the prelude never calls them that way. |
| `match` in path mode gives its outputs no path, as spec §8.1 says. | The reference gives a different error there (see the report). |

### Host boundary

| Difference | Reason |
| --- | --- |
| One error type, the enum `JqError`, returned in `Result`. | Rust has no exceptions; the reference's three exception types are its first three variants. |
| The default clock is the system clock **in UTC**; `FixedOffsetClock` and the `JqClock` trait give another zone. | The standard library cannot tell the local zone, and the crate has no dependency for it. |
| Values cross the boundary as `JqValue`, the engine's own type, made from JSON text (`JqValue::parse`) or from Rust values (`From`), and written with `to_json`. | The standard library has no JSON type to adapt, as `JsonElement` is in .NET; `json.rs` is the counterpart of `JsonElementAdapter`. |
| `parse(expression)` and `parse_with(expression, &options)`; no `parse(expression, resolver)`. | Rust has no overloads; `JqOptions { resolver: Some(…), ..Default::default() }` says it. |
| `evaluate(input)`, `evaluate_with(input, &variables)`, `for_each(input, variables, on_result)`. | The reference's overloads of `Evaluate`, named. |
| No resource resolver (`JqResourceResolver`). | A Rust binary has no embedded resources to look up by name: a host that embeds modules with `include_str!` implements `JqResolver` over them in a few lines. |
| No JSONL helpers (`EvaluateAsync`). | They are host conveniences over a stream of values; the standard library has no asynchronous streams. |
| The benchmark runner is a package of its own (`bench/`), a member of the workspace. | It needs a build script (the compiler's version, for its `runtime` field); the library has none. |

### Tests of the reference that are not ported as they are

| Reference test | Here |
| --- | --- |
| Lone surrogates in `JqValue.FromString` and in the lexer's source; `Well_formed_strings_are_not_copied` | A Rust string is well formed. The decoding of ill-formed UTF-8 (`@base64d`, `@urid`) is tested instead. |
| `Text_is_culture_invariant` | Rust formats and parses numbers without a culture. |
| `Builder_replaces_in_place_and_removes`, `Slices_share_storage` | There are no builders and slices copy: ported as tests of an object changed in place and of slices that never change. |
| The tests that measure allocated bytes with the garbage collector's counters | `tests/linear.rs`, with a counting allocator. |
| `An_exception_of_the_callback_propagates`, the JSONL helpers | No exceptions, no such helpers. |

## Performance

The runner (`bench/src/main.rs`) drives the core engine as `bench/README.md` says, and its outputs
equal `bench/cases/<name>.expected` byte for byte for the twelve cases. What profiling found (a
counting allocator, and a sampler of the evaluating thread):

- **Allocation is the cost.** On Windows an allocation and its release take 50 to 90 ns with the
  system allocator, more than most steps of the interpreter. Removed: the list of a one-variable
  frame and the frame itself in loops, the argument lists of natives, one string per object key
  and the growth of containers in the JSON reader, the index of objects of 9 to 16 keys, the list
  of keys per path in updates, the error of `.[]?`.
- **A value is two allocations** when it is a string, an array or an object (`Arc<String>`,
  `Arc<Vec>`): the price of changing them in place without `unsafe` code. A program that creates
  many small containers pays it.
- **Compiling** (parse and bind) allocates a box per node of the two trees and a string per name:
  about 2,700 allocations for the 6 KB WhatsApp filter. It takes about twice the reference's time
  there; an expression is parsed once and evaluated many times.
- **A host that evaluates a lot** can install a faster global allocator (`#[global_allocator]`) in
  its binary; the library does not choose one.
