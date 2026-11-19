# jqlite 1.0

jqlite is the profile of the [jq](https://jqlang.org) language that JQLite implements and that its ports (JavaScript/TypeScript, Python, Java, Go, Rust) reproduce. This file is normative: a port is written from it, from `spec/prelude.jq` (the builtins written in jqlite, assembled from §9) and from the conformance corpus `spec/tests/` (§14), without reading the C# code or the jq manual.

Inside the profile jqlite behaves as **jq 1.8**, except for the differences listed in §13. "jq" below always means jq 1.8; every example of this file was checked against jq 1.8.2 and matches it unless it illustrates a difference of §13.

Contents: [1 Conventions](#1-conventions) · [2 Lexical grammar](#2-lexical-grammar) · [3 Syntax](#3-syntax) · [4 Values](#4-values) · [5 Text](#5-text) · [6 Evaluation](#6-evaluation) · [7 Constructs](#7-constructs) · [8 Paths and assignment](#8-paths-and-assignment) · [9 Builtins](#9-builtins) · [10 Errors](#10-errors) · [11 Limits](#11-limits) · [12 Host interface](#12-host-interface) · [13 Differences from jq](#13-differences-from-jq-18) · [14 Conformance](#14-conformance)

## 1. Conventions

- MUST, MUST NOT, MAY are normative. Everything not marked as an example or a note is normative.
- A *program* is jqlite source text. A *filter* is any expression; applied to one *input* value it produces a *stream* of zero or more *outputs*, or stops with a *signal* (§6.2).
- `T(v)` is the type name of a value (`null`, `boolean`, `number`, `string`, `array`, `object`). `D(v)` is the *error dump* of a value (§10.1).
- Code samples are jqlite. `p` → `a, b` means program `p` (input `null` unless shown) outputs `a` then `b`.
- Text is a sequence of Unicode code points. "Length", "index", "offset" of a string always count code points, never UTF-16 units or bytes, unless the text says *bytes* (then: UTF-8 bytes).
- Nothing in this profile depends on a host JSON library, regex engine, number formatter, locale, time-zone database or C library: every such behavior is defined here.

## 2. Lexical grammar

Source text is a sequence of code points. Tokens are matched longest-first.

| Token | Definition |
| --- | --- |
| whitespace | `U+0020`, `U+0009`, `U+000A`, `U+000D` (only these) |
| comment | `#` up to the end of the line. If the line ends with an odd number of `\` before the line break, the comment continues on the next line. Only U+000A ends a line (a lone U+000D does not: `1 # c` CR `+ 1` → `1`; with CRLF the CR is whitespace before the LF). |
| `IDENT` | `[A-Za-z_][A-Za-z0-9_]*` followed by any number of `::[A-Za-z_][A-Za-z0-9_]*` (a qualified name: `a::b::c`) |
| `FIELD` | `.` immediately followed by `[A-Za-z_][A-Za-z0-9_]*` (no whitespace between) |
| `VAR` | `$` immediately followed by `[A-Za-z_][A-Za-z0-9_]*` and any number of `::[A-Za-z_][A-Za-z0-9_]*` |
| `FORMAT` | `@` immediately followed by `[A-Za-z0-9_]+` |
| `NUMBER` | `([0-9]+(\.[0-9]*)?\|\.[0-9]+)([eE][+-]?[0-9]+)?` |
| `STRING` | `"` … `"`, see below |
| keywords | `def` `as` `if` `then` `elif` `else` `end` `reduce` `foreach` `try` `catch` `label` `break` `import` `include` `module` `and` `or` |
| punctuation | `.` `..` `[` `]` `{` `}` `(` `)` `\|` `,` `:` `;` `?` `?//` `=` `==` `!=` `<` `<=` `>` `>=` `+` `-` `*` `/` `%` `+=` `-=` `*=` `/=` `%=` `//` `//=` `\|=` |

Rules:

- Identifiers are ASCII only. Letters outside ASCII are a syntax error.
- Keywords are reserved as function names and bare identifiers. They are allowed after `.` (`FIELD`: `.end`), as object keys (`{if: 1}`), as variable and label names (`$then`, `label $if`), and as keys of object patterns.
- `null`, `true`, `false` as bare identifiers always denote the literals. `def true: 1;` is accepted but has no effect on them (`def true: 1; true` → `true`). `not` is an ordinary function (§9).
- `.` followed by a digit starts a `NUMBER` (`.5`). `1.` is a number. `.e0` is a `FIELD`.
- `?//` is valid only between the patterns of a binding (§3.2); anywhere else it is a syntax error (`.a?//1` fails; write `.a? // 1`).
- A `NUMBER` is converted to the nearest double (round half to even); overflow gives ±Infinity, underflow gives 0.

**Strings.** Inside `"…"` every code point except `"` and `\` stands for itself (raw line breaks included). Escapes:

| Escape | Meaning |
| --- | --- |
| `\"` `\\` `\/` | `"`, `\`, `/` |
| `\b` `\f` `\n` `\r` `\t` | U+0008, U+000C, U+000A, U+000D, U+0009 |
| `\uXXXX` | 4 hex digits (any case). A high surrogate escape immediately followed by a low surrogate escape is one code point; any other surrogate becomes U+FFFD (§13). |
| `\(` *Pipe* `)` | interpolation (§7.12) |

Any other escape is the syntax error `Invalid escape`.

**Positions.** Lines and columns are 1-based; lines are counted at U+000A only; columns count code points. Every syntax or binding error reports the position of the token where it was detected.

## 3. Syntax

### 3.1 Precedence

From loosest to tightest. Assoc: L left, R right, N non-associative (chaining is a syntax error).

| Level | Operators | Assoc | Notes |
| --- | --- | --- | --- |
| 1 | `\|` | R | `def …;`, `label $x \|` and `… as $x \|` bodies extend to the end of the enclosing pipe |
| 2 | `,` | L | |
| 3 | *Alt* `as` *Patterns* `\|` | — | the source of a binding, and of `reduce`/`foreach`, is a level-4 expression: everything that binds tighter than `,` |
| 4 | `//` | R | |
| 5 | `=` `\|=` `+=` `-=` `*=` `/=` `%=` `//=` | N | |
| 6 | `or` | L | |
| 7 | `and` | L | |
| 8 | `==` `!=` `<` `<=` `>` `>=` | N | |
| 9 | `+` `-` | L | |
| 10 | `*` `/` `%` | L | |
| 11 | unary `-` | — | its operand is a postfix expression or another unary minus: `-1 / 2` is `(-1) / 2` |
| 12 | postfix `?`, `.name`, `."s"`, `[…]`, `.[…]` | — | |
| 13 | terms | — | literals, `.`, `..`, `$x`, calls, `( )`, `[ ]`, `{ }`, `reduce`, `foreach`, `if`, `try`, `break`; the operands of `try` and `catch` are level-11 expressions |

Examples that follow from it:

| Program | Parses as | Output |
| --- | --- | --- |
| `1, 2 as $x \| $x * 10` | `1, (2 as $x \| $x * 10)` | `1, 20` |
| `1 + 2 as $x \| -$x` | `(1 + 2) as $x \| -$x` | `-3` |
| `[-1 as $x \| 1, $x]` | `[(-1) as $x \| (1, $x)]` | `[1,-1]` |
| `1 // 2 == 2` | `1 // (2 == 2)` | `1` |
| `{} \| .a = .b // 1` | `{} \| (.a = .b) // 1` | `{"a":null}` |
| `[1 // 2 as $x \| 10]` | `[(1 // 2) as $x \| 10]` | `[10]` |
| `reduce 1, 2 as $x (0; . + $x)` | | syntax error (the source stops before `,`) |
| `try (2 * -3 / "a") catch .` | `(2 * (-3)) / "a"` | `"number (-6) and string (\"a\") cannot be divided"` |
| `1 + try 2 catch 3 + 4` | `1 + (try 2 catch 3) + 4` | `7` |
| `try 1 * 2 catch 3` | `(try 1) * 2`, then `catch`: syntax error | |
| `try -.? catch .` on `"a"` | `try (-(.?)) catch .` | `"string (\"a\") cannot be negated"` |
| `def f: 1; f \| f, 2` | `def f: 1; (f \| (f, 2))` | `1, 2` |
| `{a: 1 \| 2, b: 3}` | `{a: (1 \| 2), b: 3}` | `{"a":2,"b":3}` |
| `{a: 1 as $x \| $x}` | | syntax error (no `as` in an object value) |

### 3.2 Grammar (EBNF)

`X?` optional, `X*` repetition, `'t'` a token, `A | B` alternatives (the `'|'` token is quoted).

```
Program     = ModuleDecl? Import* Body
ModuleDecl  = 'module' ConstObject ';'
Import      = 'import' PathString 'as' (IDENT | VAR) ConstObject? ';'
            | 'include' PathString ConstObject? ';'
PathString  = STRING                                  (no interpolation: "Import path must be constant")
Body        = FuncDef* Pipe?                          (only definitions: the program is `.`)
ModuleBody  = ModuleDecl? Import* FuncDef*            (a module file)

FuncDef     = 'def' IDENT Params? ':' Pipe ';'        (IDENT: not a keyword; it may contain '::', as `def f::g: 1;`)
Params      = '(' Param (';' Param)* ')'
Param       = IDENT | VAR                             (an IDENT parameter may contain '::')

Pipe        = Comma ('|' Pipe)?
Comma       = Item (',' Item)*
Item        = FuncDef Pipe                            (a definition consumes the rest of the pipe)
            | 'label' VAR '|' Pipe                    (idem)
            | Alt ('as' Patterns '|' Pipe)?           (a binding consumes the rest of the pipe)
Alt         = Assign ('//' Alt)?
Assign      = Or (AssignOp Or)?
AssignOp    = '=' | '|=' | '+=' | '-=' | '*=' | '/=' | '%=' | '//='
Or          = And ('or' And)*
And         = Compare ('and' Compare)*
Compare     = Add (CmpOp Add)?
CmpOp       = '==' | '!=' | '<' | '<=' | '>' | '>='
Add         = Mul (('+' | '-') Mul)*
Mul         = Unary (('*' | '/' | '%') Unary)*
Unary       = '-' Unary | Postfix
Postfix     = Term Suffix*
Suffix      = '?' | FIELD | '.' STRING | '.'? '[' Index? ']'
Index       = Pipe | Pipe ':' Pipe? | ':' Pipe         (Pipe alone: index; with ':' a slice with at least one bound; nothing: iterate)

Term        = '.' | '..' | NUMBER | String | FORMAT
            | FIELD | '.' STRING
            | VAR
            | '(' Pipe ')'
            | '[' Pipe? ']'
            | '{' (Entry (',' Entry)* ','?)? '}'       (one trailing ',' allowed: `{a: 1,}`)
            | IDENT ('(' Pipe (';' Pipe)* ')')?       (function call; null/true/false: literals)
            | 'reduce' Alt 'as' Patterns '(' Pipe ';' Pipe ')'
            | 'foreach' Alt 'as' Patterns '(' Pipe ';' Pipe (';' Pipe)? ')'
            | 'if' Pipe 'then' Pipe ('elif' Pipe 'then' Pipe)* ('else' Pipe)? 'end'
            | 'try' Unary ('catch' Unary)?
            | 'break' VAR
String      = FORMAT? STRING                          (STRING may contain interpolations)
Keyword     = any keyword of §2

Entry       = (IDENT | Keyword | VAR | String | '(' Pipe ')') ':' ObjVal
            | IDENT | Keyword | VAR | String          (shorthand, §7.11)
ObjVal      = Alt ('|' Alt)*                          (no ',', no `as`, no `def` or `label` at the top level)

Patterns    = Pattern ('?//' Pattern)*
Pattern     = VAR
            | '[' Pattern (',' Pattern)* ']'
            | '{' PEntry (',' PEntry)* '}'
PEntry      = VAR (':' Pattern)?
            | (IDENT | Keyword | String | '(' Pipe ')') ':' Pattern

ConstObject = '{' (CEntry (',' CEntry)*)? '}'         (a constant: "Module metadata must be constant")
CEntry      = (IDENT | Keyword | STRING) ':' Const
Const       = NUMBER | STRING | 'null' | 'true' | 'false'  (no sign, no operator: §13)
            | '[' (Const (',' Const)*)? ']' | ConstObject
```

Static rules (checked when the program is parsed and bound; violations are syntax/binding errors, §10.2):

- An object key that is not an identifier, keyword, variable, string or parenthesized expression is the error `May need parentheses around object key expression` (`{1+2: 3}`).
- A parenthesized key whose expression is a number, `true`, `false` or `null` literal is the error `Cannot use <T> (<D>) as object key` (`{(0): 1}`, `. as {(true): $x} | …`).
- `ModuleDecl` and `ConstObject` metadata must be constant objects (`Module metadata must be constant`, `Module metadata must be an object`).
- `def` and `label` start a pipe or a comma item only (`[1, def f: 2; f]`, `1 | label $x | 2`); as an operand (`1 + def f: 1; f`, `-def …`, `try def …`, `{a: def …}`) they are a syntax error.
- Program nesting is limited (§11).

### 3.3 Desugaring

The following are defined by rewriting; ports MAY implement them directly with the same results.

| Form | Means |
| --- | --- |
| `..` | `recurse` |
| `E?` | `try E` |
| `.name`, `."s"` | `.["name"]`, `.["s"]` |
| `T.name` | `T \| .name`, but the input of the index expression is the input of `T` (§7.6) |
| `if c then a end` | `if c then a else . end` (also for a final `elif`) |
| `try E` | `try E catch empty` |
| `{a}`, `{"s"}`, `{$x}` | `{a: .a}`, `{"s": ."s"}`, `{x: $x}` |
| `{"\(e)"}` | for each output `k` of the key (evaluated once), `{(k): .[k]}`: `{"x1":"a","x2":"b"} \| [{"x\(1,2)"}]` → `[{"x1":"a"},{"x2":"b"}]` (not the cartesian product of a rewrite to `{"\(e)": .["\(e)"]}`) |
| `def f($a; g): B;` | `def f(a; g): a as $a \| B;` (the filter `a` is also callable) |
| `L = R` | `_assign(L; R)` |
| `L \|= R` | `_modify(L; R)` |
| `L op= R` (`op` in `+ - * / %`) | `R as $v \| _modify(L; . op $v)` with a fresh `$v` |
| `L //= R` | `R as $v \| _modify(L; . // $v)` with a fresh `$v` |

## 4. Values

### 4.1 Kinds

There are seven kinds: `null`, `false`, `true` (both of type `boolean`), `number`, `string`, `array`, `object`. Values are immutable; no operation observes identity.

- **Numbers** are IEEE-754 binary64 doubles. NaN, +Infinity, −Infinity and −0 exist inside the engine. All arithmetic is IEEE (round half to even).
- **Strings** are sequences of Unicode scalar values (code points U+0000–U+10FFFF, excluding surrogates). Wherever text enters the engine (program literals, the JSON reader, host strings, `implode`, `@base64d`), a lone surrogate becomes U+FFFD, and bytes are decoded as UTF-8 with one U+FFFD per *maximal subpart* of an ill-formed sequence (below).

**Decoding UTF-8.** The well-formed sequences are exactly these (byte values in hex):

| Code points | Byte 1 | Byte 2 | Byte 3 | Byte 4 |
| --- | --- | --- | --- | --- |
| U+0000–U+007F | 00–7F | | | |
| U+0080–U+07FF | C2–DF | 80–BF | | |
| U+0800–U+0FFF | E0 | A0–BF | 80–BF | |
| U+1000–U+CFFF | E1–EC | 80–BF | 80–BF | |
| U+D000–U+D7FF | ED | 80–9F | 80–BF | |
| U+E000–U+FFFF | EE–EF | 80–BF | 80–BF | |
| U+10000–U+3FFFF | F0 | 90–BF | 80–BF | 80–BF |
| U+40000–U+FFFFF | F1–F3 | 80–BF | 80–BF | 80–BF |
| U+100000–U+10FFFF | F4 | 80–8F | 80–BF | 80–BF |

At each position: if the byte is not a first byte of the table (80–C1, F5–FF), it alone becomes one U+FFFD. Otherwise take the row of that first byte and consume the following bytes while each is in the range of its column; if the row is complete, it is one code point; if a byte is missing (end of input) or out of range, the bytes consumed so far (the maximal subpart) become one U+FFFD and decoding resumes at the byte that did not fit. This is what the standard decoders of JavaScript (`TextDecoder`), Python (`errors="replace"`), Java, Rust (`from_utf8_lossy`) and .NET do. Examples:

| Bytes | Result | Why |
| --- | --- | --- |
| C3 A9 | é | well-formed |
| ED A0 80 (a surrogate) | U+FFFD ×3 | A0 is outside ED's 80–9F: ED alone, then A0 and 80 (not first bytes) |
| E0 80 80 (overlong) | U+FFFD ×3 | 80 is outside E0's A0–BF |
| F4 90 80 80 (above U+10FFFF) | U+FFFD ×4 | 90 is outside F4's 80–8F |
| C0 A0 | U+FFFD ×2 | C0 is not a first byte |
| E2 82 41 | U+FFFD, `A` | E2 82 is a maximal subpart; 41 does not fit and starts again |
| E9 65 | U+FFFD, `e` | E9 alone; 65 starts again |
| F0 9F 98 (end of input) | U+FFFD | F0 9F 98 is a maximal subpart |
- **Arrays** are finite sequences of values.
- **Objects** are finite maps from strings to values that remember insertion order:
  - setting an existing key replaces its value in place; setting a new key appends it;
  - deleting a key removes it (adding it again appends);
  - `keys` sorts; iteration, `keys_unsorted`, `to_entries`, `tojson` use insertion order;
  - `a + b`: the keys of `a` in order (values of `b` win), then the new keys of `b` in order. `a * b` likewise, recursively.

**Truthiness**: `null` and `false` are false; every other value (including `0`, `""`, `[]`, NaN) is true.

### 4.2 Equality

`a == b` is true iff:

- both are numbers and `a = b` in IEEE (so `nan == nan` is false, `-0 == 0` is true);
- both are `null`, both `true` or both `false`;
- both are strings with the same code points;
- both are arrays of equal length with pairwise equal elements;
- both are objects with the same key set and equal values per key (key order is ignored).

### 4.3 Ordering

`compare(a, b)` is a total preorder used by `<`, `<=`, `>`, `>=`, `sort`, `group_by`, `unique`, `min`, `max`, `bsearch` and `delpaths`:

1. By kind: `null` < `false` < `true` < numbers < strings < arrays < objects.
2. Numbers: NaN is below every other number and equal to NaN; otherwise numeric order (−0 equals 0).
3. Strings: lexicographic by code point.
4. Arrays: lexicographic by element (`compare`), a proper prefix first.
5. Objects: first the arrays of their keys sorted by code point, compared as arrays of strings; if equal, the values compared key by key in that sorted order.

`a < b` is `compare(a, b) < 0`, and so on. Note: `nan < nan` is false and `nan <= nan` is true, while `nan == nan` is false (§13).

Example: `{"a":2,"b":1} < {"a":1,"c":0}` → `true` (keys `["a","b"] < ["a","c"]`).

### 4.4 Arithmetic

Binary operators on values `a` (left) and `b` (right). Any combination not listed is the error of the last column.

| Op | Operands → result | Error otherwise |
| --- | --- | --- |
| `+` | `null + x` = `x`; `x + null` = `x`; numbers: sum; strings: concatenation; arrays: concatenation; objects: merge (§4.1) | `<T> (<D>) and <T> (<D>) cannot be added` |
| `-` | numbers: difference; arrays: `a` without every element equal to some element of `b` | `… cannot be subtracted` |
| `*` | numbers: product; string × number or number × string: repetition (below); objects: deep merge (below) | `… cannot be multiplied` |
| `/` | numbers: quotient, error if `b == 0` (also `0/0`); strings: `a \| split(b)` | `… cannot be divided` |
| `%` | numbers: integer remainder (below) | `… cannot be divided (remainder)` |

- **Division by zero**: `<T> (<D>) and <T> (<D>) cannot be divided because the divisor is zero`.
- **Remainder** `a % b`: if `a` or `b` is NaN the result is NaN. Otherwise each is converted to a 64-bit signed integer: truncate toward zero, then clamp to [−2^63, 2^63−1] (±Infinity clamp to the ends). If the divisor is 0: `<T> (<D>) and <T> (<D>) cannot be divided (remainder) because the divisor is zero`. If the divisor is −1 the result is 0. Otherwise the result is the C remainder (`ia − ib·trunc(ia/ib)`, sign of `ia`), computed exactly and converted to a double. Example: `[(infinite, -infinite) % (1, -1, infinite)]` → `[0,0,0,0,0,-1]`.
- **Repetition** `s * n`: if `n` is NaN or `n < 0` the result is `null`; otherwise `s` repeated `trunc(n)` times (`"x" * 0` → `""`, `"ab" * 1.5` → `"ab"`). If the result would exceed 2^31−1 UTF-8 bytes: `Repeat string result too long`; a host that cannot hold a shorter string than that (its own string length limit) reports the same error.
- **Deep merge** `a * b` (objects): like `+`, except that when both values of a key are objects, they are merged with `*` recursively.
- **Unary minus** `-x`: numbers only, IEEE negation (`0 | -.` is −0); otherwise `<T> (<D>) cannot be negated`.

## 5. Text

### 5.1 Canonical JSON writer

One writer is used by `tojson`, `tostring`, `@json`, `@text`, string interpolation, `@csv`/`@tsv`/`@sh` numbers and the error dump. Output is compact: no whitespace anywhere.

- `null`, `true`, `false` as such.
- Numbers: §5.2.
- Strings: `"`, then each code point: `"` → `\"`; `\` → `\\`; U+0008 → `\b`; U+000C → `\f`; U+000A → `\n`; U+000D → `\r`; U+0009 → `\t`; other U+0000–U+001F and U+007F → `\u` and 4 lowercase hex digits; every other code point as itself (no escaping of `/`, non-ASCII or U+2028); then `"`.
- Arrays: `[` elements separated by `,` `]`. Objects: `{` `"key":value` pairs in insertion order separated by `,` `}`.
- A value nested deeper than 10001 levels (the value written is level 1; scalars count) is written as the text `<skipped: too deep>`: `reduce range(10001) as $i (1; [.]) | tojson` ends with `[<skipped: too deep>]]]…`, while `reduce range(10000) as $i (1; [.])` is written whole.

Example: `"é<" | tojson` → `"\"é<\""`.

### 5.2 Number formatter

- NaN → `null`. +Infinity → `1.7976931348623157e+308`, −Infinity → `-1.7976931348623157e+308`.
- −0 → `-0`; +0 → `0`.
- Otherwise the result is ECMAScript `Number::toString(x)` (ECMA-262 §6.1.6.1.20) for radix 10: let `n`, `k`, `s` be integers with `k ≥ 1`, `10^(k−1) ≤ s < 10^k`, `s × 10^(n−k)` = x, and `k` as small as possible (the shortest digit string that reads back as x; if several, the one closest to x). Let `d` be the `k` digits of `s` (a minus sign precedes negative numbers). Then:
  - `k ≤ n ≤ 21`: `d` followed by `n − k` zeros (`1e20` → `100000000000000000000`);
  - `0 < n ≤ 21`: the first `n` digits, `.`, the remaining digits (`1.5`);
  - `−6 < n ≤ 0`: `0.`, `−n` zeros, `d` (`0.000001`);
  - otherwise `d₁`, then `.` and the other digits if `k > 1`, then `e`, then `+` or `-`, then `|n − 1|` (`1e+21`, `1e-7`, `1.5e+300`).

### 5.3 JSON reader

Used by `fromjson`, `tonumber`, module data files and the conformance harness. It reads RFC 8259 JSON with these rules:

- Whitespace: space, tab, LF, CR. A leading U+FEFF is ignored.
- Literals `null`, `true`, `false`.
- Numbers: the *numeric text* grammar `[+-]? ( [0-9]+ ('.' [0-9]*)? | '.' [0-9]+ ) ([eE] [+-]? [0-9]+)?`, plus `[+-]?` followed by `nan` or `snan` (NaN) or `inf` or `infinity` (±Infinity), these words in any ASCII case. The token must be followed by whitespace, `,`, `]`, `}`, `:`, `"` or the end (`NaN1` is an error).
- Strings: escapes as in JSON (`\" \\ \/ \b \f \n \r \t \uXXXX`, surrogates as in §2, a lone one becomes U+FFFD); an unescaped code point below U+0020 is an error.
- Duplicate object keys: the later value replaces the earlier one, at the earlier position.
- Containers nested deeper than 10000 levels: the error `Exceeds depth limit for parsing`.
- `fromjson` requires exactly one value (only whitespace around it): no value is `Expected JSON value`, more than one is `Unexpected extra JSON values`.
- Errors: `<reason> (while parsing '<text>')` where `<text>` is the whole input string. `<reason>` is `Expected JSON value` (no value) or `Unexpected extra JSON values` (more than one) without a position, and otherwise a description followed by ` at line L, column C` whose wording is implementation-defined (the reference uses jq's: `Unfinished JSON term at EOF`, `Invalid literal`, `Invalid numeric literal`, `Objects must consist of key:value pairs`, …). The depth limit's reason is `Exceeds depth limit for parsing`.

## 6. Evaluation

### 6.1 Streams and environments

A filter is evaluated with an *input* value and an *environment* and produces outputs one at a time, in a defined order. The environment binds, lexically: variables (`$name` → value), functions (`name/arity` → definition with its own defining environment), closure parameters (`name/0` → an argument expression with the caller's environment) and labels (`$name` → a label instance).

Evaluation is lazy: an output is produced only when the consumer asks for it, and a consumer that stops (`limit`, `first`, `label`/`break`, an error) stops the producers. No construct evaluates more outputs of a sub-expression than its semantics needs.

### 6.2 Signals

Besides producing outputs, evaluation may stop with one of three signals. A signal propagates outward through every construct except the one that handles it:

| Signal | Raised by | Handled by |
| --- | --- | --- |
| `Error(v)` | `error`, failed operations (§10) | `try … catch`, `?`, `?//` (all alternatives but the last) |
| `Break(l)` | `break $name` | the label instance `l` it names (§7.15) |
| `Halt(v?, code)` | `halt`, `halt_error` | the host only |

Outputs produced before a signal are kept.

**Errors of the consumer are not errors of the producer.** A construct that handles errors (`try`, `?`, `?//`) handles only errors raised while evaluating its own operand, never an error raised by whatever consumes the outputs of that operand. For `?//` the operand is the destructuring and the body `B` of the binding (or the `UPDATE` and `EXTRACT` of `reduce`/`foreach`, §7.8, §7.9); an error raised after the binding's outputs leave it is the consumer's. Examples: `"x" | try ((try . catch "too much") | error) catch "right"` → `"right"`; `[[1]] | try ((.[] as [$a] ?// $a | $a) | error(tostring)) catch .` → `"1"` (§13).

Note: the reference interpreter is a push-based tree walker, `Eval(node, env, input, path, emit) → Signal?`, where `emit(value, path)` returns the consumer's signal; an error-handling construct tells its operand's own errors from the consumer's by marking the signals that come back from `emit`.

### 6.3 Order of outputs

When a construct combines several sub-streams, it enumerates their cartesian product as nested loops. Unless stated otherwise in §7, the order is:

| Construct | Outermost … innermost (innermost varies fastest) |
| --- | --- |
| `a op b` (arithmetic, comparison) | `b`, `a` — the left operand varies fastest |
| `T[e]`, `T[e1:e2]` | `e` (or `e1`, `e2`), then `T` |
| `{k1: v1, k2: v2}` | `k1`, `v1`, `k2`, `v2` — later entries vary fastest |
| `"a\(x)b\(y)"` | `y`, `x` — earlier interpolations vary fastest |
| `f($a; $b)` (prelude or user `$` parameters) | `$a`, `$b` |
| native with several value arguments | last argument, …, first argument — the first varies fastest (exception: `range/2`, §9.4) |

Examples: `[(1,2) + (10,20)]` → `[11,12,21,22]`; `[{a:(1,2), b:(3,4)}]` → `[{"a":1,"b":3},{"a":1,"b":4},{"a":2,"b":3},{"a":2,"b":4}]`; `[range(0,1; 3,4)]` → `[0,1,2,0,1,2,3,1,2,1,2,3]`.

## 7. Constructs

### 7.1 Identity, literals, recursion

- `.` outputs its input. Literals output themselves. `..` is `recurse` (every value of the input, pre-order: the value, then recursively each element or object value in order).
- `empty` outputs nothing.

### 7.2 Pipe and comma

- `a | b`: for each output of `a`, the outputs of `b` applied to it.
- `a, b`: the outputs of `a`, then the outputs of `b`, both on the same input.

### 7.3 Operators

- Arithmetic and comparisons: §4.4, §4.2, §4.3, order §6.3.
- `a and b`: for each output `x` of `a`: if `x` is false, output `false`; otherwise for each output `y` of `b`, output the truthiness of `y`. `a or b`: for each `x` of `a`: if `x` is true, output `true`; otherwise for each `y` of `b`, output its truthiness. The right side is not evaluated when the left decides (`false and error("x")` → `false`).
- `not`: `if . then false else true end`.
- `a // b`: outputs the true outputs of `a`, as they come; if `a` produced no true output, the outputs of `b`. Errors raised by `a` propagate. Examples: `[(false, null, 1) // 42]` → `[1]`; `[(1, null, 2) // 3]` → `[1,2]`; `[empty // 3]` → `[3]`; `[(1, error("x")) // 3]` → error `x`.

### 7.4 Conditionals

`if c then a elif c2 then b else d end`: for each output of `c` (on the input), the outputs of `a` if it is true, else of the rest. A missing `else` is `.`. Example: `[if 1, null, 2 then 3 else 4 end]` → `[3,4,3]`.

### 7.5 Variables and bindings

- `E as $x | B`: for each output `v` of `E` (on the input), `B` on the same input with `$x` bound to `v`.
- `E as P | B` with a pattern `P` destructures each `v`:
  - `$name` binds `v`;
  - `[P1, …, Pn]` matches `Pi` against `v[i-1]` (index semantics, §7.6: `null` gives `null`, a non-array is an index error), from the last position to the first: `1 | . as [$a, $b] | 0` fails with `Cannot index number with number (1)`;
  - `{key: P, …}` matches `P` against `v[key]`, entries from the first to the last; `$name` binds `$name` to `v.name`; `$name: P` binds `$name` to `v.name` and also matches `P` against it; `(e): P` evaluates `e` with input `v` in the scope *outside* the binding (variables of the same pattern are not visible to it: `"z" as $x | {"a":"b","b":2} | . as {a: $x, ($x): $y} | [$x, $y]` → `["b",null]`; with no outer `$x`, the `$x` of the key is a host variable, §7.5) and matches `P` against `v[k]` for each output `k`, with the indexing rules of §7.6 whatever the kind of `k` (`[5,6] | . as {(.[0] * 0 + 1): $v} | $v` → `6`; `{"a":1} | . as {(.a): $v} | $v` fails with `Cannot index object with number (1)`; only a literal non-string key is rejected statically, §3.2); a key expression with several outputs gives several bindings, in order.
- **A variable twice in one pattern.** *Matching order* is: array elements from the last to the first, object entries from the first to the last, each nested pattern matched completely where it occurs. Without `?//`, the **first** occurrence in matching order gives the value; in a binding with `?//` (every alternative), the **last** one does. Examples: `[1,2] | . as [$a, $a] | $a` → `2`; `{"a":1,"b":2} | . as {$a, b: $a} | $a` → `1`; `[3,[1,2]] | . as [$a, [$a, $a]] | $a` → `2`; `[1,2] | . as [$a, $a] ?// $b | $a` → `1`; `{"a":1,"b":2} | . as {$a, b: $a} ?// $c | $a` → `2`. The same rule holds for the patterns of `reduce` and `foreach`.
- `E as P1 ?// P2 ?// … | B`: every variable of every pattern is bound in `B`; those absent from the pattern in use are `null`. For each output `v` of `E`, try `P1`: if destructuring fails, or `B` raises an error, try the next pattern with the same `v` (outputs already produced stay); errors with the last pattern propagate. Errors raised by the consumer of the binding's outputs are not handled (§6.2).
- Scope is lexical: a variable is visible in `B` (and nested definitions there), shadowing outer bindings of the same name.
- `$ENV` is predefined in the outermost scope: an object of the host environment variables (§12). It can be shadowed.
- `$__loc__` is the object `{"file": F, "line": L}`: `L` the line of its `$` (§2), `F` `"<top-level>"` in the main program and the module's identity in a module (§9.11). It is not a variable: `$__loc__` as a pattern, a parameter or a data import alias is a syntax error; `{$__loc__}` is `{"__loc__": $__loc__}`.
- **Host variables**: a `$name` (without `::`) that has no lexical binding and is not `$ENV` or `$__loc__` refers to a host-supplied variable, looked up when evaluated. If the host did not supply it: the error `$name is not defined`. This holds inside a module too: a module's unbound `$name` is the same host variable as in the main program (§13). A `$alias::name` must be bound by a data import (§9.11), otherwise it is a binding error.

### 7.6 Indexing

`T[e]` evaluates `e` with the input of `T` (not with the output of `T`): `{"a":{"x":1},"k":"x"} | .a[.k]` → `1`. For each output `k` of `e`, for each output `t` of `T`, output `t[k]`:

| `t` | `k` | Result |
| --- | --- | --- |
| object | string | the value, or `null` if absent |
| array | number | NaN → `null`; else `i = trunc(k)`, `i < 0` → `i + length`; out of range → `null` |
| array | array | the indices at which `k` occurs as a contiguous sub-array (`[]` if none; `k = []` → `[]`) |
| array, string | object | a slice (below); the object must have both keys `start` and `end`, each a number or `null`, else `Array/string slice indices must be integers` |
| null | string, number, object | `null` |
| anything else | | `Cannot index <T(t)> with <T(k)> (<D(k)>)` (`{} \| .[0]` → `Cannot index object with number (0)`; `1 \| .[1:2]` → `Cannot index number with object ({"start":1,"end":2})`) |

**Slices** `T[s:e]` (either bound may be omitted, which is `null`): with `len` the length (code points for strings): `s` null or NaN → 0, `e` null or NaN → `len`; negative bounds add `len`; clamp both to [0, `len`]; `s = floor(s)`, `e = ceil(e)`; if `e < s` then `e = s`. The result is the sub-array or substring `[s, e)`. `null` sliced is `null`. Bounds that are neither numbers nor null: `Array/string slice indices must be integers`. Examples: `[range(10)] | .[1.2:3.5]` → `[1,2,3]`; `"abcdefghi" | .[-2:]` → `"hi"`; `"aé😀b" | .[2:3]` → `"😀"`.

`T[]` outputs the elements of an array or the values of an object (insertion order); any other value: `Cannot iterate over <T> (<D>)` (`Cannot iterate over null (null)`).

`T?`, `T[…]?`: §7.13.

### 7.7 Functions

- `def f: B; R` and `def f(p1; …; pn): B; R` define `f/n` for `R` and for `B` itself (recursion). A definition is visible after its own `def` keyword only; later definitions with the same name and arity shadow earlier ones. Definitions inside a function body are local to it.
- A call `f(a1; …; an)` resolves at bind time to the innermost visible definition of `f/n`, else the builtin `f/n`, else the error `f/n is not defined`. A qualified name (`a::f`) is an ordinary name of the same lexical scope: `def a::f: 1; a::f` → `1` needs no import, and an import directive defines `alias::f/n` at that point for each export `f/n` (§9.11), which later definitions shadow like any other (`import "a" as a; def g: a::f; def a::f: "local"; [g, a::f]` → `["a.f","local"]`; `import "a" as a; [a::f, (def a::f: 2; a::f)]` → `["a.f",2]`; an alias without that export: `a::zz/0 is not defined`). A closure parameter `p` is a definition of `p/0`: it shadows functions named `p` of arity 0 only (`def x(a): 7; def f(x): x(0); f(1)` → `7`), and a definition inside the body shadows it in turn (`def f(x): def x: 5; x; f(1)` → `5`).
- The body `B` is evaluated with the call's input in the environment where `f` was **defined** (plus its parameters). Free variables, functions and labels of `B` are those visible at the definition: `1 as $x | def f: $x; 2 as $x | f` → `1`.
- A filter argument `ai` is a closure: each time the body evaluates `pi`, `ai` is evaluated on the input given to `pi` at that point, in the caller's environment. Example: `[1] | def f(x): x | x; f(. + [2])` → `[1,2,2]`.
- A `$p` parameter binds each output of the argument (evaluated on the call's input) as `$p`, first parameter outermost (§3.3); it also defines the filter `p/0`.
- Parameters are bound from left to right, so a later parameter shadows an earlier one of the same name: `def f(a; a): a; f(1; 2)` → `2`; `def f(a; $a): [a, $a]; f(1; 2)` → `[2,2]`; `def f($a; a): [a, $a]; f(1; 2)` → `[2,1]`.
- Builtins are resolved like user functions, after them; a user definition of a builtin's name and arity shadows it.

**Single expressions.** An expression is *single* when the rules below say that it yields at most one output, whatever its input and its environment. The rules look at the text of the program only: `1, empty` yields one output and is not single. A single call ends when it has given its output, which is what the call depth of §11 counts.

| Expression | Single |
| --- | --- |
| `.`, a literal, `$x`, `$__loc__`, `$ENV`, `@format`, `break $x` | always |
| `[…]` | always, whatever it contains |
| `a \| b`, `a op b`, `a and b`, `a or b`, `a // b`, `-a`, `T[e]`, `T[e1:e2]`, `{…}`, an interpolated string, `if`, `try` and `?`, `label $x \| B` | when every operand is: the keys and values of the object, the interpolations of the string, the condition and every branch, the body and the handler |
| `S as P \| B`, with any number of `?//` alternatives | when `S`, the key expressions of the patterns and `B` are |
| `reduce S as P (INIT; UPDATE)` | when `INIT` is |
| `T[]`, `a, b`, `foreach` | never |
| a call of a closure parameter | never |
| a call of a native with value arguments (§9.1) | when every argument is |
| a call of a native that takes closures (§9.1) | `empty`, `halt`, `halt_error(c)`, `last(f)`: always; `getpath(p)`, `until(c; u)`: when their arguments are; the others never |
| a call `f(a1; …; an)` of a function defined in jqlite (the program's, a module's, the prelude's) | when `f` is single and the argument of each of its `$` parameters is |

A function is single when its body is single, its `$` parameters being variables there (the binding of §3.3 is not counted). Inside that body a call of the function itself is taken as single: if the body is then single, the function is; if not, the function is not single and neither are those calls. Seen from the body of a definition nested in it, a function that is still being defined is not single.

Examples: `def fib: if . < 2 then . else (. - 1 | fib) + (. - 2 | fib) end;` is single. `def r: ., (.[]? | r);` is not. With `def f($a): $a + 1;`, `f(1)` is single and `f(1, 2)` is not. With `def f(g): g + 1;`, no call of `f` is single, and with `def f(g): [g];` every call is. In `def f: def g: f; g;` neither `g` nor `f` is single.

### 7.8 reduce

`reduce SRC as P (INIT; UPDATE)`: for each output `s0` of `INIT` (on the input), a separate reduction: the state starts as `s0`; for each output `v` of `SRC` (on the input), bound by `P` (patterns as in §7.5), the state becomes the **last** output of `UPDATE` applied to the state, or `null` if `UPDATE` produced none. The final state is output.

**`?//` in `reduce` and `foreach`.** For each output `v` of `SRC` the patterns are tried in order. An alternative is abandoned when destructuring fails or when `UPDATE` (or, for `foreach`, `EXTRACT`) raises an error; the next alternative starts again from the state as it was before `v`, and the outputs `foreach` already produced stay. An error with the last alternative propagates. Errors raised by the consumer of `foreach`'s outputs are not handled (§6.2). Example: `[reduce ([1],[5]) as [$a] ?// $a (10; if $a == 1 then error("e") else [., $a] end)]` → `[[[10,[1]],5]]` (§13). Examples: `reduce (1,2,3) as $x (0; empty)` → `null`; `reduce empty as $x (1, 2; .)` → `1, 2`.

### 7.9 foreach

`foreach SRC as P (INIT; UPDATE; EXTRACT)`: for each output `s0` of `INIT`, a separate run with state `s0`; for each output `v` of `SRC`, bound by `P`: for each output `u` of `UPDATE` applied to the state, the state becomes `u` and the outputs of `EXTRACT` applied to `u` are output. If `UPDATE` produced no output for `v`, the state becomes `null`. Without `EXTRACT` it is `.`. Patterns and `?//` as in §7.5 and §7.8. Example: `foreach (1,2) as $x (0, 1; . + $x)` → `1, 3, 2, 4`; `[foreach ([1]) as [$a] ?// $a (10; . + 1; if $a == 1 then ., error("x") else [., $a] end)]` → `[11,[11,[1]]]` (§13).

### 7.10 Arrays

`[f]` outputs one array of all outputs of `f` (on the input). `[]` is the empty array.

### 7.11 Objects

`{k1: v1, …}` outputs one object per combination of the outputs of the keys and values (§6.3). Keys:

| Entry | Key | Value |
| --- | --- | --- |
| `name: v`, `keyword: v` | `"name"` | outputs of `v` |
| `"s": v`, `@f "s": v` | the string (with interpolation, possibly several) | outputs of `v` |
| `$x: v` | the value of `$x` | outputs of `v` |
| `(e): v` | each output of `e` | outputs of `v` |
| `name`, `keyword`, `"s"` (interpolation allowed) | each output `k` of the key, evaluated once | `.[k]` for that `k` (one object per key output) |
| `$x` | `"x"` | `$x` |

A key that is not a string: `Cannot use <T> (<D>) as object key`. A value expression with no output gives no object (`{a: empty}` → no output). Duplicate keys: the later one wins at the earlier position.

### 7.12 Strings and interpolation

`"a\(e)b"` outputs one string per output of `e`: the literal parts with `e`'s output converted by `tostring`. With several interpolations, see §6.3. `@f "a\(e)b"` converts each interpolated value with the format `@f` (§9.9) instead of `tostring`; the literal parts are not formatted. `@f` alone applies the format to the input.

### 7.13 try, catch and `?`

`try B catch H`: outputs the outputs of `B`. If `B` raises `Error(v)` (its own, §6.2), `B` stops and the outputs of `H` applied to `v` follow; outputs of `B` before the error are kept. Errors raised by `H` propagate. `Break` and `Halt` are not caught. `try B` is `try B catch empty`; `E?` is `try E`.

Examples: `[try (1, error("x"), 3) catch .]` → `[1,"x"]`; `try error(null) catch .` → `null`; `[.[] | try .a]` on `[{}, true, {"a":1}]` → `[null,1]`.

### 7.14 Errors and halting

- `error` raises `Error(.)`; `error(m)` raises `Error(m)` for the first output of `m`. Any value can be an error value.
- `halt` stops the whole program with exit code 0: no further outputs. `halt_error(c)` stops it with the input as the halt value and `trunc(c)` as the exit code. Neither is catchable.

### 7.15 label and break

`label $name | B`: creates a new label instance `l`, evaluates `B` with `$name` → `l`. `break $name` raises `Break(l)` for the instance visible at the `break` (lexically, as with variables: a `break` inside a function refers to the label visible where the function is defined). When `Break(l)` reaches its own `label`, the label stops producing outputs and outputs nothing more; the signal does not propagate further. A `break $name` with no visible `label $name` is the binding error `$*label-name is not defined`.

Example: `[label $f | def g: break $f; (label $f | 1, g, 2), 3]` → `[1]`.

## 8. Paths and assignment

### 8.1 Path mode

Any expression can be evaluated in *path mode*. Each output then carries a *path* (an array of keys from the root input) or no path. A path element is a string (object key), a number (array index) or a slice object `{"start": s, "end": e}` (`null` bounds allowed). `path(f)` outputs the path of each output of `f` evaluated in path mode on its input, whose path is `[]`.

| Construct in path mode | Behavior |
| --- | --- |
| `.` | the input with its path |
| `T[e]`, `T.name`, `T[s:e]`, `T[]` | `e`, `s`, `e` in value mode; the operation on each tracked output of `T` appends the key (`T[]` appends each index or key) |
| `T?`, `try B catch H` | `B` in path mode; `H` in path mode with the error value as an untracked input |
| `a \| b`, `a, b` | path mode throughout |
| `if c then a else b end` | `c` in value mode; branches in path mode |
| `a // b` | `a` in path mode (truthiness by value), `b` in path mode |
| `E as P \| B` | `E` in value mode, `B` in path mode |
| `reduce`, `foreach` | `SRC` in value mode; `INIT` in path mode on the input; `UPDATE` and `EXTRACT` in path mode on the tracked state |
| `label`, `break`, `def`, function and closure calls | transparent (bodies and arguments evaluated in path mode where used) |
| `empty`, `error` | no output |
| `getpath(p)` | `p` in value mode; output: the value at `p` with path `path(.) + p`, even if absent |
| `first/1`, `last/1`, `limit/2`, `skip/2`, `until/2`, `while/2`, `repeat/1`, `recurse`, `select` | as their definitions: they pass the paths of their argument's outputs |
| everything else (literals, `$x`, constructors, operators, other builtins) | untracked outputs |

Errors:

- an untracked output reaching `path`: `Invalid path expression with result <D(v)>`;
- an index on an untracked value: `Invalid path expression near attempt to access element <D(k)> of <D(t)>`;
- `[]` on an untracked value: `Invalid path expression near attempt to iterate through <D(t)>`.

Examples: `path(.a[0, 1])` → `["a",0], ["a",1]`; `path(..)` on `{"a":[1]}` → `[], ["a"], ["a",0]`; `path(.a | map(select(.b == 0)))` → error `Invalid path expression with result [{"b":0}]`; `path(.a[path(.b)[0]])` on `{"a":{"b":0}}` → `["a","b"]`.

### 8.2 getpath, setpath, delpaths

- `getpath(p)`: `p` must be an array, else `Path must be specified as an array`. `getpath([])` is the input; otherwise `.[p0] | getpath(rest)` with the indexing rules of §7.6 (so `null` stays `null`, and a wrong type is an index error).
- `setpath(p; v)`: `p` must be an array. `setpath([]; v)` is `v`. Otherwise with `k = p[0]`: `child = .[k]` (§7.6, so its errors come first: `{} | setpath([0]; 1)` is `Cannot index object with number (0)`), then the container with `k` set to `setpath(rest; v)` applied to `child`:

| Container | Key | Set |
| --- | --- | --- |
| object or null | string | set the key (null becomes `{}`) |
| array or null | number | NaN: `Cannot set array element at NaN index`; `i = trunc(k)`; `i < 0` → `i + length`, still negative: `Out of bounds negative array index`; `i > 536870911`: `Array index too large`; beyond the end: pad with `null` (null becomes `[]`) |
| array or null | slice object | bounds as §7.6 on the current length (`Array/string slice indices must be integers`); the value must be an array (`A slice of an array can only be assigned another array`), which replaces `[s, e)` |
| string | slice object | `Cannot update string slices` |
| other | other | `Cannot update field at <T(k)> index of <T(container)>` |

- `delpaths(ps)`: `ps` must be an array (`Paths must be specified as an array`), each element an array (`Path must be specified as array, not <T>`). No paths: the input. If one path is `[]`: `null`. Otherwise sort `ps` (§4.3), then delete level by level: group the paths by their first key; a group containing the one-element path deletes that key (more specific paths of the group are ignored); for the other groups, `child = .[k]` (index errors propagate; `null` child: nothing to do) is replaced by the deletion of the rest of the group's paths in it. The keys to delete at one level are removed all at once, relative to the container as it was:
  - `null`: unchanged;
  - array: numbers (NaN ignored; `trunc`; negative + length; out of range ignored) and slices (§7.6 bounds) select the elements removed; another key kind: `Cannot delete <T(k)> element of array`;
  - object: string keys removed (absent keys ignored); another key kind: `Cannot delete <T(k)> field of object`;
  - other containers: `Cannot delete fields from <T>` (also when a path goes through a scalar: `{"a":1} | delpaths([["a","b"]])`).

Example: `del(.[1], .[-6], .[2], .[-3:9])` on `[0,1,2,3,4,5,6,7,8,9]` → `[0,3,5,6,9]`.

### 8.3 Assignment operators

The desugarings of §3.3 use two prelude functions:

```jq
def _assign(paths; $value): reduce path(paths) as $p (.; setpath($p; $value));
def _modify(paths; update):
  reduce path(paths) as $p ([., []];
    . as [$x, $del]
    | label $out
    | (($x | getpath($p) | update) as $v | [($x | setpath($p; $v)), $del] | ., break $out),
      [$x, $del + [$p]])
  | . as [$x, $del] | $x | delpaths($del);
```

So:

- `L = R`: `R` is evaluated on the input; for each output, all paths of `L` are set to it; one output per output of `R` (`(.a, .b) = range(2)` → two objects).
- `L |= f`: for each path of `L` (computed on the original input), the first output of `f` applied to the current value at that path replaces it; if `f` has no output, the path is deleted (all deletions happen at the end): `[1,5,3,0,7] | (.[] | select(. >= 2)) |= empty` → `[1,0]`.
- `L op= R`: one output per output of `R` evaluated on the input: `{"a":0} | .a += (1,2)` → `{"a":1}, {"a":2}`.

## 9. Builtins

### 9.1 Conventions

- Each builtin is identified by `name/arity`. **N** = native (implemented in the host language, listed in the native table of the port); **P** = prelude (defined in jqlite; the definition given here goes to `spec/prelude.jq` as is: first the two definitions of §8.3, then those of §9 in the order of this section). A port MAY implement a prelude builtin natively for speed if the results, the errors and the path behavior are identical, and its calls are single (§7.7) exactly when those of the definition are.
- Names starting with `_` are internal: callable, never listed by `builtins`.
- In the tables, `\|` is the character `|` (Markdown escaping); `spec/prelude.jq` contains the definitions without it.
- Natives receive their arguments as closures. Unless an entry says otherwise, a native with value arguments evaluates each argument on the input and calls itself once per combination (§6.3: first argument fastest).
- "num req" means: if the input (or argument) is not a number, `<T> (<D>) number required`.
- Natives marked *loop* MUST NOT consume the call depth (§11) for their iterations: they iterate (with an explicit stack where the definition branches) instead of recursing.

### 9.2 Core

| Builtin | K | Meaning | Errors |
| --- | --- | --- | --- |
| `empty/0` | N | no output | |
| `error/0` | N | raise `Error(.)` | |
| `error/1` | P | `def error($msg): $msg \| error;` | |
| `not/0` | P | `def not: if . then false else true end;` | |
| `type/0` | N | `"null"`, `"boolean"`, `"number"`, `"string"`, `"array"`, `"object"` | |
| `length/0` | N | null → 0; number → absolute value; string → code points; array → elements; object → keys | boolean: `<T> (<D>) has no length` |
| `utf8bytelength/0` | N | UTF-8 byte length of a string | `<T> (<D>) only strings have UTF-8 byte length` |
| `builtins/0` | N | array of `"name/arity"` of every non-internal builtin of the port (prelude and native; `modulemeta/0` only with modules), sorted by code point | |
| `env/0` | P | `def env: $ENV;` | |
| `halt/0` | N | `Halt(none, 0)` | |
| `halt_error/1` | N | `Halt(., trunc(c))` for the first output `c` | `c` not a number: `<T> (<D>) halt_error/1: number required`, the dump being the input's |
| `halt_error/0` | P | `def halt_error: halt_error(5);` | |
| `select/1` | P | `def select(f): if f then . else empty end;` | |
| `recurse/1` | P | `def recurse(f): def r: ., (f \| r); r;` | |
| `recurse/2` | P | `def recurse(f; cond): def r: ., (f \| select(cond) \| r); r;` | |
| `recurse/0` | P | `def recurse: recurse(.[]?);` | |
| `isempty/1` | P | `def isempty(g): [limit(1; g \| null)] == [];` | |
| `first/1` | P | `def first(f): label $out \| (f \| ., break $out);` | |
| `first/0` | P | `def first: .[0];` | |
| `last/0` | P | `def last: .[-1];` | |
| `last/1` | N | the last output of `f`, with its path in path mode; nothing if `f` has none. Same as `reduce f as $x (null; [$x]) \| .[]?` without losing the path. *loop* | |
| `nth/1` | P | `def nth($n): .[$n];` | |
| `nth/2` | P | `def nth($n; f): if $n < 0 then error("nth doesn't support negative indices") else first(skip($n; f)) end;` | |
| `have_decnum/0` | P | `def have_decnum: false;` (numbers are doubles, §4.1) | |
| `have_literal_numbers/0` | P | `def have_literal_numbers: false;` (the text of number literals is not kept, §13) | |
| `debug/0` | N | gives `.` to the host's debug handler (§12), then outputs `.` | |
| `debug/1` | P | `def debug(msgs): (msgs \| debug \| empty), .;` | |
| `stderr/0` | N | gives `.` to the host's stderr handler (§12), then outputs `.` | |
| `input_filename/0` | P | `def input_filename: null;` (inputs are values, not files) | |
| `input_line_number/0` | P | `def input_line_number: 0;` (inputs are values, not lines of text) | |

### 9.3 Types and selection

| Builtin | K | Definition / meaning | Errors |
| --- | --- | --- | --- |
| `values/0` | P | `def values: select(. != null);` | |
| `nulls/0` | P | `def nulls: select(. == null);` | |
| `booleans/0` | P | `def booleans: select(type == "boolean");` | |
| `numbers/0` | P | `def numbers: select(type == "number");` | |
| `strings/0` | P | `def strings: select(type == "string");` | |
| `arrays/0` | P | `def arrays: select(type == "array");` | |
| `objects/0` | P | `def objects: select(type == "object");` | |
| `iterables/0` | P | `def iterables: select(type \| . == "array" or . == "object");` | |
| `scalars/0` | P | `def scalars: select(type \| . != "array" and . != "object");` | |
| `infinite/0` | N | +Infinity | |
| `nan/0` | N | NaN | |
| `isinfinite/0` | N | ±Infinity → true; any other value, numbers or not, → false | |
| `isnan/0` | N | NaN → true; any other value → false | |
| `isnormal/0` | N | a finite, non-zero, not subnormal number (\|x\| ≥ 2^−1022) → true; any other value → false | |
| `isfinite/0` | P | `def isfinite: type == "number" and (isinfinite \| not);` (true for NaN, as in jq) | |
| `finites/0` | P | `def finites: select(isfinite);` (keeps NaN, as in jq) | |
| `normals/0` | P | `def normals: select(isnormal);` | |

### 9.4 Objects, arrays and collections

| Builtin | K | Definition / meaning | Errors |
| --- | --- | --- | --- |
| `keys/0` | N | object: keys sorted by code point; array: `[0, …, length−1]` | `<T> (<D>) has no keys` |
| `keys_unsorted/0` | N | as `keys`, objects in insertion order | idem |
| `has/1` | N | object & string: key present; array & number: NaN → false, else `0 ≤ trunc(k) < length`; null → false | `Cannot check whether <T> has a <T(k)> key` |
| `in/1` | P | `def in(xs): . as $x \| xs \| has($x);` | |
| `contains/1` | N | `a \| contains(b)`: strings: `b` is a substring; arrays: every element of `b` is contained (recursively) by some element of `a`; objects: every key of `b` is in `a` and `a[k]` contains `b[k]`; other kinds: equality. At the top level the kinds must be equal (`true` and `false` are different kinds); inside, a kind mismatch is `false` | `<T> (<D>) and <T> (<D>) cannot have their containment checked` |
| `inside/1` | P | `def inside(xs): . as $x \| xs \| contains($x);` | |
| `map/1` | P | `def map(f): [.[] \| f];` | |
| `map_values/1` | P | `def map_values(f): .[] \|= f;` | |
| `add/1` | P | `def add(f): reduce f as $x (null; . + $x);` | |
| `add/0` | P | `def add: add(.[]);` | |
| `any/2` | P | `def any(generator; condition): isempty(first(generator \| condition or empty)) \| not;` | |
| `all/2` | P | `def all(generator; condition): isempty(first(generator \| condition and empty));` | |
| `any/1` | P | `def any(condition): any(.[]; condition);` | |
| `all/1` | P | `def all(condition): all(.[]; condition);` | |
| `any/0` | P | `def any: any(.);` | |
| `all/0` | P | `def all: all(.);` | |
| `range/2` | N | `$from` outermost, `$upto` inner (exception to §9.1); outputs `v = from`, then `v + 1`, … while `v < upto`. *loop* | non-numbers: `Range bounds must be numeric` |
| `range/1` | P | `def range($x): range(0; $x);` | |
| `range/3` | P | `def range($from; $upto; $by): if $by > 0 then $from \| while(. < $upto; . + $by) elif $by < 0 then $from \| while(. > $upto; . + $by) else empty end;` | |
| `limit/2` | N | as `def limit($n; f): if $n > 0 then label $out \| foreach f as $item (0; . + 1; $item, if . >= $n then break $out else empty end) elif $n == 0 then empty else error("limit doesn't support negative count") end;` passing paths. *loop* | |
| `skip/2` | N | as `def skip($n; f): if $n > 0 then foreach f as $item ($n; . - 1; if . < 0 then $item else empty end) elif $n == 0 then f else error("skip doesn't support negative count") end;` passing paths. *loop* | |
| `while/2` | N | as `def while(cond; update): def _while: if cond then ., (update \| _while) else empty end; _while;` *loop* | |
| `until/2` | N | as `def until(cond; update): def _until: if cond then . else (update \| _until) end; _until;` *loop* | |
| `repeat/1` | N | as `def repeat(f): def _repeat: f, _repeat; _repeat;`: the outputs of `f` on the **same input**, again and again (jq 1.8.2: `[limit(4; 1 \| repeat(. + 1))]` → `[2,2,2,2]`, `[limit(4; repeat(1, 2))]` → `[1,2,1,2]`). *loop* | |
| `reverse/0` | P | `def reverse: [.[length - 1 - range(0; length)]];` (null → `[]`; `""` → `[]`; another string: an index error, as in jq) | |
| `sort/0` | N | stable sort by `compare` (§4.3) | `<T> (<D>) cannot be sorted, as it is not an array` |
| `_sort_by_impl/1` | N | input and `keys` arrays of equal length: the input stably sorted by the corresponding keys | `<T> (<D>) and <T> (<D>) cannot be sorted, as they are not both arrays` |
| `_group_by_impl/1` | N | as `_sort_by_impl`, then consecutive elements with equal keys grouped: an array of arrays | idem |
| `_min_by_impl/1` | N | `null` if empty; else the **first** element with the smallest key | `<T> (<D>) and <T> (<D>) cannot be iterated over` |
| `_max_by_impl/1` | N | `null` if empty; else the **last** element with the largest key | idem |
| `sort_by/1` | P | `def sort_by(f): _sort_by_impl(map([f]));` (several outputs of `f` sort by the tuple) | |
| `group_by/1` | P | `def group_by(f): _group_by_impl(map([f]));` | |
| `unique_by/1` | P | `def unique_by(f): [group_by(f)[] \| .[0]];` | |
| `unique/0` | N | `sort`, then consecutive equal elements kept once | as `sort` |
| `min_by/1` | P | `def min_by(f): _min_by_impl(map([f]));` | |
| `max_by/1` | P | `def max_by(f): _max_by_impl(map([f]));` | |
| `min/0` | N | as `_min_by_impl` with the elements as keys | `<T> (<D>) and <T> (<D>) cannot be iterated over` (the input twice) |
| `max/0` | N | as `_max_by_impl` with the elements as keys | idem |
| `_flatten/1` | P | `def _flatten($x): reduce .[] as $i ([]; if $i \| type == "array" and $x != 0 then . + ($i \| _flatten($x - 1)) else . + [$i] end);` | |
| `flatten/1` | P | `def flatten($x): if $x < 0 then error("flatten depth must not be negative") else _flatten($x) end;` | |
| `flatten/0` | P | `def flatten: _flatten(-1);` | |
| `to_entries/0` | P | `def to_entries: [keys_unsorted[] as $k \| {key: $k, value: .[$k]}];` | |
| `from_entries/0` | P | `def from_entries: reduce .[] as $x ({}; . + {($x \| .key // .Key // .name // .Name): ($x \| if has("value") then .value else .Value end)});` (a key that is not a string is an error, `false` and `null` keys fall through to the next name) | |
| `with_entries/1` | P | `def with_entries(f): to_entries \| map(f) \| from_entries;` | |
| `transpose/0` | P | `def transpose: [range(0; map(length) \| max // 0) as $i \| [.[][$i]]];` | |
| `combinations/0` | P | `def combinations: if length == 0 then [] else .[0][] as $x \| (.[1:] \| combinations) as $w \| [$x] + $w end;` | |
| `combinations/1` | P | `def combinations(n): . as $dot \| [range(n)] \| map($dot) \| combinations;` | |
| `walk/1` | P | `def walk(f): def w: if type == "object" then map_values(w) elif type == "array" then map(w) else . end \| f; w;` | |
| `bsearch/1` | N | on a sorted array: `lo = 0, hi = length − 1`; while `lo ≤ hi`: `mid = floor((lo + hi) / 2)`, `c = compare(.[mid], x)`; `c = 0` → output `mid`; `c < 0` → `lo = mid + 1`, else `hi = mid − 1`. Not found: `−1 − lo` | `<T> (<D>) cannot be searched from` |
| `INDEX/2` | P | `def INDEX(stream; idx_expr): reduce stream as $row ({}; .[$row \| idx_expr \| tostring] \|= $row);` | |
| `INDEX/1` | P | `def INDEX(idx_expr): INDEX(.[]; idx_expr);` | |
| `IN/1` | P | `def IN(s): any(s == .; .);` | |
| `IN/2` | P | `def IN(src; s): any(src == s; .);` | |
| `JOIN/2` | P | `def JOIN($idx; idx_expr): [.[] \| [., $idx[idx_expr]]];` | |
| `JOIN/3` | P | `def JOIN($idx; stream; idx_expr): stream \| [., $idx[idx_expr]];` | |
| `JOIN/4` | P | `def JOIN($idx; stream; idx_expr; join_expr): stream \| [., $idx[idx_expr]] \| join_expr;` | |

### 9.5 Paths

| Builtin | K | Meaning | Errors |
| --- | --- | --- | --- |
| `path/1` | N | §8.1 | §8.1 |
| `getpath/1` | N | §8.2; path-capable | §8.2 |
| `setpath/2` | N | §8.2 (`p` fastest) | §8.2 |
| `delpaths/1` | N | §8.2 | §8.2 |
| `paths/0` | P | `def paths: path(..) \| select(length > 0);` | |
| `paths/1` | P | `def paths(node_filter): . as $dot \| paths \| select(. as $p \| $dot \| getpath($p) \| node_filter);` | |
| `del/1` | P | `def del(f): delpaths([path(f)]);` | |
| `pick/1` | P | `def pick(pathexps): . as $top \| reduce path(pathexps) as $p (null; setpath($p; $top \| getpath($p)));` | |
| `tostream/0` | P | `def tostream: path(def r: (.[]? \| r), .; r) as $p \| getpath($p) \| reduce path(.[]?) as $q ([$p, .]; [$p + $q]);` (`[1,[2]]` → `[[0],1]`, `[[1,0],2]`, `[[1,0]]`, `[[1]]`) | |
| `fromstream/1` | P | `def fromstream(f): {x: null, e: false} as $init \| foreach f as $i ($init; if .e then $init else . end \| if $i \| length == 2 then setpath(["e"]; $i[0] \| length == 0) \| setpath(["x"] + $i[0]; $i[1]) else setpath(["e"]; $i[0] \| length == 1) end; if .e then .x else empty end);` | |
| `truncate_stream/1` | P | `def truncate_stream(stream): . as $n \| null \| stream \| . as $input \| if (.[0] \| length) > $n then setpath([0]; $input[0][$n:]) else empty end;` (`stream` runs on `null`) | |
| `_assign/2`, `_modify/2` | P | §8.3 | |

### 9.6 Strings

| Builtin | K | Meaning | Errors |
| --- | --- | --- | --- |
| `tostring/0` | N | strings unchanged; other values: canonical text (§5.1) | |
| `tojson/0` | N | canonical text | |
| `fromjson/0` | N | the JSON reader (§5.3) | non-string: `<T> (<D>) only strings can be parsed`; reader errors |
| `tonumber/0` | N | numbers unchanged; strings: the whole string must match the numeric text of §5.3 (no whitespace): `"+5.43"` → `5.43`, `".89"` → `0.89`, `"nan"` → NaN, `" 4"` → error | `<T> (<D>) cannot be parsed as a number` |
| `toboolean/0` | N | booleans unchanged; `"true"` → `true`, `"false"` → `false` | any other value: `<T> (<D>) cannot be parsed as a boolean` |
| `format/1` | N | the format named by the string `f` (without `@`) applied to the input, as `@f` (§9.9): `[1,"a"] \| format("csv")` → `"1,\"a\""` | `f` not a string: `<T> (<D>) is not a valid format`; another name: `<f> is not a valid format` |
| `explode/0` | N | array of code points | `explode input must be a string` |
| `implode/0` | N | string of the code points `trunc(n)`; values outside [0, 0x10FFFF] or in [0xD800, 0xDFFF] become U+FFFD | non-array: `implode input must be an array`; non-number or NaN element: `<T> (<D>) can't be imploded, unicode codepoint needs to be numeric` |
| `ascii_downcase/0` | N | `A`–`Z` → `a`–`z`, other code points unchanged | `explode input must be a string` |
| `ascii_upcase/0` | N | `a`–`z` → `A`–`Z` | `explode input must be a string` |
| `startswith/1` | N | prefix test | non-strings: `startswith() requires string inputs` |
| `endswith/1` | N | suffix test | `endswith() requires string inputs` |
| `ltrimstr/1` | P | `def ltrimstr($x): if startswith($x) then .[($x \| length):] else . end;` | |
| `rtrimstr/1` | P | `def rtrimstr($x): if endswith($x) then .[:length - ($x \| length)] else . end;` | |
| `trimstr/1` | P | `def trimstr($x): ltrimstr($x) \| rtrimstr($x);` | |
| `trim/0`, `ltrim/0`, `rtrim/0` | N | the string without its leading and trailing / leading / trailing whitespace: the code points U+0009–U+000D, U+0020, U+0085, U+00A0, U+1680, U+2000–U+200A, U+2028, U+2029, U+202F, U+205F, U+3000 (Unicode `White_Space`) | `trim input must be a string` (all three) |
| `split/1` | N | split on each non-overlapping occurrence of the separator, left to right; `""` input → `[]`; `""` separator → one string per code point | `split input and separator must be strings` |
| `join/1` | P | `def join($x): reduce .[] as $i (null; (if . == null then "" else . + $x end) + ($i \| if . == null then "" elif type == "boolean" or type == "number" then tojson else . end)) // "";` | |
| `_strindices/1` | N | the offsets (code points) of every occurrence of the needle in the input, overlapping occurrences included, ascending; `""` needle → `[]` | input: `<T> (<D>) cannot be searched, as it is not a string`; needle: `<T> (<D>) is not a string` |
| `indices/1` | P | `def indices($i): if type == "array" and ($i \| type) == "array" then .[$i] elif type == "array" then .[[$i]] elif type == "string" and ($i \| type) == "string" then _strindices($i) else .[$i] end;` | |
| `index/1` | P | `def index($i): indices($i) \| .[0];` | |
| `rindex/1` | P | `def rindex($i): indices($i) \| .[-1:][0];` | |
| `test/2` | N | §9.10: whether the regex matches (`re` fastest, then `flags`) | §9.10 |
| `match/2` | N | §9.10: one match object per match (`re` fastest, then `flags`) | §9.10 |
| `test/1` | P | `def test($val): ($val \| type) as $vt \| if $vt == "string" then test($val; null) elif $vt == "array" and ($val \| length) > 1 then test($val[0]; $val[1]) elif $vt == "array" and ($val \| length) > 0 then test($val[0]; null) else error($vt + " not a string or array") end;` | |
| `match/1` | P | `def match($val): ($val \| type) as $vt \| if $vt == "string" then match($val; null) elif $vt == "array" and ($val \| length) > 1 then match($val[0]; $val[1]) elif $vt == "array" and ($val \| length) > 0 then match($val[0]; null) else error($vt + " not a string or array") end;` | |
| `capture/2` | P | `def capture(re; mods): match(re; mods) \| [.captures[] \| select(.name != null) \| {key: .name, value: .string}] \| from_entries;` | |
| `capture/1` | P | `def capture($val): ($val \| type) as $vt \| if $vt == "string" then capture($val; null) elif $vt == "array" and ($val \| length) > 1 then capture($val[0]; $val[1]) elif $vt == "array" and ($val \| length) > 0 then capture($val[0]; null) else error($vt + " not a string or array") end;` | |
| `scan/2` | P | `def scan($re; $flags): match($re; "g" + $flags) \| if (.captures \| length) > 0 then [.captures[].string] else .string end;` | |
| `scan/1` | P | `def scan($re): scan($re; null);` | |
| `split/2` | P | `def split($re; flags): . as $s \| [match($re; "g" + flags) \| (.offset, .offset + .length)] as $ms \| [0] + $ms + [$s \| length] \| [range(0; length; 2) as $i \| $s[.[$i]:.[$i + 1]]];` | |
| `splits/2` | P | `def splits($re; flags): split($re; flags) \| .[];` | |
| `splits/1` | P | `def splits($re): splits($re; null);` | |
| `sub/3` | P | below | |
| `sub/2` | P | `def sub($re; str): sub($re; str; "");` | |
| `gsub/3` | P | `def gsub($re; str; $flags): sub($re; str; $flags + "g");` | |
| `gsub/2` | P | `def gsub($re; str): sub($re; str; "g");` | |

```jq
def sub($re; str; $flags): . as $in
  | (reduce match($re; $flags) as $edit ({result: [], previous: 0};
        $in[.previous:($edit | .offset)] as $gap
        | [$edit | [.captures[] | select(.name != null) | {key: .name, value: .string}] | from_entries | str] as $inserts
        | reduce range(0; $inserts | length) as $ix (.; .result[$ix] += $gap + $inserts[$ix])
        | .previous = ($edit | .offset + .length))
     | .result[] + $in[.previous:])
    // $in;
```

`str` is applied to an object of the named captures (unmatched groups: `null`); several outputs of `str` give several results, the *i*-th result using the *i*-th output at every match: `"aB" | [sub("(?<a>.)"; "\(.a | ascii_upcase)", "\(.a | ascii_downcase)")]` → `["AB","aB"]`.

### 9.7 Dates

| Builtin | K | Meaning | Errors |
| --- | --- | --- | --- |
| `now/0` | N | current time from the host clock, seconds since the Unix epoch (fraction allowed) | |
| `gmtime/0` | N | broken-down UTC time of a number of seconds (§9.12) | `gmtime requires numeric inputs`; non-finite or \|t\| ≥ 2^53: `error converting number of seconds since epoch to datetime` |
| `localtime/0` | N | the same in the host zone: `gmtime` of `t + offset(t)` | `localtime requires numeric inputs`; idem |
| `mktime/0` | N | seconds since the epoch of a broken-down time read as UTC (§9.12), an integer | not an array: `mktime requires array inputs`; a non-number element: `mktime requires parsed datetime inputs` |
| `strftime/1` | N | format a number (via `gmtime`) or a broken-down time as UTC with the directives of §9.12 | input neither: `strftime/1 requires parsed datetime inputs`; format not a string: `strftime/1 requires a string format`; directive errors |
| `strflocaltime/1` | N | the same, a number via `localtime`, a broken-down time read as local time | `strflocaltime/1 requires parsed datetime inputs`, `strflocaltime/1 requires a string format` |
| `strptime/1` | N | parse a string into a broken-down time (§9.12) | non-strings: `strptime/1 requires string inputs and arguments`; mismatch: `date "<input>" does not match format "<format>"` |
| `todateiso8601/0` | P | `def todateiso8601: strftime("%Y-%m-%dT%H:%M:%SZ");` | |
| `fromdateiso8601/0` | P | `def fromdateiso8601: strptime("%Y-%m-%dT%H:%M:%SZ") \| mktime;` | |
| `todate/0` | P | `def todate: todateiso8601;` | |
| `fromdate/0` | P | `def fromdate: fromdateiso8601;` | |

### 9.8 Math

All natives. One-input functions take a number input (num req). Multi-input functions ignore the input and require number arguments (num req on the first non-number argument, in argument order), first argument fastest. A NaN result is a NaN value (written `null`), an infinite result is ±Infinity.

**Exactly specified** (every port MUST give the same double):

| Builtin | Definition |
| --- | --- |
| `floor/0`, `ceil/0`, `trunc/0` | IEEE roundings (`ceil(-1.5)` → `-1`) |
| `round/0` | nearest integer, halves away from zero (`-2.5` → `-3`); a zero result keeps the sign of `x` (`-0.4` → `-0`); ±Infinity and NaN unchanged |
| `nearbyint/0`, `rint/0` | nearest integer, halves to even (`2.5` → `2`); zero sign, infinities and NaN as `round` |
| `fabs/0` | absolute value |
| `sqrt/0` | IEEE correctly rounded square root |
| `fmod/2` | C `fmod`: exact remainder with the sign of the first argument |
| `remainder/2`, `drem/2` | IEEE remainder: `x − n·y` with `n` the integer nearest `x/y`, halves to even; exact |
| `fma/3` | `x·y + z` with a single rounding |
| `fmin/2`, `fmax/2` | if one argument is NaN, the other; else the smaller / larger; if equal (including `0` and `-0`), the first (§13) |
| `fdim/2` | NaN if an argument is NaN; `x − y` if `x > y`; else `0` |
| `copysign/2` | `\|x\|` with the sign of `y` (`-0` is negative); a NaN `y` counts as positive (§13) |
| `nextafter/2`, `nexttoward/2` | NaN if an argument is NaN; `y` if `x == y`; else the double next to `x` in the direction of `y` (from `0` or `-0`, the smallest subnormal with the sign of `y`; from the largest finite double, ±Infinity) |
| `ldexp/2`, `scalbln/2` | `x · 2^trunc(e)`, correctly rounded (including subnormal results); a NaN `e` gives NaN; an infinite `e` is the limit (`ldexp(1; infinite)` → +Infinity, `ldexp(1; -infinite)` → `0`, `ldexp(0; infinite)` → `0`, `ldexp(infinite; -infinite)` → +Infinity); any `e` beyond ±4000 acts as ±4000, which already overflows or underflows every finite non-zero `x` (§13) |
| `scalb/2` | NaN if `x` or `e` is NaN, or `e` is finite and not an integer; `e` = +Infinity: `x · e` (`scalb(0; infinite)` → NaN); `e` = −Infinity: `x / +Infinity` (`scalb(infinite; -infinite)` → NaN); else `ldexp(x; e)` |
| `logb/0` | the unbiased binary exponent of `x` as a number (subnormals included); `0` → −Infinity; ±Infinity → +Infinity; NaN → NaN |
| `significand/0` | `2 · m` for the `m` of `frexp`: `x · 2^−logb(x)`, between 1 (included) and 2 in magnitude; `0`, `-0`, ±Infinity and NaN unchanged |
| `frexp/0` | `[m, e]` with `x = m · 2^e` and `0.5 ≤ \|m\| < 1`; `0` → `[0, 0]`; ±Infinity → `[±Infinity, 0]`; NaN → `[NaN, 0]` (C leaves this exponent unspecified; jq 1.8.2 on Windows gives −1) |
| `modf/0` | `[frac, int]`, both with the sign of `x` (`int = trunc(x)`); ±Infinity → `[±0, ±Infinity]`; NaN → `[NaN, NaN]` |

**Library functions** (C99 / IEEE semantics, including the C99 Annex F special cases, e.g. `pow(1; nan)` = 1, `pow(-1; infinite)` = 1): `acos asin atan acosh asinh atanh cbrt cos cosh exp log log10 log2 sin sinh tan tanh` (one input) and `atan2/2`, `pow/2`, `hypot/2` (no intermediate overflow). Results SHOULD be correctly rounded and MAY differ in the last bit between hosts; the corpus rounds them inside the programs.

**Defined by formula** (no standard library equivalent, or one whose accuracy differs between hosts; written so they port digit for digit; `exp`, `sin`, `cos`, `log`, `pow`, `sqrt` below are the library functions above):

`exp2/0`, `exp10/0`, `expm1/0`, `log1p/0` (`expm1` by Kahan's method, `log1p` by Goldberg's: the rounding error of `exp(x)` or `1 + x` cancels out):

```
exp2(x):  pow(2, x)
exp10(x): pow(10, x)
expm1(x): u = exp(x)
          if u == 1: return x
          d = u - 1
          if d == -1 or u is ±Infinity: return d
          return d * x / log(u)
log1p(x): u = 1 + x
          if u == 1 or u == +Infinity: return x
          return log(u) * x / (u - 1)
```

`erf/0` (Abramowitz & Stegun 7.1.26):

```
erf(x):  if x is NaN: return NaN
         s = sign(x)            (1, -1, or 0 for ±0)
         a = |x|
         t = 1.0 / (1.0 + 0.3275911 * a)
         y = 1.0 - (((((1.061405429 * t + -1.453152027) * t) + 1.421413741) * t + -0.284496736) * t + 0.254829592) * t * exp(-a * a)
         return s * y
erfc(x): 1.0 - erf(x)
```

`tgamma/0`, `lgamma/0`, `gamma/0`, `lgamma_r/0` (Lanczos, g = 7):

```
tgamma(x): if x <= 0 and floor(x) == x: return +Infinity
           if x < 0.5: return π / (sin(π * x) * tgamma(1 - x))
           x = x - 1
           c = [0.99999999999980993, 676.5203681218851, -1259.1392167224028,
                771.32342877765313, -176.61502916214059, 12.507343278686905,
                -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7]
           sum = c[0]; for i in 1..8: sum = sum + c[i] / (x + i)
           t = x + 7 + 0.5
           return sqrt(2 * π) * pow(t, x + 0.5) * exp(-t) * sum
lgamma(x):   log(|tgamma(x)|)
gamma(x):    lgamma(x)
lgamma_r(x): [lgamma(x), -1 if tgamma(x) < 0 else 1]
```

`π` is the double nearest π (3.141592653589793).

`j0/0`, `j1/0`, `y0/0`, `y1/0` (Numerical Recipes rational approximations):

```
j0(x): a = |x|
       if a <= 8.0:
         y = a * a
         n = 57568490574.0 + y * (-13362590354.0 + y * (651619640.7 + y * (-11214424.18 + y * (77392.33017 + y * (-184.9052456)))))
         d = 57568490411.0 + y * (1029532985.0 + y * (9494680.718 + y * (59272.64853 + y * (267.8532712 + y))))
         return n / d
       z = 8.0 / a; y = z * z; xx = a - 0.785398164
       p = 1.0 + y * (-0.1098628627e-2 + y * (0.2734510407e-4 + y * (-0.2073370639e-5 + y * 0.2093887211e-6)))
       q = -0.1562499995e-1 + y * (0.1430488765e-3 + y * (-0.6911147651e-5 + y * (0.7621095161e-6 - y * 0.934935152e-7)))
       return sqrt(0.636619772 / a) * (p * cos(xx) - z * q * sin(xx))

j1(x): s = 1.0; a = x; if a < 0.0: a = -a; s = -1.0
       if a <= 8.0:
         y = a * a
         n = a * (72362614232.0 + y * (-7895059235.0 + y * (242396853.1 + y * (-2972611.439 + y * (15704.48260 + y * (-30.16036606))))))
         d = 144725228442.0 + y * (2300535178.0 + y * (18583304.74 + y * (99447.43394 + y * (376.9991397 + y))))
         return s * n / d
       z = 8.0 / a; y = z * z; xx = a - 2.356194491
       p = 1.0 + y * (0.183105e-2 + y * (-0.3516396496e-4 + y * (0.2457520174e-5 + y * (-0.240337019e-6))))
       q = 0.04687499995 + y * (-0.2002690873e-3 + y * (0.8449199096e-5 + y * (-0.88228987e-6 + y * 0.105787412e-6)))
       return s * sqrt(0.636619772 / a) * (p * cos(xx) - z * q * sin(xx))

y0(x): if x < 0: return NaN
       if x == 0: return -Infinity
       if x < 8.0:
         y = x * x
         n = -2957821389.0 + y * (7062834065.0 + y * (-512359803.6 + y * (10879881.29 + y * (-86327.92757 + y * 228.4622733))))
         d = 40076544269.0 + y * (745249964.8 + y * (7189466.438 + y * (47447.26470 + y * (226.1030244 + y))))
         return n / d + 0.636619772 * j0(x) * log(x)
       z = 8.0 / x; y = z * z; xx = x - 0.785398164
       p = 1.0 + y * (-0.1098628627e-2 + y * (0.2734510407e-4 + y * (-0.2073370639e-5 + y * 0.2093887211e-6)))
       q = -0.1562499995e-1 + y * (0.1430488765e-3 + y * (-0.6911147651e-5 + y * (0.7621095161e-6 + y * (-0.934945152e-7))))
       return sqrt(0.636619772 / x) * (p * sin(xx) + z * q * cos(xx))

y1(x): if x < 0: return NaN
       if x == 0: return -Infinity
       if x < 8.0:
         y = x * x
         n = x * (-0.4900604943e13 + y * (0.1275274390e13 + y * (-0.5153438139e11 + y * (0.7349264551e9 + y * (-0.4237922726e7 + y * 0.8511937935e4)))))
         d = 0.2499580570e14 + y * (0.4244419664e12 + y * (0.3733650367e10 + y * (0.2245904002e8 + y * (0.1020426050e6 + y * (0.3549632885e3 + y)))))
         return n / d + 0.636619772 * (j1(x) * log(x) - 1.0 / x)
       z = 8.0 / x; y = z * z; xx = x - 2.356194491
       p = 1.0 + y * (0.183105e-2 + y * (-0.3516396496e-4 + y * (0.2457520174e-5 + y * (-0.240337019e-6))))
       q = 0.04687499995 + y * (-0.2002690873e-3 + y * (0.8449199096e-5 + y * (-0.88228987e-6 + y * 0.105787412e-6)))
       return sqrt(0.636619772 / x) * (p * sin(xx) + z * q * cos(xx))
```

`jn/2`, `yn/2` (Bessel functions of integer order `n`, the first argument; `jn` by upward recurrence when `|x| > n`, else by Miller's downward recurrence, as Numerical Recipes `bessj`; `yn` by upward recurrence, as `bessy`):

```
jn(n, x): if n or x is NaN, or |n| >= 2^31 (Infinity included): return NaN
          n = trunc(n)
          if n < 0: return -jn(-n, x) if n is odd else jn(-n, x)
          if n == 0: return j0(x)
          if n == 1: return j1(x)
          if x == 0: return 0
          a = |x|; t = 2.0 / a
          if a > n:
            p = j0(a); r = j1(a)
            for k in 1..n-1: s = k * t * r - p; p = r; r = s
          else:
            m = 2 * floor((n + floor(sqrt(160.0 * n))) / 2)
            even = false; p = 0.0; r = 0.0; sum = 0.0; b = 1.0
            for k in m down to 1:
              s = k * t * b - p; p = b; b = s
              if |b| > 1e10: b = b * 1e-10; p = p * 1e-10; r = r * 1e-10; sum = sum * 1e-10
              if even: sum = sum + b
              even = not even
              if k == n: r = p
            r = r / (2.0 * sum - b)
          return -r if x < 0 and n is odd else r

yn(n, x): if n or x is NaN, or |n| >= 2^31 (Infinity included): return NaN
          n = trunc(n)
          if n < 0: return -yn(-n, x) if n is odd else yn(-n, x)
          if n == 0: return y0(x)
          if n == 1: return y1(x)
          if x < 0: return NaN
          if x == 0: return -Infinity
          t = 2.0 / x; p = y0(x); r = y1(x)
          for k in 1..n-1: s = k * t * r - p; p = r; r = s
          return r
```

Operations are evaluated left to right as written, each rounded to double (no fused multiply-add, no extended precision).

> [!NOTE]
> Non-normative: these formulas are approximations with known weak points, kept as specified. `erf` is accurate to about 1.5e−7 only. `tgamma` overflows to +Infinity above about x = 141 (the true value is finite up to about 171.6), gives +Infinity at the poles (C gives NaN for negative integers) and NaN for +Infinity. `lgamma` is `log|tgamma|`, so it overflows where `tgamma` does, far below the true limit of `lgamma`. `j0`, `j1`, `y0`, `y1`, `jn` and `yn` give NaN for ±Infinity (C gives 0); `j0`, `j1`, `y0` and `y1` are accurate to about 1e−8, and so are `jn` for `|x| > n` and `yn`. `frexp` of ±Infinity and NaN gives the exponent 0, where C leaves it unspecified (jq 1.8.2 on Windows: −1).

`abs/0` is prelude: `def abs: if . < 0 then -. else . end;` (as jq 1.8.2: `-0` stays `-0`, values above all numbers such as strings are returned unchanged, `null` and booleans are below 0 and fail with `cannot be negated`).

### 9.9 Formats

`@name` applied to a value `v` produces a string. Formats that say "tostring" first convert `v` with `tostring`. Another name: the runtime error `<name> is not a valid format` (without `@`).

| Format | Result | Errors |
| --- | --- | --- |
| `@text` | `v \| tostring` | |
| `@json` | `v \| tojson` | |
| `@html` | tostring, then `&` → `&amp;`, `<` → `&lt;`, `>` → `&gt;`, `'` → `&apos;`, `"` → `&quot;` | |
| `@uri` | tostring, UTF-8 bytes; `A-Z a-z 0-9 - _ . ~` kept, every other byte → `%XX` (uppercase hex) | |
| `@urid` | tostring; `%XX` (hex of either case) → that byte, other code points → their UTF-8 bytes; the bytes decoded as UTF-8 | a `%` not followed by two hex digits, or bytes that are not well-formed UTF-8 (`"%FF"`): `<T> (<D>) is not a valid uri encoding` |
| `@csv` | array required; elements joined by `,`: string → `"` + the string with `"` doubled + `"`; number → canonical text (NaN → empty); `true`/`false`; `null` → empty | non-array: `<T> (<D>) cannot be csv-formatted, only array`; array/object element: `<T> (<D>) is not valid in a csv row` |
| `@tsv` | array required; elements joined by U+0009: string with `\` → `\\`, tab → `\t`, LF → `\n`, CR → `\r`; number → canonical text (NaN → empty); `true`/`false`; `null` → empty | `<T> (<D>) cannot be tsv-formatted, only array`; array/object element: `<T> (<D>) is not valid in a csv row` (sic) |
| `@sh` | a non-array is treated as a one-element array; elements joined by a space: string → `'` + the string with `'` → `'\''` + `'`; number, boolean, null → canonical text | array/object element: `<T> (<D>) can not be escaped for shell` |
| `@base64` | tostring, UTF-8 bytes, RFC 4648 base64 with `=` padding | |
| `@base64d` | tostring; the text is cut at its first `=` and everything from that `=` on is ignored, valid or not (`"Zg==Zg=="` → `"f"`, `"Zm9v=!!!"` → `"foo"`, `"=Zg"` → `""`); the part before it must be in `A-Z a-z 0-9 + /` and its length mod 4 must not be 1 (`"Z=g"` is an error); decoded (a final group of 2 or 3 characters gives 1 or 2 bytes); the bytes decoded as UTF-8 (§4.1) | another character: `<T> (<D>) is not valid base64 data`; length mod 4 = 1: `<T> (<D>) trailing base64 byte found` (the dump is of the converted string) |

### 9.10 Regular expressions

**Dialect.** Patterns are strings of code points. The dialect is the common subset of RE2/Go, Rust `regex`, ECMAScript (`u` mode), Java, Python `re` and .NET:

| Element | Syntax |
| --- | --- |
| literal | any code point except `\ . * + ? ( ) [ ] { } \| ^ $` |
| escapes | `\\ \. \* \+ \? \( \) \[ \] \{ \} \| \^ \$ \/ \-` (the character); `\t \n \r \f \v` (U+0009, U+000A, U+000D, U+000C, U+000B; §13 for `\v`); `\xHH` (exactly two hex digits). Other code points: write them literally (jq string escapes `\uXXXX` produce them) |
| any | `.`: any code point except U+000A (any code point with the `m`/`p` flags) |
| classes | `[…]`, `[^…]` with at least one item: literals, ranges `a-z` (both ends single code points, literal or escaped, low ≤ high, by code point), the escapes above and `\d \w \s \D \W \S`. Inside a class, `[` and `]` must be escaped; `-` is literal only first, last or escaped. `[]` and `[^]` are errors |
| shorthands | `\d` = `[0-9]`, `\w` = `[0-9A-Za-z_]`, `\s` = `[\t\n\v\f\r ]` (ASCII only); `\D \W \S` their complements |
| anchors | `^` start of the input, `$` end of the input (no line mode); `\b`, `\B` ASCII word boundary (by `\w`) |
| groups | `( … )` capturing, `(?: … )` non-capturing, `(?<name> … )` named capturing; names `[A-Za-z_][A-Za-z0-9_]*`, unique |
| quantifiers | `* + ?`, `{n}`, `{n,}`, `{n,m}` (n ≤ m ≤ 1000), each optionally followed by `?` (lazy); a quantifier follows a literal, `.`, a class, a shorthand or a group, never an anchor, another quantifier (`a**` is an error), `\|`, `(` or the start |
| alternation | `a\|b` (empty alternatives allowed) |

Everything else is the error `Regex failure: <reason>` (as jq; the reason is implementation-defined), in particular: lookahead and lookbehind, backreferences, possessive quantifiers, atomic groups, inline flags `(?i)`, `\p{…}`, POSIX classes, `\A \z \Z \G \K \Q…\E`, `\0`, octal, `\cX`, `\u…`, `\x{…}`, a `{` or `}` that is not a quantifier, a lone `]`.

Matching is leftmost, with Perl priority (greedy or lazy quantifiers, left alternative first). A group inside a repetition reports its last successful participation. Offsets and lengths are in code points.

Note for ports: engines with a different native meaning (`.` excluding `\r` in JS and Java, Unicode `\d` in Python and .NET, UTF-16 units in .NET and JS without `u`, `$` before a final newline in Python, Java and .NET, capture reset in JS repetitions, `(?<name>…)` spelled `(?P<name>…)` in Python, no `_` in Java group names) translate the pattern; a pattern is never handed to an engine unchecked. `\b` and `\B` need an engine with an ASCII word boundary (Go, JavaScript, Rust `(?-u:\b)`) or lookaround to emulate one (`(?<=[0-9A-Za-z_])(?![0-9A-Za-z_])|…`); the reference runs the patterns that use them, and only those, on .NET's backtracking engine for that reason.

**Flags** (a string, or `null` for none; each letter once or more, any order):

| Flag | Meaning |
| --- | --- |
| `g` | all matches (not only the first) |
| `i` | case-insensitive for the ASCII letters only: each ASCII letter of a literal, a range or a class also matches its other case (a class is closed under ASCII case before `^` negates it); every other code point matches only itself. `"ABC" \| test("abc"; "i")` → `true`; `"A" \| test("[^a]"; "i")` → `false`; `"É" \| test("é"; "i")` → `false` (§13). The translator does it; a port MUST NOT use its engine's ignore-case option unless it is exactly this |
| `x` | extended: before compiling, unescaped whitespace (U+0009–U+000D, U+0020) outside classes is removed and `#` outside classes starts a comment up to the next U+000A |
| `n` | ignore empty matches |
| `m`, `p` | `.` also matches U+000A (Oniguruma's "multi line") |
| `s` | single-line mode: no effect (`^`/`$` already match only at the input ends) |
| `l` | longest match: accepted, no effect (§13) |

Another character: `<flags> is not a valid modifier string`. Flags not a string or null: `<T> (<D>) is not a string`.

**Matching** (`match(re; flags)`, `test(re; flags)`): the input must be a string (`<T> (<D>) cannot be matched, as it is not a string`), `re` a string (`<T> (<D>) is not a string`). Matches are searched from position `p = 0`; each search sees the whole input (`^` and `\b` are not affected by `p`):

```
p = 0
while p <= length(input):
    m = first match starting at or after p     (none: stop)
    if not (flag n and m is empty): report m   (without g: stop after the first reported match)
    p = m.end if m.end > m.start else m.end + 1
```

`test` outputs whether a match is reported. `match` outputs, for each reported match:

```
{"offset": o, "length": l, "string": s, "captures": [c1, …]}
c = {"offset": o, "length": l, "string": s, "name": n}    one per capturing group, in order of their "(";
                                                            n is null for unnamed groups;
                                                            a group that did not participate: {"offset": -1, "string": null, "length": 0, "name": n}
                                                            (this key order, as jq)
```

Examples: `"abc" | [match(""; "g")] | length` → `4`; `"abc" | gsub(""; "-")` → `"-a-b-c-"`; `"foo bar foo" | [match("foo"; "g") | .offset]` → `[0,8]`.

### 9.11 Modules (optional group)

A port MAY leave out the whole group: `import`, `include`, `module` and `modulemeta/0`. A port without it rejects those keywords with a syntax error and does not list `modulemeta/0`.

- **Resolution** is the host's, as two operations: *resolve* maps (the directive's path, kind `code` or `data`, the directive's metadata object, the identity of the importing module or of the main program) to a module identity; *read* maps an identity to its text. A path that resolves to nothing is the binding error `module not found: <path>`. Within one binding of a program a module is read and bound once per identity, however many directives name it. A module that imports itself, directly or through others, is the binding error `circular import of <identity>`.
- **Errors in modules.** A syntax or binding error inside a module is reported with the module's identity and its line and column. A data module that is not a sequence of JSON values is a binding error naming the module (text implementation-defined).
- **Code modules** consist of `ModuleBody` (§3.2). A module sees the builtins, its own imports and its own top-level definitions, nothing of the importer (an unbound `$name` in it is a host variable, §7.5). It exports only the definitions written at its top level: not those nested in function bodies, and not those it got from its own `include` or `import` (`incb` = `include "b"; def k: f;` exports `k/0` only: `import "incb" as x; x::f` is `x::f/0 is not defined`).
- `import "p" as name;` defines `name::f/n` for every export `f/n` of `p`, at the directive (§7.7); `name` may itself contain `::` (`import "a" as x::y; x::y::f`). `include "p";` makes them callable unqualified, as if defined at that point (later definitions shadow earlier ones). Re-importing under the same alias: the later import wins.
- `import "p" as $name;` reads a **data** module: its text is a sequence of JSON values (§5.3, separated by whitespace), bound as an array to both `$name` and `$name::name` (`import "values" as $a::b;` binds `$a::b` and `$a::b::a::b`). A data module with no value binds `[]`.
- `module {…};` attaches a metadata object to a module file. In a main program it is accepted and has no effect (`module {"x":1}; 5` → `5`).
- `modulemeta`: input a module path string (resolved as from the main program); output the module's metadata object, followed by `"deps"`: one object per import in order (`include` too), with the import's metadata keys first, then `"as"` (the alias, without `$`; absent for `include`), `"is_data"`, `"relpath"`; then `"defs"`: the exported `"name/arity"` in the order of their first definition, each once (`def x: 1; def y: 1; def x: 2; def x(a): 3;` → `["x/0","y/0","x/1"]`). A module the program already loaded is reused, not read again. Errors: non-string input `modulemeta input module name must be a string`; the resolution and module errors above, raised as runtime errors (catchable: `try ("nosuch" | modulemeta) catch .` → `"module not found: nosuch"`).

Example: for a module `c` with `module {"whatever":null}; import "a" as foo; include "b" {"search":"./"}; import "data" as $d; def c: 1; def aa: 2;`, `"c" | modulemeta` → `{"whatever":null,"deps":[{"as":"foo","is_data":false,"relpath":"a"},{"search":"./","is_data":false,"relpath":"b"},{"as":"d","is_data":true,"relpath":"data"}],"defs":["c/0","aa/0"]}`.

### 9.12 Broken-down time and directives

A **broken-down time** is an array `[year, month, mday, hours, minutes, seconds, wday, yday]`: `month` 0–11, `mday` 1–31, `wday` 0–6 (Sunday = 0), `yday` 0–365. All are integers except that `gmtime`/`localtime` keep the fraction of the input in `seconds`.

**Calendar.** Proleptic Gregorian, any integer year. With `days(y, m, d)` the number of days from 1970-01-01 to the date (`m` 1–12, `d` may be 0 or exceed the month; integer arithmetic, `div` = floor division):

```
days(y, m, d): y = y - (1 if m <= 2 else 0)
               era = y div 400;  yoe = y - era * 400
               doy = (153 * (m + (-3 if m > 2 else 9)) + 2) div 5 + d - 1
               doe = yoe * 365 + yoe div 4 - yoe div 100 + doy
               return era * 146097 + doe - 719468
```

- `gmtime(t)`: `D = floor(t / 86400)`, `r = t − D·86400` (0 ≤ r < 86400); the date of day `D` (inverse of `days`); `hours = floor(r / 3600)`, `minutes = floor(r / 60) mod 60`, `seconds = r mod 60` (fraction kept); `wday = (D + 4) mod 7` (non-negative mod); `yday = D − days(year, 1, 1)`.
- `mktime(bt)`: missing elements are 0; elements must be finite numbers (else `mktime requires parsed datetime inputs`); each is truncated toward zero and clamped to ±2^53. With `m = month`: `Y = year + floor(m / 12)`, `M = m mod 12` (non-negative); result `(days(Y, M + 1, 1) + mday − 1) · 86400 + hours · 3600 + minutes · 60 + seconds` (out-of-range fields roll over, as C `timegm`).
- `strftime`/`strflocaltime` on a broken-down time first normalize it like `mktime` (with their own error text) and recompute `wday` and `yday`; the given `wday`/`yday` are ignored. A result out of the range of `gmtime` is `error converting number of seconds since epoch to datetime`.
- **Local time.** The host supplies a zone: `offset(t)` in seconds east of UTC for an instant, and optionally an abbreviation. `localtime(t)` is `gmtime(t + offset(t))` (fraction kept). `strflocaltime` of a broken-down time reads it as local seconds `L` (as `mktime` does) and takes the instant `L − offset(L − offset(L))`, whose offset `%z`, `%Z` and `%s` use (this settles ambiguous and skipped local times without asking the host).

**strftime directives.** A directive is `%`, optional flags, an optional modifier and a conversion character. Numbers are decimal, padded to the width given, with zeros unless the table says spaces (a negative number: `-` then the padded digits). Names are English.

| Directive | Output |
| --- | --- |
| `%a` / `%A` | `Sun Mon Tue Wed Thu Fri Sat` / `Sunday` … `Saturday` |
| `%b`, `%h` / `%B` | `Jan` … `Dec` / `January` … `December` |
| `%c` | `%a %b %e %H:%M:%S %Y` |
| `%C` | `floor(year / 100)`, width 2 |
| `%d` / `%e` | day of month, width 2 / width 2 padded with spaces |
| `%D`, `%x` | `%m/%d/%y` |
| `%F` | `%Y-%m-%d` |
| `%g` / `%G` | ISO 8601 week-based year mod 100 (non-negative), width 2 / the year, width 4 |
| `%H` / `%I` | hour 0–23 / 1–12, width 2 |
| `%k` / `%l` | hour 0–23 / 1–12, width 2 padded with spaces |
| `%j` | `yday + 1`, width 3 |
| `%m` | `month + 1`, width 2 |
| `%M` | minutes, width 2 |
| `%n` / `%t` | U+000A / U+0009 |
| `%p` / `%P` | `AM` / `am` (hours < 12) or `PM` / `pm` |
| `%r` | `%I:%M:%S %p` |
| `%R` / `%T`, `%X` | `%H:%M` / `%H:%M:%S` |
| `%s` | seconds since the epoch of the instant (an integer: `mktime`, minus the offset for local time) |
| `%S` | `floor(seconds)`, width 2 |
| `%u` / `%w` | weekday 1–7 (Monday = 1) / 0–6 (Sunday = 0) |
| `%U` / `%W` | week of the year, weeks starting on Sunday / Monday, the days before the first one in week 0: `floor((yday + 7 − wday) / 7)` / `floor((yday + 7 − (wday + 6) mod 7) / 7)`, width 2 |
| `%V` | ISO 8601 week number, width 2 |
| `%y` | `year mod 100` (non-negative), width 2 |
| `%Y` | year, width 4 |
| `%z` | `+hhmm` or `-hhmm` (UTC: `+0000`) |
| `%Z` | UTC: `UTC`; local: the host's abbreviation, else the offset as `+hh` (or `+hhmm` if the minutes are not 0) |
| `%%` | `%` |

ISO week: with `iw = ((wday + 6) mod 7) + 1`, `week = floor((yday + 1 − iw + 10) / 7)`; if `week < 1` it is the last week of `year − 1` (and `%G` is `year − 1`); if `week` exceeds the weeks of `year` it is week 1 of `year + 1`. A year has 53 weeks iff `p(y) = 4` or `p(y − 1) = 3`, with `p(y) = (y + floor(y/4) − floor(y/100) + floor(y/400)) mod 7`.

- **Flags** (GNU), any number in any order: `_` pad with spaces, `-` do not pad, `0` pad with zeros, `^` upper case. The last of `_`, `-`, `0` decides how the numbers of a padded directive of the table are padded (`%-d` → `5`, `%_m` → ` 3`, `%0e` → `05`); it has no effect on the directives made of others (`%c %D %F %r %R %T %x %X`), nor on `%s`, `%z`, `%Z`. `^` writes the ASCII letters of the directive's output in upper case (`%^a` → `THU`, `%^c`), except for `%P`.
- **Modifiers** (the alternative forms of a locale, which jqlite does not have): `E` before `c C x X y Y` and `O` before `d e H I m M S u U V w W y` change nothing (`%Ey` is `%y`).
- Another directive: `Unsupported strftime format specifier %<s>`, `<s>` being what follows the `%` up to and including the first character that does not fit (`%Q`, `%Ea`, `%#a` gives `%#`; at the end of the format, what was read: `%E`); a lone `%` at the end of the format: `Invalid format: trailing %` (§13).

**strptime directives.** The input is read left to right. A whitespace character in the format matches zero or more whitespace characters of the input; another literal character matches itself exactly. Before a numeric directive, input whitespace is skipped; a numeric directive reads 1 up to *n* digits and checks the range.

| Directive | Reads |
| --- | --- |
| `%Y` | year, up to 4 digits, 0–9999 |
| `%y` | year of the century, up to 2 digits, 0–99: 69–99 are 1969–1999, 0–68 are 2000–2068 |
| `%m` | month, up to 2 digits, 1–12 (stored − 1) |
| `%d`, `%e` | day, up to 2 digits, 1–31 |
| `%H`, `%k` / `%M` / `%S` | up to 2 digits, 0–23 / 0–59 / 0–60 |
| `%I`, `%l` | hour, up to 2 digits, 1–12 (12 is stored as 0) |
| `%p` | `AM` or `PM`, ASCII case-insensitive |
| `%j` | day of year, up to 3 digits, 1–366 (stored − 1) |
| `%a`, `%A` | a weekday name, full or abbreviated, ASCII case-insensitive (full names tried first) |
| `%u` / `%w` | weekday, 1 digit, 1–7 (7 is Sunday) / 0–6 |
| `%b`, `%h`, `%B` | a month name, full or abbreviated, likewise |
| `%z` | `Z`, or `+`/`-` with `hh`, `hhmm` or `hh:mm` (read and ignored) |
| `%Z` | a run of non-whitespace characters (read and ignored) |
| `%n`, `%t` | zero or more whitespace characters |
| `%%` | `%` |
| `%c`, `%D`, `%F`, `%r`, `%R`, `%T`, `%x`, `%X` | what their strftime expansion reads (`%D`: `%m/%d/%y`) |

After the format, only whitespace may remain; a final lone `%` in the format is a mismatch. Fields not read are `year` 1900, `month` 0, `mday` 0 and 0 for the time. A `PM` read by `%p` adds 12 to an hour read by `%I` or `%l`; without them `%p` changes nothing. `wday` and `yday` are computed from `year`, `month`, `mday` (`wday = (days(year, month + 1, mday) + 4) mod 7`, `yday = days(year, month + 1, mday) − days(year, 1, 1)`) unless `%a`, `%A`, `%u`, `%w` or `%j` read them; `%j` sets `yday` only, never `month` or `mday` (`"2015-063" | strptime("%Y-%j")` → `[2015,0,0,0,0,0,3,62]`; §13). An unknown directive, flags and modifiers included, is a mismatch. Any mismatch: `date "<input>" does not match format "<format>"`.
Example: `"2015-03-05T23:51:47Z" | strptime("%Y-%m-%dT%H:%M:%SZ")` → `[2015,2,5,23,51,47,4,63]`.

## 10. Errors

### 10.1 Message rules

- `<T>` is `T(v)`. `<D>` is the error dump `D(v)`: the canonical text of `v` (§5.1) when it is at most 29 UTF-8 bytes long. A longer text is cut: its longest prefix of at most 25 bytes that ends on a code point boundary (it may end inside a key, an escape or a nested value), then `...`, then the closing character of `v` itself: `"` for a string, `]` for an array, `}` for an object (only the outermost one; nested values get nothing). The result is at most 29 bytes. Other kinds are never longer than 29 bytes (a number's text is at most 24). Examples:
  - `"very-long-long-long-long-string" | try -. catch .` → `"string (\"very-long-long-long-long...\") cannot be negated"`;
  - `[1,2,3,4,5,6,7,8,9,10,11,12,13]` → `[1,2,3,4,5,6,7,8,9,10,11,...]`; `{"a":[1,2,3,4,5,6,7,8,9,10,11,12]}` → `{"a":[1,2,3,4,5,6,7,8,9,1...}`; `[["aaaaaaaaaaaaaaaaaaaaaaaaaaaaa"]]` → `[["aaaaaaaaaaaaaaaaaaaaaa...]`;
  - `["é","ééééééééééééééééé"]` → `["é","ééééééééé...]` (25 bytes: `é` is 2);
  - a 27-character string (29 bytes with its quotes) and `[1,2,3,4,5,6,7,8,9,10,11,123]` (29 bytes) are not cut.
- An error value is any value. `try … catch .` sees it unchanged.
- When an error reaches the host, its message is the value itself if it is a string, else `D'(v) (not a string)` where `D'` is the full canonical text.

### 10.2 Binding and syntax errors

Reported before evaluation, with line and column (§2). Their message texts are implementation-defined except these fixed texts (placeholders filled in), which the error message MUST contain:

| Message | When |
| --- | --- |
| `<name>/<arity> is not defined` | call of an unknown function (including `alias::f` not exported by `alias`) |
| `$*label-<name> is not defined` | `break $name` without a visible `label $name` |
| `$<alias>::<name> is not defined` | qualified variable without its data import |
| `Cannot use <T> (<D>) as object key` | constant non-string key (§3.2) |
| `May need parentheses around object key expression` | §3.2 |
| `Module metadata must be constant` / `… must be an object` | §3.2 |
| `Import path must be constant` | interpolated import path |
| `Invalid escape` | §2 |

### 10.3 Runtime errors

| Id | Message | When |
| --- | --- | --- |
| add | `<T> (<D>) and <T> (<D>) cannot be added` | §4.4 |
| sub | `… cannot be subtracted` | |
| mul | `… cannot be multiplied` | |
| div | `… cannot be divided` | `/` on unsupported kinds |
| mod | `… cannot be divided (remainder)` | `%` on unsupported kinds |
| div0 | `… cannot be divided because the divisor is zero` | `/` by 0 |
| mod0 | `… cannot be divided (remainder) because the divisor is zero` | `%` by 0 |
| neg | `<T> (<D>) cannot be negated` | unary `-` |
| repeat | `Repeat string result too long` | §4.4 |
| index | `Cannot index <T> with <T> (<D>)` | §7.6 (the dump is the key's) |
| slicenum | `Array/string slice indices must be integers` | §7.6 |
| iter | `Cannot iterate over <T> (<D>)` | `.[]` on null or a scalar |
| objkey | `Cannot use <T> (<D>) as object key` | §7.11 |
| undefvar | `$<name> is not defined` | unbound host variable |
| format | `<name> is not a valid format` | unknown `@name` (§9.9) |
| path-result | `Invalid path expression with result <D>` | §8.1 |
| path-access | `Invalid path expression near attempt to access element <D> of <D>` | §8.1 |
| path-iter | `Invalid path expression near attempt to iterate through <D>` | §8.1 |
| patharray | `Path must be specified as an array` | `getpath`, `setpath` |
| delpath | `Path must be specified as array, not <T>` | `delpaths` |
| pathsarray | `Paths must be specified as an array` | §8.2 |
| negidx | `Out of bounds negative array index` | §8.2 |
| bigidx | `Array index too large` | §8.2 |
| nanidx | `Cannot set array element at NaN index` | §8.2 |
| slicearr | `A slice of an array can only be assigned another array` | §8.2 |
| strslice | `Cannot update string slices` | §8.2 |
| setfield | `Cannot update field at <T> index of <T>` | §8.2 |
| delelem | `Cannot delete <T> element of array` | §8.2 |
| delfield | `Cannot delete <T> field of object` | §8.2 |
| delfrom | `Cannot delete fields from <T>` | §8.2 |
| depth | `Maximum call depth exceeded` | §11 |
| *builtins* | the texts in the tables of §9 | |

All runtime errors, including `depth`, are ordinary `Error` signals: `try` catches them.

## 11. Limits

| Limit | Value | On excess |
| --- | --- | --- |
| Call depth: calls of jqlite-defined functions (user and prelude) active at the same time on the evaluation stack | host option `MaxDepth`, default 1000 | error `Maximum call depth exceeded` (catchable) |
| Program nesting: brackets `(`, `[`, `{`, interpolations and `if`/`reduce`/`foreach`/`try`/`def` constructs open at once | 1000 | syntax error `Program too deeply nested` |
| JSON reader nesting | 10000 levels | error `Exceeds depth limit for parsing` |
| JSON writer nesting | values at level 10002 or deeper | written as `<skipped: too deep>` (§5.1) |
| Array index set by `setpath` | 536870911 | error `Array index too large` |
| String repetition result | 2^31 − 1 UTF-8 bytes | error `Repeat string result too long` |
| Regex repetition count | 1000 | invalid regex |

- There are no tail calls: every call of a jqlite function counts while it is active, however it is called. Natives marked *loop* in §9 do not count their iterations.
- A single call (§7.7) is active until it has given its output, or ended without one: the output is consumed after the call. The depth of a recursion made of single calls is therefore the depth of the recursion: `def fib: if . < 2 then . else (. - 1 | fib) + (. - 2 | fib) end; 25 | fib` is 25 calls deep. Any other call stays active while its outputs are consumed downstream, as in the push model, so there the depth counts the calls nested along the current output: `def f: ., (. + 1 | f)` grows the depth with each output, and two such calls joined by an operator nest (`def g: if . < 2 then ., empty else (. - 1 | g) + (. - 2 | g) end; 15 | g` exceeds the default limit).
- Programs whose result depends on the exact depth at which the limit triggers are not portable; the corpus uses depths far from it.
- `MaxDepth` is the only limit on recursion a program can observe: up to it, a port MUST NOT fail because its host stack is short (it grows or replaces the stack, or does not recurse on the host stack); past it, the error is always `Maximum call depth exceeded`, never a host crash.
- No operation may fail by exhausting the host stack for values or programs within these limits: natives that walk values (equality, ordering, `tojson`, `contains`, the reader) iterate or are written so that 10000 levels work.
- `range`, `repeat`, `limit` and friends are lazy and have no length limit.

## 12. Host interface

What the engine needs from its host and gives back (names are the reference's, `JqOptions`):

| In | Meaning |
| --- | --- |
| program text | parsed and bound once; reusable and safe to evaluate concurrently with different inputs and variables |
| input value | converted from the host representation (§4.1 replacement for strings); an evaluation has exactly one. A host API that takes several values (a JSON Lines stream, for example) runs one evaluation on each value, alone: nothing of one evaluation reaches the next |
| host variables | a map `name → value`; names are identifiers without `$` (`[A-Za-z_][A-Za-z0-9_]*`), `ENV` is not allowed; invalid names are rejected before evaluation |
| `Environment` | the map for `$ENV` (string → string; object key order as the host gives it); default: the process environment |
| clock (`TimeProvider`) | optional: `now`, and the local zone (`offset(t)`, optional abbreviation) of `localtime`, `strflocaltime`, `%Z`, `%z`. Without one, `now` is the error `now/0 requires a host clock` and the local zone is UTC with no abbreviation (`localtime` = `gmtime`; `strflocaltime` prints `%z` `+0000` and `%Z` `+00`) |
| `Resolver` | module resolution (§9.11), optional: *resolve* (path, kind, metadata, importer's identity → identity) and *read* (identity → text) |
| `MaxDepth` | §11 |
| `Debug`, `Stderr` | optional handlers: the value `debug` gives (each message of `debug/1`) / the value `stderr` gives; without one, the value is dropped |

| Out | Meaning |
| --- | --- |
| outputs | pushed one by one; the host may stop the stream (stopping is not an error) |
| end | normal completion |
| `Error(v)` | uncaught error, message per §10.1 |
| `Halt(v?, code)` | `halt` (no value, code 0) or `halt_error` (the value and code) |
| syntax/binding error | message, line, column |

## 13. Differences from jq 1.8

Each difference makes jqlite portable or simpler to specify; jq programs that do not depend on them behave identically. The jq column was observed on jq 1.8.2.

| Area | jqlite | jq 1.8 | Why |
| --- | --- | --- | --- |
| Numbers | IEEE doubles only; canonical text §5.2 (`1e-7`, `0.00001`, `100000000000000000000`, `1e+21`); `0 \| -.` prints `-0` | keeps the literal text of numbers that are not computed (decNumber: `1e20` prints `1E+20`, `-0` and `0 \| -.` print `0`); computed numbers in C `%.17g` layout (`1e+20`, `1e-05`, `1e-07`) | exact in every language without a decimal library |
| Surrogates | a lone surrogate becomes U+FFFD everywhere text enters, including `"\ud800"` in a program and in `fromjson` | rejects a lone `\uXXXX` surrogate escape (`Invalid \uXXXX\uXXXX surrogate pair escape`) | one rule for UTF-16 and UTF-8 hosts |
| `break` and `try` | `try` does not catch `break`: `label $out \| try (0, break $out, 1) catch 99` → `0` | `break` is an error that `try` catches: `0, 99` | `break` is a signal of its own (§6.2) |
| Regex | the dialect of §9.10: no lookaround, backreferences, inline flags, `\A \z`, Unicode or POSIX classes; ASCII `\d \w \s \b`; `$` only at the end of the input | Oniguruma Perl-NT: all of these; Unicode `\d`; `$` also before a final newline | the only subset common to Go, Rust, JS, Java, Python, .NET; linear time |
| Regex `i` flag | ASCII letters only: `"É" \| test("é"; "i")`, `"ß" \| test("SS"; "i")`, `"ſ" \| test("s"; "i")` → `false` | Unicode full case folding: all three `true` | engines disagree on Unicode folding (by engine and Unicode version; .NET's non-backtracking engine folds neither `ſ` with `s` nor any letter above U+FFFF); only ASCII is exact everywhere without a folding table |
| Regex `\v` | U+000B | the letter `v` (`"v" \| test("\\v")` → `true`, `"\u000b" \| test("\\v")` → `false`) | the escape every other engine has |
| Regex empty matches | the search moves one code point after an empty match: `"é😀é😀b" \| [match(""; "g")] \| length` → `6` | moves one UTF-8 byte, reporting repeated offsets: `14` (offsets `[0,1,1,2,2,2,2,…]`) | offsets are code points everywhere else |
| Regex `l` flag | accepted, no effect | longest match | no common engine support |
| Recursion | depth limit (§11), no tail calls; a call that is not single (§7.7) stays in the depth while its outputs are consumed | grows the heap, tail calls optimized | host stacks differ (Python 1000 frames) |
| Special functions | `erf`, `erfc`, `tgamma`, `lgamma`, `gamma`, `lgamma_r`, `j0`, `j1`, `jn`, `y0`, `y1`, `yn`, `exp2`, `exp10`, `expm1`, `log1p` by the formulas of §9.8 | libm | JavaScript, Java, Rust lack some; .NET's `expm1`/`log1p` are inaccurate near 0 |
| Math edge cases | `fmin`/`fmax` of `0` and `-0` give the first argument; `ldexp`/`scalbln` with a NaN exponent give NaN, with an infinite or huge one the limit (`ldexp(1; 1e10)` → +Infinity); `frexp` of ±Infinity/NaN has exponent 0; `copysign` with a NaN `y` gives `\|x\|`; `jn`/`yn` with `\|n\| ≥ 2^31` give NaN | libm and C casts: `fmin` gives `-0` and `fmax` `0` for both orders; the exponent is cast to a C `int` (`ldexp(1; nan)`, `ldexp(1; infinite)`, `ldexp(1; 1e10)` → `0`); `frexp` exponent −1 (Windows); `copysign(1; -nan)` → `-1` (the sign bit of the NaN); the order of `jn`/`yn` is cast to a C `int` | exact and the same in every language; JavaScript cannot tell the sign of a NaN |
| NaN ordering | `compare` treats NaNs as equal and below all numbers: `[nan < nan, nan <= nan, nan > nan, nan >= nan]` → `[false,true,false,true]`; `unique`/`group_by` merge NaNs | `[true,true,false,false]` | a consistent total order sorts identically everywhere |
| Unbound `$x` | host variable: runtime error `$x is not defined` | compile error | host variables (`--arg`) without declaring them |
| Unbound `$x` in a module | the same host variable as in the main program | compile error located in the module (`$zz is not defined at …/freevar.jq, line 1, column 8`) | one rule for programs and modules |
| Path tracking | structural (§8.1): variables never carry paths; `limit`, `skip`, `last/1`, `first/1` pass paths | by value identity (a variable holding the value at the current path may pass) | no identity in values |
| Dates | own calendar and directive tables (§9.12), English names, no locale; `gmtime` floors negative fractional seconds; strptime defaults and computed `wday`/`yday`, and `%j` leaves `month` and `mday` at 0 (jq: `"2015-063" \| strptime("%Y-%j")` → `[2015,2,4,…]`); an unknown strftime directive is `Unsupported strftime format specifier %<c>`; the flags `_ - 0` change only the padding of a directive's own numbers (`%_z` → `+0000`); no `#` flag, no field width (`%10Y`), and strptime has no `%C %U %W %V %G %g %s` nor modifiers (§9.12) | C library: glibc writes an unknown directive as it is (`%Q`), pads `%_z` as `+   0`, has `#` and widths; jq on Windows: `strftime/1: unknown system failure` | no libc |
| `%Z` | `UTC`, the host abbreviation, or `+hh` | C library zone names | portable |
| `fromjson` error text | `<reason> (while parsing '<text>')`, reason implementation-defined | jq parser messages with line and column | parser-specific |
| `builtins` | sorted, only this profile's names | unsorted, jq's names | deterministic |
| Host variable `ENV` | rejected | `$ENV` wins | no ambiguity |
| `have_decnum`, `have_literal_numbers` | `false` | `true` (the official builds) | numbers are doubles and literals keep no text (Numbers row) |
| `input_filename`, `input_line_number` | `null`, `0` | the file being read and the number of lines read from it | inputs are values (§12), not files |
| `debug`, `stderr` | the value goes to the host's handler (§12) | written to standard error: `["DEBUG:",v]` and the compact text of `v` | a library has no process streams |
| Ill-formed UTF-8 (`@base64d`) | one U+FFFD per maximal subpart (§4.1): `["7aCA","4ICA","9JCAgA==","6WU="] \| map(@base64d \| explode)` → `[[65533,65533,65533],[65533,65533,65533],[65533,65533,65533,65533],[65533,101]]` | one U+FFFD for a whole overlong, surrogate or too-large sequence, and an incomplete sequence at the end swallows the remaining bytes: `[[65533],[65533],[65533],[65533]]` | the standard decoders of five of the six port languages do this; Go needs a short loop |
| `?//` and the consumer | an error raised downstream of the binding is not handled: `[[1]] \| try ((.[] as [$a] ?// $a \| $a) \| error(tostring)) catch .` → `"1"` | backtracks into the next alternative: `"[1]"` | same principle as `try` (§6.2); jq's behaviour is an artifact of backtracking |
| `?//` in `reduce`/`foreach` | a retried alternative starts from the state before the source value (§7.8): `[reduce ([1],[5]) as [$a] ?// $a (10; if $a == 1 then error("e") else [., $a] end)]` → `[[[10,[1]],5]]`; `[foreach ([1]) as [$a] ?// $a (10; . + 1; if $a == 1 then ., error("x") else [., $a] end)]` → `[11,[11,[1]]]` | after an `UPDATE` error the next alternative sees the state as `null` (`[[null,[1]],5]`); after an `EXTRACT` error it sees the failed alternative's new state (`[11,[12,[1]]]`); an error downstream of `foreach` also retries | artifacts of jq's bytecode (its state variable is cleared when loaded) |
| Constant pattern keys | a non-string key computed by an expression (`. as {(1+0): $v}`) is matched at run time with the indexing rules: `Cannot index object with number (1)` on an object | folds constant expressions and rejects them at compile time: `Cannot use number (1) as object key` | no constant folder; literal keys are still rejected statically (§3.2) |
| Module metadata | literal constants only: `module {a: 1+1};` is `Module metadata must be constant` | folds constant expressions (`{a: 1+1}` accepted), yet rejects `{a: -1}` | no constant folder to specify and port |

**Not in the profile.** Of the builtins of jq 1.8.2 (its `builtins`), jqlite leaves out only these, and a program using one gets `name/arity is not defined`:

- `get_search_list/0`, `get_jq_origin/0` and `get_prog_origin/0` describe the jq command line (its module search path and directories); module resolution is the host's (§9.11).
- `input/0` and `inputs/0` read jq's stream of inputs. An evaluation has one input value, and a host that takes several values evaluates each alone (§12). Reading the next value in the middle of an evaluation would make the interpreter wait on the host's source, which cannot be done the same way in every language when that source is asynchronous (JavaScript cannot block on it).

## 14. Conformance

### 14.1 Corpus format

`spec/tests/*.test` uses jq's test format:

- Lines starting with `#` and blank lines separate cases and are otherwise ignored.
- A case is: one line with the program, one line with the input (JSON), then one line per expected output (JSON), ended by a blank line or the end of the file.
- A case starting with a line `%%FAIL` (or `%%FAIL IGNORE MSG`) is: the program line, then zero or more message lines. It passes iff the program fails to parse or bind (§10.2). Under `%%FAIL`, each message line that is one of the fixed texts of §10.2 (placeholders filled in) must be contained in the implementation's error message; other message lines (jq's own wording, such as `syntax error, unexpected …`) are not compared. Under `%%FAIL IGNORE MSG` no message is compared.

### 14.2 Running a case

- The input and expected lines are read with the JSON reader (§5.3).
- The program runs on the input with no host variables, an empty `$ENV`, `MaxDepth` 1000, and (for `spec/tests/localtime.test`) a host zone with the fixed offset +02:00 (7200 s), no daylight saving and no abbreviation (so `%Z` prints `+02`, `%z` prints `+0200`).
- The case passes iff the program ends normally or by `halt` (not by an uncaught error or `halt_error`) and its outputs equal the expected values one by one, in number and order.
- Each output is compared as it reads back from its canonical text (§5.1, §5.3): NaN compares as `null` and ±Infinity as ±1.7976931348623157e+308, the way jq prints them. Equality for the test is then structural: numbers compared as doubles (−0 equals 0), strings by code points, arrays elementwise, objects with **the same keys in the same insertion order** and equal values.
- Text forms (number text, escaping, key order of `tojson`) are tested through `tojson`, `tostring` or formats inside the programs, so the harness never compares text.
- Expected errors are tested with `try … catch .` inside the program.
- Transcendental results are rounded inside the programs (`sin * 1e9 | round`).
- Module cases (`spec/tests/modules.test`) run with a resolver over `spec/tests/modules/`: a path `p` of a code import or `include` names the file `p.jq`, of a data import `p.json`, in the directory of the file that holds the directive (for the main program, `spec/tests/modules/`); the module's identity is that file; a path that names no file is a missing module. A port without the modules group skips them.

### 14.3 Conformance of a port

A port conforms to jqlite 1.0 when it passes every case of the corpus (modules excepted if it leaves the group out), lists exactly the builtins of §9 in `builtins`, embeds `spec/prelude.jq` unchanged (or natives with identical behavior, §9.1), and follows §11 for limits.