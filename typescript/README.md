# jqlite for TypeScript

The TypeScript port of jqlite, the profile of the [jq](https://jqlang.org) language specified in
[`spec/jqlite.md`](../spec/jqlite.md). It is a port, file by file, of the reference engine
[`dotnet/JQLite/Core`](../dotnet/JQLite/Core) as [`docs/porting.md`](../docs/porting.md) describes
it, and passes the shared conformance corpus [`spec/tests`](../spec/tests).

- ESM, strict TypeScript, no run-time dependency.
- The engine and the main entry run in a browser as well as in Node; only `jqlite/node` (the
  file resolver) needs Node.

## Build, test, benchmark

Node 20 or later (developed on Node 24), npm.

```
npm ci            # typescript and @types/node, the only (development) dependencies
npm run build     # copies spec/prelude.jq into src/binding/preludeSource.ts, then tsc to dist/
npm test          # builds, then runs every test with node:test
npm run check     # tsc --noEmit, strict: once with Node's types, once without for the core
```

`npm test` runs 2,589 tests:

| Tests | What |
| --- | --- |
| `test/conformance.test.ts` | every case of `spec/tests/*.test` (1,108), read from the shared folder, **through the public API** and **through the core without the native overrides**; and each file once more with plain values as input and output |
| `test/values`, `test/syntax`, `test/binding`, `test/runtime` | the unit tests of `dotnet/Tests/Core`, ported (264 tests); the catalogue against the N and P lists of §9; the limits of §11 |
| `test/builtins` | the regex engine, which is this port's own: random patterns against a plain backtracking matcher of the same programs, empty iterations as jq 1.8.2 reports them, patterns and inputs that need linear time, the limits of a pattern |
| `test/host` | the public API: options, errors, variables, modules, depth, evaluations at once |
| `test/portability.test.ts` | the portability rules of `AGENTS.md`, checked on the sources |

The cross-language benchmark ([`bench/README.md`](../bench/README.md)): `bench.json` builds with
`npm ci` and `npm run build` and runs `node dist/bench/crossBench.js`, the runner on the core
engine. `node scripts/bench-check.mjs [warmup-ms measure-ms]` runs it on every case of
`bench/cases.json` and compares each output file with its `.expected` file, byte for byte.

## The API

```ts
import { Jq, JqError } from "jqlite";

// Parse once, evaluate many times.
const expression = Jq.parse(".items[] | select(.price > $min) | {name, total: (.price * .count)}");
expression.evaluate({ items: [{ name: "a", price: 3, count: 2 }, { name: "b", price: 1, count: 9 }] }, { min: 2 });
// → [{ name: "a", total: 6 }]

// Stream the outputs and stop early.
Jq.parse("range(1e9)").evaluateEach(null, (n) => (n as number) < 2);

// Text in, text out: the canonical JSON text of each output.
Jq.parse(".a").evaluateText('{"a":[1,2.50]}'); // → ["[1,2.5]"]

try {
  Jq.evaluate('error("boom")', null);
} catch (e) {
  if (e instanceof JqError) console.log(e.message, e.value); // boom boom
}
```

| | |
| --- | --- |
| `Jq.parse(expression, options?)` | parses and binds once into a `JqExpression`, immutable; throws `JqParseError` (`line`, `column`) |
| `expression.evaluate(input, variables?)` | all the outputs, as plain values |
| `expression.evaluateEach(input, onResult, variables?)` | each output as it is produced; `onResult` returns false to stop |
| `expression.evaluateText(inputText, variables?)` | JSON text in, the canonical text of each output out |
| `Jq.evaluate(expression, input, variables?)` | parse with the default options and evaluate |
| `Jq.evaluateAsync(expression, inputs, variables?)` | an async generator over the outputs of each input of an (async) iterable, each evaluated alone (JSON Lines) |
| `JqError` | an uncaught error: `message` (§10.1) and `value`; `JqHaltError` adds `exitCode` (`halt_error`; `halt` ends normally) |

Options (`JqOptions`), read when the expression is parsed:

| Option | Meaning |
| --- | --- |
| `resolver` | a `JqResolver` (`getCanonicalPath(path, kind, metadata, fromPath)`, `open(canonicalPath)`) for `import`, `include` and `modulemeta`. `JqFileResolver` (files, relative to the importing module) is in `jqlite/node` |
| `environment` | the object of `$ENV`, a plain object or a `Map`; default: `process.env` where there is one |
| `clock` | `now()`, `offset(seconds)` and optionally `abbreviation(seconds)`; default: the system clock and zone, from `Date`, without abbreviations (`%Z` prints the offset) |
| `maxDepth` | the call depth limit of §11, default 1,000 |
| `debug`, `stderr` | receive the values of `debug` and `stderr`; default: dropped |

Host variables are given per evaluation, by name without `$`, as a plain object or a `Map`; a
name that is not a jq identifier, or `ENV`, is a `TypeError` before anything is evaluated.

### Plain values and key order

Inputs and outputs are JavaScript's own JSON values. Three things differ from the engine's values
(`src/json/jsonValueAdapter.ts`):

- **Key order.** A jqlite object keeps the insertion order of its keys (§4.1). A plain object
  does too, except for keys that look like array indexes (`"0"`, `"42"`): JavaScript enumerates
  those first, in numeric order. So `{"b": 1, "10": 2}` given as a plain object arrives as
  `"10", "b"`, and an output whose order is `"b", "10"` enumerates `"10"` first once it is a
  plain object. Every other key keeps its position. For the exact order, give a `Map` as input
  (its entries in order) and take the outputs with `evaluateText`, which the engine writes itself.
- **Numbers.** An output is what its canonical text reads back as (§5.1): NaN is `null`,
  ±Infinity is ±1.7976931348623157e308; `-0` stays `-0`. Inputs are taken as they are.
- **Strings.** A lone surrogate becomes U+FFFD on the way in (§4.1), in values and in keys.

A property that is `undefined` is left out and an `undefined` array element is `null`, as in
`JSON.stringify`; a function, a symbol or a bigint is a `TypeError`. Deep values convert without
recursion.

### Threads

One thread runs JavaScript at a time and workers share no objects, so "one expression evaluated
from many threads" has two counterparts here, both tested: evaluations of one expression started
while another is running (from a result callback or a `debug` handler), and workers that each
parse and evaluate the same program. An expression holds no state of an evaluation.

## Recursion and the host stack

§11: `MaxDepth` is the only limit on recursion a program may meet. The reference is a recursive
tree walker that continues on a fresh thread stack when the host stack runs short (`StackGuard`).
JavaScript can neither grow its stack nor switch to another, and a recursive walker ported as it
is needs several host frames for each call of a jqlite function, and as many again for each
level of nesting inside a function body: no fixed stack covers `MaxDepth` × nesting.

Measured on Node 24 with its default stack (984 KB), by letting the direct evaluation described
below recurse without its bound: it is the cheapest recursive form, three frames a level and no
callbacks, and it overflows (`RangeError`) between 2,895 and 3,618 nested `[…]`, and between
1,853 and 2,316 nested `(… | .) + 1`. A call in the recursive walker costs at least such a level
(`Eval`, `EvalMany`, the construct, `Eval` again) plus the callbacks its output comes back
through, for every level of nesting between two calls: a recursion 1,000 calls deep fits only
when the function body is almost flat and the caller's own stack is shallow, and a higher
`MaxDepth` does not fit at all.

So the interpreter of this port **does not use the host stack for the evaluation**
(`src/runtime/interpreter.ts`). It keeps the reference's shape, `eval(node, env, input, path,
emit)` with one function per construct, as a trampoline:

- `eval`, `forEach`, `output` and `ret` only *schedule* a step, and one loop runs the steps;
- what the reference does after a call returns is a callback (`After`) on the interpreter's own
  stack, an array on the heap; `return signal` is `ret(signal)`.

Function calls, generators feeding generators, nesting and long sequences therefore cost heap,
not stack. **What it guarantees: any `MaxDepth`**, as far as memory allows. The tests run
recursions to exactly `MaxDepth` for 1,000, 5,000, 20,000 and 200,000 (and 1,000,000 through the
API) on Node's default stack, and on worker threads with stacks of 256 KB and 1 MB; past the
limit the error is always `Maximum call depth exceeded`. Nothing catches `RangeError`, lowers
the limit or asks for `--stack-size`.

The direct evaluation of the reference (`EvalOne`: no callbacks, no allocation) is kept for what
needs no trampoline: nodes that yield at most one output without calling a jqlite function, a
closure parameter or a native that takes closures, nested at most 64 levels (`direct` in
`src/binding/bound.ts`). That is most of the arithmetic, indexing and construction of a program,
and its recursion on the host stack is bounded by a constant.

What still uses the host stack, by a bounded amount:

- the **parser** recurses per nesting level, as the reference's (three frames a level); at the
  limit of 1,000 levels it needs about half of Node's default stack (the limit tests pass with
  `--stack-size=550` and not with `450`; the default is 984). The binder, the value operations,
  the JSON reader and writer and the plain-value adapter use explicit stacks, as in the reference;
- **patterns** whose keys are direct are destructured by a recursion per level of pattern nesting
  (at most 1,000, the parser's limit). A pattern with a key of several outputs is destructured in
  a loop over what is left of it (`matchRest`), each key a step of the trampoline: the number of
  its variables and its nesting use no host stack.

## Differences from the reference's structure

The files, types and functions have the reference's names (`src/values/valueOps.ts` is
`Values/ValueOps.cs`, `tryAdd` is `TryAdd`). What differs, and why:

| Where | Difference | Why |
| --- | --- | --- |
| `runtime/interpreter.ts` | a trampoline: `Emit` and the functions that evaluate return nothing and schedule steps; `After` callbacks on an explicit stack replace the code after a call; loops that give outputs (`.[]`, `range`, `limit`…) are callbacks that push themselves. No `runtime/stackGuard.ts` | the host stack cannot grow (above) |
| `runtime/interpreter.ts` | a single call (§7.7) leaves the depth when it gives its output (`leaving`) and is back in it when the consumer returns (`reenter`), instead of `EvalCallOne` | calls are never evaluated on the host stack; the depth a program observes is the same |
| `binding/bound.ts` | every node has `height` and `direct`, a binder `keysHeight` and `keysDirect`, beside the reference's `atMostOne` | which nodes `evalOne` takes; the interpreter tests `direct` where the reference tests `AtMostOne` |
| `builtins/*Natives.ts` | natives that take closures (`path`, `limit`, `range`, `until`, `match`, `_modify`…) are written in the same scheduling style; registered with `addClosure`, value natives with `add` | they evaluate their arguments through the interpreter |
| `builtins/regexEngine.ts`, `regexSyntax.ts` | the engine is this port's own, and `RegexSyntax` translates a pattern to its instructions instead of to a host pattern text. The matcher reads the input once and follows every way the program can match at the same time, in priority order (a Pike machine): the time is the input times the pattern, the memory is the pattern's size, and a search for the leftmost match does not start again at each position. An iteration that matches nothing ends its repetition, counted or not, as in jq. Its limits, all `Regex failure: pattern too large`: 200,000 instructions (counted repetitions are written out), 200,000 states more than instructions (nested repetitions that can match nothing), and 2^24 capture slots for the threads together (instructions that read a code point × 2 · (groups + 1)) | ECMAScript resets the captures of a group at each iteration of an enclosing repetition; §9.10 wants its last participation (`"ab" \| match("(?:(a)\|b)+")` captures `"a"`), and no rewriting of a pattern gives that with `RegExp`; it also backtracks, so `"a" * 28 + "c" \| test("^(a*)*b$")` would take seconds where §13 promises linear time. Nothing uses the host's regex |
| `values/jqValue.ts` | a value is the host's `null`, `boolean`, `number`, `string`, or a `JqArray`/`JqObject`; no `FromX`/`AsX`; a fallible operation returns `JqValue \| Fail`; a missing key is `undefined` | the guide's shape for TypeScript (§2.1, §2.2) |
| `values/jqArray.ts`, `jqObject.ts` | the shared buffer is a JavaScript array: its length is the reference's `Used`, no compare-and-swap, no lock; the empty array and object are never extended in place | one thread; arrays grow by themselves |
| `values/valueText.ts` | `formatNumber` is `String(x)` (ECMAScript's `Number::toString` is the spec's layout), the reader converts with `Number(text)` after checking the grammar | host-specific step of the guide (§3.1) |
| `values/valueOps.ts` | `%` goes through `BigInt` beyond 2^53; the longest repeated string is V8's limit | 64-bit integers; the host's string limit (§4.4) |
| `builtins/mathExtra.ts` | `fma` computes the exact sum with `BigInt` and rounds once; `pow` is `Math.pow` with the two C99 cases JavaScript differs in; bit casts are typed arrays | JavaScript has no fused multiply-add (§9.8) |
| `builtins/calendar.ts` | `seconds` goes through `BigInt` when a field is beyond 1e9 | the reference computes in 64-bit integers; a number is exact to 2^53 |
| `binding/modules.ts`, `resolver.ts` | the part of `Resolver` that the reference keeps in `Modules.cs` (a partial class) is two functions of `modules.ts`; the members of `Resolver` they use are marked internal | no partial classes |
| `binding/prelude.ts`, `preludeSource.ts` | the prelude is a string constant generated from `spec/prelude.jq` by `scripts/generate-prelude.mjs` at build time, with LF line ends; a test compares the two | no text import that every bundler and runtime share |
| `builtins/nativeTable.ts` | filled on first use, not in a static constructor | the modules of the families and the table import each other |
| out parameters | results a function returns beside its value are fields read at once: `interpreter.has/value/at`, `ValueOps.sliceFirst/sliceLast` | the guide's scratch object (§2.3) |
| `syntax/parser.ts` | `ParseReduceOrForeachHead` is part of `parseReduceOrForeach`; the token kind is read through `kind()` | the split kept .NET frames small; the accessor stops the compiler narrowing across `advance()` |

The host boundary (`src/*.ts`, `src/json`, `src/host`) is this port's own, with the features of
the .NET one: `Jq`, `JqExpression`, `JqOptions`, `JqResolver`, `JqFileResolver`, `JqError`
(`JqException`), `JqParseError`, `JqHaltError`. There is no resolver over embedded resources.

Unit tests of the reference left out, with their reason in the test files: the size of the value
struct and the accessors that throw (`CollectionTests`), appends from several threads (replaced
by many versions from one base), the current-culture test (`ValueTextTests`); the allocation
tests check time instead of bytes; tests that ran on 1 MB threads run on Node's main stack.
