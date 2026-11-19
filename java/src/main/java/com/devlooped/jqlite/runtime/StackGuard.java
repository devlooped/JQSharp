package com.devlooped.jqlite.runtime;

import java.util.concurrent.ExecutionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.function.Supplier;

/**
 * The one host-specific mechanism of the runtime: what to do when the host stack runs short. Spec
 * §11 lets a program observe no recursion limit but MaxDepth, so a deep evaluation step that finds
 * the stack nearly exhausted continues on a fresh stack instead of failing, and only the call depth
 * counter (Interpreter) ever stops a recursion.
 *
 * <p>Java: the JVM tells neither how much stack a thread has nor how much is left, so the
 * interpreter counts the guarded steps that are active on the current thread (Eval, EvalOne, Output)
 * in that thread's {@link Budget}, and asks {@link #hasStack} whether one more fits: a small budget
 * on the host's threads (whose stack size is unknown; it fits a 256 KB stack), a large one on the
 * threads this class creates, whose stack size it chooses. The count is the thread's, not the
 * evaluation's: an evaluation that the host starts from a callback of another (an output handler,
 * debug, a resolver) on the same thread goes on from what the outer one uses. A step beyond the budget runs on such a thread
 * ({@code new Thread(group, task, name, stackSize)}, here from a cached pool of them so that a hop
 * is a hand-off rather than a thread creation) while the current thread waits for it; the pool
 * creates a new thread when none is idle, so a hop within a hop cannot starve it. The step reads
 * and writes the interpreter of the evaluation, which nothing ties to a thread, while its own
 * thread is blocked; values and bound programs are immutable.
 *
 * <p>A hop costs two thread hand-offs, and a program whose evaluation recurses past the host budget
 * again and again (a recursive function applied to each element of a stream) would pay them for
 * each step: when an evaluation hops {@link #DEEP_HOPS} times or more, its bound program is marked
 * deep (one volatile flag of the program, BoundProgram.markDeep), and later evaluations of it start on
 * a fresh stack at once. A hint only: it changes no result.
 *
 * <p>Cost: a level of recursion of a jqlite function takes about 2 KB of JVM stack, up to 4 KB, so
 * a hop's 64 MB thread carries at least 16,000 levels; deeper recursion nests hops, one thread per
 * 16,384 guarded steps. The memory is committed as the stack grows; MaxDepth bounds it.
 */
public final class StackGuard {
    // 64 MB: reserved address space, committed only as the stack grows.
    static final long STACK_SIZE = 64L * 1024 * 1024;

    /** The guarded steps allowed on a thread of unknown stack size (the host's). */
    static final int HOST_BUDGET = 120;

    /** The guarded steps allowed on a thread of this class, at about 4 KB each at most. */
    static final int FRESH_BUDGET = (int) (STACK_SIZE / 4096);

    /** The hops of one evaluation after which later evaluations of its program start on a fresh stack. */
    static final int DEEP_HOPS = 4;

    /** The guarded steps active on one thread, and how many fit on its stack. */
    static final class Budget {
        int active;
        final int limit;

        Budget(int limit) {
            this.limit = limit;
        }
    }

    // The budgets of the host's threads.
    private static final ThreadLocal<Budget> HOST = ThreadLocal.withInitial(() -> new Budget(HOST_BUDGET));

    // A thread of the pool, with its stack size and budget.
    private static final class GuardThread extends Thread {
        final Budget budget = new Budget(FRESH_BUDGET);

        GuardThread(Runnable task, String name) {
            super(null, task, name, STACK_SIZE);
        }
    }

    private static final AtomicInteger THREADS = new AtomicInteger();

    private static final ExecutorService POOL = Executors.newCachedThreadPool(task -> {
        Thread thread = new GuardThread(task, "jqlite-stack-" + THREADS.incrementAndGet());
        thread.setDaemon(true);
        return thread;
    });

    private StackGuard() {
    }

    /** The budget of the current thread. */
    static Budget current() {
        return Thread.currentThread() instanceof GuardThread thread ? thread.budget : HOST.get();
    }

    /** Whether one more guarded step fits in the budget of a thread. */
    static boolean hasStack(Budget budget) {
        return budget.active < budget.limit;
    }

    /** Runs {@code step} on a fresh stack and returns its result. */
    public static <T> T onFreshStack(Supplier<T> step) {
        Future<T> future = POOL.submit(step::get);
        boolean interrupted = false;
        try {
            while (true) {
                try {
                    return future.get();
                } catch (InterruptedException e) {
                    // The step must finish: the evaluation's state is in its hands.
                    interrupted = true;
                }
            }
        } catch (ExecutionException e) {
            // Not control flow: a defect of the step surfaces on the caller's thread.
            Throwable cause = e.getCause();
            if (cause instanceof RuntimeException runtime) {
                throw runtime;
            }
            if (cause instanceof Error error) {
                throw error;
            }
            throw new IllegalStateException(cause);
        } finally {
            if (interrupted) {
                Thread.currentThread().interrupt();
            }
        }
    }
}