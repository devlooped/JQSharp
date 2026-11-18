# jqlite conformance corpus

Every `*.test` file here is a list of cases that each implementation of jqlite (the reference
in C# and every port) must pass. The format is the one of jq's own `tests/jq.test`.

## File format

The file is read line by line. Blank lines and lines starting with `#` separate cases.

A normal case is a group of consecutive lines:

1. the program, on one line;
2. the input, one JSON value on one line;
3. zero or more expected outputs, one JSON value per line, in order (no line: no output).

```
[try (1, error("x"), 3) catch .]
null
[1,"x"]
```

A case that starts with a `%%FAIL` line is a program that must not parse (a compile error):

```
%%FAIL
. as $foo | break $foo
$*label-foo is not defined
```

The lines after the program are the jq 1.8 error message, and the error of the implementation
must contain them. With `%%FAIL IGNORE MSG` only the failure is checked, not the message.

## Running a case

- Parse the input line as JSON, run the program on it, and collect every output.
- An uncaught error fails the case. Cases that test an error catch it in the program
  (`try ... catch .`), so the message is an ordinary output.
- The case passes when there are as many outputs as expected lines and each output equals its line.

## Comparison rule

Each expected line is parsed as JSON and compared *structurally* with the output:

- numbers as IEEE-754 doubles (`1`, `1.0` and `1e0` are equal);
- strings by code points;
- arrays element by element, in order;
- objects with the same keys **in the same order** (insertion order), each value equal.

Number formatting and string escaping are therefore not compared on outputs: cases that test the
text of a value produce it as a string inside the program (`tojson`, `tostring`, `@json`, `@text`).

## Host requirements

Expected outputs do not depend on the machine (environment variables, current time, culture), except:

- `localtime.test` runs with the host time zone fixed at UTC+02:00, with no daylight saving time.

## pending.txt

`pending.txt` lists the cases the current engine of JQSharp fails, one per line: file name, program
and input, separated by tabs. It belongs to the C# runner (`src/Tests/ConformanceTests.cs`), not to
the corpus: a port starts from an empty list.
