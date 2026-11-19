# The cross-language benchmark

Compares the implementations of jqlite (the reference in `dotnet/` and each port) on the same
programs and inputs, with the same measurements. `bench/run.ps1` builds every implementation, runs
every case and writes the tables; .NET is the baseline: the reference library (built for `net8.0`) running on .NET 10, the framework of `dotnet/Benchmarks`.

## Cases

`cases.json` lists them: `name`, `program` and `input` (paths relative to this folder), `take` (how
many outputs to take before stopping the evaluation; 0 takes them all) and `what` it shows.
`cases/<name>.expected` holds the outputs of the case: the canonical text (spec §5.1) of each
output followed by a line feed, in UTF-8 without a byte order mark. `cases/records.json` is written
by `cases/records.gen.jq`. The files of `cases/` are byte-exact (`.gitattributes`).

## The runner of an implementation

Each implementation has a runner, a program with six arguments:

```
<runner> <program.jq> <input.json> <take> <warmup-ms> <measure-ms> <output-file>
```

`dotnet/Benchmarks/CrossBench.cs` is the reference; a port's runner does the same steps, on its
core engine (its own values, JSON reader and writer), not through its host boundary:

1. Read the program and the input as UTF-8 text. Compile the program (parse and bind) and read the
   input with the engine's JSON reader. Options are the defaults: no host variables, an empty
   `$ENV`, `MaxDepth` 1000, no clock, no modules.
2. **Check run.** Evaluate once, stopping after `take` outputs when `take` is not 0, and write the
   outputs to `<output-file>` in the format of `.expected`. A program that does not compile, an
   uncaught error or a `halt_error` is a failure: a message on standard error and exit code 1.
3. **Measure** three operations, one after the other, each as described below:
   - `compile`: the program text to a bound program (the prelude is loaded before);
   - `eval`: the bound program on the input value read in step 1, its outputs (`take` of them at
     most) collected in a new list, as values;
   - `text`: the input text read into a value, the program evaluated on it, and each output written
     as canonical text followed by a line feed into one string.
4. Print one line of JSON on standard output and exit with code 0:

   ```json
   {"impl":"dotnet","runtime":".NET 10.0.15","compile_ns":9996.0,"eval_ns":23193650.0,"text_ns":14704950.0,"outputs":1}
   ```

   `impl` is the name in `bench.json`, `runtime` the host and its version, the three numbers are
   nanoseconds per operation and `outputs` is the number of outputs of the check run.

Measuring an operation:

1. **Warm up**: repeat the operation until `warmup-ms` have passed (at least once), counting the
   repetitions.
2. `batch` = max(1, floor((`measure-ms` / 10) / (the time of the warm-up / its repetitions))).
3. Run **10 rounds** of `batch` repetitions, timing each round as a whole with the host's
   monotonic high-resolution clock; the time of a round is its duration divided by `batch`.
4. The result is the **median** of the ten rounds (the mean of the fifth and the sixth).

No garbage collection is forced, and nothing is written to the console while measuring.

## bench.json

Each implementation says how to build and start its runner in `<port>/bench.json`:

```json
{
  "name": "dotnet",
  "build": [["dotnet", "build", "-c", "Release", "Benchmarks/Benchmarks.csproj"]],
  "run": ["dotnet", "Benchmarks/bin/Release/net10.0/Benchmarks.dll", "cross"]
}
```

`build` is a list of commands and `run` the start of the runner's command line, to which the six
arguments are added (as absolute paths); both run in the port's folder. Builds are optimized
(release) builds.

## Running

```
pwsh bench/run.ps1                       # every implementation that has a bench.json
pwsh bench/run.ps1 -Impl dotnet,go       # some of them (dotnet is always run: it is the baseline)
pwsh bench/run.ps1 -Case whatsapp -WarmupMs 200 -MeasureMs 500
```

The script stops on an implementation whose outputs differ from `.expected`, and writes
`bench/results/results.json` and `bench/results/results.md` (not committed): one table per
measurement, each cell the time of the implementation divided by the time of .NET on that case.