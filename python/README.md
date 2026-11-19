# jqlite for Python

The Python port of [jqlite](../spec/jqlite.md), the profile of the [jq](https://jqlang.org) language
whose reference implementation is the C# engine in [`dotnet/JQLite/Core`](../dotnet/JQLite/Core). It
is a 1:1 port, file by file, of that engine ([`docs/porting.md`](../docs/porting.md)), proven by the
same conformance corpus ([`spec/tests`](../spec/tests)). Python 3.11 or later, standard library only.

## Use

```python
import jqlite

jqlite.evaluate(".a + 1", {"a": 41})                  # [42]

expression = jqlite.parse("[.[] | select(. > $min)]")  # parse and bind once
expression.evaluate([1, 5, 10], {"min": 4})            # [[5, 10]], from any thread

# Stream the results, and stop when you have enough (returning False stops; it is not an error).
seen = []
jqlite.parse("range(1000000000)").stream(None, lambda r: seen.append(r) or len(seen) < 3)

# Options: modules, $ENV, clock and zone, the depth limit, debug and stderr handlers.
options = jqlite.Options(
    resolver=jqlite.FileResolver("."),         # import/include: files relative to the importing one
    environment={"HOME": "/home/me"},          # $ENV; None (default) is os.environ
    max_depth=1000,                            # calls of jq functions active at once (spec §11), 0 to 100,000
    debug=print, stderr=print,
)
jqlite.parse("$ENV.HOME", options).evaluate(None)   # ["/home/me"]

# One evaluation per value of a JSON Lines stream (also evaluate_each_async).
for result in jqlite.evaluate_each(".name", [{"name": "a"}, {"name": "b"}]):
    print(result)

try:
    jqlite.evaluate("error({code: 1})", None)
except jqlite.JqHaltError as e:      # halt_error: e.value, e.exit_code
    ...
except jqlite.JqParseError as e:     # syntax or binding error: e.line, e.column, e.module
    ...
except jqlite.JqError as e:          # uncaught error: e.value; str(e) is spec §10.1's message
    ...
```

Values are Python's JSON values, as `json.loads` gives them. In: `int` becomes the nearest `float`,
tuples are lists, any mapping with string keys an object, and a lone surrogate in a string or a key
becomes U+FFFD (§4.1). Out: each value as `json.loads` would read its canonical text (§5.1): a
number without fraction below 1e21 in magnitude is an `int`, other numbers are `float`, NaN is `None`,
±Infinity is ±1.7976931348623157e+308, and objects keep their key order. Host variables are checked
before evaluation (`ValueError` for a name that is not an identifier, or `ENV`). `halt` ends an
evaluation normally.

The callbacks (`stream`'s, `debug`, `stderr`, a resolver's methods) run on the thread that
parses or evaluates: the library starts no thread. `max_depth` is an `int` from 0 to 100,000
(`jqlite.Options` raises `ValueError` otherwise): 100,000 levels of calls run on a thread with a
256 KB stack and take about 70 MB while they are active (0.7 KB a level).

Importing `jqlite` changes nothing in the process. While a parse or an evaluation runs, the
library raises Python's recursion limit (`sys.setrecursionlimit`, a setting of the process) to
what that parse or evaluation may need (spec §11 lets a program meet no recursion limit but
`max_depth`); when the last parse or evaluation that runs ends, on any thread, the limit is put back
to what it was. The host's own code running meanwhile, its callbacks included, sees the raised limit.

Differences from the .NET API: no resource resolver (the reference's `JqResourceResolver` reads
.NET embedded resources; implement `Resolver` to read from package data or anywhere else), no
`IObservable` evaluation (`stream` with a callback, `evaluate_each` and `evaluate_each_async` cover
streaming), and values are Python's JSON values instead of `JsonElement`.

## Build, test, check

There is nothing to build. From this folder:

```
python -m unittest                 # 2,509 tests: the corpus both ways, the unit tests of every layer
python -m compileall -q jqlite tests crossbench.py
python -m mypy                     # optional tools (pip install mypy ruff), configured in pyproject.toml
python -m ruff check
```

- `tests/test_conformance.py`: every case of `spec/tests/*.test` (read from the shared folder), one
  test each, through the public API (`CorpusPublicApi`) and through the core with every prelude
  builtin bound to its definition (`CorpusWithoutNativeOverrides`): 1,108 + 1,108 tests.
  `localtime.test` runs with a zone fixed at UTC+02:00, `modules.test` over `spec/tests/modules/`.
- `tests/test_catalogue.py`: the N and P lists of §9, read from the spec, against the native
  catalogue and the prelude; the package's `jqlite/prelude.jq` against `spec/prelude.jq`.
- `tests/test_values.py`, `test_syntax.py`, `test_binding.py`, `test_runtime.py`: the reference's
  `dotnet/Tests/Core/<Layer>` tests, ported.
- `tests/test_host.py`: the boundary, resolvers, threads, the depth limit through the API.
- `tests/test_portability.py`: the portability rules of `AGENTS.md` on the core's code.

`jqlite/prelude.jq` is a copy of `spec/prelude.jq` (package data); when the spec's prelude changes,
copy it again (`cp ../spec/prelude.jq jqlite/prelude.jq`): `test_the_embedded_prelude_is_spec_prelude_jq`
fails until then.

## Benchmark

`bench.json` runs `python crossbench.py <program.jq> <input.json> <take> <warmup-ms> <measure-ms>
<output-file>` (the protocol of [`bench/README.md`](../bench/README.md)), on the core engine.
`pwsh ../bench/run.ps1 -Impl python` builds the .NET baseline and runs both.

## Layout

| Reference (`dotnet/JQLite/Core`) | Python (`jqlite/`) |
| --- | --- |
| `Values/*.cs` | `values/jq_value.py`, `jq_array.py`, `jq_array_builder.py`, `jq_object.py`, `jq_object_builder.py`, `jq_string.py`, `code_points.py`, `utf8.py`, `value_ops.py`, `value_text.py`, `errors.py` |
| `Syntax/*.cs` | `syntax/token.py`, `lexer.py`, `ast.py`, `parser.py`, `syntax_error.py` |
| `Binding/*.cs` | `binding/bound.py`, `resolver.py`, `prelude.py`, `modules.py` |
| `Runtime/*.cs` | `runtime/interpreter.py`, `env.py`, `path.py`, `signal.py`, `evaluation.py`, `stack_guard.py`, and `native_table.py` (`Builtins/NativeTable.cs`, see below) |
| `Builtins/*.cs` | `builtins/natives.py`, `core_natives.py`, `loop_natives.py`, `collection_natives.py`, `containment.py`, `string_natives.py`, `path_natives.py`, `assign_natives.py`, `formats.py`, `math_natives.py`, `math_extra.py`, `calendar.py`, `strftime.py`, `date_natives.py`, `regex_syntax.py`, `regex_engine.py`, `regex_natives.py`, `module_natives.py`, and `registration.py` |
| the .NET boundary (`Jq.cs`, `JqExpression.cs`, …) | `__init__.py` (`parse`, `evaluate`, `evaluate_each`), `expression.py`, `options.py`, `resolvers.py`, `exceptions.py`, `json_adapter.py` |

Host-specific files (**H** in the guide), each saying in its header what it uses: `values/value_text.py`
(`repr` and `float` for number text), `values/utf8.py` (the UTF-8 codec), `binding/prelude.py`
(`importlib.resources`), `syntax/recursion_limit.py` (`sys.setrecursionlimit`, see *The host
stack* below) and `runtime/stack_guard.py` (the frames an evaluation may need),
`builtins/math_natives.py` (`math`), `builtins/regex_engine.py` (`re`).

Other host uses in the core, each the counterpart of something every language has:

- `struct` in `builtins/math_extra.py`: the bit casts between a double and a 64-bit integer
  (`BitConverter.DoubleToInt64Bits` in the reference) of `nextafter` and of exact powers of two.
- `threading.Lock`, for what is done once per process or shared between threads, as the
  reference's locks and static constructors: `values/jq_array.py` (the claim of a shared buffer's
  next slot, arrays, objects and strings), `binding/prelude.py` (the prelude, bound once),
  `builtins/core_natives.py` (the list of `builtins`, computed once), `builtins/registration.py`
  (the native table, filled once) and `syntax/recursion_limit.py` (the count of the parses and
  evaluations that run).
