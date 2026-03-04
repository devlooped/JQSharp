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

### - [ ] Phase 3 — Builtin Function Infrastructure & Zero-Argument Builtins

Parser and evaluator infrastructure to recognize bare identifiers as builtin function calls. Implement all zero-argument builtins:

- **Generator:** `empty`
- **Type introspection:** `type`, `length`, `utf8bytelength`, `infinite`, `nan`, `isinfinite`, `isnan`, `isfinite`, `isnormal`
- **Type selectors:** `arrays`, `objects`, `iterables`, `booleans`, `numbers`, `normals`, `finites`, `strings`, `nulls`, `values`, `scalars`
- **Object/array:** `keys`, `keys_unsorted`, `reverse`, `sort`, `unique`, `flatten`, `add`, `any`, `all`, `min`, `max`, `to_entries`, `from_entries`, `paths`, `transpose`, `combinations`
- **Conversion:** `tonumber`, `tostring`, `toboolean`, `tojson`, `fromjson`, `explode`, `implode`, `ascii_downcase`, `ascii_upcase`
- **Math:** `abs`, `floor`, `sqrt`
- **String:** `trim`, `ltrim`, `rtrim`
- **Other:** `recurse` (0-arg), `halt`, `env`, `builtins`, `first`, `last` (array accessors)

### - [ ] Phase 4 — String Interpolation

- `\(exp)` inside string literals — parser change to handle nested expressions within strings

### - [ ] Phase 5 — try-catch & Error Handling

- `try EXP catch EXP`
- `try EXP` (shorthand for `try EXP catch empty` — depends on `empty` from Phase 3)
- `error` (0-arg, throws input as error)
- Refactor existing `?` operator as syntactic sugar for `try EXP`

### - [ ] Phase 6 — Parameterized Builtin Functions

Parser support for `name(expr)` and `name(expr; expr; ...)` call syntax. Implement all parameterized builtins:

- **1-arg:** `has`, `map`, `map_values`, `select`, `contains`, `inside`, `startswith`, `endswith`, `ltrimstr`, `rtrimstr`, `trimstr`, `split`, `join`, `flatten(n)`, `sort_by`, `group_by`, `unique_by`, `min_by`, `max_by`, `index`, `rindex`, `indices`, `any(cond)`, `all(cond)`, `add(gen)`, `recurse(f)`, `bsearch`, `paths(filter)`, `in`, `combinations(n)`, `walk`, `del`, `path`, `pick`, `getpath`, `delpaths`, `isempty`, `halt_error(code)`, `error(msg)`
- **Multi-arg:** `range(upto)` / `range(from; upto)` / `range(from; upto; by)`, `any(gen; cond)`, `all(gen; cond)`, `recurse(f; cond)`, `limit(n; expr)`, `skip(n; expr)`, `first(expr)`, `last(expr)`, `nth(n)` / `nth(n; expr)`, `while(cond; update)`, `until(cond; next)`, `repeat(exp)`, `with_entries(f)`, `setpath(path; value)`
- **Special:** `$__loc__`

### - [ ] Phase 7 — Variables & Binding

- `EXP as $identifier | ...` binding syntax
- Destructuring: `. as [$a, $b]`, `. as {$a, $b}`, `. as {key: $var}`
- Variable scoping infrastructure
- `$ENV` variable

### - [ ] Phase 8 — reduce & foreach

- `reduce EXP as $var (INIT; UPDATE)` — depends on variable binding (Phase 7)
- `foreach EXP as $var (INIT; UPDATE; EXTRACT)` — depends on variable binding (Phase 7)

### - [ ] Phase 9 — User-Defined Functions

- `def name: body;`
- `def name(f)` with filter arguments
- `def name($var)` with value arguments (sugar for `def name(f): f as $var | ...`)
- Function scoping, recursion, multiple definitions by arity

### - [ ] Phase 10 — Assignment Operators

- Path expression infrastructure (tracking paths through filter evaluation)
- Update-assignment: `|=`
- Plain assignment: `=`
- Arithmetic update-assignment: `+=`, `-=`, `*=`, `/=`, `%=`, `//=`
- Complex assignments (LHS with iterators, `select`, etc.)

### - [ ] Phase 11 — Format Strings & Escaping

- `@base64`, `@base64d`, `@html`, `@uri`, `@urid`, `@csv`, `@tsv`, `@sh`, `@json`, `@text`
- Combined format with interpolation: `@foo "string with \(expr)"`

### - [ ] Phase 12 — Regular Expressions

- `test(val)`, `test(regex; flags)`
- `match(val)`, `match(regex; flags)`
- `capture(val)`, `capture(regex; flags)`
- `scan(regex)`, `scan(regex; flags)`
- `split(regex; flags)` (2-arg overload)
- `splits(regex)`, `splits(regex; flags)`
- `sub(regex; tostr)`, `sub(regex; tostr; flags)`
- `gsub(regex; tostr)`, `gsub(regex; tostr; flags)`

### - [ ] Phase 13 — Math Functions

- One-input: `acos`, `acosh`, `asin`, `asinh`, `atan`, `atanh`, `cbrt`, `ceil`, `cos`, `cosh`, `erf`, `erfc`, `exp`, `exp2`, `expm1`, `fabs`, `floor`, `log`, `log10`, `log2`, `round`, `sin`, `sinh`, `tan`, `tanh`, `trunc`, etc.
- Two-input: `atan2`, `pow`, `fmax`, `fmin`, `fmod`, `hypot`, `remainder`, etc.
- Three-input: `fma`

### - [ ] Phase 14 — Date Functions

- `now`, `todate`, `fromdate`, `todateiso8601`, `fromdateiso8601`
- `strptime(fmt)`, `strftime(fmt)`, `strflocaltime(fmt)`
- `gmtime`, `localtime`, `mktime`

### - [ ] Phase 15 — Advanced Control Flow

- `label $name | ... break $name ...`
- Destructuring alternative operator `?//`
- SQL-style operators: `INDEX`, `IN`, `JOIN`

### - [ ] Phase 16 — I/O

- `input`, `inputs`
- `debug`, `debug(msgs)`, `stderr`
- `input_filename`, `input_line_number`

### - [ ] Phase 17 — Streaming

- `tostream`, `fromstream(stream_expression)`, `truncate_stream(stream_expression)`

### - [ ] Phase 18 — Modules

- `import RelativePathString as NAME [<metadata>];`
- `include RelativePathString [<metadata>];`
- `import RelativePathString as $NAME [<metadata>];`
- `module <metadata>;`
- `modulemeta`
- Module search paths
