using Devlooped.Core.Binding;
using Devlooped.Core.Runtime;
using Devlooped.Core.Values;
using Xunit.Sdk;

namespace Devlooped.Tests.Core.Runtime;

/// <summary>Runs programs on the new core for the runtime tests: outputs as canonical text, and how the evaluation ended.</summary>
static class Evaluate
{
    public static JqValue Json(string text)
        => ValueText.TryParse(text, out var value, out var error) ? value : throw new XunitException($"bad JSON {text}: {error}");

    public static BoundProgram Compile(string program)
        => Resolver.TryCompile(program, out var bound, out var error) ? bound : throw new XunitException($"'{program}': {error.Message} at {error.Line}:{error.Column}");

    /// <summary>The outputs, each as canonical text, then "error: &lt;value&gt;" or "halt …" when the evaluation did not complete.</summary>
    public static string[] Run(string program, string input = "null", EvaluationOptions? options = null, int take = int.MaxValue)
        => Run(Compile(program), Json(input), options, take);

    public static string[] Run(BoundProgram program, JqValue input, EvaluationOptions? options = null, int take = int.MaxValue)
    {
        var outputs = new List<string>();
        var outcome = Interpreter.Run(program, input, options ?? new EvaluationOptions(), output =>
        {
            outputs.Add(ValueText.ToJson(output));
            return outputs.Count < take;
        });
        switch (outcome.Kind)
        {
            case OutcomeKind.Error:
                outputs.Add("error: " + ValueText.ToJson(outcome.Value));
                break;
            case OutcomeKind.Halt:
                outputs.Add($"halt {outcome.Code}" + (outcome.HasValue ? " " + ValueText.ToJson(outcome.Value) : ""));
                break;
            case OutcomeKind.Stopped when outputs.Count < take:
                outputs.Add("stopped");
                break;
        }

        return [.. outputs];
    }

    /// <summary>One line: the outputs separated by ", ".</summary>
    public static string Line(string program, string input = "null", EvaluationOptions? options = null) => string.Join(", ", Run(program, input, options));

    /// <summary>As <see cref="Line"/>, on a thread with a 1 MB stack (the default of a Windows main thread).</summary>
    public static string OnSmallStack(string program, string input = "null", EvaluationOptions? options = null)
    {
        var bound = Compile(program);
        var value = Json(input);
        string? result = null;
        var thread = new Thread(() => result = string.Join(", ", Run(bound, value, options)), 1024 * 1024);
        thread.Start();
        thread.Join();
        return result!;
    }
}
