using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Devlooped.Core.Runtime;

/// <summary>
/// The one host-specific mechanism of the runtime: what to do when the host stack runs short.
/// Spec §11 lets a program observe no recursion limit but MaxDepth, so a deep evaluation step that
/// finds the stack nearly exhausted continues on a fresh stack instead of failing, and only the call
/// depth counter (Interpreter) ever stops a recursion.
/// </summary>
/// <remarks>
/// <para>
/// .NET: the step runs on a new thread with its own stack while the current one waits for it (as
/// System.Linq.Expressions' StackGuard does). A dedicated thread rather than a task: its stack size
/// is chosen, and a pool thread blocked in a wait, possibly in a hop within a hop, could starve
/// the pool. The step reads and writes the interpreter of the evaluation, which nothing ties to a
/// thread, while its own thread is blocked; values and bound programs are immutable.
/// </para>
/// <para>
/// Other ports: Go grows its stacks, nothing to do; Rust uses the <c>stacker</c> crate
/// (<c>maybe_grow</c>) at the same place; Java runs the step on a <c>new Thread(group, task, name,
/// stackSize)</c> and joins it; Python raises the recursion limit and runs the step on a thread
/// created after <c>threading.stack_size(…)</c>; JavaScript cannot grow its stack and keeps a lower
/// MaxDepth.
/// </para>
/// </remarks>
static class StackGuard
{
    // 16 MB: a hop costs a thread creation, so each one should last. In a Debug build a level of
    // non-tail recursion takes up to about 4 KB, so one hop carries some 4,000 levels; the memory is
    // reserved address space, committed only as the stack grows.
    const int StackSize = 16 * 1024 * 1024;

    /// <summary>Whether the current stack has room for a step (the runtime keeps a safety margin).</summary>
    public static bool HasStack() => RuntimeHelpers.TryEnsureSufficientExecutionStack();

    /// <summary>Runs <paramref name="step"/> on a fresh stack and returns its result.</summary>
    public static T OnFreshStack<T>(Func<T> step)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            // Not control flow: a defect of the step surfaces on the caller's thread.
            try
            {
                result = step();
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
        }, StackSize);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}
