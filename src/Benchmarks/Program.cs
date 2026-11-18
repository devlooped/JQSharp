using System.Diagnostics;
using BenchmarkDotNet.Running;

if (Debugger.IsAttached)
{
    await RunBenchmarksUnderDebuggerAsync();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(WhatsAppMessageBenchmarks).Assembly).Run(args);

static async Task RunBenchmarksUnderDebuggerAsync()
{
    var benchmarks = new WhatsAppMessageBenchmarks();

    benchmarks.Setup();

    Console.WriteLine($"{nameof(WhatsAppMessageBenchmarks.DevloopedJQ)}: {await benchmarks.DevloopedJQ()}");
    Console.WriteLine($"{nameof(WhatsAppMessageBenchmarks.JQLiteWithoutExpressionCaching)}: {benchmarks.JQLiteWithoutExpressionCaching()}");
    Console.WriteLine($"{nameof(WhatsAppMessageBenchmarks.JQLiteWithExpressionCaching)}: {benchmarks.JQLiteWithExpressionCaching()}");
}