- Two caches of 256 entries, emptied when full: `builtins/regex_engine.py` (compiled patterns, as
  the reference) and `builtins/regex_syntax.py` (translations, by pattern and flags). The
  translation is a pure function of the pattern and the flags and a map is in every language, so
  the cache stays beside the translator; the reference translates on every call, which costs
  little in C# and most of a short match in Python.

## Differences from the reference's structure

Each is deliberate; the behaviour is the spec's in every case (the corpus runs both ways).

- **Values.** A value is Python's own `None`, `bool`, `float`, `str` or a `JqArray`/`JqObject`
  (guide §2.1), or a `JqString` where `values/jq_string.py` allows one (see *Strings*); the engine
  tests `type(v)` exactly, so `bool` is never a number, and no `int` enters.
  For a type checker `JqValue` is `Any`: the engine dispatches on a saved `type(v)`, which checkers
  do not follow to narrow a union.
- **Fallible operations** return the result or a `Fail(error)` (guide §2.2) instead of `bool` and two
  `out` parameters.
- **`eval_one`** returns the output, `NOTHING` (no output) or a `Signal`, and leaves the output's
  path in `self.at`, instead of the tuple the guide suggests for Python: no allocation per node.
  `NOTHING` is a `Signal` object that is never a signal, so one type test tells a value from the rest.
