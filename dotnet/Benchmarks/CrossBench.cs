using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using JQLite.Core.Binding;
using JQLite.Core.Runtime;
using JQLite.Core.Values;

/// <summary>
/// The runner of the cross-language benchmark (bench/README.md): the same command line, the same
/// three measurements and the same line of output in every port. It drives the core engine, so the
/// numbers do not include the host boundary (here, <c>JsonElement</c>).
/// </summary>
static class CrossBench
{
    const int Rounds = 10;

    public static int Run(string[] args)
    {
        if (args.Length != 6)
        {
            Console.Error.WriteLine("usage: cross <program.jq> <input.json> <take> <warmup-ms> <measure-ms> <output-file>");
            return 2;
        }

        var source = File.ReadAllText(args[0]);
        var inputText = File.ReadAllText(args[1]);
        var take = int.Parse(args[2], CultureInfo.InvariantCulture);
        var warmupMs = int.Parse(args[3], CultureInfo.InvariantCulture);
        var measureMs = int.Parse(args[4], CultureInfo.InvariantCulture);

        if (!Resolver.TryCompile(source, out var program, out var syntaxError))
        {
            Console.Error.WriteLine($"{syntaxError.Message} at {syntaxError.Line}:{syntaxError.Column}");
            return 1;
        }

        if (!ValueText.TryParse(inputText, out var input, out var inputError))
        {
            Console.Error.WriteLine($"bad input: {inputError}");
            return 1;
        }

        var options = new EvaluationOptions();

        // The check run: the outputs as canonical text, one per line.
        var text = new StringBuilder();
        var count = 0;
        var outcome = Interpreter.Run(program, input, options, output =>
        {
            text.Append(ValueText.ToJson(output)).Append('\n');
            count++;
            return take == 0 || count < take;
        });
        if (outcome.Kind == OutcomeKind.Error || (outcome.Kind == OutcomeKind.Halt && outcome.HasValue))
        {
            Console.Error.WriteLine($"error: {ValueText.ToJson(outcome.Value)}");
            return 1;
        }

        File.WriteAllText(args[5], text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // compile: the program text to a bound program (the prelude is loaded already).
        var compileNs = Measure(warmupMs, measureMs, () => Resolver.TryCompile(source, out _, out _));

        // eval: the bound program on the input value, outputs collected as values.
        var evalNs = Measure(warmupMs, measureMs, () =>
        {
            var outputs = new List<JqValue>();
            Interpreter.Run(program, input, options, output =>
            {
                outputs.Add(output);
                return take == 0 || outputs.Count < take;
            });
        });

        // text: the input text read, the program evaluated, the outputs written as text.
        var textNs = Measure(warmupMs, measureMs, () =>
        {
            ValueText.TryParse(inputText, out var value, out _);
            var written = new StringBuilder();
            var outputs = 0;
            Interpreter.Run(program, value, options, output =>
            {
                written.Append(ValueText.ToJson(output)).Append('\n');
                outputs++;
                return take == 0 || outputs < take;
            });
            _ = written.ToString();
        });

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{{\"impl\":\"dotnet\",\"runtime\":\"{RuntimeInformation.FrameworkDescription}\",\"compile_ns\":{compileNs:F1},\"eval_ns\":{evalNs:F1},\"text_ns\":{textNs:F1},\"outputs\":{count}}}"));
        return 0;
    }

    // Nanoseconds per operation: the median of ten rounds of equal size, sized from the warm-up.
    static double Measure(int warmupMs, int measureMs, Action operation)
    {
        var clock = Stopwatch.StartNew();
        long warmups = 0;
        do
        {
            operation();
            warmups++;
        }
        while (clock.Elapsed.TotalMilliseconds < warmupMs);

        var perOperationMs = clock.Elapsed.TotalMilliseconds / warmups;
        var batch = Math.Max(1, (long)(measureMs / (double)Rounds / perOperationMs));

        var rounds = new double[Rounds];
        for (var round = 0; round < Rounds; round++)
        {
            var start = Stopwatch.GetTimestamp();
            for (long i = 0; i < batch; i++)
                operation();
            rounds[round] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / batch;
        }

        Array.Sort(rounds);
        return (rounds[Rounds / 2 - 1] + rounds[Rounds / 2]) / 2;
    }
}
