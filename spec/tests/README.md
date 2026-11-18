# jqlite conformance corpus

Every `*.test` file here is a list of cases that each implementation of jqlite (the reference
in C# and every port) must pass. The format is the one of jq's own `tests/jq.test`.

## File format

The file is UTF-8, read line by line. A line ends at LF (U+000A); a CR just before the LF is part of
the line ending, and a lone CR is part of the line (`syntax.test` has a program with one). Blank
lines and lines starting with `#` separate cases.

A normal case is a group of consecutive lines:

1. the program, on one line;
2. the input, one JSON value on one line;
3. zero or more expected outputs, one JSON value per line, in order (no line: no output).

```
[try (1, error("x"), 3) catch .]
null
[1,"x"]
```

A case that starts with a `%%FAIL` line is a program that must fail to parse or to bind (spec §10.2):

```
%%FAIL
. as $foo | break $foo
$*label-foo is not defined
```

The lines after the program are the error message. A line that is one of the fixed texts of spec
§10.2 (`f/1 is not defined`, `$*label-foo is not defined`, `Cannot use number (0) as object key`,
…) must be contained in the implementation's message; other lines are jq's own wording and are not
compared. With `%%FAIL IGNORE MSG` no line is compared.

## Running a case

- Parse the input line as JSON (the reader of spec §5.3), run the program on it with no host
  variables, an empty `$ENV` and the default depth limit, and collect every output.
- The program must end normally or by `halt`. An uncaught error or `halt_error` fails the case;
  cases that test an error catch it in the program (`try ... catch .`), so the message is an
  ordinary output.
- The case passes when there are as many outputs as expected lines and each output equals its line.

## Comparison rule

Each output is written as canonical text (spec §5.1) and read back, so NaN compares as `null` and
±Infinity as ±1.7976931348623157e+308, the way jq prints them. Each expected line is parsed as JSON
and compared *structurally* with it:

- numbers as IEEE-754 doubles (`1`, `1.0` and `1e0` are equal, and so are `-0` and `0`);
- strings by code points;
- arrays element by element, in order;
- objects with the same keys **in the same order** (insertion order), each value equal.

Number formatting and string escaping are therefore not compared on outputs: cases that test the
text of a value produce it as a string inside the program (`tojson`, `tostring`, `@json`, `@text`).

## Host requirements

Expected outputs do not depend on the machine (environment variables, current time, culture), except:

- `localtime.test` runs with the host time zone fixed at UTC+02:00, with no daylight saving time.

## pending.txt and pending-core.txt

`pending.txt` lists the cases the current engine of JQSharp fails, and `pending-core.txt` those the
new core (`src/JQSharp/Core`) fails, grouped by cause; one per line: file name, program and input,
separated by tabs. They belong to the C# runner (`src/Tests/ConformanceTests.cs`), not to the
corpus: a port starts from an empty list.