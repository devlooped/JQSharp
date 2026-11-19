using System.Diagnostics;
using BenchmarkDotNet.Running;

// The runner of the cross-language benchmark (bench/README.md).
if (args is ["cross", ..])
    return CrossBench.Run(args[1..]);

if (Debugger.IsAttached)
{
    await RunBenchmarksUnderDebuggerAsync();
    return 0;
}

BenchmarkSwitcher.FromAssembly(typeof(WhatsAppMessageBenchmarks).Assembly).Run(args);
return 0;

static async Task RunBenchmarksUnderDebuggerAsync()
{
    var benchmarks = new WhatsAppMessageBenchmarks();

    benchmarks.Setup();

    Console.WriteLine($"{nameof(WhatsAppMessageBenchmarks.DevloopedJQ)}: {await benchmarks.DevloopedJQ()}");
    Console.WriteLine($"{nameof(WhatsAppMessageBenchmarks.JQLiteWithoutExpressionCaching)}: {benchmarks.JQLiteWithoutExpressionCaching()}");
    Console.WriteLine($"{nameof(WhatsAppMessageBenchmarks.JQLiteWithExpressionCaching)}: {benchmarks.JQLiteWithExpressionCaching()}");
}
