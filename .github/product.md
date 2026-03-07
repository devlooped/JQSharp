# jq# Implementation Plan

Phased implementation plan for the jq# (jqsharp) C# implementation of the jq language.
Each phase builds on the previous ones and unlocks subsequent capabilities.

## Currently Implemented

- **Basic selectors:** `.`, `.foo`, `.foo.bar`, `.[string]`, `.[number]`, `.[n:n]`, `.[]`, `.[]?`
- **Composition:** `|` (pipe), `,` (comma), `()` (parenthesis)
- **Constructors:** `[expr]` (array), `{key: value}` (object)
- **Recursive descent:** `..`
- **Arithmetic:** `+`, `-`, `*`, `/`, `%` (with type-aware behavior for strings, arrays, objects)
- **Unary negation:** `-expr`
- **Comparison:** `==`, `!=`, `<`, `>`, `<=`, `>=`
- **Conditionals:** `if-then-elif-else-end`
- **Optional:** `?` (error suppression)
- **Literals:** `null`, `true`, `false`, numbers, strings (with escape sequences)

---

## Implementation Phases

### - [x] Phase 1 — Comments

- `#` line comments (with `\` continuation to next line)

### - [x] Phase 2 — Logical Operators & Alternative Operator

- `and`, `or` as infix keyword operators (new precedence levels between comma and comparison)
- `not` as a builtin filter (piped: `true | not`)
- `//` (alternative operator — produces non-false/non-null values from left, or falls back to right)

### - [x] Phase 3 — Builtin Function Infrastructure & Zero-Argument Builtins

Parser and evaluator infrastructure to recognize bare identifiers as builtin function calls. Implement all zero-argument builtins:

- **Generator:** `empty`
- **Type introspection:** `type`, `length`, `utf8bytelength`, `infinite`, `nan`, `isinfinite`, `isnan`, `isfinite`, `isnormal`
- **Type selectors:** `arrays`, `objects`, `iterables`, `booleans`, `numbers`, `normals`, `finites`, `strings`, `nulls`, `values`, `scalars`
- **Object/array:** `keys`, `keys_unsorted`, `reverse`, `sort`, `unique`, `flatten`, `add`, `any`, `all`, `min`, `max`, `to_entries`, `from_entries`, `paths`, `transpose`, `combinations`
- **Conversion:** `tonumber`, `tostring`, `toboolean`, `tojson`, `fromjson`, `explode`, `implode`, `ascii_downcase`, `ascii_upcase`
- **Math:** `abs`, `floor`, `sqrt`
- **String:** `trim`, `ltrim`, `rtrim`
- **Other:** `recurse` (0-arg), `halt`, `env`, `builtins`, `first`, `last` (array accessors)

### - [x] Phase 4 — String Interpolation

- `\(exp)` inside string literals — parser change to handle nested expressions within strings

### - [x] Phase 5 — try-catch & Error Handling

- `try EXP catch EXP`
- `try EXP` (shorthand for `try EXP catch empty` — depends on `empty` from Phase 3)
- `error` (0-arg, throws input as error)
- Refactor existing `?` operator as syntactic sugar for `try EXP`

### - [x] Phase 6.1 — Parser: Parameterized Call Syntax

Parser and evaluator infrastructure to support parameterized function calls:

- `name(expr)` — single-argument call syntax
- `name(expr; expr; ...)` — multi-argument call syntax (semicolon-separated)

### - [x] Phase 6.2 — Type/Value Testing & Selection

Parameterized builtins that test or filter values:

- `has(key)` — test object key / array index existence
- `in` — reverse of `has`
- `select(boolean_expression)` — filter values by condition
- `contains(element)` — recursive containment check
- `inside` — reverse of `contains`
- `isempty(exp)` — true if expression produces no outputs
- `any(condition)`, `all(condition)` — test array elements with a condition
- `any(generator; condition)`, `all(generator; condition)` — test generator outputs

### - [x] Phase 6.3 — String Operations

Parameterized string manipulation builtins:

- `startswith(str)`, `endswith(str)` — prefix/suffix tests
- `ltrimstr(str)`, `rtrimstr(str)`, `trimstr(str)` — strip prefix/suffix
- `split(str)` — split string on separator
- `join(str)` — join array elements with separator

### - [x] Phase 6.4 — Array/Collection Transformation

Parameterized builtins that transform arrays or collections:

- `map(f)`, `map_values(f)` — apply filter to array elements / object values
- `with_entries(f)` — apply filter to `to_entries` then convert back
- `flatten(depth)` — flatten with depth limit
- `combinations(n)` — n-fold Cartesian product
- `add(generator)` — reduce generator outputs with `+`

### - [x] Phase 6.5 — Sorting, Grouping & Extrema

Parameterized ordering and grouping builtins:

- `sort_by(path_expression)` — sort array by key
- `group_by(path_expression)` — group array elements by key
- `unique_by(path_exp)` — deduplicate by key
- `min_by(path_exp)`, `max_by(path_exp)` — extrema by key

### - [x] Phase 6.6 — Search & Indexing

Parameterized search builtins:

- `index(s)`, `rindex(s)` — first/last occurrence of value or subsequence
- `indices(s)` — all occurrence positions
- `bsearch(x)` — binary search in sorted array

### - [x] Phase 6.7 — Path Expressions & Structural Manipulation

Parameterized builtins operating on paths and structure:

- `path(path_expression)` — emit path arrays for matching nodes
- `paths(node_filter)` — all paths whose leaf satisfies a filter
- `pick(pathexps)` — build object/array from selected paths
- `del(path_expression)` — delete value(s) at path(s)
- `getpath(PATHS)` — get value at a path array
- `delpaths(PATHS)` — delete multiple paths
- `setpath(PATHS; VALUE)` — set value at a path array

### - [x] Phase 6.8 — Generators & Iteration

Parameterized generator and iteration control builtins:

- `range(upto)` / `range(from; upto)` / `range(from; upto; by)` — numeric range generator
- `limit(n; expr)` — take first n outputs of expr
- `skip(n; expr)` — skip first n outputs of expr
- `first(expr)`, `last(expr)` — first/last output of expr
- `nth(n)` / `nth(n; expr)` — nth output
- `while(cond; update)` — emit values while condition holds
- `until(cond; next)` — iterate until condition holds
- `repeat(exp)` — repeat expression indefinitely (used with `limit`)
- `recurse(f)`, `recurse(f; condition)` — parameterized recursive descent
- `walk(f)` — bottom-up recursive traversal applying f

### - [x] Phase 6.9 — Error Control & Special

Remaining parameterized builtins:

- `error(message)` — throw a custom error message
- `halt_error(exit_code)` — halt with a specific exit code
- `$__loc__` — special variable yielding current source location

### - [x] Phase 7 — Variables & Binding

- `EXP as $identifier | ...` binding syntax
- Destructuring: `. as [$a, $b]`, `. as {$a, $b}`, `. as {key: $var}`
- Variable scoping infrastructure
- `$ENV` variable

### - [x] Phase 8 — reduce & foreach

- `reduce EXP as $var (INIT; UPDATE)` — depends on variable binding (Phase 7)
- `foreach EXP as $var (INIT; UPDATE; EXTRACT)` — depends on variable binding (Phase 7)

### - [x] Phase 9 — User-Defined Functions

- `def name: body;`
- `def name(f)` with filter arguments
- `def name($var)` with value arguments (sugar for `def name(f): f as $var | ...`)
- Function scoping, recursion, multiple definitions by arity

### - [x] Phase 10 — Assignment Operators

- Path expression infrastructure (tracking paths through filter evaluation)
- Update-assignment: `|=`
- Plain assignment: `=`
- Arithmetic update-assignment: `+=`, `-=`, `*=`, `/=`, `%=`, `//=`
- Complex assignments (LHS with iterators, `select`, etc.)

### - [x] Phase 11 — Format Strings & Escaping

- `@base64`, `@base64d`, `@html`, `@uri`, `@urid`, `@csv`, `@tsv`, `@sh`, `@json`, `@text`
- Combined format with interpolation: `@foo "string with \(expr)"`

### - [x] Phase 12 — Regular Expressions

- `test(val)`, `test(regex; flags)`
- `match(val)`, `match(regex; flags)`
- `capture(val)`, `capture(regex; flags)`
- `scan(regex)`, `scan(regex; flags)`
- `split(regex; flags)` (2-arg overload)
- `splits(regex)`, `splits(regex; flags)`
- `sub(regex; tostr)`, `sub(regex; tostr; flags)`
- `gsub(regex; tostr)`, `gsub(regex; tostr; flags)`

### - [x] Phase 13 — Math Functions

- One-input: `acos`, `acosh`, `asin`, `asinh`, `atan`, `atanh`, `cbrt`, `ceil`, `cos`, `cosh`, `erf`, `erfc`, `exp`, `exp2`, `expm1`, `fabs`, `floor`, `log`, `log10`, `log2`, `round`, `sin`, `sinh`, `tan`, `tanh`, `trunc`, etc.
- Two-input: `atan2`, `pow`, `fmax`, `fmin`, `fmod`, `hypot`, `remainder`, etc.
- Three-input: `fma`

### - [x] Phase 14 — Date Functions

- `now`, `todate`, `fromdate`, `todateiso8601`, `fromdateiso8601`
- `strptime(fmt)`, `strftime(fmt)`, `strflocaltime(fmt)`
- `gmtime`, `localtime`, `mktime`

### - [x] Phase 15 — Advanced Control Flow

- `label $name | ... break $name ...`
- Destructuring alternative operator `?//`
- SQL-style operators: `INDEX`, `IN`, `JOIN`

### - [x] Phase 16 - Streaming

Implement support for JSONL/NDJSON async streaming

### - [x] Phase 16.1 — Include Modules 

- `include RelativePathString [<metadata>];`

Uses a JqResolver (inspired by XmlUrlResolver) to resolve RelativePathString > TextReader.
Module content is cached by path to avoid redundant parsing.

### - [ ] Phase 16.2 — Import Modules 

- `import RelativePathString as NAME [<metadata>];`
- `import RelativePathString as $NAME [<metadata>];`

### - [ ] Phase 16.3 — Modules Metadata 

- `module <metadata>;`
- `modulemeta`

## Out of Scope

Features from the jq manual we will not implement.

### I/O

- `input`, `inputs`
- `debug`, `debug(msgs)`, `stderr`
- `input_filename`, `input_line_number`

### Streaming

- `tostream`, `fromstream(stream_expression)`, `truncate_stream(stream_expression)`