- **Dispatch by table**: the non-tail constructs of `eval` and `eval_one` are handlers in two lists
  indexed by node kind (`_MANY`, `_OTHER`); the tail positions stay loops, as in the reference.
  A few fast paths avoid calls without changing the order of evaluation: `.name` on the input, a
  literal or `.` operand of one operator, `as $x` bindings and `reduce … as $x` without matching.
- **Arrays and objects** (guide §2.9): the buffer is a growable Python list; the latest version
  appends in place, the check and the append done under one lock (the reference's compare-and-swap).
  Every object keeps its key index (a dict shared by the versions of a buffer, extended under the
  same lock): a dict lookup is faster in Python than the reference's scan of up to 8 keys.
- **Strings** are Python `str`, already code points: no code point map, no UTF-16 ordering fix.
  `sanitize` also joins a high and a low surrogate that a host `str` holds as two code points.
  A `+` on strings that gives 256 code points or more makes a `JqString` (guide §2.9: a buffer of
  `str` parts, joined once when read), which the next `+` extends in place, so `add` and `join` are
  linear. Unlike the reference's, it is not a value every part of the engine takes: only the values
  layer and the interpreter see one, which turns it into a `str` where it enters a container, a
  native, a template or a format, or reaches the host (the list is in `values/jq_string.py`), so the
  natives and the boundary test `type(v) is str` as before. `LongStringTests` runs programs through
  each of those places with every concatenation a `JqString`; running the whole suite with
  `MIN_LENGTH = 1` checks the corpus the same way.
- **UTF-8** uses Python's codec (`errors="replace"` gives one U+FFFD per maximal subpart, as §4.1
  says, guide §3.2) instead of the reference's loops.
- **Sorting** uses Python's stable sort (the same order as the reference's stable merge sort, by the
  same total preorder), keyed by tuples for scalars and arrays of them, by `compare` otherwise.
- **The JSON reader** keeps the reference's algorithm and messages, with fast paths for strings
  without escapes and for plain integers.
- **The native table** (guide §1.1) is `runtime/native_table.py`, filled by
  `builtins/registration.py` when the package is imported; the formats of §9.9, which the
  interpreter applies, are reached through it too (`native_table.FORMATS`), so the runtime does not
  import the builtins.
- **Modules**: `BindImports` and `BindModule`, a partial class of the Resolver in C#, are the
  functions `bind_imports` and `bind_module` of `binding/modules.py`.
- **The host stack** (guide §3.6): CPython 3.11+ runs Python-to-Python calls without growing the C
  stack, and nothing on the recursive paths of the parser, the resolver or the interpreter calls
  back into Python through C, so no thread is needed (the reference continues on a fresh thread):
  `max_depth` 100,000 runs on a 256 KB thread (`test_the_largest_max_depth_runs_on_a_small_stack`).
  What limits depth is the recursion limit, a counter of Python frames: `syntax/recursion_limit.py`
  raises it while a parse, a binding or an evaluation runs and puts it back when the last one ends.
  A parse needs 8 frames per level of nesting (`PARSE_FRAMES`); an evaluation 16 frames per node of
  the bound program (its `size`, prelude included) per level of `max_depth` (`stack_guard.py`), a
  loose bound that costs nothing until the frames exist.
- **Regex** (guide §3.3): `re` works on code points, so the translator writes code point classes
  directly (no surrogate pairs) and the natives need no code point map; `\b` and `\B` are lookarounds
  as in the reference (`re`'s own `\B` never matches in an empty string). Translations are cached
  (see above).
- **Math** (guide §3.5): `math` raises where IEEE gives a value; each library function checks its
  domain or catches the overflow and returns the IEEE result. `math.fma` (3.13+) raises on
  overflow; there, and before 3.13, `fma` is computed exactly with `fractions.Fraction`.
- **`s * n`** fails only past 2^31 − 1 UTF-8 bytes, the spec's limit: Python has no smaller string
  limit (the reference's .NET limit stops `"ab" * 1e9`).
- **Tests left out**, because they are about .NET: the size of a value (16 bytes), the culture of
  number text (`repr` has none), and the tests of the two .NET APIs this port does not have
  (`JqObservableTests`, `JqResourceResolver`). The allocation measurements of `CollectionTests` and
  `AssignTests` are replaced by structural checks (one buffer shared by 10,000 appends, or by
  100,000 string appends) and, for the native `_modify`, by the growth of its time from 4,000 to
  32,000 elements (linear, not quadratic as the prelude's definition, which the test also measures).
- **`JqArray` and `JqObject`** have no `__len__`: a value is never tested for truth (an empty
  array would be false), the engine reads `count`.

The single-call rule of spec §7.7 and §11 (commit `3074345`: `Function.single`,
`CallFunction.single`, `Native.single_when`, the resolver's `Defining` and replay, `eval_one` of
calls and closure natives) is ported, with the reference's two unit tests.
